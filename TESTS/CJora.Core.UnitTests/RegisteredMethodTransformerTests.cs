using System.Net;
using System.Text;
using CJora.Routing;
using Microsoft.AspNetCore.Http;

namespace CJora.Core.UnitTests;

public sealed class RegisteredMethodTransformerTests
{
    [Fact]
    public async Task Multipart_bytes_and_boundary_survive_while_credentials_and_forged_identity_headers_do_not()
    {
        const string boundary = "CJora-binary-boundary";
        var bytes = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"sample.bin\"\r\n\r\n\0binary\u00ff\r\n--{boundary}--\r\n");
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.ContentLength = bytes.Length;
        context.Request.QueryString = new QueryString("?reference=a%20b");
        context.Request.Headers.Authorization = "Bearer must-not-leak";
        context.Request.Headers.Cookie = "session=must-not-leak";
        context.Request.Headers[GatewayRouteContext.TokenHeader] = "internal-key-must-not-leak";
        context.Request.Headers[GatewayRouteContext.RouteHeader] = "internal-route-must-not-leak";
        context.Request.Headers["X-Program-UUID"] = "forged";
        context.Request.Headers["X-Forwarded-Host"] = "evil.test";
        using var request = new HttpRequestMessage(HttpMethod.Post, (Uri?)null)
        { Content = new StreamContent(context.Request.Body) };
        var transformer = new RegisteredMethodTransformer(new Uri("http://localhost:5082/api/v1/doc/diagnostics/upload"), "trusted-correlation");

        await transformer.TransformRequestAsync(context, request, "http://localhost:5082", CancellationToken.None);

        Assert.Equal("http://localhost:5082/api/v1/doc/diagnostics/upload?reference=a%20b", request.RequestUri!.AbsoluteUri);
        Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync());
        Assert.Contains(boundary, request.Content.Headers.ContentType!.ToString());
        Assert.Null(request.Headers.Host);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains(GatewayRouteContext.TokenHeader));
        Assert.False(request.Headers.Contains(GatewayRouteContext.RouteHeader));
        Assert.False(request.Headers.Contains("X-Program-UUID"));
        Assert.False(request.Headers.Contains("X-Forwarded-Host"));
        Assert.Equal("trusted-correlation", Assert.Single(request.Headers.GetValues("X-Correlation-ID")));
    }

    [Fact]
    public async Task Redirects_are_not_followed_or_exposed_to_the_client()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://unapproved.example.test");
        var transformer = new RegisteredMethodTransformer(new Uri("http://localhost:5075/health/live"), "correlation");

        var copy = await transformer.TransformResponseAsync(context, response, CancellationToken.None);

        Assert.False(copy);
        Assert.Equal(502, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task Diagnostic_responses_cannot_set_browser_session_cookies()
    {
        var context = new DefaultHttpContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=unexpected");
        var transformer = new RegisteredMethodTransformer(new Uri("http://localhost:5075/health/live"), "correlation");
        Assert.True(await transformer.TransformResponseAsync(context, response, CancellationToken.None));
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal("correlation", context.Response.Headers["X-Correlation-ID"]);
    }
}