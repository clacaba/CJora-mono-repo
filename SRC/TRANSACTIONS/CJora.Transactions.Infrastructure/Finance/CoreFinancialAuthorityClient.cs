using System.Net.Http.Json;
using CJora.Finance.Contracts;
using CJora.Transactions.Application.Finance;

namespace CJora.Transactions.Infrastructure.Finance;

public sealed class CoreFinancialAuthorityClient(HttpClient client,string secret,string invocation,FinanceInvocation call,Guid? recipient=null):IFinancialAuthorityProvider
{
    public async Task<FinancialAuthorityContext> AuthorizeAsync(Guid? intent,string? terms,CancellationToken ct)
    {
        try
        {
            using var message=new HttpRequestMessage(HttpMethod.Post,"internal/finance/authority")
            {Content=JsonContent.Create(new FinanceAuthorityRequest(invocation,intent,terms,recipient))};
            message.Headers.Add(FinanceProtocol.WorkloadHeader,secret);
            using var response=await client.SendAsync(message,ct);
            if(!response.IsSuccessStatusCode)
            {
                var status=(int)response.StatusCode;
                throw new FinanceFailure(status is 401 or 403 or 404?status:503,status==401?"SESSION_EXPIRED":status==403?"FINANCIAL_ACCESS_DENIED":status==404?"RESOURCE_NOT_AVAILABLE":"FINANCIAL_AUTHORITY_UNAVAILABLE");
            }
            var value=await response.Content.ReadFromJsonAsync<FinanceAuthority>(cancellationToken:ct)??throw new FinanceFailure(503,"FINANCIAL_AUTHORITY_UNAVAILABLE");
            FinanceProtocol.TryRoute(call.Method,call.PathAndQuery.Split('?')[0],out var permission);
            if(value.RequestUuid!=call.RequestUuid||value.CoreSessionUuid!=call.CoreSessionUuid||value.Permission!=permission||value.IntentUuid!=intent||value.TermsHash!=terms)
                throw new FinanceFailure(403,"FINANCIAL_AUTHORITY_REJECTED");
            var context=new FinancialAuthorityContext(new(value.ProgramUuid,value.CustomerUuid,value.UserUuid,value.CoreSessionUuid,value.AuthSessionUuid),
                [value.Permission],value.ValidatedAtUtc,value.ExpiresAtUtc);
            FinancialAuthorization.Require(context,permission,DateTime.UtcNow);return context;
        }
        catch(FinanceFailure){throw;}
        catch{throw new FinanceFailure(503,"FINANCIAL_AUTHORITY_UNAVAILABLE");}
    }
    public Task<FinancialAuthorityContext> ValidateForConfirmationAsync(Guid coreSessionUuid,Guid intentUuid,RequestFingerprint confirmedTerms,CancellationToken ct)
    {
        if(coreSessionUuid!=call.CoreSessionUuid)throw new FinanceFailure(403,"FINANCIAL_AUTHORITY_REJECTED");
        return AuthorizeAsync(intentUuid,confirmedTerms.Hex,ct);
    }
}
