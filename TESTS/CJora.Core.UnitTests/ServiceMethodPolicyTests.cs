using CJora.Routing;

namespace CJora.Core.UnitTests;

public sealed class ServiceMethodPolicyTests
{
    private readonly ServiceMethodPolicy _policy = new(new Dictionary<string, string>
    {
        ["core"] = "http://localhost:5075", ["documents"] = "http://localhost:5082"
    });
    private static ServiceMethod Health => new(Guid.NewGuid(), "core", "health", "http://localhost:5075", "/health/live",
        "GET", "A", true, false, "PUBLIC_DIAGNOSTIC", false, "NONE", 8192, 10000);

    [Fact]
    public void Approved_health_selects_only_the_registered_destination()
    {
        Assert.Equal("http://localhost:5075/health/live", Validate(Health).AbsoluteUri);
    }

    [Theory]
    [InlineData("Core")]
    [InlineData("core/other")]
    [InlineData("../core")]
    [InlineData("core%2fother")]
    [InlineData("")]
    public void Invalid_public_service_names_are_rejected(string service)
    {
        var exception = Assert.Throws<RouteRejectedException>(() => _policy.Validate(Health, service, "health", "GET", null, 0, true));
        Assert.Equal(400, exception.StatusCode);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://localhost:5075/core")]
    [InlineData("http://user:password@localhost:5075")]
    [InlineData("http://localhost:5075?target=other")]
    [InlineData("http://localhost:5075#fragment")]
    [InlineData("file:///tmp/example")]
    [InlineData("https://example.test")]
    public void Nonapproved_origins_and_embedded_credentials_are_rejected(string host)
    {
        Assert.Equal("DESTINATION_NOT_APPROVED", Reject(Health with { Host = host }).Code);
    }

    [Theory]
    [InlineData("//example.test")]
    [InlineData("/../health")]
    [InlineData("/%2e%2e/health")]
    [InlineData("/api/v1/cor/dispatch/core/health")]
    [InlineData("/health/live?x=1")]
    [InlineData("/health\\live")]
    public void Traversal_dispatch_loops_and_query_injection_in_paths_are_rejected(string path)
    {
        Assert.Equal("INVALID_METHOD_PATH", Reject(Health with { Path = path }).Code);
    }

    [Fact]
    public void Disabled_or_untested_methods_are_unavailable()
    {
        Assert.Equal(404, Reject(Health with { Status = "D" }).StatusCode);
        Assert.Equal(404, Reject(Health with { IsTested = false }).StatusCode);
    }

    [Fact]
    public void Http_verb_must_match_the_registered_contract()
    {
        var exception = Assert.Throws<RouteRejectedException>(() => _policy.Validate(Health, "core", "health", "POST", null, 0, true));
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public void Registry_presence_and_public_flags_cannot_bypass_session_authorization()
    {
        Assert.Equal("SESSION_AUTHORIZATION_NOT_READY", Reject(Health with { AuthorizationPolicy = "SESSION" }).Code);
        Assert.Equal(403, Reject(Health with { IsSensitive = true }).StatusCode);
        Assert.Equal(403, Reject(Health with { Path = "/api/v1/cor/customers/me" }).StatusCode);
    }

    [Theory]
    [InlineData(0, 10000)]
    [InlineData(10485761, 10000)]
    [InlineData(8192, 100)]
    [InlineData(8192, 60000)]
    public void Invalid_size_and_timeout_configuration_is_rejected(int size, int timeout)
    {
        Assert.Equal(503, Reject(Health with { MaxRequestBytes = size, TimeoutMs = timeout }).StatusCode);
    }

    [Fact]
    public void Oversized_body_is_rejected_before_forwarding()
    {
        var exception = Assert.Throws<RouteRejectedException>(() => _policy.Validate(Health, "core", "health", "GET", null, 8193, true));
        Assert.Equal(413, exception.StatusCode);
    }

    [Fact]
    public void Json_sample_is_development_only_even_if_its_database_flag_is_changed()
    {
        var row = Health with { MethodName = "echo", Path = "/api/v1/cor/diagnostics/echo", RequestType = "POST", BodyType = "JSON" };
        Assert.Equal("/api/v1/cor/diagnostics/echo", _policy.Validate(row, "core", "echo", "POST", "application/json; charset=utf-8", 16, true).AbsolutePath);
        var exception = Assert.Throws<RouteRejectedException>(() => _policy.Validate(row, "core", "echo", "POST", "application/json", 16, false));
        Assert.Equal(403, exception.StatusCode);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data")]
    [InlineData("multipart/form-data; boundary=\"\"")]
    public void Uploads_require_a_valid_multipart_content_type_and_boundary(string contentType)
    {
        var row = Upload;
        var exception = Assert.Throws<RouteRejectedException>(() => _policy.Validate(row, "documents", "upload-check", "POST", contentType, 16, true));
        Assert.Equal(415, exception.StatusCode);
    }

    [Fact]
    public void Multipart_accepts_a_quoted_boundary_without_rewriting_it()
    {
        Assert.Equal("/api/v1/doc/diagnostics/upload", _policy.Validate(Upload, "documents", "upload-check", "POST",
            "multipart/form-data; boundary=\"CJora-test-boundary\"", 1024, true).AbsolutePath);
    }

    private static ServiceMethod Upload => Health with { ServiceName = "documents", MethodName = "upload-check",
        Host = "http://localhost:5082", Path = "/api/v1/doc/diagnostics/upload", RequestType = "POST", BodyType = "MULTIPART", DevelopmentOnly = true };
    private Uri Validate(ServiceMethod row) => _policy.Validate(row, "core", "health", "GET", null, 0, true);
    private RouteRejectedException Reject(ServiceMethod row) => Assert.Throws<RouteRejectedException>(() => Validate(row));
}