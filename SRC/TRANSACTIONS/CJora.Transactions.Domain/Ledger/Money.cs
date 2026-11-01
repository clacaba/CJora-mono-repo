using System.Collections.Frozen;
using System.Globalization;

namespace CJora.Transactions.Domain.Ledger;

public sealed class LedgerRuleViolation(string code):Exception("The financial operation violates a ledger rule.")
{
    public string Code { get; } = code;
}

/// <summary>Small verified currency catalog, not a Program's enablement policy.</summary>
public sealed record Currency
{
    public string Code { get; }
    public int MinorDigits { get; }
    private Currency(string code,int minorDigits){Code=code;MinorDigits=minorDigits;}
    private static readonly FrozenDictionary<string,Currency> Catalog=new[]
    {
        new Currency("PHP",2),new Currency("SGD",2),new Currency("USD",2),new Currency("JPY",0),new Currency("KWD",3)
    }.ToFrozenDictionary(x=>x.Code,StringComparer.Ordinal);
    public static Currency Resolve(string? code)=>
        code is not null&&Catalog.TryGetValue(code,out var currency)?currency:throw new LedgerRuleViolation("UNSUPPORTED_CURRENCY");
}

/// <summary>Nonnegative amount. Public wire format is a canonical decimal string of minor units.</summary>
public sealed record Money
{
    public Currency Currency { get; }
    public long MinorUnits { get; }
    public string MinorUnitsText=>MinorUnits.ToString(CultureInfo.InvariantCulture);
    private Money(Currency currency,long minorUnits){Currency=currency;MinorUnits=minorUnits;}
    public static Money FromMinorUnits(string currency,string? minorUnits)
    {
        var definition=Currency.Resolve(currency);
        if(string.IsNullOrEmpty(minorUnits)||minorUnits.Length>19||
           (minorUnits.Length>1&&minorUnits[0]=='0')||minorUnits.Any(c=>c<'0'||c>'9')||
           !long.TryParse(minorUnits,NumberStyles.None,CultureInfo.InvariantCulture,out var value))
            throw new LedgerRuleViolation("INVALID_MINOR_UNITS");
        return new Money(definition,value);
    }
}
