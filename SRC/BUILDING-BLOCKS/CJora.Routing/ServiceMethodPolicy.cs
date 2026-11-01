using System.Text.RegularExpressions;

namespace CJora.Routing;

/// <summary>Database routing never grants authorization or unrestricted outbound access.</summary>
public sealed class ServiceMethodPolicy(IReadOnlyDictionary<string, string> approvedDestinations)
{
    public const int MaximumBodyBytes = 10 * 1024 * 1024;
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern = new("^/[a-zA-Z0-9/_-]+$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string name) => NamePattern.IsMatch(name);

    public Uri Validate(ServiceMethod row, string service, string method, string verb,
        string? contentType, long? contentLength, bool development)
    {
        if (!IsValidName(service) || !IsValidName(method)) Reject(400, "INVALID_ROUTE", "Invalid service or method name.");
        if (row.ServiceName != service || row.MethodName != method || row.RequestType != verb)
            Reject(404, "METHOD_NOT_FOUND", "Registered method not found.");
        if (row.Status != "A" || !row.IsTested || (row.DevelopmentOnly && !development))
            Reject(404, "METHOD_NOT_FOUND", "Registered method not found.");
        if (row.TimeoutMs is < 500 or > 30000 || row.MaxRequestBytes is < 1 or > MaximumBodyBytes)
            Reject(503, "INVALID_METHOD_CONFIGURATION", "Method configuration is unavailable.");
        Uri? origin = null;
        Uri? approvedOrigin = null;
        if (!approvedDestinations.TryGetValue(service, out var approved) ||
            !Uri.TryCreate(row.Host, UriKind.Absolute, out origin) ||
            !Uri.TryCreate(approved, UriKind.Absolute, out approvedOrigin) ||
            !IsOrigin(origin) || !IsOrigin(approvedOrigin) ||
            origin.GetLeftPart(UriPartial.Authority) != approvedOrigin.GetLeftPart(UriPartial.Authority))
            Reject(503, "DESTINATION_NOT_APPROVED", "Method destination is unavailable.");
        if (!PathPattern.IsMatch(row.Path) || row.Path.Contains("//", StringComparison.Ordinal) ||
            row.Path.StartsWith("/api/v1/cor/dispatch", StringComparison.OrdinalIgnoreCase))
            Reject(503, "INVALID_METHOD_PATH", "Method destination is unavailable.");

        // The linked-session authorization engine is not implemented yet. Never interpret registry presence as permission.
        if (row.AuthorizationPolicy != "PUBLIC_DIAGNOSTIC")
            Reject(503, "SESSION_AUTHORIZATION_NOT_READY", "Session authorization is not available for this method.");
        var health = verb == "GET" && row.Path == "/health/live";
        var developmentSample = development && verb == "POST" &&
            ((service == "core" && row.Path == "/api/v1/cor/diagnostics/echo") ||
             (service == "documents" && row.Path == "/api/v1/doc/diagnostics/upload"));
        if (row.IsSensitive || !(health || developmentSample))
            Reject(403, "PUBLIC_METHOD_NOT_APPROVED", "This operation requires session authorization.");
        if (contentLength > row.MaxRequestBytes) Reject(413, "REQUEST_TOO_LARGE", "Request exceeds the method size limit.");
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        var supportedBody = row.BodyType switch
        {
            "NONE" => contentLength is null or 0 && string.IsNullOrWhiteSpace(contentType),
            "JSON" => string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase),
            "MULTIPART" => string.Equals(mediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase) && HasMultipartBoundary(contentType),
            _ => false
        };
        if (!supportedBody) Reject(415, "UNSUPPORTED_BODY_TYPE", "Request content type is not supported by this method.");
        return new Uri(origin!.GetLeftPart(UriPartial.Authority) + row.Path);
    }

    private static bool HasMultipartBoundary(string? contentType)
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed)) return false;
        var boundaries = parsed.Parameters.Where(parameter => parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)).ToArray();
        var boundary = boundaries.Length == 1 ? boundaries[0].Value?.Trim('"') : null;
        return !string.IsNullOrWhiteSpace(boundary) && boundary.Length <= 70 && !boundary.Any(char.IsControl);
    }

    private static bool IsOrigin(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == "/";

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(int status, string code, string message) => throw new RouteRejectedException(status, code, message);
}