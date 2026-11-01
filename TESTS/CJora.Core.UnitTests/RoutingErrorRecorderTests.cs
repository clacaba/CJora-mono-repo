using System.Text.Json;
using CJora.Routing;
using CJora.Routing.Persistence;

namespace CJora.Core.UnitTests;

public sealed class RoutingErrorRecorderTests
{
    [Fact]
    public async Task Registry_outage_writes_recoverable_safe_error_records_without_a_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cjora-routing-unit-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "errors.jsonl");
        try
        {
            var recorder = new RoutingErrorRecorder(null, file);
            await recorder.RecordAsync(new RoutingError("REGISTRY_UNAVAILABLE", "Dependency", "correlation-one", null, "The method registry is unavailable."));
            await recorder.RecordAsync(new RoutingError("METHOD_NOT_FOUND", "Validation", "correlation-two", null, "Registered method not found."));
            var lines = await File.ReadAllLinesAsync(file);
            Assert.Equal(2, lines.Length);
            using var record = JsonDocument.Parse(lines[0]);
            Assert.Equal("REGISTRY_UNAVAILABLE", record.RootElement.GetProperty("error").GetProperty("Code").GetString());
            Assert.Equal("correlation-one", record.RootElement.GetProperty("error").GetProperty("CorrelationId").GetString());
            Assert.True(record.RootElement.TryGetProperty("uuid", out _));
            Assert.True(record.RootElement.TryGetProperty("occurredAtUtc", out _));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}