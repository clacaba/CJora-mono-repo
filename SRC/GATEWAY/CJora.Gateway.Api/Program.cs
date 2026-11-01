using CJora.Gateway.Api;
using System.Net;
using CJora.Routing;
using CJora.Routing.Persistence;
using CJora.Gateway.Application.Routing;
using CJora.Gateway.Infrastructure.Routing;
using Yarp.ReverseProxy.Transforms;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 7000);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { status = 429, title = "Request limit exceeded.", code = "RATE_LIMITED" },
            options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 10 * 1024 * 1024);
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context => context.AddRequestTransform(transform =>
    {
        transform.ProxyRequest.Headers.Remove(GatewayRouteContext.TokenHeader);
        transform.ProxyRequest.Headers.Remove(GatewayRouteContext.RouteHeader);
        transform.ProxyRequest.Headers.Remove(LocalIdentityCaller.Header);
        if (builder.Environment.IsDevelopment() && GatewayRouteContext.IsConfiguredToken(builder.Configuration["GatewayBridge:Token"]) &&
            (transform.HttpContext.Request.Path.StartsWithSegments("/api/v1/idm") || transform.HttpContext.Request.Path.StartsWithSegments("/api/v1/cor/customers") ||
             transform.HttpContext.Request.Path.StartsWithSegments("/api/v1/cor/sessions") || transform.HttpContext.Request.Path.StartsWithSegments("/api/v1/cor/programs")))
            transform.ProxyRequest.Headers.TryAddWithoutValidation(GatewayRouteContext.TokenHeader,builder.Configuration["GatewayBridge:Token"]);
        return ValueTask.CompletedTask;
    }));
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton<IServiceMethodRegistry>(_ => new MySqlServiceMethodRegistry(builder.Configuration.GetConnectionString("GatewayRegistry")));
builder.Services.AddSingleton<IRoutingErrorRecorder>(_ => new RoutingErrorRecorder(builder.Configuration.GetConnectionString("GatewayRegistry"),
    Path.Combine(builder.Environment.ContentRootPath, ".local", "errors", "routing-errors.jsonl"), "CJORA_GATEWAY", "Gateway"));
builder.Services.AddSingleton(new ServiceMethodPolicy(builder.Configuration.GetSection("ServiceRouting:ApprovedDestinations")
    .GetChildren().ToDictionary(section => section.Key, section => section.Value!, StringComparer.Ordinal)));
builder.Services.AddSingleton(_ => new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
    AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
}));

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseHsts();
// Local diagnostics remain convenient over loopback HTTP; public/versioned dispatch always redirects to HTTPS.
app.UseWhen(context => !app.Environment.IsDevelopment() ||
    !(context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/swagger") ||
      string.Equals(context.Request.Path.Value, "/WeatherForecast", StringComparison.OrdinalIgnoreCase)),
    branch => branch.UseHttpsRedirection());
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Response.StatusCode >= 500)
        app.Logger.LogWarning("Gateway forwarding failure. Status: {Status}; trace: {Trace}", context.Response.StatusCode, context.TraceIdentifier);
});
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapWebFrontend();
// Serve fixed frontend assets before the dynamic two-segment API route is selected.
app.UseRouting();
app.MapControllers();
app.MapReverseProxy();
app.Run();