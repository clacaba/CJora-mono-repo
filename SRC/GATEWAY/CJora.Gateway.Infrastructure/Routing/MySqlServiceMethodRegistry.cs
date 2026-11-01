using CJora.Routing;
using CJora.Gateway.Application.Routing;
using MySqlConnector;

namespace CJora.Gateway.Infrastructure.Routing;

public sealed class MySqlServiceMethodRegistry(string? connectionString) : IServiceMethodRegistry
{
    public async Task<ServiceMethod?> FindAsync(string service, string method, string requestType, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new RegistryUnavailableException("INVALID_CONNECTION_CONFIGURATION");
            var settings = new MySqlConnectionStringBuilder(connectionString);
            if (settings.Database != "CJORA_GATEWAY") throw new RegistryUnavailableException("INVALID_CONNECTION_CONFIGURATION");
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                SELECT UUID,SERVICE_NAME,METHOD_NAME,METHOD_HOST,METHOD_PATH,METHOD_REQUEST_TYPE,METHOD_STATUS,
                       IS_TESTED,IS_DATATYPE_SENSITIVE,AUTHORIZATION_POLICY,DEVELOPMENT_ONLY,BODY_TYPE,MAX_REQUEST_BYTES,TIMEOUT_MS
                FROM SERVICE_METHOD
                WHERE SERVICE_NAME=@service AND METHOD_NAME=@method AND METHOD_REQUEST_TYPE=@verb
                LIMIT 1
                """, connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("@service", service);
            command.Parameters.AddWithValue("@method", method);
            command.Parameters.AddWithValue("@verb", requestType);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new ServiceMethod(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7),
                reader.GetBoolean(8), reader.GetString(9), reader.GetBoolean(10), reader.GetString(11), reader.GetInt32(12), reader.GetInt32(13));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RegistryUnavailableException) { throw; }
        catch (MySqlException exception) { throw new RegistryUnavailableException($"MYSQL_{exception.Number}"); }
        catch (Exception exception) { throw new RegistryUnavailableException(exception.GetType().Name); }
    }
}