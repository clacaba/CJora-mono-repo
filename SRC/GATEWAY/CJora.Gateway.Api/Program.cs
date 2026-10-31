var builder = WebApplication.CreateBuilder(args);
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { service = "Gateway", status = "alive" }));
app.MapReverseProxy();
app.Run();