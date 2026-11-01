using CJora.Identity.Contracts;
using CJora.IdentityManagement.Application.Authentication;
using CJora.IdentityManagement.Domain.Authentication;

namespace CJora.IdentityManagement.UnitTests;

public sealed class LoginCoordinationTests
{
    private const string Issuer="http://localhost:8080/realms/cjora";
    private static readonly DateTime Now=new(2026,11,1,0,0,0,DateTimeKind.Utc);
    private sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>new(Now);}
    private static VerifiedIdentity Verified()=>new(Issuer,"provider-subject","alex@example.test",true,Now);
    private sealed class Store:IAuthenticationStore
    {
        public bool RejectCreate,Created,Active,Complete,Revoked;
        public Task<AuthRecord> CreateAsync(VerifiedIdentity identity,Guid program,Guid operation,string correlation)
        {
            if(RejectCreate)throw new IdentityFailure(401,"LOGIN_ALREADY_USED");
            Created=true;return Task.FromResult(new AuthRecord(Guid.NewGuid(),operation,Guid.NewGuid(),program,Now.AddHours(1)));
        }
        public Task ActivateAsync(AuthRecord auth,Guid core,string correlation){Active=true;return Task.CompletedTask;}
        public Task CompleteActivationAsync(Guid uuid){Complete=true;return Task.CompletedTask;}
        public Task RevokeAsync(RevokeSession request,string correlation){Revoked=true;return Task.CompletedTask;}
        public Task<ValidatedIdentity> ValidateAsync(ValidateSession request)=>throw new NotSupportedException();
        public Task<IReadOnlyList<RecoveryWork>> RecoveryAsync()=>throw new NotSupportedException();
        public Task FinishRecoveryAsync(Guid uuid,bool success)=>throw new NotSupportedException();
        public Task AuditSecurityAsync(string op,string correlation)=>throw new NotSupportedException();
    }
    private sealed class Core(Store store):ICoreSessionClient
    {
        public bool FailPrepare,FailActivate,Replay,Revoked,PrepareCalled;
        public Task<CoreSessionReference> PrepareAsync(PrepareSession request,CancellationToken ct)
        {
            PrepareCalled=true;Assert.True(store.Created);if(FailPrepare)throw new IdentityFailure(503,"CORE_UNAVAILABLE");
            return Task.FromResult(new CoreSessionReference(Guid.NewGuid()));
        }
        public Task<SessionCookie> ActivateAsync(ActivateSession request,CancellationToken ct)
        {
            Assert.True(store.Active);if(FailActivate)throw new IdentityFailure(503,"CORE_UNAVAILABLE");
            return Task.FromResult(new SessionCookie(request.CoreSessionUuid,Replay?null:new string('a',64),Now.AddHours(1)));
        }
        public Task RevokeAsync(RevokeSession request,CancellationToken ct){Revoked=true;return Task.CompletedTask;}
    }
    [Fact] public async Task SuccessfulLoginRequiresBothActivationsBeforeReturningCookie()
    {
        var store=new Store();var core=new Core(store);
        var cookie=await new IdentityLoginService(store,core,Issuer,new Clock()).CompleteAsync(Verified(),Guid.NewGuid(),Guid.NewGuid(),"test",default);
        Assert.NotNull(cookie.Secret);Assert.True(store.Complete);Assert.False(store.Revoked);Assert.False(core.Revoked);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task PartialFailureCompensatesBothSessions(bool prepare)
    {
        var store=new Store();var core=new Core(store){FailPrepare=prepare,FailActivate=!prepare};
        await Assert.ThrowsAsync<IdentityFailure>(()=>new IdentityLoginService(store,core,Issuer,new Clock()).CompleteAsync(Verified(),Guid.NewGuid(),Guid.NewGuid(),"test",default));
        Assert.True(store.Revoked);Assert.True(core.Revoked);Assert.False(store.Complete);
    }
    [Fact] public async Task DuplicateOperationDoesNotRevokePreviouslySuccessfulLogin()
    {
        var store=new Store{RejectCreate=true};var core=new Core(store);
        await Assert.ThrowsAsync<IdentityFailure>(()=>new IdentityLoginService(store,core,Issuer,new Clock()).CompleteAsync(Verified(),Guid.NewGuid(),Guid.NewGuid(),"test",default));
        Assert.False(store.Revoked);Assert.False(core.Revoked);Assert.False(core.PrepareCalled);
    }
    [Fact] public async Task MissingOneTimeSecretCannotIssueBrowserSession()
    {
        var store=new Store();var core=new Core(store){Replay=true};
        await Assert.ThrowsAsync<IdentityFailure>(()=>new IdentityLoginService(store,core,Issuer,new Clock()).CompleteAsync(Verified(),Guid.NewGuid(),Guid.NewGuid(),"test",default));Assert.True(store.Revoked);
    }
    [Theory][InlineData(false,"http://localhost:8080/realms/cjora")][InlineData(true,"https://untrusted.example/")]
    public async Task UnverifiedEmailOrWrongIssuerCannotCreateAuthenticationSession(bool verified,string issuer)
    {
        var store=new Store();var core=new Core(store);var identity=Verified() with{EmailVerified=verified,Issuer=issuer};
        var failure=await Assert.ThrowsAsync<IdentityFailure>(()=>new IdentityLoginService(store,core,Issuer,new Clock()).CompleteAsync(identity,Guid.NewGuid(),Guid.NewGuid(),"test",default));
        Assert.Equal(403,failure.Status);Assert.False(store.Created);Assert.False(core.PrepareCalled);
    }
}
