using CJora.Finance.Contracts;
using CJora.Transactions.Application.Finance;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

namespace CJora.Transactions.Api.Finance;

public sealed record FinancialCursor(Guid Program,Guid Customer,Guid? Account,string Kind,long Upper,long Before,int Limit,DateTime ExpiresUtc);
public sealed class FinancialCursors(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector=provider.CreateProtector("CJora.Transactions.Finance.Cursor.v1");
    public string Encode(FinancialCursor cursor)=>_protector.Protect(JsonSerializer.Serialize(cursor));
    public FinancialCursor? Parse(string? value,FinancialSessionScope scope,Guid? account,string kind,int? limit)
    {
        if(value is null)return null;
        try
        {
            if(value.Length>2048)throw new FormatException();
            var cursor=JsonSerializer.Deserialize<FinancialCursor>(_protector.Unprotect(value))??throw new FormatException();
            if(cursor.Program!=scope.ProgramUuid||cursor.Customer!=scope.CustomerUuid||cursor.Account!=account||cursor.Kind!=kind||
                cursor.Upper<0||cursor.Before<=0||cursor.Limit is <1 or >50||(limit.HasValue&&limit!=cursor.Limit)||
                cursor.ExpiresUtc.Kind!=DateTimeKind.Utc||cursor.ExpiresUtc<=DateTime.UtcNow)throw new FormatException();
            return cursor;
        }
        catch{throw new FinanceFailure(400,"INVALID_CURSOR");}
    }
}
