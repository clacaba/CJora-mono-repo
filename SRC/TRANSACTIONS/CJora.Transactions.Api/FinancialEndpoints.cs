using System.Collections.Concurrent;
using CJora.Finance.Contracts;
using CJora.Routing;
using CJora.Routing.Persistence;
using CJora.Transactions.Application.Finance;
using CJora.Transactions.Domain.Ledger;
using CJora.Transactions.Infrastructure.Finance;
using Microsoft.AspNetCore.DataProtection;
using static CJora.Routing.Persistence.SessionSql;
using MySqlConnector;

namespace CJora.Transactions.Api.Finance;

public sealed class InvocationReplayGuard
{
    private readonly ConcurrentDictionary<Guid,DateTime> _seen=new();
    public void Accept(FinanceInvocation call)
    {
        foreach(var pair in _seen)if(pair.Value<DateTime.UtcNow)_seen.TryRemove(pair.Key,out _);
        if(!_seen.TryAdd(call.RequestUuid,call.IssuedAtUtc.AddSeconds(30)))throw new FinanceFailure(409,"FINANCE_INVOCATION_REPLAYED");
    }
}
public static class FinancialEndpoints
{
    public static void AddTransactionsFinance(this WebApplicationBuilder builder)
    {
        if(builder.Configuration.GetValue<bool>("Finance:Enabled")&&!builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Fictional finance requires Development.");
        var connection=builder.Configuration.GetConnectionString("TransactionsFinance")??"";
        if(builder.Configuration.GetValue<bool>("Finance:Enabled")&&(!GatewayRouteContext.IsConfiguredToken(builder.Configuration["Finance:CallerToken"])||new MySqlConnectionStringBuilder(connection).Database!="CJORA_TRANSACTIONS"))throw new InvalidOperationException("Finance requires its isolated configured runtime connection and workload credential.");
        builder.Services.AddSingleton(new FinancialStore(connection));
        builder.Services.AddSingleton<InvocationReplayGuard>();
        builder.Services.AddSingleton<FinancialCursors>();
        var protection=builder.Services.AddDataProtection().SetApplicationName("CJora.Transactions.Finance")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath,".local","data-protection")));
        if(OperatingSystem.IsWindows())protection.ProtectKeysWithDpapi();
        builder.Services.AddHttpClient("finance-core",c=>{c.BaseAddress=new Uri("http://localhost:5075/");c.Timeout=TimeSpan.FromSeconds(5);})
            .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false});
        builder.Services.AddSingleton<IRoutingErrorRecorder>(_=>new RoutingErrorRecorder(connection,
            Path.Combine(builder.Environment.ContentRootPath,".local","errors","finance.jsonl"),"CJORA_TRANSACTIONS","Transactions"));
        builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Critical);
        builder.Logging.AddFilter("System.Net.Http.HttpClient",LogLevel.Critical);
        builder.Services.AddHostedService<LedgerReconciliationWorker>();
    }
    private static FinanceInvocation Invocation(HttpContext context)=>(FinanceInvocation)context.Items["FinanceInvocation"]!;
    private static CoreFinancialAuthorityClient Client(HttpContext context,Guid? recipient=null)
    {
        var config=context.RequestServices.GetRequiredService<IConfiguration>();
        return new(context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("finance-core"),config["Finance:CallerToken"]??"",
            context.Request.Headers[FinanceProtocol.InvocationHeader].ToString(),Invocation(context),recipient);
    }
    private static string Key(HttpContext context)=>Invocation(context).IdempotencyKey;
    private static int? Limit(HttpContext context)
    {
        foreach(var field in context.Request.Query.Keys)if(field is not ("limit" or "cursor"))throw new FinanceFailure(400,"INVALID_QUERY");
        var values=context.Request.Query["limit"];if(values.Count==0)return null;
        if(values.Count!=1||!int.TryParse(values[0],out var limit)||limit is <1 or >50)throw new FinanceFailure(400,"INVALID_PAGE_LIMIT");
        return limit;
    }
    private static string? Cursor(HttpContext context)
    {
        var values=context.Request.Query["cursor"];
        if(values.Count>1)throw new FinanceFailure(400,"INVALID_CURSOR");
        return values.Count==0?null:values[0];
    }
    public static void UseTransactionsFinance(this WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            if(!context.Request.Path.StartsWithSegments("/internal/finance")){await next();return;}
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["Referrer-Policy"]="no-referrer";
            try
            {
                if(!app.Environment.IsDevelopment()||!app.Configuration.GetValue<bool>("Finance:Enabled"))throw new FinanceFailure(503,"FINANCE_NOT_CONFIGURED");
                var secret=app.Configuration["Finance:CallerToken"]??"";
                if(!LocalIdentityCaller.IsAuthorized(context,secret,true,FinanceProtocol.WorkloadHeader))throw new FinanceFailure(403,"WORKLOAD_ACCESS_DENIED");
                var token=context.Request.Headers[FinanceProtocol.InvocationHeader];
                if(token.Count!=1)throw new FinanceFailure(403,"FINANCE_INVOCATION_REJECTED");
                var call=FinanceProtocol.Verify(token[0],secret,DateTime.UtcNow);
                if(call.Method!=context.Request.Method||call.PathAndQuery!=context.Request.Path+context.Request.QueryString)
                    throw new FinanceFailure(403,"FINANCE_INVOCATION_REJECTED");
                if(context.Request.ContentLength>8192)throw new FinanceFailure(413,"REQUEST_TOO_LARGE");
                context.Request.EnableBuffering(8192,8193);
                using var body=new MemoryStream();var buffer=new byte[1024];int count;
                while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0)
                {
                    if(body.Length+count>8192)throw new FinanceFailure(413,"REQUEST_TOO_LARGE");
                    await body.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
                }
                context.Request.Body.Position=0;
                if(call.BodyHash!=FinanceProtocol.Hash(body.ToArray())||call.IdempotencyKey!=context.Request.Headers["Idempotency-Key"].ToString()||
                    context.Request.Headers["Idempotency-Key"].Count>1)throw new FinanceFailure(403,"FINANCE_INVOCATION_REJECTED");
                context.RequestServices.GetRequiredService<InvocationReplayGuard>().Accept(call);
                context.Items["FinanceInvocation"]=call;
                await next();
            }
            catch(Exception e)when(!context.Response.HasStarted)
            {
                var code=e is FinanceFailure f?f.Code:e is LedgerRuleViolation rule?rule.Code:e is BadHttpRequestException?"INVALID_REQUEST":"FINANCE_UNAVAILABLE";
                var status=e is FinanceFailure failure?failure.Status:e is BadHttpRequestException bad?bad.StatusCode:
                    e is LedgerRuleViolation?code is "FINANCIAL_ACCESS_DENIED" or "FINANCIAL_AUTHORITY_EXPIRED"?403:400:503;
                var reference=Guid.NewGuid().ToString("D");
                await context.RequestServices.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(new(code,"FINANCE",reference,null,"Financial operation failed; payload and native error excluded."));
                if(status is 401 or 403)await SecurityAudit(app.Configuration,code,reference);
                context.Response.StatusCode=status;
                await context.Response.WriteAsJsonAsync(new{type="about:blank",title="Financial operation unavailable.",status,code,reference},
                    options:(System.Text.Json.JsonSerializerOptions?)null,contentType:"application/problem+json",cancellationToken:context.RequestAborted);
            }
        });
        app.MapGet("/internal/finance/accounts",async(HttpContext context,FinancialStore store,FinancialCursors cursors)=>
        {
            var auth=await Client(context).AuthorizeAsync(null,null,context.RequestAborted);
            var limit=Limit(context);var cursor=cursors.Parse(Cursor(context),auth.Scope,null,"ACCOUNTS",limit);
            var size=cursor?.Limit??limit??20;
            var page=await store.AccountsAsync(auth.Scope,cursor?.Upper,cursor?.Before??long.MaxValue,size+1,context.RequestAborted);
            var selected=page.Rows.Take(size).ToArray();
            var next=page.Rows.Count>size?cursors.Encode(new(auth.Scope.ProgramUuid,auth.Scope.CustomerUuid,null,"ACCOUNTS",page.Upper,selected[^1].Id,size,DateTime.UtcNow.AddMinutes(15))):null;
            return Results.Ok(new{items=selected.Select(r=>r.View),nextCursor=next});
        });
        app.MapPost("/internal/finance/transfer-intents",async(CreateTransferIntentRequest request,HttpContext context,FinancialStore store)=>
        {
            var auth=await Client(context).AuthorizeAsync(null,null,context.RequestAborted);
            var scope=FinancialRequestScope.Create(auth.Scope,FinancialOperation.PrepareTransfer,Key(context));
            var fingerprint=RequestFingerprint.Prepare(scope,request);
            var cached=await store.CachedAsync(scope,fingerprint,context.RequestAborted);
            if(cached is not null)return await Outcome(context,cached);
            var recipient=await store.RecipientAsync(auth.Scope,request.SourceAccountUuid,request.DestinationAccountUuid,context.RequestAborted);
            auth=await Client(context,recipient).AuthorizeAsync(null,null,context.RequestAborted);
            return await Outcome(context,await store.PrepareAsync(auth,scope,fingerprint,request,context.RequestAborted));
        });
        app.MapGet("/internal/finance/transfer-intents/{intentUuid:guid}",async(Guid intentUuid,HttpContext context,FinancialStore store)=>
        {
            if(context.Request.Query.Count!=0)throw new FinanceFailure(400,"INVALID_QUERY");
            var auth=await Client(context).AuthorizeAsync(intentUuid,null,context.RequestAborted);
            return Results.Ok((await store.IntentAsync(auth.Scope,intentUuid,context.RequestAborted)).View);
        });
        app.MapPost("/internal/finance/transfer-intents/{intentUuid:guid}/confirm",async(Guid intentUuid,ConfirmTransferIntentRequest request,HttpContext context,FinancialStore store)=>
        {
            var auth=await Client(context).AuthorizeAsync(intentUuid,null,context.RequestAborted);
            var scope=FinancialRequestScope.Create(auth.Scope,FinancialOperation.ConfirmTransfer,Key(context));
            var fingerprint=RequestFingerprint.Confirm(scope,intentUuid,request);
            var cached=await store.CachedAsync(scope,fingerprint,context.RequestAborted);
            if(cached is not null)return await Outcome(context,cached);
            var intent=await store.IntentAsync(auth.Scope,intentUuid,context.RequestAborted);
            var client=Client(context,intent.View.State=="PREPARED"?intent.RecipientCustomerUuid:null);
            auth=await client.ValidateForConfirmationAsync(Invocation(context).CoreSessionUuid,intentUuid,intent.VerifiedTerms(),context.RequestAborted);
            return await Outcome(context,await store.ConfirmAsync(auth,scope,fingerprint,intent,request.ReviewVersion,context.RequestAborted));
        });
        app.MapGet("/internal/finance/accounts/{accountUuid:guid}/transactions",async(Guid accountUuid,HttpContext context,FinancialStore store,FinancialCursors cursors)=>
        {
            var auth=await Client(context).AuthorizeAsync(null,null,context.RequestAborted);
            var limit=Limit(context);var cursor=cursors.Parse(Cursor(context),auth.Scope,accountUuid,"HISTORY",limit);
            var size=cursor?.Limit??limit??20;
            var page=await store.HistoryAsync(auth.Scope,accountUuid,cursor?.Upper,cursor?.Before??long.MaxValue,size+1,null,context.RequestAborted);
            var selected=page.Rows.Take(size).ToArray();
            var next=page.Rows.Count>size?cursors.Encode(new(auth.Scope.ProgramUuid,auth.Scope.CustomerUuid,accountUuid,"HISTORY",page.Upper,selected[^1].Sequence,size,DateTime.UtcNow.AddMinutes(15))):null;
            return Results.Ok(new{items=selected.Select(r=>r.View),nextCursor=next});
        });
        app.MapGet("/internal/finance/accounts/{accountUuid:guid}/transactions/{transferUuid:guid}",async(Guid accountUuid,Guid transferUuid,HttpContext context,FinancialStore store)=>
        {
            if(context.Request.Query.Count!=0)throw new FinanceFailure(400,"INVALID_QUERY");
            var auth=await Client(context).AuthorizeAsync(null,null,context.RequestAborted);
            var page=await store.HistoryAsync(auth.Scope,accountUuid,null,long.MaxValue,1,transferUuid,context.RequestAborted);
            return page.Rows.Count==1?Results.Ok(page.Rows[0].View):throw new FinanceFailure(404,"RESOURCE_NOT_AVAILABLE");
        });
    }
    private static async Task<IResult> Outcome(HttpContext context,FinancialResult result)
    {
        if(result.Status>=400)await context.RequestServices.GetRequiredService<IRoutingErrorRecorder>().RecordAsync(
            new(result.Body.TryGetProperty("code",out var code)?code.GetString()??"FINANCIAL_REJECTED":"FINANCIAL_REJECTED",
                "BUSINESS",Guid.NewGuid().ToString("D"),null,"Authorized financial operation rejected; business outcome persisted."));
        return Results.Json(result.Body,statusCode:result.Status,contentType:result.Status>=400?"application/problem+json":"application/json");
    }
    private static async Task SecurityAudit(IConfiguration config,string code,string reference)
    {
        try
        {
            await using var c=new MySqlConnection(config.GetConnectionString("TransactionsFinance"));await c.OpenAsync();
            await using var tx=await c.BeginTransactionAsync();
            await Audit(c,tx,Guid.NewGuid(),null,null,null,code,"SECURITY",null,"CHECK","DENIED",reference,"DENIED");
            await tx.CommitAsync();
        }catch{}
    }
}
public sealed class LedgerReconciliationWorker(FinancialStore store,IConfiguration configuration,IRoutingErrorRecorder errors):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("Finance:Enabled"))return;
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        while(await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                long after=0;
                while(!ct.IsCancellationRequested)
                {
                    var page=await store.ReconciliationBatchAsync(after,ct);if(page.Count==0)break;
                    foreach(var row in page){await store.ReconcileAsync(row.Uuid,ct);after=row.Id;}
                }
            }
            catch when(!ct.IsCancellationRequested){await errors.RecordAsync(new("LEDGER_RECONCILIATION_UNAVAILABLE","DEPENDENCY",Guid.NewGuid().ToString("D"),null,"Ledger reconciliation could not complete."));}
        }
    }
}
