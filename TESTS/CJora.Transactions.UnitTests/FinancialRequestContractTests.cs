using System.Text.Json;
using CJora.Transactions.Application.Finance;
using CJora.Transactions.Domain.Ledger;
namespace CJora.Transactions.UnitTests;

public sealed class FinancialRequestContractTests
{
    private static readonly DateTime Now=new(2026,11,1,0,0,0,DateTimeKind.Utc);
    private static FinancialSessionScope Session()=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
    private static FinancialRequestScope Scope(FinancialSessionScope session,FinancialOperation operation=FinancialOperation.PrepareTransfer)=>FinancialRequestScope.Create(session,operation,Guid.NewGuid().ToString("D"));
    private static CreateTransferIntentRequest Request()=>new(Guid.NewGuid(),Guid.NewGuid(),new("USD","100"),null);
    private static FinancialAuthorityContext Authority(FinancialSessionScope scope,DateTime? validated=null,DateTime? expires=null)=>new(scope,[FinancialPermissions.TransfersCreate],validated??Now,expires??Now.AddHours(1));
    private static void Code(string code,Action action)=>Assert.Equal(code,Assert.Throws<LedgerRuleViolation>(action).Code);

    [Fact] public void IdempotencyReplaysOnlyTheSameScopeKeyAndCanonicalBusinessTerms()
    {
        var scope=Scope(Session());var request=Request();var fingerprint=RequestFingerprint.Prepare(scope,request);
        Assert.Equal(IdempotencyDecision.Execute,FinancialIdempotency.Decide(scope,fingerprint,null));
        var stored=new StoredFinancialRequest(scope,fingerprint);
        Assert.Equal(IdempotencyDecision.Replay,FinancialIdempotency.Decide(scope,RequestFingerprint.Prepare(scope,request with{Reference=""}),stored));
        Assert.Equal(IdempotencyDecision.Conflict,FinancialIdempotency.Decide(scope,RequestFingerprint.Prepare(scope,request with{Amount=new("USD","101")}),stored));
        var otherCustomer=scope with{CustomerUuid=Guid.NewGuid()};
        Assert.Equal(IdempotencyDecision.Conflict,FinancialIdempotency.Decide(otherCustomer,RequestFingerprint.Prepare(otherCustomer,request),stored));
        var otherProgram=scope with{ProgramUuid=Guid.NewGuid()};
        Assert.Equal(IdempotencyDecision.Conflict,FinancialIdempotency.Decide(otherProgram,RequestFingerprint.Prepare(otherProgram,request),stored));
        Assert.Equal(IdempotencyDecision.Conflict,FinancialIdempotency.Decide(scope with{Key=Guid.NewGuid()},fingerprint,stored));
    }
    [Fact] public void FingerprintsBindAccountsReferenceCurrencyAndOperation()
    {
        var scope=Scope(Session());var request=Request();var original=RequestFingerprint.Prepare(scope,request);
        Assert.False(original.Matches(RequestFingerprint.Prepare(scope,request with{SourceAccountUuid=Guid.NewGuid()})));
        Assert.False(original.Matches(RequestFingerprint.Prepare(scope,request with{DestinationAccountUuid=Guid.NewGuid()})));
        Assert.False(original.Matches(RequestFingerprint.Prepare(scope,request with{Reference="invoice"})));
        Assert.False(original.Matches(RequestFingerprint.Prepare(scope,request with{Amount=new("SGD","100")})));
        var confirmScope=scope with{Operation=FinancialOperation.ConfirmTransfer};
        Assert.False(original.Matches(RequestFingerprint.Confirm(confirmScope,Guid.NewGuid(),new(1))));
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("00000000-0000-0000-0000-000000000000")][InlineData("ABCDEFAB-CDEF-4ABC-8DEF-ABCDEFABCDEF")][InlineData("not-a-key")]
    public void IdempotencyKeyRequiresCanonicalNonemptyUuid(string? key)=>Code("INVALID_IDEMPOTENCY_SCOPE",()=>FinancialRequestScope.Create(Session(),FinancialOperation.PrepareTransfer,key));
    [Theory][InlineData("line\nbreak")][InlineData("line\rbreak")][InlineData("café")]
    public void ReferenceCannotHideControlsOrUnsupportedCharacters(string reference)=>Code("INVALID_TRANSFER_REFERENCE",()=>RequestFingerprint.Prepare(Scope(Session()),Request() with{Reference=reference}));
    [Fact] public void AmountAndIdentityCannotBeInjectedThroughBrowserDto()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<CreateTransferIntentRequest>("""{"sourceAccountUuid":"00000000-0000-0000-0000-000000000001","destinationAccountUuid":"00000000-0000-0000-0000-000000000002","amount":{"currency":"USD","minorUnits":100}}""",options));
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<ConfirmTransferIntentRequest>("""{"reviewVersion":1,"amount":{"currency":"USD","minorUnits":"999"}}""",options));
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<CreateTransferIntentRequest>("""{"programUuid":"00000000-0000-0000-0000-000000000001"}""",options));
    }
    [Fact] public void ProfilePermissionAndUnknownPermissionDoNotAuthorizeFinancialOperations()
    {
        var context=new FinancialAuthorityContext(Session(),["CUSTOMERS.PROFILE.READ"],Now,Now.AddHours(1));
        Code("FINANCIAL_ACCESS_DENIED",()=>FinancialAuthorization.Require(context,FinancialPermissions.TransfersCreate,Now));
        Code("FINANCIAL_ACCESS_DENIED",()=>FinancialAuthorization.Require(Authority(Session()),"UNKNOWN",Now));
    }
    [Theory][InlineData(-30,3600)][InlineData(1,3600)][InlineData(0,0)]
    public void StaleFutureAndExpiredAuthorityFailClosed(int validatedSeconds,int expiresSeconds)=>Code("FINANCIAL_AUTHORITY_EXPIRED",()=>
        FinancialAuthorization.Require(Authority(Session(),Now.AddSeconds(validatedSeconds),Now.AddSeconds(expiresSeconds)),FinancialPermissions.TransfersCreate,Now));
    [Fact] public void PermissionsCannotBeMutatedAfterValidation()
    {
        var permissions=new HashSet<string>{FinancialPermissions.AccountsRead};
        var context=new FinancialAuthorityContext(Session(),permissions,Now,Now.AddHours(1));
        permissions.Add(FinancialPermissions.TransfersCreate);
        Code("FINANCIAL_ACCESS_DENIED",()=>FinancialAuthorization.Require(context,FinancialPermissions.TransfersCreate,Now));
    }
    [Fact] public void ConfirmationRequiresFreshAuthorityTheSameSessionsAndUnexpiredReview()
    {
        var owner=Session();var scope=Scope(owner);var fingerprint=RequestFingerprint.Prepare(scope,Request());
        var review=TransferReview.Create(Guid.NewGuid(),owner,fingerprint,Now);
        review.RequireForExecution(Authority(owner),1,Now);
        Code("REVIEW_SESSION_CHANGED",()=>review.RequireForExecution(Authority(owner with{CoreSessionUuid=Guid.NewGuid()}),1,Now));
        Code("REVIEW_SESSION_CHANGED",()=>review.RequireForExecution(Authority(owner with{AuthSessionUuid=Guid.NewGuid()}),1,Now));
        Code("INVALID_REVIEW_VERSION",()=>review.RequireForExecution(Authority(owner),2,Now));
        Code("TRANSFER_REVIEW_EXPIRED",()=>review.RequireForExecution(Authority(owner,Now.AddMinutes(5),Now.AddHours(1)),1,Now.AddMinutes(5)));
    }
    [Theory]
    [InlineData(TransferIntentState.Prepared,TransferIntentState.Posted,true)]
    [InlineData(TransferIntentState.Prepared,TransferIntentState.Rejected,true)]
    [InlineData(TransferIntentState.Prepared,TransferIntentState.Expired,true)]
    [InlineData(TransferIntentState.Posted,TransferIntentState.Prepared,false)]
    [InlineData(TransferIntentState.Posted,TransferIntentState.Posted,false)]
    [InlineData(TransferIntentState.Rejected,TransferIntentState.Posted,false)]
    [InlineData(TransferIntentState.Expired,TransferIntentState.Posted,false)]
    public void OnlyPreparedIntentsMayReachATerminalState(TransferIntentState from,TransferIntentState to,bool expected)=>Assert.Equal(expected,TransferIntentLifecycle.CanTransition(from,to));
    [Fact] public void ReviewCannotUseAnotherCustomersTermsOrAConfirmationFingerprint()
    {
        var owner=Session();var scope=Scope(owner);
        var otherCustomer=RequestFingerprint.Prepare(scope with{CustomerUuid=Guid.NewGuid()},Request());
        Code("INVALID_TRANSFER_REVIEW",()=>TransferReview.Create(Guid.NewGuid(),owner,otherCustomer,Now));
        var confirmation=RequestFingerprint.Confirm(scope with{Operation=FinancialOperation.ConfirmTransfer},Guid.NewGuid(),new(1));
        Code("INVALID_TRANSFER_REVIEW",()=>TransferReview.Create(Guid.NewGuid(),owner,confirmation,Now));
    }
    [Fact] public void FingerprintCannotBeCarriedIntoAnotherScopeOrOperation()
    {
        var scope=Scope(Session());var fingerprint=RequestFingerprint.Prepare(scope,Request());
        Code("INVALID_IDEMPOTENCY_SCOPE",()=>FinancialIdempotency.Decide(scope with{ProgramUuid=Guid.NewGuid()},fingerprint,null));
        Code("INVALID_IDEMPOTENCY_SCOPE",()=>FinancialIdempotency.Decide(scope with{CustomerUuid=Guid.NewGuid()},fingerprint,null));
        Code("INVALID_IDEMPOTENCY_SCOPE",()=>FinancialIdempotency.Decide(scope with{Operation=FinancialOperation.ConfirmTransfer},fingerprint,null));
    }
}
