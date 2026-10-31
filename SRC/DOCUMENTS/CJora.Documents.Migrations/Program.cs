using CJora.DatabaseMigrations;
return await MigrationRunner.RunAsync(typeof(Program).Assembly, "DOCUMENTS", args);