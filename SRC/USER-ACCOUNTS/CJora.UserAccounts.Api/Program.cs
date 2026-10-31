var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { service = "UserAccounts", status = "alive" }));
// Login endpoints are intentionally absent until OIDC, session persistence and authorization exist.
app.Run();