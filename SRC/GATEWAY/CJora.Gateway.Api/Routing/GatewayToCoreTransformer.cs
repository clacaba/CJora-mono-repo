using CJora.Routing;

namespace CJora.Gateway.Api.Routing;

public sealed class GatewayToCoreTransformer(Uri destination, string correlationId, ServiceMethod method, string token)
    : RegisteredMethodTransformer(destination, correlationId)
{
    public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        // The base transform removes all caller-supplied identity, route and credential headers.
        await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
        request.Headers.TryAddWithoutValidation(GatewayRouteContext.TokenHeader, token);
        request.Headers.TryAddWithoutValidation(GatewayRouteContext.RouteHeader, GatewayRouteContext.Encode(method));
    }
}