using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CJora.Finance.Contracts;

public sealed class FinanceFailure(int status,string code):Exception("The financial operation could not be completed.")
{public int Status{get;}=status;public string Code{get;}=code;}
public sealed record FinanceInvocation(Guid RequestUuid,Guid CoreSessionUuid,string Method,string PathAndQuery,string BodyHash,string IdempotencyKey,DateTime IssuedAtUtc);
public sealed record FinanceAuthorityRequest(string Invocation,Guid? IntentUuid,string? TermsHash,Guid? RecipientCustomerUuid);
public sealed record FinanceAuthority(Guid RequestUuid,Guid ProgramUuid,Guid CustomerUuid,Guid UserUuid,Guid CoreSessionUuid,Guid AuthSessionUuid,
    string Permission,DateTime ValidatedAtUtc,DateTime ExpiresAtUtc,Guid? IntentUuid,string? TermsHash);
public static class FinanceProtocol
{
    public const string WorkloadHeader="X-CJora-Core-Transactions";
    public const string InvocationHeader="X-CJora-Finance-Invocation";
    public const string AccountsRead="ACCOUNTS.READ";
    public const string TransfersCreate="TRANSFERS.CREATE";
    public const string TransactionsRead="TRANSACTIONS.READ";
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static string Encode(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] Decode(string value)=>Convert.FromBase64String(value.Replace('-','+').Replace('_','/').PadRight((value.Length+3)/4*4,'='));
    public static string Sign(FinanceInvocation call,string key)
    {
        var payload=JsonSerializer.SerializeToUtf8Bytes(call);
        return Encode(payload)+"."+Encode(HMACSHA256.HashData(Convert.FromHexString(key),payload));
    }
    public static FinanceInvocation Verify(string? token,string key,DateTime now)
    {
        try
        {
            if(token is null||token.Length>4096||key.Length!=64)throw new FormatException();
            var parts=token.Split('.');if(parts.Length!=2)throw new FormatException();
            var bytes=Decode(parts[0]);var signature=Decode(parts[1]);
            if(!CryptographicOperations.FixedTimeEquals(signature,HMACSHA256.HashData(Convert.FromHexString(key),bytes)))throw new FormatException();
            var call=JsonSerializer.Deserialize<FinanceInvocation>(bytes)??throw new FormatException();
            if(call.RequestUuid==Guid.Empty||call.CoreSessionUuid==Guid.Empty||call.IssuedAtUtc.Kind!=DateTimeKind.Utc||
                call.IssuedAtUtc>now.AddSeconds(1)||now-call.IssuedAtUtc>=TimeSpan.FromSeconds(30)||call.BodyHash.Length!=64||
                !TryRoute(call.Method,call.PathAndQuery.Split('?')[0],out _))throw new FormatException();
            return call;
        }
        catch {throw new FinanceFailure(403,"FINANCE_INVOCATION_REJECTED");}
    }
    public static bool TryRoute(string method,string path,out string permission)
    {
        permission="";
        const string uuid="[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
        if(method=="GET"&&path=="/internal/finance/accounts"){permission=AccountsRead;return true;}
        if((method=="POST"&&path=="/internal/finance/transfer-intents")||
           (method=="GET"&&Regex.IsMatch(path,"^/internal/finance/transfer-intents/"+uuid+"$"))||
           (method=="POST"&&Regex.IsMatch(path,"^/internal/finance/transfer-intents/"+uuid+"/confirm$")))
        {permission=TransfersCreate;return true;}
        if(method=="GET"&&Regex.IsMatch(path,"^/internal/finance/accounts/"+uuid+"/transactions(/"+uuid+")?$"))
        {permission=TransactionsRead;return true;}
        return false;
    }
}
