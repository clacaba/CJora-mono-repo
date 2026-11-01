using System.Collections.ObjectModel;
using System.Numerics;

namespace CJora.Transactions.Domain.Ledger;

public enum PostingSide { Debit=1,Credit=2 }
public sealed record LedgerPosting(Guid AccountUuid,Guid ProgramUuid,Currency Currency,PostingSide Side,long MinorUnits);

/// <summary>Validated immutable proposal, not a posted or persisted journal.</summary>
public sealed class BalancedJournal
{
    public Guid OperationUuid { get; }
    public Guid ProgramUuid { get; }
    public Currency Currency { get; }
    public long TotalDebitMinor { get; }
    public long TotalCreditMinor=>TotalDebitMinor;
    public ReadOnlyCollection<LedgerPosting> Postings { get; }

    private BalancedJournal(Guid operation,Guid program,Currency currency,long total,LedgerPosting[] postings)
    {OperationUuid=operation;ProgramUuid=program;Currency=currency;TotalDebitMinor=total;Postings=Array.AsReadOnly(postings);}

    public static BalancedJournal Create(Guid operation,IEnumerable<LedgerPosting>? postings)
    {
        if(operation==Guid.Empty||postings is null)throw new LedgerRuleViolation("INVALID_JOURNAL");
        // Bound enumeration as well as the final count.
        var entries=postings.Take(1001).ToArray();
        if(entries.Length<2||entries.Length>1000)throw new LedgerRuleViolation("INVALID_POSTING_COUNT");
        if(entries.Any(e=>e is null||e.AccountUuid==Guid.Empty||e.ProgramUuid==Guid.Empty||e.Currency is null||
            e.MinorUnits<=0||e.Side is not (PostingSide.Debit or PostingSide.Credit)))
            throw new LedgerRuleViolation("INVALID_POSTING");
        var first=entries[0];
        if(entries.Any(e=>e.ProgramUuid!=first.ProgramUuid))throw new LedgerRuleViolation("CROSS_PROGRAM_JOURNAL");
        if(entries.Any(e=>e.Currency!=first.Currency))throw new LedgerRuleViolation("MIXED_CURRENCY_JOURNAL");
        BigInteger debit=0,credit=0;
        foreach(var e in entries){if(e.Side==PostingSide.Debit)debit+=e.MinorUnits;else credit+=e.MinorUnits;}
        if(debit!=credit)throw new LedgerRuleViolation("UNBALANCED_JOURNAL");
        if(debit>long.MaxValue)throw new LedgerRuleViolation("JOURNAL_TOTAL_OVERFLOW");
        return new BalancedJournal(operation,first.ProgramUuid,first.Currency,(long)debit,entries);
    }
}
