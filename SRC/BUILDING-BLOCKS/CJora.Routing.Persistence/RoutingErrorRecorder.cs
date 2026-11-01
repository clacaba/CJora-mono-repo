using System.Text.Json;
using CJora.Routing;
using MySqlConnector;

namespace CJora.Routing.Persistence;

public sealed class RoutingErrorRecorder(string? connectionString, string fallbackFile, string database = "CJORA_CORE", string serviceName = "Core") : IRoutingErrorRecorder
{
    private readonly SemaphoreSlim _fallbackLock = new(1, 1);

    public async Task RecordAsync(RoutingError error)
    {
        var uuid = Guid.NewGuid();
        var now = DateTime.UtcNow;
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException();
            var settings = new MySqlConnectionStringBuilder(connectionString);
            if (settings.Database != database) throw new InvalidOperationException();
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand("""
                INSERT INTO ERROR_LOGS
                (UUID,CORRELATION_ID,TRACE_ID,SERVICE_NAME,ERROR_CODE,ERROR_CATEGORY,SEVERITY,SAFE_MESSAGE,CREATED_DATETIME)
                VALUES (@uuid,@correlation,@trace,@service,@code,@category,'Warning',@message,@now)
                """, connection) { CommandTimeout = 3 };
            command.Parameters.AddWithValue("@uuid", uuid.ToString("D"));
            command.Parameters.AddWithValue("@correlation", error.CorrelationId);
            command.Parameters.AddWithValue("@trace", error.TraceId);
            command.Parameters.AddWithValue("@service", serviceName);
            command.Parameters.AddWithValue("@code", error.Code);
            command.Parameters.AddWithValue("@category", error.Category);
            command.Parameters.AddWithValue("@message", error.SafeMessage);
            command.Parameters.AddWithValue("@now", now);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Only this allowlisted structure reaches the fallback, never exception text, URLs, bodies or credentials.
            await _fallbackLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fallbackFile)!);
                await using var stream = new FileStream(fallbackFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                await using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { uuid, occurredAtUtc = now, serviceName, error }));
                await writer.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            finally { _fallbackLock.Release(); }
        }
    }
}