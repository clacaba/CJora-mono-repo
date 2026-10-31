using CJora.DatabaseMigrations;
return await MigrationRunner.RunAsync(typeof(Program).Assembly, "TRANSACTIONS", args);