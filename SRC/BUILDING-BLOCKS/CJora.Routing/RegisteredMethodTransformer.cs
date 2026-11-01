using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace CJora.Routing;

/// <summary>Preserves original body bytes and content type/boundary while selecting a validated path.</summary>
public class RegisteredMethodTransformer(Uri destination, string correlationId) : HttpTransformer
{
    private static readonly HashSet<string> ForwardedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Accept-Encoding", "Content-Type", "Content-Length", "Content-Encoding", "Idempotency-Key"
    };

    public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
        request.RequestUri = RequestUtilities.MakeDestinationAddress(
            destination.GetLeftPart(UriPartial.Authority), destination.AbsolutePath, context.Request.QueryString);
        request.Headers.Host = null;
        foreach (var header in request.Headers.ToArray())
            if (!ForwardedHeaders.Contains(header.Key)) request.Headers.Remove(header.Key);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext context,
        HttpResponseMessage? response, CancellationToken cancellationToken)
    {
        if (response is not null && (int)response.StatusCode is >= 300 and < 400)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new { title = "Redirecting method destinations are not supported.",
                status = 502, code = "DESTINATION_REDIRECT_REJECTED", correlationId }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
            return false;
        }
        var copyBody = await base.TransformResponseAsync(context, response, cancellationToken);
        context.Response.Headers.Remove("Set-Cookie");
        context.Response.Headers.Remove("Location");
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return copyBody;
    }
}