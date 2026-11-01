using System.Collections.Frozen;

namespace CJora.Transactions.Domain.Ledger;

public enum CustomerAccountStatus { Active=1,Restricted=2,Closed=3 }
/// <summary>Locked database state supplied by a future posting repository, never bound from browser JSON.</summary>
public sealed record CustomerAccountState(Guid AccountUuid,Guid ProgramUuid,Guid CustomerUuid,Currency Currency,
    CustomerAccountStatus Status,long PostedMinor,long Version);

public sealed class TransferRules
{
    public Guid ProgramUuid { get; }
    private readonly FrozenDictionary<string,long> _limits;
    public TransferRules(Guid program,IReadOnlyDictionary<string,long> perTransferLimits)
    {
        if(program==Guid.Empty||perTransferLimits is null)throw new LedgerRuleViolation("INVALID_TRANSFER_POLICY");
        foreach(var pair in perTransferLimits)
        {
            Currency.Resolve(pair.Key);
            if(pair.Value<=0)throw new LedgerRuleViolation("INVALID_TRANSFER_LIMIT");
        }
        ProgramUuid=program;_limits=perTransferLimits.ToFrozenDictionary(StringComparer.Ordinal);
    }
    public long LimitFor(Currency currency)=>_limits.TryGetValue(currency.Code,out var limit)?limit:
        throw new LedgerRuleViolation("CURRENCY_NOT_ENABLED");
}
public sealed record TransferPlan(BalancedJournal Journal,long SourceAfterMinor,long DestinationAfterMinor);

public static class InternalTransferPlanner
{
    public static TransferPlan Plan(Guid operation,Guid program,Guid customer,CustomerAccountState source,CustomerAccountState destination,Money amount,TransferRules rules)
    {
        if(operation==Guid.Empty||program==Guid.Empty||customer==Guid.Empty||source is null||destination is null||amount is null||rules is null)
            throw new LedgerRuleViolation("INVALID_TRANSFER");
        if(rules.ProgramUuid!=program)throw new LedgerRuleViolation("TRANSFER_POLICY_SCOPE_MISMATCH");
        if(source.AccountUuid==Guid.Empty||destination.AccountUuid==Guid.Empty||source.CustomerUuid==Guid.Empty||destination.CustomerUuid==Guid.Empty||
            source.Currency is null||destination.Currency is null||source.Version<=0||destination.Version<=0||source.PostedMinor<0||destination.PostedMinor<0)
            throw new LedgerRuleViolation("INVALID_ACCOUNT_STATE");
        if(source.ProgramUuid!=program||source.CustomerUuid!=customer)throw new LedgerRuleViolation("SOURCE_ACCOUNT_NOT_AVAILABLE");
        if(destination.ProgramUuid!=program)throw new LedgerRuleViolation("DESTINATION_NOT_AVAILABLE");
        if(source.AccountUuid==destination.AccountUuid)throw new LedgerRuleViolation("SAME_ACCOUNT_TRANSFER");
        if(source.Status!=CustomerAccountStatus.Active||destination.Status!=CustomerAccountStatus.Active)
            throw new LedgerRuleViolation("ACCOUNT_RESTRICTED");
        if(source.Currency!=amount.Currency||destination.Currency!=amount.Currency)throw new LedgerRuleViolation("CURRENCY_MISMATCH");
        if(amount.MinorUnits<=0)throw new LedgerRuleViolation("TRANSFER_AMOUNT_MUST_BE_POSITIVE");
        if(amount.MinorUnits>rules.LimitFor(amount.Currency))throw new LedgerRuleViolation("TRANSFER_LIMIT_EXCEEDED");
        if(source.PostedMinor<amount.MinorUnits)throw new LedgerRuleViolation("INSUFFICIENT_FUNDS");
        long destinationAfter;
        try{destinationAfter=checked(destination.PostedMinor+amount.MinorUnits);}
        catch(OverflowException){throw new LedgerRuleViolation("BALANCE_LIMIT_EXCEEDED");}
        // Customer deposits are liabilities in the platform ledger: debit reduces source, credit increases destination.
        var journal=BalancedJournal.Create(operation,[
            new(source.AccountUuid,program,amount.Currency,PostingSide.Debit,amount.MinorUnits),
            new(destination.AccountUuid,program,amount.Currency,PostingSide.Credit,amount.MinorUnits)]);
        return new TransferPlan(journal,source.PostedMinor-amount.MinorUnits,destinationAfter);
    }
}
