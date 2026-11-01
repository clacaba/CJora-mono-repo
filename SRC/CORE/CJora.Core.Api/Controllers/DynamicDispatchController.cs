using System.Diagnostics;
using System.Net.Http.Headers;
using CJora.Routing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Forwarder;

namespace CJora.Core.Api.Controllers;

[ApiController]
[PreserveRequestBody]
[Route("api/v1/cor/dispatch/{service}/{method}")]
public sealed class DynamicDispatchController(
    GatewayRouteContextReader routeContext, ServiceMethodPolicy policy, IHttpForwarder forwarder,
    HttpMessageInvoker client, IWebHostEnvironment environment, IRoutingErrorRecorder errors,
    ILogger<DynamicDispatchController> logger) : ControllerBase
{
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE")]
    public async Task Dispatch([FromRoute] string service, [FromRoute] string method)
    {
        var correlationId = Guid.NewGuid().ToString("D");
        Response.Headers["X-Correlation-ID"] = correlationId;
        Response.Headers.CacheControl = "no-store";
        try
        {
            if (!ServiceMethodPolicy.IsValidName(service) || !ServiceMethodPolicy.IsValidName(method))
                throw new RouteRejectedException(400, "INVALID_ROUTE", "Invalid service or method name.");
            var row = routeContext.Read(HttpContext);
            if (Request.Headers.TryGetValue("X-Correlation-ID", out var trustedCorrelation) && trustedCorrelation.Count == 1 &&
                Guid.TryParseExact(trustedCorrelation[0], "D", out var correlation))
            {
                correlationId = correlation.ToString("D");
                Response.Headers["X-Correlation-ID"] = correlationId;
            }
            var destination = policy.Validate(row, service, method, Request.Method,
                Request.ContentType, Request.ContentLength, environment.IsDevelopment());
            if (row.BodyType == "NONE" && HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
                throw new RouteRejectedException(415, "UNSUPPORTED_BODY_TYPE", "This method does not accept a request body.");
            if (row.BodyType == "MULTIPART")
            {
                var parsed = MediaTypeHeaderValue.Parse(Request.ContentType!);
                var boundary = parsed.Parameters.FirstOrDefault(parameter => parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
                if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 70 || boundary.Contains('\r') || boundary.Contains('\n'))
                    throw new RouteRejectedException(415, "INVALID_MULTIPART_BOUNDARY", "A valid multipart boundary is required.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(row.TimeoutMs);
            if (row.BodyType != "NONE") await RequestBodyPreflight.BufferUnknownLengthAsync(Request, row.MaxRequestBytes, timeout.Token);
            var maxBody = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBody is { IsReadOnly: false }) maxBody.MaxRequestBodySize = row.MaxRequestBytes;
            var result = await forwarder.SendAsync(HttpContext, destination.GetLeftPart(UriPartial.Authority), client,
                new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMilliseconds(row.TimeoutMs) },
                new RegisteredMethodTransformer(destination, correlationId), timeout.Token);
            if (result != ForwarderError.None)
            {
                var status = timeout.IsCancellationRequested && !HttpContext.RequestAborted.IsCancellationRequested ? 504 : 502;
                var feature = HttpContext.Features.Get<IForwarderErrorFeature>();
                if (feature?.Exception is BadHttpRequestException { StatusCode: 413 }) status = 413;
                if (!Response.HasStarted)
                {
                    Response.Clear();
                    await Fail(status, "METHOD_FORWARDING_FAILED", "The method destination did not complete the request.", correlationId);
                }
                else await Record("METHOD_STREAM_FAILED", "Dependency", "The method response stream failed.", correlationId);
            }
            else if (Response.StatusCode >= 400)
                await Record("METHOD_DESTINATION_REJECTED", "Dependency", "The method destination returned an error status.", correlationId);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await Fail(504, "METHOD_TIMEOUT", "The method did not complete within its time limit.", correlationId);
        }
        catch (RouteRejectedException rejection)
        {
            await Fail(rejection.StatusCode, rejection.Code, rejection.Message, correlationId);
        }
        catch
        {
            await Fail(503, "DISPATCH_UNAVAILABLE", "The method could not be processed.", correlationId);
        }
    }

    private async Task Record(string code, string category, string message, string correlationId)
    {
        try { await errors.RecordAsync(new RoutingError(code, category, correlationId, Activity.Current?.TraceId.ToString(), message)); }
        catch { logger.LogCritical("Routing error persistence and fallback failed. Code: {Code}; correlation: {CorrelationId}", code, correlationId); }
    }

    private async Task Fail(int status, string code, string message, string correlationId)
    {
        await Record(code, status >= 500 ? "Dependency" : "Validation", message, correlationId);
        if (Response.HasStarted) return;
        Response.StatusCode = status;
        Response.Headers["X-Correlation-ID"] = correlationId;
        Response.Headers.CacheControl = "no-store";
        Response.ContentType = "application/problem+json";
        await Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = message,
            Extensions = { ["code"] = code, ["correlationId"] = correlationId }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: HttpContext.RequestAborted);
    }
}