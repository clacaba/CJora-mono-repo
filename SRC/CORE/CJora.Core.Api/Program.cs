using System.Net;
using CJora.Routing;
using CJora.Routing.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = ServiceMethodPolicy.MaximumBodyBytes);
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton(new GatewayRouteContextReader(builder.Configuration["GatewayBridge:Token"], builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<IRoutingErrorRecorder>(_ => new RoutingErrorRecorder(builder.Configuration.GetConnectionString("CoreErrors"),
    Path.Combine(builder.Environment.ContentRootPath, ".local", "errors", "routing-errors.jsonl")));
builder.Services.AddSingleton(new ServiceMethodPolicy(builder.Configuration.GetSection("ServiceRouting:ApprovedDestinations")
    .GetChildren().ToDictionary(section => section.Key, section => section.Value!, StringComparer.Ordinal)));
builder.Services.AddSingleton(_ => new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
    AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5)
}));


var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// Public TLS terminates at Gateway; this same-host Development listener is private loopback HTTP.
app.UseAuthorization();
app.MapControllers();

app.Run();