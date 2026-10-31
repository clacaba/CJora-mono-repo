using CJora.DatabaseMigrations;
return await MigrationRunner.RunAsync(typeof(Program).Assembly, "TPP", args);