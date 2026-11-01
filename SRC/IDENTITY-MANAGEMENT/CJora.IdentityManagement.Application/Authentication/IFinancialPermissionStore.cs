using CJora.Identity.Contracts;
namespace CJora.IdentityManagement.Application.Authentication;
public interface IFinancialPermissionStore
{
    Task<ValidatedIdentity> AuthorizePermissionAsync(AuthorizeSessionPermission request);
}
