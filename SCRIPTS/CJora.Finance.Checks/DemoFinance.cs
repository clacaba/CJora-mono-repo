using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CJora.Core.Application.Customers;
using CJora.Core.Infrastructure.Customers;
using CJora.CustomerEvents;
using CJora.Kafka;
using CJora.Routing.Persistence;
using CJora.Transactions.Infrastructure;
using CJora.Transactions.Domain.Ledger;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

internal static class DemoFinance
{
    internal static string Env(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException("MISSING_FINANCE_CHECK_CONNECTION");
    internal static async Task<int> Run(string currency,string max,string funding)
    {
        var limit=Money.FromMinorUnits(currency,max);var amount=Money.FromMinorUnits(currency,funding);
        if(limit.MinorUnits==0||amount.MinorUnits==0)throw new InvalidOperationException("EXPLICIT_POSITIVE_DEMO_AMOUNTS_REQUIRED");
        var program=Guid.Parse("c6a8a500-0000-4000-8000-000000000001");
        var source=Guid.Parse("c6a8a500-0000-4000-8000-000000000003");
        var recipient=Guid.Parse("c6a8a500-0000-4000-8000-000000000101");
        var recipientUser=Guid.Parse("c6a8a500-0000-4000-8000-000000000102");
        var core=Env("CJORA_CORE_CUSTOMER_CONNECTION");
        await using(var connection=new MySqlConnection(core))
        {
            await connection.OpenAsync();
            await using var exists=Command(connection,null,"SELECT COUNT(*) FROM CUSTOMER WHERE UUID=@uuid",("@uuid",recipient.ToString("D")));
            if(Convert.ToInt64(await exists.ExecuteScalarAsync())==0)
                await new CustomerWriter(core).SaveAsync(new(recipient,program,recipientUser,"Fictional recipient","recipient@example.test","ACTIVE",0,Guid.NewGuid()),default);
        }
        await new CustomerWriter(core).ReconcileAsync(default);
        var sync=Env("CJORA_TX_SYNC_CONNECTION");
        var errors=new RoutingErrorRecorder(sync,Path.Combine(Environment.CurrentDirectory,".local","finance-bootstrap-errors.jsonl"),"CJORA_TRANSACTIONS","Transactions");
        var snapshotStore=new CustomerSnapshotStore(sync);
        using var consumer=new SnapshotConsumer("localhost:9092",CustomerSnapshot.Topic,"cjora-finance-bootstrap-v1",snapshotStore.ApplyAsync,errors);
        using var publisher=new OutboxPublisher(new CustomerOutbox(Env("CJORA_CORE_OUTBOX_CONNECTION")),"localhost:9092",CustomerSnapshot.Topic,
            new RoutingErrorRecorder(core,Path.Combine(Environment.CurrentDirectory,".local","finance-core-bootstrap-errors.jsonl")));
        await consumer.StartAsync(default);await publisher.StartAsync(default);
        try
        {
            bool ready=false;
            for(int i=0;i<120;i++)
            {
                await using var c=new MySqlConnection(sync);await c.OpenAsync();
                await using var count=Command(c,null,"SELECT COUNT(*) FROM CUSTOMER_REFERENCE WHERE PROGRAM_UUID=@program AND UUID IN (@source,@recipient) AND STATUS='ACTIVE'",
                    ("@program",program.ToString("D")),("@source",source.ToString("D")),("@recipient",recipient.ToString("D")));
                if(Convert.ToInt64(await count.ExecuteScalarAsync())==2){ready=true;break;}
                await Task.Delay(500);
            }
            if(!ready)throw new InvalidOperationException("DEMO_CUSTOMER_SYNC_NOT_READY");
        }
        finally{await publisher.StopAsync(default);await consumer.StopAsync(default);}
        var result=await SeedAccounts(Env("CJORA_TX_FINANCE_ADMIN"),program,source,recipient,currency,limit.MinorUnits,amount.MinorUnits);
        Directory.CreateDirectory(".local");
        await File.WriteAllTextAsync(Path.Combine(".local","finance-fixture.json"),JsonSerializer.Serialize(new{
            programUuid=program,sourceCustomerUuid=source,destinationCustomerUuid=recipient,
            sourceAccountUuid=result.Source,destinationAccountUuid=result.Destination,currency,
            maxTransferMinor=limit.MinorUnitsText,initialFundingMinor=amount.MinorUnitsText
        },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine("Explicit fictional accounts configured; existing policy/funding is preserved. Public fixture identifiers saved under ignored .local/finance-fixture.json.");
        return 0;
    }
    internal static async Task<(Guid Source,Guid Destination)> SeedAccounts(string connectionString,Guid program,Guid source,Guid destination,string currency,long max,long funding)
    {
        var actor=Guid.Parse("00000000-0000-0000-0000-000000000001");
        await using var c=new MySqlConnection(connectionString);await c.OpenAsync();await using var tx=await c.BeginTransactionAsync();
        await using(var policy=Command(c,tx,"SELECT IS_ENABLED,MAX_TRANSFER_MINOR FROM PROGRAM_FINANCIAL_POLICY WHERE PROGRAM_UUID=@program AND CURRENCY=@currency FOR UPDATE",("@program",program.ToString("D")),("@currency",currency)))
        await using(var r=await policy.ExecuteReaderAsync())
        {
            if(await r.ReadAsync())
            {
                if(!r.GetBoolean(0)||r.GetInt64(1)!=max)throw new InvalidOperationException("EXISTING_DEMO_POLICY_REQUIRES_EXPLICIT_REVIEW");
            }
            else
            {
                await r.DisposeAsync();
                var uuid=Guid.NewGuid();
                await using var create=Command(c,tx,"""
                    INSERT INTO PROGRAM_FINANCIAL_POLICY(UUID,PROGRAM_UUID,CURRENCY,IS_ENABLED,DEMO_ONLY,MAX_TRANSFER_MINOR,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
                    VALUES(@uuid,@program,@currency,1,1,@max,@actor,UTC_TIMESTAMP(6),@actor,UTC_TIMESTAMP(6))
                    """,("@uuid",uuid.ToString("D")),("@program",program.ToString("D")),("@currency",currency),("@max",max),("@actor",actor.ToString("D")));
                await create.ExecuteNonQueryAsync();
                await Audit(c,tx,Guid.NewGuid(),program,null,null,"DEMO_FINANCIAL_POLICY_CREATED","PROGRAM_FINANCIAL_POLICY",uuid,"CREATE","ENABLED","explicit-fictional-finance");
            }
        }
        async Task<(long Id,Guid Uuid,long Balance,long Sequence)> Account(Guid? customer,string type,string label)
        {
            await using var query=Command(c,tx,"SELECT ID,UUID,POSTED_MINOR,LAST_ENTRY_SEQUENCE FROM ACCOUNT WHERE PROGRAM_UUID=@program AND CURRENCY=@currency AND ACCOUNT_TYPE=@type AND CUSTOMER_UUID<=>@customer FOR UPDATE",
                ("@program",program.ToString("D")),("@currency",currency),("@type",type),("@customer",customer?.ToString("D")));
            await using(var r=await query.ExecuteReaderAsync())if(await r.ReadAsync())return(r.GetInt64(0),r.GetGuid(1),r.GetInt64(2),r.GetInt64(3));
            var uuid=Guid.NewGuid();
            await using var create=Command(c,tx,"""
                INSERT INTO ACCOUNT(UUID,PROGRAM_UUID,CUSTOMER_UUID,CURRENCY,ACCOUNT_TYPE,LABEL,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
                VALUES(@uuid,@program,@customer,@currency,@type,@label,@actor,UTC_TIMESTAMP(6),@actor,UTC_TIMESTAMP(6))
                """,("@uuid",uuid.ToString("D")),("@program",program.ToString("D")),("@customer",customer?.ToString("D")),
                ("@currency",currency),("@type",type),("@label",label),("@actor",actor.ToString("D")));
            await create.ExecuteNonQueryAsync();var id=create.LastInsertedId;
            await Audit(c,tx,Guid.NewGuid(),program,null,null,"DEMO_ACCOUNT_CREATED","ACCOUNT",uuid,"CREATE","ZERO_BALANCE","explicit-fictional-finance");
            return(id,uuid,0,0);
        }
        var sourceAccount=await Account(source,"CUSTOMER_LIABILITY","Fictional source");
        var destinationAccount=await Account(destination,"CUSTOMER_LIABILITY","Fictional recipient");
        var clearing=await Account(null,"DEMO_CLEARING_ASSET","Fictional clearing asset");
        var operation=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("cjora-demo-funding-v1:"+sourceAccount.Uuid+":"+currency)).AsSpan(0,16));
        await using(var existing=Command(c,tx,"SELECT DEBIT_TOTAL_MINOR FROM LEDGER_JOURNAL WHERE PROGRAM_UUID=@program AND OPERATION_UUID=@op",
            ("@program",program.ToString("D")),("@op",operation.ToString("D"))))
        {
            var previous=await existing.ExecuteScalarAsync();
            if(previous is not null)
            {
                if(Convert.ToInt64(previous)!=funding)throw new InvalidOperationException("EXISTING_DEMO_FUNDING_REQUIRES_EXPLICIT_REVIEW");
            }
            else
            {
                if(sourceAccount.Balance!=0||sourceAccount.Sequence!=0)throw new InvalidOperationException("NONEMPTY_DEMO_ACCOUNT_HAS_NO_FUNDING_RECEIPT");
                if(clearing.Balance>long.MaxValue-funding||clearing.Sequence==long.MaxValue)throw new InvalidOperationException("DEMO_FUNDING_OVERFLOW");
                var journal=Guid.NewGuid();
                await using var header=Command(c,tx,"""
                    INSERT INTO LEDGER_JOURNAL(UUID,PROGRAM_UUID,CURRENCY,OPERATION_UUID,JOURNAL_TYPE,DEBIT_TOTAL_MINOR,CREDIT_TOTAL_MINOR,POSTED_DATETIME,CREATED_BY_UUID,CREATED_DATETIME)
                    VALUES(@uuid,@program,@currency,@op,'DEMO_FUNDING',@amount,@amount,UTC_TIMESTAMP(6),@actor,UTC_TIMESTAMP(6))
                    """,("@uuid",journal.ToString("D")),("@program",program.ToString("D")),("@currency",currency),("@op",operation.ToString("D")),("@amount",funding),("@actor",actor.ToString("D")));
                await header.ExecuteNonQueryAsync();var journalId=header.LastInsertedId;
                await using var entries=Command(c,tx,"""
                    INSERT INTO LEDGER_ENTRY(UUID,JOURNAL_ID,ACCOUNT_ID,PROGRAM_UUID,CURRENCY,SIDE,AMOUNT_MINOR,ENTRY_SEQUENCE,CREATED_BY_UUID,CREATED_DATETIME)
                    VALUES(@debit,@journal,@clearing,@program,@currency,'DEBIT',@amount,@clearSequence,@actor,UTC_TIMESTAMP(6)),
                          (@credit,@journal,@source,@program,@currency,'CREDIT',@amount,1,@actor,UTC_TIMESTAMP(6))
                    """,("@debit",Guid.NewGuid().ToString("D")),("@credit",Guid.NewGuid().ToString("D")),("@journal",journalId),("@clearing",clearing.Id),("@source",sourceAccount.Id),
                    ("@program",program.ToString("D")),("@currency",currency),("@amount",funding),("@clearSequence",clearing.Sequence+1),("@actor",actor.ToString("D")));
                await entries.ExecuteNonQueryAsync();
                await using var update=Command(c,tx,"UPDATE ACCOUNT SET POSTED_MINOR=POSTED_MINOR+@amount,LAST_ENTRY_SEQUENCE=LAST_ENTRY_SEQUENCE+1,UPDATED_BY_UUID=@actor,UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE ID IN (@source,@clearing)",
                    ("@amount",funding),("@actor",actor.ToString("D")),("@source",sourceAccount.Id),("@clearing",clearing.Id));
                if(await update.ExecuteNonQueryAsync()!=2)throw new InvalidOperationException("DEMO_FUNDING_ACCOUNT_MISMATCH");
                await Audit(c,tx,operation,program,null,null,"EXPLICIT_DEMO_FUNDING_POSTED","LEDGER_JOURNAL",journal,"FUND","FICTIONAL_FUNDS","explicit-fictional-finance");
            }
        }
        await tx.CommitAsync();return(sourceAccount.Uuid,destinationAccount.Uuid);
    }
}
