using CJora.Identity.Contracts;
namespace CJora.Core.UnitTests;
public sealed class BrowserReturnPathTests
{
    [Theory]
    [InlineData("/app",true)]
    [InlineData("/api/v1/cor/customers/me",true)]
    [InlineData("/api/v1/cor/sessions/current",true)]
    [InlineData("//attacker.example",false)]
    [InlineData("https://attacker.example",false)]
    [InlineData("/app?redirect=https://attacker.example",false)]
    [InlineData("/app/../internal",false)]
    public void BrowserReturnPathsAreExplicitlyAllowlisted(string path,bool expected)=>
        Assert.Equal(expected,IdentityProtocol.IsAllowedReturnPath(path));
}
