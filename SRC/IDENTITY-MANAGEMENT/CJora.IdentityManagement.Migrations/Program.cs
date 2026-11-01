using CJora.DatabaseMigrations;
return await MigrationRunner.RunAsync(typeof(Program).Assembly, "IDENTITY_MANAGEMENT", args);