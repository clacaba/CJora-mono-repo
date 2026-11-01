using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace CJora.Routing;

public static class LocalIdentityCaller
{
    public const string Header = "X-CJora-Core-Identity";
    public static bool IsAuthorized(HttpContext context, string? expected, bool development, string header = Header)
    {
        if (!development || !GatewayRouteContext.IsConfiguredToken(expected) || context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)) return false;
        var supplied=context.Request.Headers[header];
        return supplied.Count == 1 && supplied[0] is { } token && token.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }
}