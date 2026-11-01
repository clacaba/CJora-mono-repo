using System.Collections.Frozen;
using CJora.Transactions.Domain.Ledger;

namespace CJora.Transactions.Application.Finance;

public static class FinancialPermissions
{
    public const string AccountsRead="ACCOUNTS.READ";
    public const string TransfersCreate="TRANSFERS.CREATE";
    public const string TransactionsRead="TRANSACTIONS.READ";
    internal static readonly FrozenSet<string> Known=new[]{AccountsRead,TransfersCreate,TransactionsRead}.ToFrozenSet(StringComparer.Ordinal);
}
/// <summary>
/// Input from the authenticated authority adapter after live linked-session validation.
/// This object is neither proof of authentication nor a browser request DTO.
/// </summary>
public sealed class FinancialAuthorityContext
{
    public FinancialSessionScope Scope { get; }
    public FrozenSet<string> Permissions { get; }
    public DateTime ValidatedAtUtc { get; }
    public DateTime SessionExpiresAtUtc { get; }
    public FinancialAuthorityContext(FinancialSessionScope scope,IEnumerable<string> permissions,DateTime validatedAtUtc,DateTime expiresAtUtc)
    {
        Scope=scope;Permissions=(permissions??[]).ToFrozenSet(StringComparer.Ordinal);ValidatedAtUtc=validatedAtUtc;SessionExpiresAtUtc=expiresAtUtc;
    }
}
public static class FinancialAuthorization
{
    public static void Require(FinancialAuthorityContext context,string permission,DateTime nowUtc)
    {
        if(context is null||context.Scope is null||!context.Scope.HasIdentifiers||!FinancialPermissions.Known.Contains(permission)||
           !context.Permissions.Contains(permission))throw new LedgerRuleViolation("FINANCIAL_ACCESS_DENIED");
        if(nowUtc.Kind!=DateTimeKind.Utc||context.ValidatedAtUtc.Kind!=DateTimeKind.Utc||context.SessionExpiresAtUtc.Kind!=DateTimeKind.Utc||
           context.ValidatedAtUtc>nowUtc||nowUtc-context.ValidatedAtUtc>=TimeSpan.FromSeconds(30)||nowUtc>=context.SessionExpiresAtUtc)
            throw new LedgerRuleViolation("FINANCIAL_AUTHORITY_EXPIRED");
    }
}
/// <summary>Financial authority adapter contract; the Development implementation performs authenticated live Core validation.</summary>
public interface IFinancialAuthorityProvider
{
    Task<FinancialAuthorityContext> ValidateForConfirmationAsync(Guid coreSessionUuid,Guid intentUuid,RequestFingerprint confirmedTerms,CancellationToken cancellationToken);
}
