using MySqlConnector;

if(args is ["--validate-schema"])
{
    var admin=Environment.GetEnvironmentVariable("CJORA_FINANCE_VALIDATION_ADMIN")??throw new InvalidOperationException("Validation administrator connection required.");
    const string database="CJORA_TRANSACTIONS_FINANCE_CHECKS";
    await using var root=new MySqlConnection(admin);await root.OpenAsync();
    async Task Execute(string sql,MySqlConnection c){await using var command=new MySqlCommand(sql,c){CommandTimeout=120};await command.ExecuteNonQueryAsync();}
    await Execute($"DROP DATABASE IF EXISTS {database}; CREATE DATABASE {database} CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin; GRANT SELECT,INSERT,UPDATE,EXECUTE ON {database}.* TO 'cjora_tx_finance_definer'@'%';",root);
    try
    {
        var settings=new MySqlConnectionStringBuilder(admin){Database=database,AllowUserVariables=true};
        await using var connection=new MySqlConnection(settings.ConnectionString);await connection.OpenAsync();
        foreach(var file in new[]{"0001_audit_error_logs.sql","0002_customer_synchronization.sql","0003_accounts_ledger_transfers.sql","0004_ledger_integrity_alerts.sql"})
            await Execute(await File.ReadAllTextAsync(Path.Combine("SRC","TRANSACTIONS","CJora.Transactions.Migrations","Scripts",file)),connection);
        Console.WriteLine("PASS: financial migration compiles in the isolated validation schema.");
        return 0;
    }
    catch(MySqlException e)
    {
        // Trusted schema DDL contains no passwords, customer records or application request values.
        Console.Error.WriteLine($"Schema validation SQL {e.Number}: {e.Message}");
        return 1;
    }
    finally
    {
        await Execute($"DROP DATABASE IF EXISTS {database}; REVOKE ALL PRIVILEGES ON {database}.* FROM 'cjora_tx_finance_definer'@'%';",root);
    }
}
if(args is ["--bootstrap-demo",var currency,var max,var funding])
{
    try{return await DemoFinance.Run(currency,max,funding);}
    catch(Exception e){Console.Error.WriteLine("Finance fixture failed: "+(e is InvalidOperationException&&System.Text.RegularExpressions.Regex.IsMatch(e.Message,"^[A-Z][A-Z0-9_]+$")?e.Message:e.GetType().Name));return 1;}
}
if(args is ["--database"])
{
    try{return await DatabaseFinanceChecks.Run();}
    catch(Exception e){Console.Error.WriteLine("Finance database check failed: "+(e is InvalidOperationException&&System.Text.RegularExpressions.Regex.IsMatch(e.Message,"^[A-Z][A-Z0-9_]+$")?e.Message:e is CJora.Finance.Contracts.FinanceFailure f?f.Code:e is MySqlException db?"SQL_"+db.Number:e.GetType().Name));return 1;}
}
Console.Error.WriteLine("Specify --validate-schema or --bootstrap-demo with explicit currency, limit and fictional funding minor units.");
return 2;
