using CJora.Core.Application.Identity;
using CJora.Identity.Contracts;

namespace CJora.Core.UnitTests;

public sealed class SessionSecurityTests
{
    private static readonly DateTime Now=new(2026,11,1,0,0,0,DateTimeKind.Utc);
    private const string Secret="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>new(Now);}
    private static CoreIdentitySession Session()=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"Alex","alex@example.test","ACTIVE",true,"ACTIVE",Now,Now.AddHours(1));
    private sealed class Store:ICoreIdentityStore
    {
        public CoreIdentitySession? Session=SessionSecurityTests.Session();public bool Touched,Revoked,Queued,Activated;
        public Task<CoreIdentitySession?> ReadAsync(byte[] hash)=>Task.FromResult(Session);
        public Task<CoreIdentitySession?> ReadByUuidAsync(Guid uuid)=>Task.FromResult(Session);
        public Task TouchAsync(Guid uuid){Touched=true;return Task.CompletedTask;}
        public Task RevokeAsync(Guid auth,Guid op,string correlation,bool queue){Revoked=true;Queued=queue;return Task.CompletedTask;}
        public Task<SessionCookie> ActivateAsync(ActivateSession request,string correlation){Activated=true;return Task.FromResult(new SessionCookie(request.CoreSessionUuid,Secret,Now.AddHours(1)));}
        public Task<ProgramInfo?> ResolveProgramAsync(string key)=>throw new NotSupportedException();
        public Task<CoreSessionReference> PrepareAsync(PrepareSession request,string correlation)=>throw new NotSupportedException();
        public Task<IReadOnlyList<PendingRevocation>> PendingRevocationsAsync()=>throw new NotSupportedException();
        public Task FinishRevocationAsync(Guid uuid,bool success)=>throw new NotSupportedException();
        public Task AuditSecurityAsync(string operation,Guid uuid,string correlation)=>throw new NotSupportedException();
    }
    private sealed class Identity(Store store):IIdentitySessionClient
    {
        public bool Unavailable,Mismatch,Validated,Revoked;
        public Task<ValidatedIdentity> ValidateAsync(ValidateSession request,CancellationToken ct)
        {
            Validated=true;if(Unavailable)throw new IdentityFailure(503,"IDENTITY_UNAVAILABLE");
            return Task.FromResult(new ValidatedIdentity(Mismatch?Guid.NewGuid():request.UserUuid,request.ProgramUuid,request.AuthSessionUuid,request.CoreSessionUuid,store.Session!.ExpiresAtUtc));
        }
        public Task RevokeAsync(RevokeSession request,CancellationToken ct){if(Unavailable)throw new IdentityFailure(503,"IDENTITY_UNAVAILABLE");Revoked=true;return Task.CompletedTask;}
    }
    [Fact] public async Task ActiveLinkedSessionReturnsOnlyAuthoritativeCustomerScope()
    {
        var store=new Store();var idm=new Identity(store);var service=new CoreSessionService(store,idm,new Clock());
        Assert.Equal(store.Session,await service.AuthorizeAsync(Secret,null,null,default));Assert.True(idm.Validated);Assert.True(store.Touched);
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("short")]
    public async Task InvalidCookieCannotReachIdentity(string? cookie)
    {
        var store=new Store();var idm=new Identity(store);var error=await Assert.ThrowsAsync<IdentityFailure>(()=>new CoreSessionService(store,idm,new Clock()).AuthorizeAsync(cookie,null,null,default));
        Assert.Equal(401,error.Status);Assert.False(idm.Validated);
    }
    [Theory][InlineData("REVOKED")][InlineData("PENDING")][InlineData("EXPIRED")]
    public async Task NonActiveSessionFailsBeforeRemoteValidation(string state)
    {
        var store=new Store();store.Session=store.Session! with{State=state};var idm=new Identity(store);
        await Assert.ThrowsAsync<IdentityFailure>(()=>new CoreSessionService(store,idm,new Clock()).AuthorizeAsync(Secret,null,null,default));Assert.False(idm.Validated);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task AbsoluteOrIdleExpiryFailsClosed(bool absolute)
    {
        var store=new Store();store.Session=absolute?store.Session! with{ExpiresAtUtc=Now}:store.Session! with{LastActivityUtc=Now.AddMinutes(-15)};
        var idm=new Identity(store);await Assert.ThrowsAsync<IdentityFailure>(()=>new CoreSessionService(store,idm,new Clock()).AuthorizeAsync(Secret,null,null,default));Assert.False(idm.Validated);
    }
    [Fact] public async Task CrossProgramAccessCannotReadOrTouchCustomer()
    {
        var store=new Store();var idm=new Identity(store);var error=await Assert.ThrowsAsync<IdentityFailure>(()=>new CoreSessionService(store,idm,new Clock()).AuthorizeAsync(Secret,Guid.NewGuid(),null,default));
        Assert.Equal(403,error.Status);Assert.False(idm.Validated);Assert.False(store.Touched);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task UnavailableOrMismatchedIdentityNeverReturnsProfile(bool unavailable)
    {
        var store=new Store();var idm=new Identity(store){Unavailable=unavailable,Mismatch=!unavailable};
        await Assert.ThrowsAsync<IdentityFailure>(()=>new CoreSessionService(store,idm,new Clock()).AuthorizeAsync(Secret,null,null,default));Assert.False(store.Touched);
    }
    [Fact] public async Task FailedRemoteLogoutRevokesLocallyAndQueuesDurableRecovery()
    {
        var store=new Store();var idm=new Identity(store){Unavailable=true};
        Assert.False(await new CoreSessionService(store,idm,new Clock()).LogoutAsync(Secret,"test",default));Assert.True(store.Revoked);Assert.True(store.Queued);
    }
    [Fact] public async Task SuccessfulLogoutRevokesBothSidesWithoutOutbox()
    {
        var store=new Store();var idm=new Identity(store);
        Assert.True(await new CoreSessionService(store,idm,new Clock()).LogoutAsync(Secret,"test",default));Assert.True(idm.Revoked);Assert.True(store.Revoked);Assert.False(store.Queued);
    }
    [Fact] public async Task RepeatedLogoutIsHarmless()
    {
        var store=new Store();store.Session=store.Session! with{State="REVOKED"};var idm=new Identity(store);
        Assert.True(await new CoreSessionService(store,idm,new Clock()).LogoutAsync(Secret,"test",default));Assert.False(idm.Revoked);Assert.False(store.Revoked);
    }
    [Fact] public async Task CoreCookieCannotBeIssuedWhenAuthenticationAuthorityIsUnavailable()
    {
        var store=new Store();var idm=new Identity(store){Unavailable=true};var service=new CoreSessionService(store,idm,new Clock());
        await Assert.ThrowsAsync<IdentityFailure>(()=>service.ActivateAsync(new(store.Session!.AuthSessionUuid,store.Session.SessionUuid,Guid.NewGuid()),"test",default));Assert.False(store.Activated);
    }
}
