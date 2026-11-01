using CJora.Transactions.Domain.Ledger;

namespace CJora.Transactions.Application.Finance;

public enum TransferIntentState { Prepared=1,Posted=2,Rejected=3,Expired=4 }
public static class TransferIntentLifecycle
{
    public static bool CanTransition(TransferIntentState from,TransferIntentState to)=>
        from==TransferIntentState.Prepared&&to is TransferIntentState.Posted or TransferIntentState.Rejected or TransferIntentState.Expired;
}
/// <summary>Immutable server-side review proposal. Storage and transaction execution are future work.</summary>
public sealed class TransferReview
{
    public Guid IntentUuid { get; }
    public FinancialSessionScope Owner { get; }
    public RequestFingerprint Terms { get; }
    public int ReviewVersion=>1;
    public DateTime ExpiresAtUtc { get; }
    private TransferReview(Guid intent,FinancialSessionScope owner,RequestFingerprint terms,DateTime expires)
    {IntentUuid=intent;Owner=owner;Terms=terms;ExpiresAtUtc=expires;}
    public static TransferReview Create(Guid intent,FinancialSessionScope owner,RequestFingerprint terms,DateTime nowUtc)
    {
        if(intent==Guid.Empty||owner is null||!owner.HasIdentifiers||terms is null||terms.ProgramUuid!=owner.ProgramUuid||terms.CustomerUuid!=owner.CustomerUuid||terms.Operation!=FinancialOperation.PrepareTransfer||nowUtc.Kind!=DateTimeKind.Utc||nowUtc>DateTime.MaxValue.AddMinutes(-5))
            throw new LedgerRuleViolation("INVALID_TRANSFER_REVIEW");
        return new(intent,owner,terms,nowUtc.AddMinutes(5));
    }
    public void RequireForExecution(FinancialAuthorityContext authority,int reviewVersion,DateTime nowUtc)
    {
        FinancialAuthorization.Require(authority,FinancialPermissions.TransfersCreate,nowUtc);
        if(authority.Scope!=Owner)throw new LedgerRuleViolation("REVIEW_SESSION_CHANGED");
        if(reviewVersion!=ReviewVersion)throw new LedgerRuleViolation("INVALID_REVIEW_VERSION");
        if(nowUtc>=ExpiresAtUtc)throw new LedgerRuleViolation("TRANSFER_REVIEW_EXPIRED");
    }
}
