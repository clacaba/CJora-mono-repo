using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace CJora.DatabaseMigrations;

/// <summary>Runs immutable, service-owned SQL scripts explicitly outside API startup.</summary>
public static class MigrationRunner
{
    public static async Task<int> RunAsync(Assembly assembly, string service, string[] args)
    {
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (args.Length == 1 && args[0] == "--list")
        {
            foreach (var script in scripts) Console.WriteLine(script);
            return 0;
        }
        if (args.Length != 1 || args[0] != "--apply")
        {
            Console.Error.WriteLine("Use --list or --apply. Supply credentials through the service migration environment variable.");
            return 2;
        }
        var variable = $"CJORA_{service}_MIGRATION_CONNECTION";
        var secret = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine($"Required environment variable is missing: {variable}");
            return 2;
        }
        try
        {
            var settings = new MySqlConnectionStringBuilder(secret);
            var database = $"CJORA_{service}";
            if (!string.Equals(settings.Database, database, StringComparison.Ordinal))
                throw new InvalidOperationException("Migration connection targets the wrong service database.");
            await using var connection = new MySqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            var lockName = $"{database}:migrations";
            await using var acquire = new MySqlCommand("SELECT GET_LOCK(@name, 10)", connection);
            acquire.Parameters.AddWithValue("@name", lockName);
            if (Convert.ToInt32(await acquire.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("Migration lock unavailable.");
            try
            {
                await using var bootstrap = new MySqlCommand("""
                    CREATE TABLE IF NOT EXISTS MIGRATION_HISTORY (
                        ID BIGINT NOT NULL AUTO_INCREMENT COMMENT 'Service-local numeric primary key',
                        UUID CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL COMMENT 'Canonical migration execution UUID',
                        SCRIPT_NAME VARCHAR(240) NOT NULL COMMENT 'Immutable embedded SQL resource name',
                        CHECKSUM CHAR(64) NOT NULL COMMENT 'SHA256 digest of original script contents',
                        STATE VARCHAR(16) NOT NULL COMMENT 'STARTED or APPLIED; interrupted execution requires operator recovery',
                        CREATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC migration start time',
                        UPDATED_DATETIME DATETIME(6) NOT NULL COMMENT 'UTC last state transition time',
                        PRIMARY KEY (ID), UNIQUE KEY UX_MIGRATION_UUID (UUID),
                        UNIQUE KEY UX_MIGRATION_SCRIPT (SCRIPT_NAME)
                    ) ENGINE=InnoDB COMMENT='Service-owned schema execution history; not a business audit';
                    """, connection);
                await bootstrap.ExecuteNonQueryAsync();
                foreach (var name in scripts)
                {
                    await using var stream = assembly.GetManifestResourceStream(name)!;
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var sql = await reader.ReadToEndAsync();
                    var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                    await using var lookup = new MySqlCommand(
                        "SELECT CHECKSUM, STATE FROM MIGRATION_HISTORY WHERE SCRIPT_NAME=@name", connection);
                    lookup.Parameters.AddWithValue("@name", name);
                    var applied = false;
                    await using (var result = await lookup.ExecuteReaderAsync())
                    {
                        if (await result.ReadAsync())
                        {
                            if (result.GetString(0) != checksum || result.GetString(1) != "APPLIED")
                                throw new InvalidOperationException("Changed or interrupted migration requires operator recovery.");
                            applied = true;
                        }
                    }
                    if (applied) { Console.WriteLine($"Already applied: {name}"); continue; }
                    await using var begin = new MySqlCommand("""
                        INSERT INTO MIGRATION_HISTORY
                        (UUID,SCRIPT_NAME,CHECKSUM,STATE,CREATED_DATETIME,UPDATED_DATETIME)
                        VALUES (@uuid,@name,@checksum,'STARTED',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                        """, connection);
                    begin.Parameters.AddWithValue("@uuid", Guid.NewGuid().ToString("D"));
                    begin.Parameters.AddWithValue("@name", name);
                    begin.Parameters.AddWithValue("@checksum", checksum);
                    await begin.ExecuteNonQueryAsync();
                    // MySQL DDL implicitly commits. Never automatically retry an interrupted script.
                    await using var execute = new MySqlCommand(sql, connection) { CommandTimeout = 120 };
                    await execute.ExecuteNonQueryAsync();
                    await using var finish = new MySqlCommand("""
                        UPDATE MIGRATION_HISTORY SET STATE='APPLIED',UPDATED_DATETIME=UTC_TIMESTAMP(6)
                        WHERE SCRIPT_NAME=@name AND STATE='STARTED'
                        """, connection);
                    finish.Parameters.AddWithValue("@name", name);
                    if (await finish.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("Migration history completion failed.");
                    Console.WriteLine($"Applied: {name}");
                }
            }
            finally
            {
                await using var release = new MySqlCommand("SELECT RELEASE_LOCK(@name)", connection);
                release.Parameters.AddWithValue("@name", lockName);
                await release.ExecuteScalarAsync();
            }
            return 0;
        }
        catch (Exception exception)
        {
            // Connection strings and server exception messages may contain credentials or personal data.
            Console.Error.WriteLine($"Migration failed ({exception.GetType().Name}). Check connectivity, target database and migration state securely; do not blindly retry.");
            return 1;
        }
    }
}