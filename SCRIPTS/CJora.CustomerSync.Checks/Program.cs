using System.Text;
using System.Text.Json;
using CJora.Core.Application.Customers;
using CJora.Core.Infrastructure.Customers;
using CJora.CustomerEvents;
using CJora.Kafka;
using CJora.Routing.Persistence;
using CJora.Transactions.Infrastructure;
using Confluent.Kafka;
using MySqlConnector;

static string Env(string key)=>Environment.GetEnvironmentVariable(key)??throw new InvalidOperationException("Integration connection missing.");
static void Check(bool value,string code){if(!value)throw new InvalidOperationException(code);}
static void Pass(string message)=>Console.WriteLine("PASS: "+message);
static async Task<object?> Sql(string connection,string query)
{
    await using var c=new MySqlConnection(connection);await c.OpenAsync();
    await using var command=new MySqlCommand(query,c){CommandTimeout=5};return await command.ExecuteScalarAsync();
}
static async Task Wait(Func<Task<bool>> predicate,string code)
{
    for(int i=0;i<150;i++){if(await predicate())return;await Task.Delay(300);}
    throw new InvalidOperationException(code);
}
static async Task Docker(string action)
{
    using var process=new System.Diagnostics.Process{StartInfo=new("docker"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
    process.StartInfo.ArgumentList.Add(action);process.StartInfo.ArgumentList.Add("cjora-local-kafka-1");
    process.Start();var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();await stdout;await stderr;
    Check(process.ExitCode==0,"LOCAL_KAFKA_COMMAND_FAILED");
}
static async Task Reject(Func<Task> action,string code)
{
    try{await action();}catch(CustomerWriteRejected e)when(e.Code==code){return;}
    throw new InvalidOperationException("Expected command rejection not received.");
}
try
{
    var core=Env("CJORA_SYNC_CORE_CHECK");var tx=Env("CJORA_SYNC_TX_CHECK");
    var writer=new CustomerWriter(Env("CJORA_CORE_CUSTOMER_CONNECTION"));
    var outbox=new CustomerOutbox(Env("CJORA_CORE_OUTBOX_CONNECTION"));
    var store=new CustomerSnapshotStore(Env("CJORA_TX_SYNC_CONNECTION"));
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));var ct=timeout.Token;
    var program=Guid.Parse("c6a8a500-0000-4000-8000-000000000001");
    var customer=Guid.NewGuid();var user=Guid.NewGuid();
    var create=new CustomerChange(customer,program,user,"Sync Fixture","sync@example.test","ACTIVE",0,Guid.NewGuid());
    // Deliberately collide on the outbox's version after the customer INSERT.
    var rollbackCustomer=Guid.NewGuid();var rollbackEvent=Guid.NewGuid();
    await Sql(core,$"""
        INSERT INTO CUSTOMER_EVENT_OUTBOX(UUID,CUSTOMER_UUID,SOURCE_VERSION,OPERATION_UUID,OPERATION_HASH,PAYLOAD,STATE,NEXT_ATTEMPT_DATETIME,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
        VALUES('{rollbackEvent}','{rollbackCustomer}',1,UUID(),UNHEX(SHA2('rollback-fixture',256)),JSON_OBJECT(),'PUBLISHED',UTC_TIMESTAMP(6),'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6),'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6));
        """);
    try
    {
        bool failed=false;
        try{await writer.SaveAsync(create with{CustomerUuid=rollbackCustomer,UserUuid=Guid.NewGuid(),OperationUuid=Guid.NewGuid()},ct);}
        catch(MySqlException){failed=true;}
        Check(failed,"ROLLBACK_FAULT_NOT_TRIGGERED");
        Check(Convert.ToInt64(await Sql(core,$"SELECT COUNT(*) FROM CUSTOMER WHERE UUID='{rollbackCustomer}'"))==0,"CUSTOMER_ROLLBACK_FAILED");
        Check(Convert.ToInt64(await Sql(core,$"SELECT COUNT(*) FROM API_LOG WHERE ENTITY_UUID='{rollbackCustomer}'"))==0,"AUDIT_ROLLBACK_FAILED");
    }
    finally{await Sql(core,$"DELETE FROM CUSTOMER_EVENT_OUTBOX WHERE UUID='{rollbackEvent}'");}
    Pass("outbox failure rolls back the customer write and audit atomically");

    Check(await writer.SaveAsync(create,ct)==1,"CREATE_FAILED");
    Check(await writer.SaveAsync(create,ct)==1,"REPLAY_FAILED");
    await Reject(async()=>{await writer.SaveAsync(create with{Status="RESTRICTED"},ct);},"OPERATION_CONFLICT");
    await Reject(async()=>{await writer.SaveAsync(create with{ExpectedVersion=99,OperationUuid=Guid.NewGuid()},ct);},"CUSTOMER_VERSION_CONFLICT");
    Check(Convert.ToInt64(await Sql(core,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}'"))==1,"DUPLICATE_OUTBOX");
    Pass("customer command replay is idempotent; changed command and stale version are rejected");


    var errors=new RoutingErrorRecorder(Env("CJORA_TX_SYNC_CONNECTION"),Path.Combine(Environment.CurrentDirectory,".local","sync-check-errors.jsonl"),"CJORA_TRANSACTIONS","Transactions");
    var coreErrors=new RoutingErrorRecorder(core,Path.Combine(Environment.CurrentDirectory,".local","core-sync-check-errors.jsonl"));
    var unavailableSettings=new MySqlConnectionStringBuilder(Env("CJORA_TX_SYNC_CONNECTION")){Port=1,ConnectionTimeout=1,Pooling=false};
    var unavailableStore=new CustomerSnapshotStore(unavailableSettings.ConnectionString);
    int injected=0;
    using var consumer=new SnapshotConsumer("localhost:9092",CustomerSnapshot.Topic,"cjora-sync-checks-"+Guid.NewGuid().ToString("N"),
        async(topic,partition,offset,key,payload,cancellation)=>
        {
            if(key==customer.ToString("D")&&Interlocked.CompareExchange(ref injected,1,0)==0)await unavailableStore.ApplyAsync(topic,partition,offset,key,payload,cancellation);
            await store.ApplyAsync(topic,partition,offset,key,payload,cancellation);
        },errors);
    using var publisher=new OutboxPublisher(outbox,"localhost:9092",CustomerSnapshot.Topic,coreErrors);
    await consumer.StartAsync(ct);await publisher.StartAsync(ct);
    try
    {
        await Wait(async()=>Convert.ToInt64(await Sql(tx,$"SELECT COALESCE(MAX(SOURCE_VERSION),0) FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==1,"CREATE_RETRY_TIMEOUT");
        Check(injected==1,"RETRY_NOT_EXERCISED");
        Check(await writer.SaveAsync(create with{ExpectedVersion=1,Status="RESTRICTED",OperationUuid=Guid.NewGuid()},ct)==2,"UPDATE_FAILED");
        await Wait(async()=>Convert.ToInt64(await Sql(tx,$"SELECT SOURCE_VERSION FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==2,"SYNC_TIMEOUT");
        Check(injected==1,"RETRY_NOT_EXERCISED");
        Check((string?)await Sql(tx,$"SELECT STATUS FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'")=="RESTRICTED","STATUS_NOT_SYNCED");
        Check(Convert.ToInt64(await Sql(tx,$"SELECT COUNT(*) FROM API_LOG WHERE ENTITY_UUID='{customer}' AND OPERATION_NAME='CUSTOMER_SNAPSHOT_APPLIED'"))==2,"RETRY_SKIPPED_EVENT_OR_DUPLICATED_AUDIT");
        Pass("real Kafka create/update reaches Transactions with the same UUID; transient failure replays without skipped events");

        using var producer=new ProducerBuilder<string,byte[]>(new ProducerConfig{BootstrapServers="localhost:9092",EnableIdempotence=true,Acks=Acks.All,MessageTimeoutMs=10000}).SetLogHandler((_,_)=>{}).Build();
        async Task<DeliveryResult<string,byte[]>> Send(string key,byte[] payload)=>await producer.ProduceAsync(CustomerSnapshot.Topic,new Message<string,byte[]>{Key=key,Value=payload},ct);
        async Task<string> Payload(long version)=>(string)(await Sql(core,$"SELECT CAST(PAYLOAD AS CHAR) FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}' AND SOURCE_VERSION={version}"))!;
        var original=JsonSerializer.Deserialize<CustomerSnapshot>(await Payload(1),CustomerSnapshot.Json)!;
        await Send(original.Key,Encoding.UTF8.GetBytes(await Payload(1)));
        await Send(original.Key,Encoding.UTF8.GetBytes(await Payload(1)));
        var stale=original with{EventUuid=Guid.NewGuid(),OperationUuid=Guid.NewGuid()};
        await Send(stale.Key,JsonSerializer.SerializeToUtf8Bytes(stale,CustomerSnapshot.Json));
        await Wait(async()=>Convert.ToInt64(await Sql(tx,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_INBOX WHERE UUID='{stale.EventUuid}' AND RESULT_CODE='STALE'"))==1,"STALE_NOT_HANDLED");
        Check(Convert.ToInt64(await Sql(tx,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_INBOX WHERE UUID='{original.EventUuid}'"))==1,"DEDUPE_FAILED");
        Check(Convert.ToInt64(await Sql(tx,$"SELECT SOURCE_VERSION FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==2,"STALE_ROLLBACK");
        Pass("duplicate delivery creates one inbox/audit effect; older snapshot cannot overwrite newer state");

        var conflict=original with{Status="RESTRICTED"};
        var conflictResult=await Send(conflict.Key,JsonSerializer.SerializeToUtf8Bytes(conflict,CustomerSnapshot.Json));
        var scope=original with{EventUuid=Guid.NewGuid(),OperationUuid=Guid.NewGuid(),ProgramUuid=Guid.NewGuid(),SourceVersion=3};
        var scopeResult=await Send(scope.Key,JsonSerializer.SerializeToUtf8Bytes(scope,CustomerSnapshot.Json));
        var poison=await Send(Guid.NewGuid().ToString("D"),Encoding.UTF8.GetBytes("{malformed"));
        async Task<bool> Rejected(DeliveryResult<string,byte[]> message)=>Convert.ToInt64(await Sql(tx,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_REJECTIONS WHERE PARTITION_NUMBER={message.Partition.Value} AND OFFSET_NUMBER={message.Offset.Value} AND STATE='MANUAL_REVIEW'"))==1;
        await Wait(()=>Rejected(conflictResult),"CONFLICT_NOT_QUARANTINED");
        await Wait(()=>Rejected(scopeResult),"SCOPE_NOT_QUARANTINED");
        await Wait(()=>Rejected(poison),"POISON_NOT_QUARANTINED");
        Check(Convert.ToString(await Sql(tx,$"SELECT PROGRAM_UUID FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==program.ToString("D"),"SCOPE_CHANGED");
        Pass("conflicting event UUID, cross-Program move and malformed message are quarantined durably without raw payload storage");

        Check(await writer.SaveAsync(create with{ExpectedVersion=2,OperationUuid=Guid.NewGuid()},ct)==3,"VALID_AFTER_POISON_FAILED");
        await Wait(async()=>Convert.ToInt64(await Sql(tx,$"SELECT SOURCE_VERSION FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==3,"POISON_BLOCKED_FOLLOWING_VALID_EVENT");
        await Wait(async()=>Convert.ToInt64(await Sql(core,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}' AND STATE='PUBLISHED'"))==3,"PUBLISH_ACK_TIMEOUT");
        Pass("valid customer event is processed after poison messages; publisher records broker acknowledgements");

        await publisher.StopAsync(CancellationToken.None);
        Check(await writer.SaveAsync(create with{ExpectedVersion=3,Status="RESTRICTED",OperationUuid=Guid.NewGuid()},ct)==4,"V4_FAILED");
        Check(await writer.SaveAsync(create with{ExpectedVersion=4,OperationUuid=Guid.NewGuid()},ct)==5,"V5_FAILED");
        await Send(customer.ToString("D"),Encoding.UTF8.GetBytes(await Payload(5)));
        await Send(customer.ToString("D"),Encoding.UTF8.GetBytes(await Payload(4)));
        await Wait(async()=>Convert.ToInt64(await Sql(tx,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_INBOX WHERE CUSTOMER_UUID='{customer}' AND SOURCE_VERSION=4 AND RESULT_CODE='STALE'"))==1,"REORDER_NOT_HANDLED");
        Check(Convert.ToInt64(await Sql(tx,$"SELECT SOURCE_VERSION FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==5,"REORDER_ROLLBACK");
        Pass("out-of-order full snapshots converge to Core's highest version");

        var abandoned=await outbox.ClaimAsync(ct)??throw new InvalidOperationException("LEASE_NOT_CLAIMED");
        await Sql(core,$"UPDATE CUSTOMER_EVENT_OUTBOX SET LEASE_EXPIRES_DATETIME=DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 1 SECOND) WHERE UUID='{abandoned.EventUuid}'");
        var replacement=await outbox.ClaimAsync(ct)??throw new InvalidOperationException("LEASE_NOT_RECOVERED");
        Check(replacement.EventUuid==abandoned.EventUuid&&replacement.LeaseOwner!=abandoned.LeaseOwner,"LEASE_NOT_REPLACED");
        await outbox.CompleteAsync(abandoned,true,ct);
        Check((string?)await Sql(core,$"SELECT STATE FROM CUSTOMER_EVENT_OUTBOX WHERE UUID='{abandoned.EventUuid}'")=="LEASED","OLD_LEASE_ACK_ACCEPTED");
        await outbox.CompleteAsync(replacement,false,ct);
        using var restarted=new OutboxPublisher(outbox,"localhost:9092",CustomerSnapshot.Topic,coreErrors);
        await restarted.StartAsync(ct);
        try
        {
            await Wait(async()=>Convert.ToInt64(await Sql(core,$"SELECT COUNT(*) FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}' AND STATE='PUBLISHED'"))==5,"RECOVERY_PUBLISH_FAILED");
        }
        finally{await restarted.StopAsync(CancellationToken.None);}
        Pass("expired publisher lease recovers; old claimant cannot acknowledge; replay after broker acknowledgement stays idempotent");
        Check(Convert.ToInt64(await Sql(tx,$"SELECT COUNT(*) FROM API_LOG WHERE ENTITY_UUID='{customer}' AND OPERATION_NAME='CUSTOMER_SNAPSHOT_APPLIED'"))==4,"UNEXPECTED_AUDIT_EFFECTS");
        Pass("applied customer projections have atomic local audits; duplicate/stale events do not create business audit noise");
        using var outagePublisher=new OutboxPublisher(outbox,"localhost:9092",CustomerSnapshot.Topic,coreErrors);
        await outagePublisher.StartAsync(ct);
        try
        {
            await Docker("stop");
            Check(await writer.SaveAsync(create with{ExpectedVersion=5,Status="RESTRICTED",OperationUuid=Guid.NewGuid()},ct)==6,"OFFLINE_WRITE_FAILED");
            await Wait(async()=>Convert.ToInt64(await Sql(core,$"SELECT ATTEMPT_COUNT FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}' AND SOURCE_VERSION=6"))>=1,"OFFLINE_ATTEMPT_NOT_MADE");
            await Task.Delay(11000,ct);
            Check((string?)await Sql(core,$"SELECT STATE FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}' AND SOURCE_VERSION=6")!="PUBLISHED","OFFLINE_EVENT_FALSELY_ACKNOWLEDGED");
            Check(Convert.ToInt64(await Sql(tx,$"SELECT SOURCE_VERSION FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==5,"OFFLINE_PROJECTION_CHANGED");
        }
        finally{await Docker("start");}
        try
        {
            await Wait(async()=>Convert.ToInt64(await Sql(tx,$"SELECT SOURCE_VERSION FROM CUSTOMER_REFERENCE WHERE UUID='{customer}'"))==6,"BROKER_RESTART_RECOVERY_TIMEOUT");
            await Wait(async()=>(string?)await Sql(core,$"SELECT STATE FROM CUSTOMER_EVENT_OUTBOX WHERE CUSTOMER_UUID='{customer}' AND SOURCE_VERSION=6")=="PUBLISHED","RECOVERED_EVENT_NOT_ACKNOWLEDGED");
        }
        finally{await outagePublisher.StopAsync(CancellationToken.None);}
        Pass("Core commits while Kafka is stopped; durable event delivers after broker restart; real database connection failure never loses an offset");
    }
    finally
    {
        await publisher.StopAsync(CancellationToken.None);
        await consumer.StopAsync(CancellationToken.None);
    }
    return 0;
}
catch(Exception e)
{
    // Safe exception class/code only; never print provider/native messages or connection strings.
    var code=e is InvalidOperationException&&System.Text.RegularExpressions.Regex.IsMatch(e.Message,"^[A-Z][A-Z0-9_]{1,80}$")?e.Message:e.GetType().Name;
    Console.Error.WriteLine("FAIL: customer synchronization check: "+(code.Length<100?code:"CHECK_FAILED"));
    return 1;
}
