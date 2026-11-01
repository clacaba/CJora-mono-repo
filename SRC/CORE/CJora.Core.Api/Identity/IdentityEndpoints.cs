using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using CJora.Core.Application.Identity;
using CJora.Core.Infrastructure.Identity;
using CJora.Identity.Contracts;
using CJora.Routing;
using Microsoft.AspNetCore.Antiforgery;

namespace CJora.Core.Api.Identity;

public sealed record LoginRequest(string ProgramKey,string Email,string ReturnPath);
public sealed class SessionAntiforgeryData : IAntiforgeryAdditionalDataProvider
{
    private static string Binding(HttpContext context) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Cookies[IdentityProtocol.CookieName] ?? "prelogin")));
    public string GetAdditionalData(HttpContext context)=>Binding(context);
    public bool ValidateAdditionalData(HttpContext context,string additionalData)=>Binding(context)==additionalData;
}
public static class IdentityEndpoints
{
    public static void AddCoreIdentity(this WebApplicationBuilder builder)
    {
        var config=builder.Configuration;
        builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Critical);
        builder.Logging.AddFilter("System.Net.Http.HttpClient",LogLevel.Critical);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICoreIdentityStore>(p=>new CoreIdentityStore(config.GetConnectionString("CoreIdentity")??"",p.GetRequiredService<TimeProvider>()));
        builder.Services.AddHttpClient("identity",c=>{c.BaseAddress=new Uri(config["Identity:ManagementAddress"]??"http://localhost:5076/");c.Timeout=TimeSpan.FromSeconds(15);})
            .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false});
        builder.Services.AddSingleton<IIdentitySessionClient>(p=>new IdentitySessionClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("identity"),config["Identity:CallerToken"]??""));
        builder.Services.AddSingleton<CoreSessionService>();
        builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.Name="__Host-cjora-csrf";o.Cookie.SecurePolicy=CookieSecurePolicy.Always;o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Lax;o.Cookie.Path="/";});
        builder.Services.AddSingleton<IAntiforgeryAdditionalDataProvider,SessionAntiforgeryData>();
        builder.Services.AddHostedService<CoreRecoveryWorker>();
    }
    public static void UseCoreIdentity(this WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            var path=context.Request.Path.Value??"";
            if(context.Request.Path.StartsWithSegments("/internal/finance")){await next();return;}
            var identityPath=path.StartsWith("/internal/",StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/idm/",StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/v1/cor/customers/",StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/cor/sessions/",StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/cor/programs/",StringComparison.OrdinalIgnoreCase);
            if (!identityPath) {await next();return;}
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["Referrer-Policy"]="no-referrer";
            try
            {
                if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("Identity:Enabled")) throw new IdentityFailure(503,"IDENTITY_NOT_CONFIGURED");
                if (path.StartsWith("/internal/",StringComparison.OrdinalIgnoreCase) && !LocalIdentityCaller.IsAuthorized(context,app.Configuration["Identity:CallerToken"],true))
                    throw new IdentityFailure(403,"WORKLOAD_ACCESS_DENIED");
                if (!path.StartsWith("/internal/",StringComparison.OrdinalIgnoreCase))
                {
                    if (!LocalIdentityCaller.IsAuthorized(context,app.Configuration["GatewayBridge:Token"],true,GatewayRouteContext.TokenHeader))
                        throw new IdentityFailure(403,"GATEWAY_REQUIRED");
                    context.Request.Scheme="https";
                }
                await next();
            }
            catch (Exception error) when (!context.Response.HasStarted)
            {
                var failure=error as IdentityFailure;
                var status=failure?.Status??(error is BadHttpRequestException bad?bad.StatusCode:503); var code=failure?.Code??(error is BadHttpRequestException?"INVALID_REQUEST":"IDENTITY_UNAVAILABLE");
                var reference=Guid.NewGuid().ToString("D");
                await context.RequestServices.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new RoutingError(code,"IDENTITY",reference,null,"Identity operation rejected."));
                if (status is 401 or 403)
                    try {await context.RequestServices.GetRequiredService<ICoreIdentityStore>().AuditSecurityAsync(code,Guid.NewGuid(),reference);} catch { }
                context.Response.StatusCode=status;
                await context.Response.WriteAsJsonAsync(new {code,reference});
            }
        });
        app.MapGet("/api/v1/cor/programs/resolve",async(string key,ICoreIdentityStore store)=>
            await store.ResolveProgramAsync(key) is {} program?Results.Ok(program):Results.NotFound());
        app.MapGet("/api/v1/idm/csrf",(HttpContext context,IAntiforgery csrf)=>Results.Ok(new {requestToken=csrf.GetAndStoreTokens(context).RequestToken}));
        app.MapPost("/api/v1/idm/sessions",async(HttpContext context,LoginRequest request,ICoreIdentityStore store,IHttpClientFactory factory,IAntiforgery csrf)=>
        {
            await ValidateBrowser(context,csrf,app.Configuration);
            if (string.IsNullOrWhiteSpace(request.ProgramKey)||request.ProgramKey.Length>64||request.Email is null||request.Email.Length>254||
                !MailAddress.TryCreate(request.Email,out var address)||address.Address!=request.Email||!IdentityProtocol.IsAllowedReturnPath(request.ReturnPath))
                throw new IdentityFailure(400,"INVALID_LOGIN_REQUEST");
            var program=await store.ResolveProgramAsync(request.ProgramKey)??throw new IdentityFailure(400,"PROGRAM_UNAVAILABLE");
            var operation=Guid.NewGuid();
            using var message=new HttpRequestMessage(HttpMethod.Post,"internal/authentication/challenge"){Content=JsonContent.Create(new BeginLogin(program.ProgramUuid,operation,request.Email,request.ReturnPath))};
            message.Headers.Add(IdentityProtocol.CallerHeader,app.Configuration["Identity:CallerToken"]);
            using var response=await factory.CreateClient("identity").SendAsync(message,context.RequestAborted);
            var location=response.Headers.Location;
            var authority=new Uri(app.Configuration["Identity:Authority"]??"http://localhost:8080/realms/cjora");
            if (response.StatusCode!=System.Net.HttpStatusCode.Redirect || location is null || !location.IsAbsoluteUri || location.GetLeftPart(UriPartial.Authority)!=authority.GetLeftPart(UriPartial.Authority))
                throw new IdentityFailure(503,"LOGIN_UNAVAILABLE");
            await store.AuditSecurityAsync("LOGIN_STARTED",operation,context.TraceIdentifier);
            if (response.Headers.TryGetValues("Set-Cookie",out var cookies))
                foreach(var cookie in cookies) context.Response.Headers.Append("Set-Cookie",cookie);
            return Results.Ok(new {authorizationUrl=location.AbsoluteUri});
        });
        app.MapGet("/api/v1/idm/sessions/callback",async(HttpContext context,IHttpClientFactory factory)=>
        {
            using var message=new HttpRequestMessage(HttpMethod.Get,"api/v1/idm/sessions/callback"+context.Request.QueryString);
            message.Headers.Add(IdentityProtocol.CallerHeader,app.Configuration["Identity:CallerToken"]);
            if (context.Request.Headers.Cookie.Count>0) message.Headers.TryAddWithoutValidation("Cookie",context.Request.Headers.Cookie.ToString());
            using var response=await factory.CreateClient("identity").SendAsync(message,context.RequestAborted);
            context.Response.StatusCode=(int)response.StatusCode;
            if (response.Headers.TryGetValues("Set-Cookie",out var cookies))
                foreach(var cookie in cookies) context.Response.Headers.Append("Set-Cookie",cookie);
            if (response.Headers.Location is {} redirect)
            {
                // Only fixed relative redirects are returned by the identity callback.
                if (!IdentityProtocol.IsAllowedReturnPath(redirect.OriginalString) && redirect.OriginalString!="/app/login-failed") throw new IdentityFailure(503,"INVALID_LOGIN_REDIRECT");
                context.Response.Headers.Location=redirect.OriginalString;
            }
            context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
        });
        app.MapGet("/api/v1/idm/sessions/failed",()=>Results.Problem("Login could not be completed. Start a new login.",statusCode:400));
        app.MapGet("/api/v1/cor/customers/me",async(HttpContext context,CoreSessionService sessions)=>
        {
            var scope=await Authorize(context,sessions);
            return Results.Ok(new {scope.CustomerUuid,scope.ProgramUuid,scope.DisplayName,scope.Email});
        });
        app.MapGet("/api/v1/cor/sessions/current",async(HttpContext context,CoreSessionService sessions)=>
        {
            var scope=await Authorize(context,sessions);
            return Results.Ok(new {scope.SessionUuid,scope.CustomerUuid,scope.ProgramUuid,scope.ExpiresAtUtc});
        });
        app.MapDelete("/api/v1/idm/sessions/current",async(HttpContext context,CoreSessionService sessions,IAntiforgery csrf)=>
        {
            await ValidateBrowser(context,csrf,app.Configuration);
            bool completed;
            try {completed=await sessions.LogoutAsync(context.Request.Cookies[IdentityProtocol.CookieName],context.TraceIdentifier,context.RequestAborted);}
            finally {context.Response.Cookies.Delete(IdentityProtocol.CookieName,new CookieOptions{Secure=true,HttpOnly=true,SameSite=SameSiteMode.Lax,Path="/"});}
            if (!completed) throw new IdentityFailure(503,"REVOCATION_PENDING");
            return Results.NoContent();
        });
        app.MapPost("/internal/sessions/prepare",async(PrepareSession request,HttpContext context,ICoreIdentityStore store)=>await store.PrepareAsync(request,context.TraceIdentifier));
        app.MapPost("/internal/sessions/activate",async(ActivateSession request,HttpContext context,CoreSessionService service)=>await service.ActivateAsync(request,context.TraceIdentifier,context.RequestAborted));
        app.MapPost("/internal/sessions/revoke",async(RevokeSession request,HttpContext context,ICoreIdentityStore store)=>{await store.RevokeAsync(request.AuthSessionUuid,request.OperationUuid,context.TraceIdentifier,false);return Results.NoContent();});
    }
    internal static async Task ValidateBrowser(HttpContext context,IAntiforgery csrf,IConfiguration config)
    {
        if (context.Request.Headers.Origin.Count!=1 || context.Request.Headers.Origin.ToString()!=(config["Identity:PublicOrigin"]??"https://localhost:7000"))
            throw new IdentityFailure(403,"ORIGIN_REJECTED");
        try {await csrf.ValidateRequestAsync(context);} catch (AntiforgeryValidationException) {throw new IdentityFailure(403,"CSRF_REJECTED");}
    }
    private static Task<CoreIdentitySession> Authorize(HttpContext context,CoreSessionService service)
    {
        Guid? Scope(string header)
        {
            if (!context.Request.Headers.ContainsKey(header)) return null;
            if (context.Request.Headers[header].Count!=1 || !Guid.TryParse(context.Request.Headers[header],out var value)) throw new IdentityFailure(403,"PROGRAM_ACCESS_DENIED");
            return value;
        }
        return service.AuthorizeAsync(context.Request.Cookies[IdentityProtocol.CookieName],Scope("X-Program-UUID"),Scope("X-Customer-UUID"),context.RequestAborted);
    }
}
public sealed class CoreRecoveryWorker(IServiceProvider services,IConfiguration configuration,IWebHostEnvironment environment):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()||!configuration.GetValue<bool>("Identity:Enabled")) return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var store=services.GetRequiredService<ICoreIdentityStore>();var client=services.GetRequiredService<IIdentitySessionClient>();
                foreach(var work in await store.PendingRevocationsAsync())
                {
                    bool success;
                    try {await client.RevokeAsync(new(work.AuthSessionUuid,work.CoreSessionUuid,work.OperationUuid),stoppingToken);success=true;} catch {success=false;}
                    await store.FinishRevocationAsync(work.Uuid,success);
                }
            }
            catch when (!stoppingToken.IsCancellationRequested)
            {
                await services.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new("REVOCATION_RECOVERY_UNAVAILABLE","IDENTITY",Guid.NewGuid().ToString("D"),null,"Session recovery could not run."));
            }
        }
    }
}
