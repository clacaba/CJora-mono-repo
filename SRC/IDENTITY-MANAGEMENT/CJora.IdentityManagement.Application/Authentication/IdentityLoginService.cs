using CJora.Identity.Contracts;
using CJora.IdentityManagement.Domain.Authentication;

namespace CJora.IdentityManagement.Application.Authentication;

public sealed record AuthRecord(Guid AuthSessionUuid,Guid OperationUuid,Guid UserUuid,Guid ProgramUuid,DateTime ExpiresAtUtc);
public sealed record RecoveryWork(Guid WorkflowUuid,Guid AuthSessionUuid,Guid? CoreSessionUuid,Guid OperationUuid,string Action);
public interface IAuthenticationStore
{
    Task<AuthRecord> CreateAsync(VerifiedIdentity identity,Guid program,Guid operation,string correlation);
    Task ActivateAsync(AuthRecord session,Guid coreSession,string correlation);
    Task CompleteActivationAsync(Guid authSession);
    Task<ValidatedIdentity> ValidateAsync(ValidateSession request);
    Task RevokeAsync(RevokeSession request,string correlation);
    Task<IReadOnlyList<RecoveryWork>> RecoveryAsync();
    Task FinishRecoveryAsync(Guid uuid,bool success);
    Task AuditSecurityAsync(string operation,string correlation);
}
public interface ICoreSessionClient
{
    Task<CoreSessionReference> PrepareAsync(PrepareSession request,CancellationToken cancellationToken);
    Task<SessionCookie> ActivateAsync(ActivateSession request,CancellationToken cancellationToken);
    Task RevokeAsync(RevokeSession request,CancellationToken cancellationToken);
}
public sealed class IdentityLoginService(IAuthenticationStore store,ICoreSessionClient core,string issuer,TimeProvider clock)
{
    public async Task<SessionCookie> CompleteAsync(VerifiedIdentity identity,Guid program,Guid operation,string correlation,CancellationToken cancellationToken)
    {
        if (!IdentityRules.IsAllowed(identity,issuer,clock.GetUtcNow().UtcDateTime)) throw new IdentityFailure(403,"VERIFIED_IDENTITY_REQUIRED");
        // A repeated operation is rejected before this invocation owns a session; it cannot revoke an earlier successful login.
        var auth=await store.CreateAsync(identity,program,operation,correlation);
        Guid? coreSession=null;
        try
        {
            coreSession=(await core.PrepareAsync(new PrepareSession(auth.AuthSessionUuid,operation,program,auth.UserUuid,auth.ExpiresAtUtc),cancellationToken)).CoreSessionUuid;
            await store.ActivateAsync(auth,coreSession.Value,correlation);
            var cookie=await core.ActivateAsync(new ActivateSession(auth.AuthSessionUuid,coreSession.Value,operation),cancellationToken);
            if (string.IsNullOrWhiteSpace(cookie.Secret)) throw new IdentityFailure(401,"LOGIN_ALREADY_USED");
            await store.CompleteActivationAsync(auth.AuthSessionUuid);
            return cookie;
        }
        catch
        {
            // Revoke authoritatively and leave durable REVOKE work if Core is unavailable.
            try { await store.RevokeAsync(new RevokeSession(auth.AuthSessionUuid,coreSession,Guid.NewGuid()),correlation); } catch { }
            try { await core.RevokeAsync(new RevokeSession(auth.AuthSessionUuid,coreSession,Guid.NewGuid()),CancellationToken.None); } catch { }
            throw;
        }
    }
}