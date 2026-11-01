using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CJora.Routing;

public static class GatewayRouteContext
{
    public const string TokenHeader = "X-CJora-Gateway-Token";
    public const string RouteHeader = "X-CJora-Route";
    public static bool IsConfiguredToken([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
    public static string Encode(ServiceMethod method) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(method));
}

/// <summary>Local-development workload authentication only; production requires real workload identity.</summary>
public sealed class GatewayRouteContextReader(string? expectedToken, bool development)
{
    public ServiceMethod Read(HttpContext context)
    {
        if (!development)
            throw new RouteRejectedException(503, "WORKLOAD_AUTHORIZATION_NOT_READY", "Production workload authorization is not configured.");
        if (!GatewayRouteContext.IsConfiguredToken(expectedToken))
            throw new RouteRejectedException(503, "GATEWAY_BRIDGE_UNAVAILABLE", "Gateway routing context is not configured.");
        var peer = context.Connection.RemoteIpAddress;
        var supplied = context.Request.Headers[GatewayRouteContext.TokenHeader];
        if (peer is null || !IPAddress.IsLoopback(peer) || supplied.Count != 1 || supplied[0] is not { } token ||
            token.Length != expectedToken!.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expectedToken)))
            throw new RouteRejectedException(403, "INVALID_GATEWAY_CALLER", "Authenticated Gateway routing context is required.");
        var encoded = context.Request.Headers[GatewayRouteContext.RouteHeader];
        if (encoded.Count != 1 || string.IsNullOrWhiteSpace(encoded[0]) || encoded[0]!.Length > 8192)
            throw InvalidContext();
        try
        {
            var row = JsonSerializer.Deserialize<ServiceMethod>(Convert.FromBase64String(encoded[0]!));
            if (row is null || row.Uuid == Guid.Empty || string.IsNullOrWhiteSpace(row.ServiceName) ||
                string.IsNullOrWhiteSpace(row.MethodName) || string.IsNullOrWhiteSpace(row.Host) || string.IsNullOrWhiteSpace(row.Path))
                throw InvalidContext();
            return row;
        }
        catch (FormatException) { throw InvalidContext(); }
        catch (JsonException) { throw InvalidContext(); }
    }
    private static RouteRejectedException InvalidContext() => new(400, "INVALID_GATEWAY_CONTEXT", "The Gateway routing context is invalid.");
}