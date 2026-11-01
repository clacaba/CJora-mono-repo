namespace CJora.Identity.Contracts;

public sealed record BeginLogin(Guid ProgramUuid, Guid OperationUuid, string Email, string ReturnPath);
public sealed record PrepareSession(Guid AuthSessionUuid, Guid OperationUuid, Guid ProgramUuid, Guid UserUuid, DateTime ExpiresAtUtc);
public sealed record CoreSessionReference(Guid CoreSessionUuid);
public sealed record ActivateSession(Guid AuthSessionUuid, Guid CoreSessionUuid, Guid OperationUuid);
public sealed record SessionCookie(Guid CoreSessionUuid, string? Secret, DateTime ExpiresAtUtc);
public sealed record ValidateSession(Guid AuthSessionUuid, Guid CoreSessionUuid, Guid ProgramUuid, Guid UserUuid);
public sealed record ValidatedIdentity(Guid UserUuid, Guid ProgramUuid, Guid AuthSessionUuid, Guid CoreSessionUuid, DateTime ExpiresAtUtc);
public sealed record RevokeSession(Guid AuthSessionUuid, Guid? CoreSessionUuid, Guid OperationUuid);
public sealed class IdentityFailure(int status, string code) : Exception("The identity operation could not be completed.")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class IdentityProtocol
{
    public const string CookieName = "__Host-cjora-session";
    public const string CallerHeader = "X-CJora-Core-Identity";
    public const string ProfilePermission = "CUSTOMERS.PROFILE.READ";
    public static readonly Guid WorkloadUuid = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(8);
    public static bool IsExpired(DateTime now, DateTime lastActivity, DateTime expires) =>
        now >= expires || now - lastActivity >= IdleTimeout || lastActivity > now.AddMinutes(1);
    public static bool IsAllowedReturnPath(string? path) => path is "/app" or "/api/v1/cor/customers/me" or "/api/v1/cor/sessions/current";
}