using CJora.DatabaseMigrations;

// The CLI uses process-wide streams and environment variables. Keep these tests serial and restore state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CJora.DatabaseMigrations.UnitTests;

public sealed class MigrationRunnerTests
{
    private const string Service = "UNIT_TESTS";
    private const string Variable = "CJORA_UNIT_TESTS_MIGRATION_CONNECTION";
    private const string Secret = "unit-test-placeholder-never-log";

    [Fact]
    public async Task List_discovers_only_owned_SQL_in_stable_order_without_credentials()
    {
        var result = await RunAsync(["--list"], null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        var scripts = result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "CJora.DatabaseMigrations.UnitTests.Fixtures.0001_first.sql",
            "CJora.DatabaseMigrations.UnitTests.Fixtures.0002_second.sql"
        }, scripts);
    }

    [Fact]
    public async Task No_command_is_rejected_instead_of_applying_migrations()
    {
        var result = await RunAsync([], null);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Use --list or --apply", result.Error);
    }

    [Theory]
    [InlineData("--drop")]
    [InlineData("--down")]
    public async Task Unsupported_command_is_rejected(string command)
    {
        var result = await RunAsync([command], null);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Extra_arguments_cannot_turn_an_invalid_command_into_an_apply()
    {
        var result = await RunAsync(["--apply", "--force"], null);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Apply_requires_the_service_specific_connection_setting()
    {
        var result = await RunAsync(["--apply"], null);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(Variable, result.Error);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public async Task Another_services_database_is_rejected_without_exposing_credentials()
    {
        var result = await RunAsync(["--apply"],
            $"Server=127.0.0.1;Database=CJORA_OTHER_SERVICE;User ID=test;Password={Secret};Connection Timeout=1");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("InvalidOperationException", result.Error);
        Assert.DoesNotContain(Secret, result.Output + result.Error);
        Assert.DoesNotContain("Password=", result.Output + result.Error);
    }

    [Fact]
    public async Task Invalid_configuration_returns_a_safe_failure_instead_of_raw_exception_details()
    {
        var result = await RunAsync(["--apply"], $"Password={Secret};InvalidSetting=invalid");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Migration failed", result.Error);
        Assert.DoesNotContain(Secret, result.Output + result.Error);
        Assert.DoesNotContain("InvalidSetting", result.Output + result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] arguments, string? connection)
    {
        var previousConnection = Environment.GetEnvironmentVariable(Variable);
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(Variable, connection);
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await MigrationRunner.RunAsync(typeof(MigrationRunnerTests).Assembly, Service, arguments);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(Variable, previousConnection);
        }
    }
}