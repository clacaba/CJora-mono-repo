using CJora.Finance.Contracts;
using CJora.Transactions.Api.Finance;
using CJora.Transactions.Application.Finance;
using Microsoft.AspNetCore.DataProtection;

namespace CJora.Transactions.UnitTests;
public sealed class FinanceBoundaryTests
{
    private const string Secret="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime Now=new(2026,11,1,0,0,0,DateTimeKind.Utc);
    private static FinanceInvocation Call()=>new(Guid.NewGuid(),Guid.NewGuid(),"POST","/internal/finance/transfer-intents",FinanceProtocol.Hash("{}"u8.ToArray()),Guid.NewGuid().ToString("D"),Now);
    private static void Rejected(Action action)=>Assert.Equal(403,Assert.Throws<FinanceFailure>(action).Status);
    [Fact] public void SignedRequestRetainsBodyRouteSessionAndKeyBinding()
    {
        var call=Call();Assert.Equal(call,FinanceProtocol.Verify(FinanceProtocol.Sign(call,Secret),Secret,Now));
        var changed=call with{IdempotencyKey=Guid.NewGuid().ToString("D")};
        Assert.NotEqual(FinanceProtocol.Sign(call,Secret),FinanceProtocol.Sign(changed,Secret));
    }
    [Fact] public void TamperedOrWrongCredentialProofIsRejected()
    {
        var token=FinanceProtocol.Sign(Call(),Secret);
        Rejected(()=>FinanceProtocol.Verify("x"+token[1..],Secret,Now));
        Rejected(()=>FinanceProtocol.Verify(token,new string('b',64),Now));
        Rejected(()=>FinanceProtocol.Verify(null,Secret,Now));
    }
    [Theory][InlineData(-30)][InlineData(2)]
    public void ExpiredAndFutureInvocationsAreRejected(int seconds)
    {
        var call=Call() with{IssuedAtUtc=Now.AddSeconds(seconds)};
        Rejected(()=>FinanceProtocol.Verify(FinanceProtocol.Sign(call,Secret),Secret,Now));
    }
    [Theory][InlineData("POST","/internal/finance/ledger/credit")][InlineData("DELETE","/internal/finance/accounts")][InlineData("GET","https://attacker.example")]
    public void OnlyApprovedInternalRoutesMayBeSigned(string method,string path)
    {
        var call=Call() with{Method=method,PathAndQuery=path};
        Rejected(()=>FinanceProtocol.Verify(FinanceProtocol.Sign(call,Secret),Secret,Now));
    }
    [Fact] public void SameProcessInvocationReplayIsRejected()
    {
        var guard=new InvocationReplayGuard();var call=Call() with{IssuedAtUtc=DateTime.UtcNow};
        guard.Accept(call);Assert.Equal(409,Assert.Throws<FinanceFailure>(()=>guard.Accept(call)).Status);
    }
    [Fact] public void CursorIsProtectedScopedAndBoundToPageSize()
    {
        var provider=new EphemeralDataProtectionProvider();var cursors=new FinancialCursors(provider);
        var scope=new FinancialSessionScope(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());var account=Guid.NewGuid();
        var cursor=new FinancialCursor(scope.ProgramUuid,scope.CustomerUuid,account,"HISTORY",100,90,20,DateTime.UtcNow.AddMinutes(1));
        var value=cursors.Encode(cursor);
        Assert.Equal(cursor,cursors.Parse(value,scope,account,"HISTORY",20));
        Assert.Equal(400,Assert.Throws<FinanceFailure>(()=>cursors.Parse(value+"x",scope,account,"HISTORY",20)).Status);
        Assert.Equal(400,Assert.Throws<FinanceFailure>(()=>cursors.Parse(value,scope with{CustomerUuid=Guid.NewGuid()},account,"HISTORY",20)).Status);
        Assert.Equal(400,Assert.Throws<FinanceFailure>(()=>cursors.Parse(value,scope,Guid.NewGuid(),"HISTORY",20)).Status);
        Assert.Equal(400,Assert.Throws<FinanceFailure>(()=>cursors.Parse(value,scope,account,"ACCOUNTS",20)).Status);
        Assert.Equal(400,Assert.Throws<FinanceFailure>(()=>cursors.Parse(value,scope,account,"HISTORY",10)).Status);
        Assert.Equal(400,Assert.Throws<FinanceFailure>(()=>cursors.Parse(cursors.Encode(cursor with{ExpiresUtc=DateTime.UtcNow.AddSeconds(-1)}),scope,account,"HISTORY",20)).Status);
    }
}
