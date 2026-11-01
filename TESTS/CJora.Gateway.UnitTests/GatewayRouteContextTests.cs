using System.Net;
using System.Text;
using CJora.Gateway.Api.Routing;
using CJora.Routing;
using Microsoft.AspNetCore.Http;

namespace CJora.Gateway.UnitTests;

public sealed class GatewayRouteContextTests
{
    private static readonly string Token = new('a', 64); // Synthetic test-only credential, not a configured secret.
    private static ServiceMethod Method => new(Guid.NewGuid(), "core", "health", "http://localhost:5075", "/health/live",
        "GET", "A", true, false, "PUBLIC_DIAGNOSTIC", false, "NONE", 8192, 10000);

    [Fact]
    public void Valid_authenticated_local_context_reaches_Core_without_a_database_lookup()
    {
        var context = Context();
        var row = Method;
        context.Request.Headers[GatewayRouteContext.RouteHeader] = GatewayRouteContext.Encode(row);
        Assert.Equal(row, new GatewayRouteContextReader(Token, true).Read(context));
    }

    [Fact]
    public void Client_supplied_metadata_without_the_Gateway_credential_is_rejected()
    {
        var context = Context();
        context.Request.Headers.Remove(GatewayRouteContext.TokenHeader);
        Assert.Equal(403, Reject(context).StatusCode);
    }

    [Fact]
    public void Wrong_credential_is_rejected()
    {
        var context = Context();
        context.Request.Headers[GatewayRouteContext.TokenHeader] = new string('b', 64);
        Assert.Equal("INVALID_GATEWAY_CALLER", Reject(context).Code);
    }

    [Fact]
    public void Duplicate_credential_headers_are_rejected()
    {
        var context = Context();
        context.Request.Headers.Append(GatewayRouteContext.TokenHeader, Token);
        Assert.Equal(403, Reject(context).StatusCode);
    }

    [Fact]
    public void Remote_peers_cannot_use_the_local_development_bridge()
    {
        var context = Context();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.20");
        Assert.Equal(403, Reject(context).StatusCode);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("")]
    public void Malformed_route_context_is_rejected(string encoded)
    {
        var context = Context();
        context.Request.Headers[GatewayRouteContext.RouteHeader] = encoded;
        Assert.Equal(400, Reject(context).StatusCode);
    }

    [Fact]
    public void Oversized_metadata_is_rejected()
    {
        var context = Context();
        context.Request.Headers[GatewayRouteContext.RouteHeader] = new string('a', 8193);
        Assert.Equal(400, Reject(context).StatusCode);
    }

    [Fact]
    public void Json_without_a_real_registration_cannot_create_an_operation()
    {
        var context = Context();
        context.Request.Headers[GatewayRouteContext.RouteHeader] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(400, Reject(context).StatusCode);
    }

    [Fact]
    public void Missing_configuration_fails_closed()
    {
        var exception = Assert.Throws<RouteRejectedException>(() => new GatewayRouteContextReader(null, true).Read(Context()));
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public void Local_shared_credential_is_never_accepted_as_production_workload_identity()
    {
        var exception = Assert.Throws<RouteRejectedException>(() => new GatewayRouteContextReader(Token, false).Read(Context()));
        Assert.Equal("WORKLOAD_AUTHORIZATION_NOT_READY", exception.Code);
    }

    [Fact]
    public async Task Gateway_replaces_forged_route_headers_and_preserves_the_request_body()
    {
        var context = Context();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sample\":true}"));
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = context.Request.Body.Length;
        context.Request.Headers[GatewayRouteContext.TokenHeader] = "forged-client-token";
        context.Request.Headers[GatewayRouteContext.RouteHeader] = "forged-client-route";
        context.Request.Headers.Authorization = "Bearer must-not-leak";
        using var request = new HttpRequestMessage(HttpMethod.Post, (Uri?)null) { Content = new StreamContent(context.Request.Body) };
        var row = Method;
        var transformer = new GatewayToCoreTransformer(new Uri("http://localhost:5075/api/v1/cor/dispatch/core/health"), "correlation", row, Token);
        await transformer.TransformRequestAsync(context, request, "http://localhost:5075", CancellationToken.None);
        Assert.Equal(Token, Assert.Single(request.Headers.GetValues(GatewayRouteContext.TokenHeader)));
        Assert.Equal(GatewayRouteContext.Encode(row), Assert.Single(request.Headers.GetValues(GatewayRouteContext.RouteHeader)));
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Equal("{\"sample\":true}", await request.Content.ReadAsStringAsync());
        Assert.Equal("/api/v1/cor/dispatch/core/health", request.RequestUri!.AbsolutePath);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers[GatewayRouteContext.TokenHeader] = Token;
        context.Request.Headers[GatewayRouteContext.RouteHeader] = GatewayRouteContext.Encode(Method);
        return context;
    }
    private static RouteRejectedException Reject(HttpContext context) =>
        Assert.Throws<RouteRejectedException>(() => new GatewayRouteContextReader(Token, true).Read(context));
}