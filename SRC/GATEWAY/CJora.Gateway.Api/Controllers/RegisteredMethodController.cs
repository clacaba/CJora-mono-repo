using System.Diagnostics;
using CJora.Gateway.Api.Routing;
using CJora.Gateway.Application.Routing;
using CJora.Routing;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Forwarder;

namespace CJora.Gateway.Api.Controllers;

[ApiController]
[PreserveRequestBody]
[Route("{service}/{method}")]
public sealed class RegisteredMethodController(
    IServiceMethodRegistry registry, ServiceMethodPolicy policy, IHttpForwarder forwarder, HttpMessageInvoker client,
    IConfiguration configuration, IWebHostEnvironment environment, IRoutingErrorRecorder errors,
    ILogger<RegisteredMethodController> logger) : ControllerBase
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
            var row = await registry.FindAsync(service, method, Request.Method, HttpContext.RequestAborted)
                ?? throw new RouteRejectedException(404, "METHOD_NOT_FOUND", "Registered method not found.");
            _ = policy.Validate(row, service, method, Request.Method, Request.ContentType, Request.ContentLength, environment.IsDevelopment());
            var token = configuration["GatewayBridge:Token"];
            if (!GatewayRouteContext.IsConfiguredToken(token))
                throw new RouteRejectedException(503, "GATEWAY_BRIDGE_UNAVAILABLE", "Gateway routing context is not configured.");
            if (!environment.IsDevelopment())
                throw new RouteRejectedException(503, "WORKLOAD_AUTHORIZATION_NOT_READY", "Production workload authorization is not configured.");
            var coreAddress = configuration["GatewayBridge:CoreAddress"];
            if (!Uri.TryCreate(coreAddress, UriKind.Absolute, out var origin) || !origin.IsLoopback ||
                (origin.Scheme != "http" && origin.Scheme != "https") || !string.IsNullOrEmpty(origin.UserInfo) ||
                origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
                throw new RouteRejectedException(503, "CORE_DESTINATION_UNAVAILABLE", "The Core destination is not configured.");
            var destination = new Uri(origin.GetLeftPart(UriPartial.Authority) + $"/api/v1/cor/dispatch/{service}/{method}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(row.TimeoutMs);
            var result = await forwarder.SendAsync(HttpContext, origin.GetLeftPart(UriPartial.Authority), client,
                new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMilliseconds(row.TimeoutMs) },
                new GatewayToCoreTransformer(destination, correlationId, row, token!), timeout.Token);
            if (result != ForwarderError.None)
            {
                if (!Response.HasStarted)
                {
                    Response.Clear();
                    await Fail(timeout.IsCancellationRequested ? 504 : 502, "CORE_FORWARDING_FAILED", "Core did not complete the request.", correlationId);
                }
                else await Record("CORE_STREAM_FAILED", "The Core response stream failed.", correlationId);
            }
            else if (Response.StatusCode >= 400) await Record("CORE_REJECTED_REQUEST", "Core returned an error status.", correlationId);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { }
        catch (RegistryUnavailableException exception)
        {
            logger.LogWarning("Gateway registry lookup failed: {DiagnosticCode}", exception.DiagnosticCode);
            await Fail(503, "REGISTRY_UNAVAILABLE", "The Gateway method registry is unavailable.", correlationId);
        }
        catch (RouteRejectedException exception) { await Fail(exception.StatusCode, exception.Code, exception.Message, correlationId); }
        catch { await Fail(503, "GATEWAY_DISPATCH_UNAVAILABLE", "The Gateway method could not be processed.", correlationId); }
    }

    private async Task Record(string code, string message, string correlationId)
    {
        try { await errors.RecordAsync(new RoutingError(code, "Routing", correlationId, Activity.Current?.TraceId.ToString(), message)); }
        catch { logger.LogCritical("Gateway error persistence and fallback failed. Code: {Code}; correlation: {CorrelationId}", code, correlationId); }
    }
    private async Task Fail(int status, string code, string message, string correlationId)
    {
        await Record(code, message, correlationId);
        if (Response.HasStarted) return;
        Response.StatusCode = status;
        Response.Headers["X-Correlation-ID"] = correlationId;
        Response.Headers.CacheControl = "no-store";
        await Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = message, Extensions = { ["code"] = code, ["correlationId"] = correlationId }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: HttpContext.RequestAborted);
    }
}