namespace CJora.Routing;

public sealed record ServiceMethod(
    Guid Uuid, string ServiceName, string MethodName, string Host, string Path,
    string RequestType, string Status, bool IsTested, bool IsSensitive,
    string AuthorizationPolicy, bool DevelopmentOnly, string BodyType, int MaxRequestBytes, int TimeoutMs);

public sealed record RoutingError(string Code, string Category, string CorrelationId, string? TraceId, string SafeMessage);

public interface IRoutingErrorRecorder
{
    Task RecordAsync(RoutingError error);
}

public sealed class RegistryUnavailableException : Exception
{
    public RegistryUnavailableException(string diagnosticCode = "REGISTRY_FAILURE") : base("The method registry is unavailable.") { DiagnosticCode = diagnosticCode; }
    public string DiagnosticCode { get; }
}

public sealed class RouteRejectedException(int statusCode, string code, string safeMessage) : Exception(safeMessage)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}