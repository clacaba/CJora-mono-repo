using CJora.Core.Application.Customers;
namespace CJora.Core.UnitTests;
public sealed class CustomerCommandTests
{
    [Theory][InlineData("ACTIVE",0,true)][InlineData("RESTRICTED",1,true)][InlineData("UNKNOWN",1,false)][InlineData("ACTIVE",-1,false)]
    public void CustomerCommandRequiresKnownStatusAndOptimisticVersion(string status,long version,bool expected)
    {
        var command=new CustomerChange(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"Sync Fixture","sync@example.test",status,version,Guid.NewGuid());
        Assert.Equal(expected,command.IsValid());
        Assert.False((command with{CustomerUuid=Guid.Empty}).IsValid());
        Assert.False((command with{Email="not-an-email"}).IsValid());
    }
}
