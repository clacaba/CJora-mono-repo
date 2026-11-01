using CJora.Transactions.Domain.Ledger;
namespace CJora.Transactions.UnitTests;

public sealed class LedgerContractTests
{
    private static readonly Guid Program=Guid.NewGuid(),Customer=Guid.NewGuid();
    private static CustomerAccountState Account(long balance=1000)=>new(Guid.NewGuid(),Program,Customer,Currency.Resolve("USD"),CustomerAccountStatus.Active,balance,1);
    private static TransferRules Rules()=>new(Program,new Dictionary<string,long>{{"USD",10000}});
    private static void Code(string code,Action action)=>Assert.Equal(code,Assert.Throws<LedgerRuleViolation>(action).Code);

    [Theory][InlineData("USD",2)][InlineData("PHP",2)][InlineData("SGD",2)][InlineData("JPY",0)][InlineData("KWD",3)]
    public void CurrencyMetadataDoesNotAssumeTwoDigits(string code,int digits)=>Assert.Equal(digits,Currency.Resolve(code).MinorDigits);
    [Theory][InlineData(null)][InlineData("usd")][InlineData(" USD")][InlineData("XXX")][InlineData("")]
    public void UnsupportedOrNoncanonicalCurrencyIsRejected(string? code)=>Code("UNSUPPORTED_CURRENCY",()=>Currency.Resolve(code));
    [Theory][InlineData("0")][InlineData("1")][InlineData("9007199254740993")][InlineData("9223372036854775807")]
    public void MinorUnitsRemainExactBeyondJavascriptSafeInteger(string text)=>Assert.Equal(text,Money.FromMinorUnits("USD",text).MinorUnitsText);
    [Theory][InlineData(null)][InlineData("")][InlineData("01")][InlineData("-1")][InlineData("+1")][InlineData("1.0")][InlineData("1e2")][InlineData(" 1")][InlineData("1 ")][InlineData("?")][InlineData("9223372036854775808")]
    public void FloatingSignedNoncanonicalAndOverflowAmountsAreRejected(string? text)=>Code("INVALID_MINOR_UNITS",()=>Money.FromMinorUnits("USD",text));
    [Fact] public void TransferDebitsSourceLiabilityCreditsRecipientAndConservesValue()
    {
        var source=Account(1000);var destination=Account(200) with{CustomerUuid=Guid.NewGuid()};
        var plan=InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,destination,Money.FromMinorUnits("USD","250"),Rules());
        Assert.Equal(750,plan.SourceAfterMinor);Assert.Equal(450,plan.DestinationAfterMinor);
        Assert.Equal(1200,plan.SourceAfterMinor+plan.DestinationAfterMinor);
        Assert.Equal(PostingSide.Debit,plan.Journal.Postings[0].Side);Assert.Equal(source.AccountUuid,plan.Journal.Postings[0].AccountUuid);
        Assert.Equal(PostingSide.Credit,plan.Journal.Postings[1].Side);Assert.Equal(250,plan.Journal.TotalDebitMinor);
    }
    [Fact] public void SourceMayBeFullySpentButCannotBeOverdrawn()
    {
        var source=Account(1000);var dest=Account(0);
        Assert.Equal(0,InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,dest,Money.FromMinorUnits("USD","1000"),Rules()).SourceAfterMinor);
        Code("INSUFFICIENT_FUNDS",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,dest,Money.FromMinorUnits("USD","1001"),Rules()));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void OwnershipAndProgramCannotBeSubstituted(bool sourceMismatch)
    {
        var source=Account();var dest=Account();
        if(sourceMismatch)source=source with{CustomerUuid=Guid.NewGuid()};else dest=dest with{ProgramUuid=Guid.NewGuid()};
        Code(sourceMismatch?"SOURCE_ACCOUNT_NOT_AVAILABLE":"DESTINATION_NOT_AVAILABLE",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,dest,Money.FromMinorUnits("USD","1"),Rules()));
    }
    [Fact] public void NoImplicitProgramCurrencyOrTransferLimitExists()
    {
        Code("CURRENCY_NOT_ENABLED",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,Account(),Account(),Money.FromMinorUnits("USD","1"),new TransferRules(Program,new Dictionary<string,long>())));
        Code("TRANSFER_LIMIT_EXCEEDED",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,Account(20000),Account(),Money.FromMinorUnits("USD","10001"),Rules()));
        Code("TRANSFER_POLICY_SCOPE_MISMATCH",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,Account(),Account(),Money.FromMinorUnits("USD","1"),new TransferRules(Guid.NewGuid(),new Dictionary<string,long>{{"USD",10}})));
    }
    [Theory][InlineData(CustomerAccountStatus.Restricted)][InlineData(CustomerAccountStatus.Closed)][InlineData((CustomerAccountStatus)999)]
    public void RestrictedClosedAndUnknownStatesCannotTransfer(CustomerAccountStatus status)=>Code("ACCOUNT_RESTRICTED",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,Account() with{Status=status},Account(),Money.FromMinorUnits("USD","1"),Rules()));
    [Fact] public void SelfZeroFxAndOverflowTransfersAreRejected()
    {
        var source=Account();var dest=Account();
        Code("SAME_ACCOUNT_TRANSFER",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,source,Money.FromMinorUnits("USD","1"),Rules()));
        Code("TRANSFER_AMOUNT_MUST_BE_POSITIVE",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,dest,Money.FromMinorUnits("USD","0"),Rules()));
        Code("CURRENCY_MISMATCH",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,dest with{Currency=Currency.Resolve("JPY")},Money.FromMinorUnits("USD","1"),Rules()));
        Code("BALANCE_LIMIT_EXCEEDED",()=>InternalTransferPlanner.Plan(Guid.NewGuid(),Program,Customer,source,dest with{PostedMinor=long.MaxValue},Money.FromMinorUnits("USD","1"),Rules()));
    }
    [Fact] public void JournalMustBalanceWithinOneCurrencyAndProgram()
    {
        var debit=new LedgerPosting(Guid.NewGuid(),Program,Currency.Resolve("USD"),PostingSide.Debit,100);
        var credit=debit with{AccountUuid=Guid.NewGuid(),Side=PostingSide.Credit};
        Code("UNBALANCED_JOURNAL",()=>BalancedJournal.Create(Guid.NewGuid(),[debit,credit with{MinorUnits=99}]));
        Code("MIXED_CURRENCY_JOURNAL",()=>BalancedJournal.Create(Guid.NewGuid(),[debit,credit with{Currency=Currency.Resolve("JPY")}]));
        Code("CROSS_PROGRAM_JOURNAL",()=>BalancedJournal.Create(Guid.NewGuid(),[debit,credit with{ProgramUuid=Guid.NewGuid()}]));
        Code("INVALID_POSTING",()=>BalancedJournal.Create(Guid.NewGuid(),[debit,credit with{MinorUnits=0}]));
        Code("INVALID_POSTING",()=>BalancedJournal.Create(Guid.NewGuid(),[debit,credit with{Side=(PostingSide)999}]));
    }
    [Fact] public void JournalTotalsCannotWrapAndCallerCannotMutateValidatedEntries()
    {
        var debit=new LedgerPosting(Guid.NewGuid(),Program,Currency.Resolve("USD"),PostingSide.Debit,long.MaxValue);
        var credit=debit with{AccountUuid=Guid.NewGuid(),Side=PostingSide.Credit};
        Code("JOURNAL_TOTAL_OVERFLOW",()=>BalancedJournal.Create(Guid.NewGuid(),[debit,credit,debit,credit]));
        var list=new List<LedgerPosting>{debit,credit};
        var journal=BalancedJournal.Create(Guid.NewGuid(),list);list.Clear();
        Assert.Equal(2,journal.Postings.Count);Assert.Equal(long.MaxValue,journal.TotalDebitMinor);
        Assert.Throws<NotSupportedException>(()=>((IList<LedgerPosting>)journal.Postings).Clear());
    }
    [Fact] public void PolicyCopiesItsLimitsAndRejectsInvalidConfiguration()
    {
        var limits=new Dictionary<string,long>{{"USD",10}};var rules=new TransferRules(Program,limits);limits["USD"]=1000;
        Assert.Equal(10,rules.LimitFor(Currency.Resolve("USD")));
        Code("INVALID_TRANSFER_LIMIT",()=>new TransferRules(Program,new Dictionary<string,long>{{"USD",0}}));
    }
}
