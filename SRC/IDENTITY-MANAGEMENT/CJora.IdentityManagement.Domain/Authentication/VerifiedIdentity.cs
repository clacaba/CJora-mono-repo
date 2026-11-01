namespace CJora.IdentityManagement.Domain.Authentication;

public sealed record VerifiedIdentity(string Issuer,string Subject,string Email,bool EmailVerified,DateTime AuthenticatedUtc);
public static class IdentityRules
{
    public static bool IsAllowed(VerifiedIdentity identity,string expectedIssuer,DateTime now) =>
        identity.Issuer==expectedIssuer && !string.IsNullOrWhiteSpace(identity.Subject) && identity.Subject.Length<=255 &&
        !string.IsNullOrWhiteSpace(identity.Email) && identity.Email.Length<=254 && identity.EmailVerified &&
        identity.AuthenticatedUtc<=now.AddMinutes(1);
}