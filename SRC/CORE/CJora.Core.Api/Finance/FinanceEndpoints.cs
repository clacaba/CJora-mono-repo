using CJora.Core.Infrastructure.Finance;
using System.Net;
using System.Net.Http.Json;
using CJora.Core.Api.Identity;
using CJora.Core.Application.Identity;
using CJora.Finance.Contracts;
using CJora.Identity.Contracts;
using CJora.Routing;
using Microsoft.AspNetCore.Antiforgery;

namespace CJora.Core.Api.Finance;

public static class FinanceEndpoints
{
    public static void AddCoreFinance(this WebApplicationBuilder builder)
    {
        if(builder.Configuration.GetValue<bool>("Finance:Enabled")&&!builder.Environment.IsDevelopment())throw new InvalidOperationException("Fictional finance requires Development.");
        if(builder.Configuration.GetValue<bool>("Finance:Enabled")&&!GatewayRouteContext.IsConfiguredToken(builder.Configuration["Finance:CallerToken"]))
            throw new InvalidOperationException("A separate configured finance workload credential is required.");
        builder.Services.AddSingleton<CoreFinancialAuthority>();
        builder.Services.AddHttpClient("transactions-finance",c=>{c.BaseAddress=new Uri("http://localhost:5079/");c.Timeout=TimeSpan.FromSeconds(15);})
            .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false});
    }
    public static void UseCoreFinance(this WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            bool internalCall=context.Request.Path.StartsWithSegments("/internal/finance");
            bool publicCall=context.Request.Path.StartsWithSegments("/api/v1/txn");
            if(!internalCall&&!publicCall){await next();return;}
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["Referrer-Policy"]="no-referrer";
            try
            {
                if(!app.Environment.IsDevelopment()||!app.Configuration.GetValue<bool>("Finance:Enabled"))throw new FinanceFailure(503,"FINANCE_NOT_CONFIGURED");
                if(internalCall)
                {
                    if(!LocalIdentityCaller.IsAuthorized(context,app.Configuration["Finance:CallerToken"],true,FinanceProtocol.WorkloadHeader))
                        throw new FinanceFailure(403,"WORKLOAD_ACCESS_DENIED");
                }
                else
                {
                    if(!LocalIdentityCaller.IsAuthorized(context,app.Configuration["GatewayBridge:Token"],true,GatewayRouteContext.TokenHeader))
                        throw new FinanceFailure(403,"GATEWAY_REQUIRED");
                    context.Request.Scheme="https";
                }
                await next();
            }
            catch(Exception e)when(!context.Response.HasStarted)
            {
                var status=e is FinanceFailure failure?failure.Status:e is IdentityFailure identity?identity.Status:e is BadHttpRequestException bad?bad.StatusCode:503;
                var code=e is FinanceFailure f?f.Code:e is IdentityFailure i?i.Code:e is BadHttpRequestException?"INVALID_REQUEST":"FINANCIAL_AUTHORITY_UNAVAILABLE";
                var reference=Guid.NewGuid().ToString("D");
                await context.RequestServices.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new(code,"FINANCE",reference,null,"Financial gateway operation failed; request contents excluded."));
                if(status is 401 or 403)
                    try{await context.RequestServices.GetRequiredService<ICoreIdentityStore>().AuditSecurityAsync(code,Guid.NewGuid(),reference);}catch{}
                context.Response.StatusCode=status;
                await context.Response.WriteAsJsonAsync(new{type="about:blank",title="Financial operation unavailable.",status,code,reference},options:(System.Text.Json.JsonSerializerOptions?)null,contentType:"application/problem+json",cancellationToken:context.RequestAborted);
            }
        });
        app.MapPost("/internal/finance/authority",async(FinanceAuthorityRequest request,CoreFinancialAuthority authority,HttpContext context)=>await authority.AuthorizeAsync(request,context.RequestAborted));
        app.MapMethods("/api/v1/txn/{**remainder}",["GET","POST"],async(HttpContext context,CoreSessionService sessions,CoreFinancialAuthority authority,IHttpClientFactory factory,IAntiforgery csrf)=>
        {
            var target="/internal/finance"+context.Request.Path.Value!["/api/v1/txn".Length..].ToLowerInvariant();
            if(!FinanceProtocol.TryRoute(context.Request.Method,target,out _))throw new FinanceFailure(404,"RESOURCE_NOT_AVAILABLE");
            string key="";
            if(context.Request.Method=="POST")
            {
                await IdentityEndpoints.ValidateBrowser(context,csrf,app.Configuration);
                var values=context.Request.Headers["Idempotency-Key"];
                if(values.Count!=1||!Guid.TryParseExact(values[0],"D",out var parsed)||parsed==Guid.Empty||parsed.ToString("D")!=values[0])
                    throw new FinanceFailure(400,"INVALID_IDEMPOTENCY_KEY");
                key=values.ToString();
                if(context.Request.ContentType?.Split(';')[0].Trim()!="application/json")throw new FinanceFailure(415,"UNSUPPORTED_MEDIA_TYPE");
                if(context.Request.ContentLength>8192)throw new FinanceFailure(413,"REQUEST_TOO_LARGE");
            }
            Guid? Expected(string name)
            {
                var values=context.Request.Headers[name];if(values.Count==0)return null;
                if(values.Count!=1||!Guid.TryParseExact(values[0],"D",out var uuid))throw new FinanceFailure(403,"PROGRAM_ACCESS_DENIED");
                return uuid;
            }
            var session=await sessions.AuthorizeAsync(context.Request.Cookies[IdentityProtocol.CookieName],Expected("X-Program-UUID"),Expected("X-Customer-UUID"),context.RequestAborted);
            using var body=new MemoryStream();
            var buffer=new byte[1024];int count;
            while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0)
            {
                if(body.Length+count>8192)throw new FinanceFailure(413,"REQUEST_TOO_LARGE");
                await body.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
            }
            if(context.Request.Method=="GET"&&body.Length!=0)throw new FinanceFailure(400,"INVALID_REQUEST");
            var bytes=body.ToArray();
            var uri=new Uri(new Uri("http://localhost:5079"),target+context.Request.QueryString);
            var call=new FinanceInvocation(Guid.NewGuid(),session.SessionUuid,context.Request.Method,uri.PathAndQuery,FinanceProtocol.Hash(bytes),key,DateTime.UtcNow);
            var token=FinanceProtocol.Sign(call,app.Configuration["Finance:CallerToken"]??"");
            // Permission failure is handled here before forwarding; Transactions revalidates immediately before execution.
            await authority.AuthorizeAsync(new(token,null,null,null),context.RequestAborted);
            using var message=new HttpRequestMessage(new HttpMethod(context.Request.Method),uri);
            message.Headers.Add(FinanceProtocol.WorkloadHeader,app.Configuration["Finance:CallerToken"]);
            message.Headers.Add(FinanceProtocol.InvocationHeader,token);
            if(key!="")message.Headers.Add("Idempotency-Key",key);
            if(context.Request.Method=="POST"){message.Content=new ByteArrayContent(bytes);message.Content.Headers.ContentType=new("application/json");}
            using var response=await factory.CreateClient("transactions-finance").SendAsync(message,context.RequestAborted);
            if((int)response.StatusCode is >=300 and <400)throw new FinanceFailure(503,"INVALID_FINANCE_REDIRECT");
            context.Response.StatusCode=(int)response.StatusCode;
            context.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            await response.Content.CopyToAsync(context.Response.Body,context.RequestAborted);
        });
    }
}
