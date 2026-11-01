using System.Globalization;
using CJora.Identity.Contracts;
using CJora.IdentityManagement.Application.Authentication;
using CJora.IdentityManagement.Domain.Authentication;
using CJora.IdentityManagement.Infrastructure.Authentication;
using CJora.Routing;
using CJora.Routing.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CJora.IdentityManagement.Api.Authentication;

public static class LoginEndpoints
{
    public static void AddIdentityLogin(this WebApplicationBuilder builder)
    {
        var config=builder.Configuration;
        var authority=config["Identity:Authority"]??"http://localhost:8080/realms/cjora";
        var publicOrigin=config["Identity:PublicOrigin"]??"https://localhost:7000";
        var callback=publicOrigin+"/api/v1/idm/sessions/callback";
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAuthenticationStore>(p=>new AuthenticationStore(config.GetConnectionString("Identity")??"",p.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IRoutingErrorRecorder>(_=>new RoutingErrorRecorder(config.GetConnectionString("Identity"),
            Path.Combine(builder.Environment.ContentRootPath,".local","errors","identity-errors.jsonl"),"CJORA_IDENTITY_MANAGEMENT","IdentityManagement"));
        builder.Services.AddHttpClient("core",c=>{c.BaseAddress=new Uri(config["Identity:CoreAddress"]??"http://localhost:5075/");c.Timeout=TimeSpan.FromSeconds(10);})
            .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false});
        builder.Services.AddSingleton<ICoreSessionClient>(p=>new CoreSessionClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("core"),config["Identity:CallerToken"]??""));
        builder.Services.AddSingleton(p=>new IdentityLoginService(p.GetRequiredService<IAuthenticationStore>(),p.GetRequiredService<ICoreSessionClient>(),authority,p.GetRequiredService<TimeProvider>()));
        var protection=builder.Services.AddDataProtection().SetApplicationName("CJora.IdentityManagement")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath,".local","data-protection")));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        builder.Services.AddAuthentication(o=>{o.DefaultScheme="oidc-ticket";o.DefaultChallengeScheme="oidc";})
            .AddCookie("oidc-ticket",o=>{o.Cookie.Name="__Host-cjora-unused-ticket";o.Cookie.SecurePolicy=CookieSecurePolicy.Always;o.Cookie.Path="/";})
            .AddOpenIdConnect("oidc",o=>
            {
                o.Authority=authority;o.RequireHttpsMetadata=!builder.Environment.IsDevelopment();
                o.ClientId=config["Identity:ClientId"]??"cjora-web";o.ClientSecret=config["Identity:ClientSecret"]??"not-configured";
                o.ResponseType=OpenIdConnectResponseType.Code;o.ResponseMode=OpenIdConnectResponseMode.Query;o.UsePkce=true;
                o.SaveTokens=false;o.MapInboundClaims=false;o.CallbackPath="/api/v1/idm/sessions/callback";
                o.Scope.Clear();o.Scope.Add("openid");o.Scope.Add("profile");o.Scope.Add("email");
                o.RemoteAuthenticationTimeout=TimeSpan.FromMinutes(5);o.BackchannelTimeout=TimeSpan.FromSeconds(10);
                o.CorrelationCookie.Name="__Host-cjora-correlation.";o.CorrelationCookie.SecurePolicy=CookieSecurePolicy.Always;o.CorrelationCookie.HttpOnly=true;o.CorrelationCookie.SameSite=SameSiteMode.Lax;o.CorrelationCookie.Path="/";
                o.NonceCookie.Name="__Host-cjora-nonce.";o.NonceCookie.SecurePolicy=CookieSecurePolicy.Always;o.NonceCookie.HttpOnly=true;o.NonceCookie.SameSite=SameSiteMode.Lax;o.NonceCookie.Path="/";
                o.TokenValidationParameters.ValidateIssuer=true;o.TokenValidationParameters.ValidIssuer=authority;
                o.TokenValidationParameters.ValidateAudience=true;o.TokenValidationParameters.ValidAudience=o.ClientId;
                o.TokenValidationParameters.ValidateLifetime=true;o.TokenValidationParameters.RequireSignedTokens=true;
                o.TokenValidationParameters.ClockSkew=TimeSpan.FromMinutes(1);
                o.Events=new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider=context=>
                    {
                        context.ProtocolMessage.RedirectUri=callback;
                        context.ProtocolMessage.LoginHint=context.Properties.GetString("email");
                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived=context=>{context.TokenEndpointRequest!.RedirectUri=callback;return Task.CompletedTask;},
                    OnTicketReceived=async context=>
                    {
                        var principal=context.Principal??throw new IdentityFailure(401,"IDENTITY_MISSING");
                        string Claim(string name)=>principal.FindFirst(name)?.Value??"";
                        var properties=context.Properties;
                        if (!Guid.TryParse(properties?.GetString("program"),out var program)||!Guid.TryParse(properties?.GetString("operation"),out var operation))
                            throw new IdentityFailure(401,"LOGIN_STATE_INVALID");
                        if (!long.TryParse(Claim("auth_time"),NumberStyles.None,CultureInfo.InvariantCulture,out var authTime))
                            throw new IdentityFailure(401,"AUTHENTICATION_TIME_REQUIRED");
                        var identity=new VerifiedIdentity(authority,Claim("sub"),Claim("email"),Claim("email_verified")=="true",DateTimeOffset.FromUnixTimeSeconds(authTime).UtcDateTime);
                        var session=await context.HttpContext.RequestServices.GetRequiredService<IdentityLoginService>().CompleteAsync(identity,program,operation,context.HttpContext.TraceIdentifier,context.HttpContext.RequestAborted);
                        context.Response.Cookies.Append(IdentityProtocol.CookieName,session.Secret!,new CookieOptions{Secure=true,HttpOnly=true,SameSite=SameSiteMode.Lax,Path="/",Expires=new DateTimeOffset(session.ExpiresAtUtc,TimeSpan.Zero)});
                        var returnPath=properties!.GetString("returnPath");
                        context.Response.StatusCode=303;context.Response.Headers.Location=IdentityProtocol.IsAllowedReturnPath(returnPath)?returnPath!:"/api/v1/cor/customers/me";
                        context.HandleResponse();
                    },
                    OnRemoteFailure=async context=>
                    {
                        var reference=Guid.NewGuid().ToString("D");
                        await context.HttpContext.RequestServices.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new("OIDC_LOGIN_REJECTED","SECURITY",reference,null,"Login could not be completed. Failure type: "+context.Failure?.GetType().Name+"; cause type: "+context.Failure?.InnerException?.GetType().Name+"; reason: "+((context.Failure as IdentityFailure)?.Code??(context.Failure?.InnerException as IdentityFailure)?.Code??"OIDC_VALIDATION")));
                        try {await context.HttpContext.RequestServices.GetRequiredService<IAuthenticationStore>().AuditSecurityAsync("OIDC_LOGIN_REJECTED",reference);} catch { }
                        context.Response.StatusCode=303;context.Response.Headers.Location="/app/login-failed";context.HandleResponse();
                    }
                };
            });
        // Auth request URLs contain codes/state; suppress framework request and backchannel URL logs.
        builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Critical);
        builder.Logging.AddFilter("System.Net.Http.HttpClient",LogLevel.Critical);
        builder.Services.AddHostedService<IdentityRecoveryWorker>();
    }
    public static void UseIdentityLogin(this WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            if (!context.Request.Path.StartsWithSegments("/internal")&&!context.Request.Path.StartsWithSegments("/api/v1/idm/sessions/callback")) {await next();return;}
            context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="no-referrer";
            try
            {
                if (!app.Environment.IsDevelopment()||!app.Configuration.GetValue<bool>("Identity:Enabled")) throw new IdentityFailure(503,"IDENTITY_NOT_CONFIGURED");
                if (!LocalIdentityCaller.IsAuthorized(context,app.Configuration["Identity:CallerToken"],true)) throw new IdentityFailure(403,"WORKLOAD_ACCESS_DENIED");
                // This endpoint is reachable only through the authenticated local Core hop.
                context.Request.Scheme="https";
                await next();
            }
            catch(Exception error) when (!context.Response.HasStarted)
            {
                var failure=error as IdentityFailure;var code=failure?.Code??(error is BadHttpRequestException?"INVALID_REQUEST":"IDENTITY_UNAVAILABLE");
                var reference=Guid.NewGuid().ToString("D");
                await context.RequestServices.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new(code,"IDENTITY",reference,null,"Identity operation rejected. Failure type: "+error.GetType().Name+"; cause type: "+error.InnerException?.GetType().Name+"; database code: "+(error as MySqlConnector.MySqlException)?.Number));
                if (failure?.Status is 401 or 403) try {await context.RequestServices.GetRequiredService<IAuthenticationStore>().AuditSecurityAsync(code,reference);} catch { }
                context.Response.StatusCode=failure?.Status??(error is BadHttpRequestException bad?bad.StatusCode:503);await context.Response.WriteAsJsonAsync(new {code,reference});
            }
        });
        app.UseAuthentication();
        app.MapPost("/internal/authentication/challenge",(BeginLogin request)=>
        {
            if (request.ProgramUuid==Guid.Empty||request.OperationUuid==Guid.Empty||!IdentityProtocol.IsAllowedReturnPath(request.ReturnPath)||request.Email.Length>254)
                throw new IdentityFailure(400,"INVALID_LOGIN_REQUEST");
            var properties=new AuthenticationProperties();
            properties.Items["program"]=request.ProgramUuid.ToString("D");properties.Items["operation"]=request.OperationUuid.ToString("D");
            properties.Items["email"]=request.Email;properties.Items["returnPath"]=request.ReturnPath;
            return Results.Challenge(properties,["oidc"]);
        });
        app.MapPost("/internal/sessions/validate",async(ValidateSession request,IAuthenticationStore store)=>await store.ValidateAsync(request));
        app.MapPost("/internal/sessions/revoke",async(RevokeSession request,HttpContext context,IAuthenticationStore store)=>{await store.RevokeAsync(request,context.TraceIdentifier);return Results.NoContent();});
    }
}
public sealed class IdentityRecoveryWorker(IServiceProvider services,IConfiguration config,IWebHostEnvironment environment):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()||!config.GetValue<bool>("Identity:Enabled")) return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var store=services.GetRequiredService<IAuthenticationStore>();var core=services.GetRequiredService<ICoreSessionClient>();
                foreach(var work in await store.RecoveryAsync())
                {
                    bool success;
                    try
                    {
                        if(work.Action=="ACTIVATE") await store.RevokeAsync(new(work.AuthSessionUuid,work.CoreSessionUuid,Guid.NewGuid()),Guid.NewGuid().ToString("D"));
                        else await core.RevokeAsync(new(work.AuthSessionUuid,work.CoreSessionUuid,work.OperationUuid),stoppingToken);
                        success=true;
                    }
                    catch {success=false;}
                    await store.FinishRecoveryAsync(work.WorkflowUuid,success);
                }
            }
            catch when (!stoppingToken.IsCancellationRequested)
            {
                await services.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new("SESSION_RECOVERY_UNAVAILABLE","IDENTITY",Guid.NewGuid().ToString("D"),null,"Session recovery could not run."));
            }
        }
    }
}
