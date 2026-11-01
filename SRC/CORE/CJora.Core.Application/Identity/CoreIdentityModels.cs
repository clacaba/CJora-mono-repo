using System.Security.Cryptography;
using System.Text;
using CJora.Identity.Contracts;

namespace CJora.Core.Application.Identity;

public sealed record ProgramInfo(Guid ProgramUuid,string Key,string DisplayName);
public sealed record CoreIdentitySession(Guid SessionUuid,Guid AuthSessionUuid,Guid UserUuid,Guid ProgramUuid,Guid CustomerUuid,
    string DisplayName,string Email,string State,bool ProgramEnabled,string CustomerStatus,DateTime LastActivityUtc,DateTime ExpiresAtUtc);
public sealed record PendingRevocation(Guid Uuid,Guid AuthSessionUuid,Guid CoreSessionUuid,Guid OperationUuid);
public interface IIdentitySessionClient
{
    Task<ValidatedIdentity> ValidateAsync(ValidateSession request,CancellationToken cancellationToken);
    Task RevokeAsync(RevokeSession request,CancellationToken cancellationToken);
}
public interface ICoreIdentityStore
{
    Task<ProgramInfo?> ResolveProgramAsync(string key);
    Task<CoreSessionReference> PrepareAsync(PrepareSession request,string correlation);
    Task<CoreIdentitySession?> ReadAsync(byte[] secretHash);
    Task<CoreIdentitySession?> ReadByUuidAsync(Guid uuid);
    Task<SessionCookie> ActivateAsync(ActivateSession request,string correlation);
    Task RevokeAsync(Guid authSessionUuid,Guid operation,string correlation,bool queueIdentity);
    Task TouchAsync(Guid sessionUuid);
    Task<IReadOnlyList<PendingRevocation>> PendingRevocationsAsync();
    Task FinishRevocationAsync(Guid uuid,bool success);
    Task AuditSecurityAsync(string operation,Guid operationUuid,string correlation);
}
public static class SessionSecrets
{
    public static bool IsValid(string? secret) => secret is {Length:64} && secret.All(Uri.IsHexDigit);
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}
public sealed class CoreSessionService(ICoreIdentityStore store,IIdentitySessionClient identity,TimeProvider clock)
{
    public async Task<CoreIdentitySession> AuthorizeAsync(string? cookie,Guid? expectedProgram,Guid? expectedCustomer,CancellationToken cancellationToken)
    {
        if (!SessionSecrets.IsValid(cookie)) throw new IdentityFailure(401,"SESSION_REQUIRED");
        var session=await store.ReadAsync(SessionSecrets.Hash(cookie!)) ?? throw new IdentityFailure(401,"SESSION_INVALID");
        if (session.State!="ACTIVE" || IdentityProtocol.IsExpired(clock.GetUtcNow().UtcDateTime,session.LastActivityUtc,session.ExpiresAtUtc))
            throw new IdentityFailure(401,"SESSION_EXPIRED");
        if (!session.ProgramEnabled || session.CustomerStatus!="ACTIVE" ||
            (expectedProgram.HasValue && session.ProgramUuid!=expectedProgram) || (expectedCustomer.HasValue && session.CustomerUuid!=expectedCustomer))
            throw new IdentityFailure(403,"PROGRAM_ACCESS_DENIED");
        var auth=await identity.ValidateAsync(new ValidateSession(session.AuthSessionUuid,session.SessionUuid,session.ProgramUuid,session.UserUuid),cancellationToken);
        if (auth.UserUuid!=session.UserUuid || auth.ProgramUuid!=session.ProgramUuid || auth.CoreSessionUuid!=session.SessionUuid ||
            auth.AuthSessionUuid!=session.AuthSessionUuid || session.ExpiresAtUtc>auth.ExpiresAtUtc)
            throw new IdentityFailure(403,"SESSION_LINK_INVALID");
        await store.TouchAsync(session.SessionUuid);
        return session;
    }
    public async Task<SessionCookie> ActivateAsync(ActivateSession request,string correlation,CancellationToken cancellationToken)
    {
        var session=await store.ReadByUuidAsync(request.CoreSessionUuid) ?? throw new IdentityFailure(403,"SESSION_LINK_INVALID");
        if (session.AuthSessionUuid!=request.AuthSessionUuid || !session.ProgramEnabled || session.CustomerStatus!="ACTIVE")
            throw new IdentityFailure(403,"SESSION_LINK_INVALID");
        var auth=await identity.ValidateAsync(new ValidateSession(request.AuthSessionUuid,session.SessionUuid,session.ProgramUuid,session.UserUuid),cancellationToken);
        if (auth.UserUuid!=session.UserUuid || auth.ProgramUuid!=session.ProgramUuid || auth.CoreSessionUuid!=session.SessionUuid ||
            session.ExpiresAtUtc>auth.ExpiresAtUtc) throw new IdentityFailure(403,"SESSION_LINK_INVALID");
        return await store.ActivateAsync(request,correlation);
    }
    public async Task<bool> LogoutAsync(string? cookie,string correlation,CancellationToken cancellationToken)
    {
        if (!SessionSecrets.IsValid(cookie)) return true;
        var session=await store.ReadAsync(SessionSecrets.Hash(cookie!));
        if (session is null || session.State=="REVOKED") return true;
        var operation=Guid.NewGuid();
        try { await identity.RevokeAsync(new RevokeSession(session.AuthSessionUuid,session.SessionUuid,operation),cancellationToken); }
        catch
        {
            await store.RevokeAsync(session.AuthSessionUuid,operation,correlation,true);
            return false;
        }
        await store.RevokeAsync(session.AuthSessionUuid,operation,correlation,false);
        return true;
    }
}