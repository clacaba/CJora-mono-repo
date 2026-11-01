using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using CJora.Core.Application.Identity;
using CJora.Finance.Contracts;
using CJora.Identity.Contracts;
using CJora.Routing.Persistence;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.Core.Infrastructure.Finance;

public sealed class CoreFinancialAuthority(ICoreIdentityStore store,IHttpClientFactory clients,IConfiguration configuration,TimeProvider clock)
{
    public async Task<FinanceAuthority> AuthorizeAsync(FinanceAuthorityRequest request,CancellationToken ct)
    {
        var call=FinanceProtocol.Verify(request.Invocation,configuration["Finance:CallerToken"]??"",clock.GetUtcNow().UtcDateTime);
        FinanceProtocol.TryRoute(call.Method,call.PathAndQuery.Split('?')[0],out var permission);
        if(request.IntentUuid.HasValue && !call.PathAndQuery.Split('?')[0].Contains("/"+request.IntentUuid.Value.ToString("D"),StringComparison.Ordinal))
            throw new FinanceFailure(403,"FINANCE_INVOCATION_REJECTED");
        var session=await store.ReadByUuidAsync(call.CoreSessionUuid)??throw new FinanceFailure(401,"SESSION_INVALID");
        var now=clock.GetUtcNow().UtcDateTime;
        if(session.State!="ACTIVE"||IdentityProtocol.IsExpired(now,session.LastActivityUtc,session.ExpiresAtUtc))
            throw new FinanceFailure(401,"SESSION_EXPIRED");
        if(!session.ProgramEnabled||session.CustomerStatus!="ACTIVE")throw new FinanceFailure(403,"PROGRAM_ACCESS_DENIED");
        using var message=new HttpRequestMessage(HttpMethod.Post,"internal/finance/authorize")
        {Content=JsonContent.Create(new AuthorizeSessionPermission(new ValidateSession(session.AuthSessionUuid,session.SessionUuid,session.ProgramUuid,session.UserUuid),permission))};
        message.Headers.Add(IdentityProtocol.CallerHeader,configuration["Identity:CallerToken"]);
        using var response=await clients.CreateClient("identity").SendAsync(message,ct);
        if(!response.IsSuccessStatusCode)throw new FinanceFailure((int)response.StatusCode is 401 or 403?(int)response.StatusCode:503,
            response.StatusCode==System.Net.HttpStatusCode.Forbidden?"FINANCIAL_PERMISSION_DENIED":"FINANCIAL_AUTHORITY_UNAVAILABLE");
        var auth=await response.Content.ReadFromJsonAsync<ValidatedIdentity>(cancellationToken:ct)??throw new FinanceFailure(503,"FINANCIAL_AUTHORITY_UNAVAILABLE");
        if(auth.UserUuid!=session.UserUuid||auth.ProgramUuid!=session.ProgramUuid||auth.CoreSessionUuid!=session.SessionUuid||auth.AuthSessionUuid!=session.AuthSessionUuid||session.ExpiresAtUtc>auth.ExpiresAtUtc)
            throw new FinanceFailure(403,"SESSION_LINK_INVALID");
        if(request.RecipientCustomerUuid is {} recipient)
        {
            var connectionString=configuration.GetConnectionString("CoreIdentity")??"";
            if(new MySqlConnectionStringBuilder(connectionString).Database!="CJORA_CORE")throw new FinanceFailure(503,"FINANCIAL_AUTHORITY_UNAVAILABLE");
            await using var connection=new MySqlConnection(connectionString);await connection.OpenAsync(ct);
            await using var query=Command(connection,null,"""
                SELECT COUNT(*) FROM CUSTOMER C JOIN PROGRAM P ON P.ID=C.PROGRAM_ID
                WHERE C.UUID=@customer AND P.UUID=@program AND C.STATUS='ACTIVE' AND P.IS_ENABLED=1
                """,("@customer",recipient.ToString("D")),("@program",session.ProgramUuid.ToString("D")));
            if(Convert.ToInt64(await query.ExecuteScalarAsync(ct))!=1)throw new FinanceFailure(404,"RESOURCE_NOT_AVAILABLE");
        }
        await store.TouchAsync(session.SessionUuid);
        return new(call.RequestUuid,session.ProgramUuid,session.CustomerUuid,session.UserUuid,session.SessionUuid,session.AuthSessionUuid,
            permission,clock.GetUtcNow().UtcDateTime,session.ExpiresAtUtc,request.IntentUuid,request.TermsHash);
    }
}
