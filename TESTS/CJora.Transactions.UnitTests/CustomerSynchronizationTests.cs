using System.Text;
using System.Text.Json;
using CJora.CustomerEvents;
using CJora.Transactions.Domain;
namespace CJora.Transactions.UnitTests;
public sealed class CustomerSynchronizationTests
{
    private static CustomerSnapshot Snapshot()=>new(Guid.NewGuid(),Guid.NewGuid(),1,"core.customer.snapshot.v1",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),1,"ACTIVE",DateTime.UtcNow);
    [Fact] public void SnapshotRoundTripsWithoutContactOrAuthenticationFields()
    {
        var e=Snapshot();var bytes=JsonSerializer.SerializeToUtf8Bytes(e,CustomerSnapshot.Json);
        Assert.Equal(e,CustomerSnapshot.Parse(e.Key,bytes));
        Assert.DoesNotContain("email",Encoding.UTF8.GetString(bytes),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token",Encoding.UTF8.GetString(bytes),StringComparison.OrdinalIgnoreCase);
    }
    [Theory][InlineData(null)][InlineData("")][InlineData("not-json")][InlineData("{\"schemaVersion\":2}")]
    public void MalformedPayloadCannotReachProjection(string? json)=>Assert.Throws<InvalidCustomerEvent>(()=>CustomerSnapshot.Parse("key",json is null?null:Encoding.UTF8.GetBytes(json)));
    [Fact] public void OversizedPayloadIsRejected()=>Assert.Throws<InvalidCustomerEvent>(()=>CustomerSnapshot.Parse("key",new byte[CustomerSnapshot.MaximumBytes+1]));
    [Fact] public void WrongOrderingKeyIsRejected()
    {
        var e=Snapshot();Assert.Throws<InvalidCustomerEvent>(()=>CustomerSnapshot.Parse(Guid.NewGuid().ToString("D"),JsonSerializer.SerializeToUtf8Bytes(e,CustomerSnapshot.Json)));
    }
    [Theory][InlineData(0,"ACTIVE",1)][InlineData(1,"UNKNOWN",1)][InlineData(1,"ACTIVE",2)]
    public void InvalidVersionStatusOrSchemaIsRejected(long version,string status,int schema)
    {
        var e=Snapshot() with{SourceVersion=version,Status=status,SchemaVersion=schema};
        Assert.Throws<InvalidCustomerEvent>(()=>CustomerSnapshot.Parse(e.Key,JsonSerializer.SerializeToUtf8Bytes(e,CustomerSnapshot.Json)));
    }
    [Fact] public void NewCustomerKeepsCanonicalCoreUuid()
    {
        var e=Snapshot();var incoming=new CustomerReference(e.CustomerUuid,e.ProgramUuid,e.UserUuid,1,"ACTIVE");
        Assert.Equal(SnapshotDecision.Apply,CustomerProjection.Decide(null,incoming));
    }
    [Theory][InlineData(1,SnapshotDecision.Stale)][InlineData(2,SnapshotDecision.Stale)][InlineData(3,SnapshotDecision.Apply)]
    public void ReorderedSnapshotsCannotRollBackNewerVersion(long version,SnapshotDecision expected)
    {
        var e=Snapshot();var current=new CustomerReference(e.CustomerUuid,e.ProgramUuid,e.UserUuid,2,"ACTIVE");
        Assert.Equal(expected,CustomerProjection.Decide(current,current with{SourceVersion=version}));
    }
    [Fact] public void SameVersionWithDifferentStatusIsConflict()
    {
        var e=Snapshot();var current=new CustomerReference(e.CustomerUuid,e.ProgramUuid,e.UserUuid,2,"ACTIVE");
        Assert.Equal(SnapshotDecision.Conflict,CustomerProjection.Decide(current,current with{Status="RESTRICTED"}));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void HigherVersionCannotMoveCustomerToAnotherProgramOrUser(bool program)
    {
        var e=Snapshot();var current=new CustomerReference(e.CustomerUuid,e.ProgramUuid,e.UserUuid,2,"ACTIVE");
        var incoming=program?current with{ProgramUuid=Guid.NewGuid(),SourceVersion=3}:current with{UserUuid=Guid.NewGuid(),SourceVersion=3};
        Assert.Equal(SnapshotDecision.Conflict,CustomerProjection.Decide(current,incoming));
    }
}
