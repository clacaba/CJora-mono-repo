using System.Security.Cryptography;
using System.Text.Json;
using CJora.CustomerEvents;
using CJora.Transactions.Application;
using CJora.Transactions.Domain;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.Transactions.Infrastructure;

public sealed class CustomerSnapshotStore(string connectionString):ICustomerSnapshotStore
{
    private const string Actor="00000000-0000-0000-0000-000000000001";
    private async Task<MySqlConnection> Open()
    {
        if(new MySqlConnectionStringBuilder(connectionString).Database!="CJORA_TRANSACTIONS")throw new InvalidOperationException("Wrong projection database.");
        var c=new MySqlConnection(connectionString);try{await c.OpenAsync();return c;}catch{await c.DisposeAsync();throw;}
    }
    public async Task ApplyAsync(string topic,int partition,long offset,string? key,byte[]? payload,CancellationToken ct)
    {
        if(topic!=CustomerSnapshot.Topic||partition<0||offset<0)throw new InvalidOperationException("Invalid consumer metadata.");
        try {await Apply(key,payload,ct);}
        catch(InvalidCustomerEvent e){await Reject(topic,partition,offset,payload,e.Code,ct);}
    }
    private async Task Apply(string? key,byte[]? payload,CancellationToken ct)
    {
        var e=CustomerSnapshot.Parse(key,payload);
        var hash=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e,CustomerSnapshot.Json));
        await using var c=await Open();await using var tx=await c.BeginTransactionAsync(ct);
        await using(var duplicate=Command(c,tx,"SELECT PAYLOAD_HASH FROM CUSTOMER_EVENT_INBOX WHERE UUID=@uuid",("@uuid",e.EventUuid.ToString("D"))))
        {
            var previous=await duplicate.ExecuteScalarAsync(ct) as byte[];
            if(previous is not null)
            {
                if(!CryptographicOperations.FixedTimeEquals(previous,hash))throw new InvalidCustomerEvent("EVENT_UUID_CONFLICT");
                return;
            }
        }
        CustomerReference? current=null;
        await using(var command=Command(c,tx,"SELECT UUID,PROGRAM_UUID,USER_UUID,SOURCE_VERSION,STATUS FROM CUSTOMER_REFERENCE WHERE UUID=@uuid FOR UPDATE",("@uuid",e.Key)))
        await using(var r=await command.ExecuteReaderAsync(ct))
            if(await r.ReadAsync(ct))current=new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetInt64(3),r.GetString(4));
        var decision=CustomerProjection.Decide(current,new(e.CustomerUuid,e.ProgramUuid,e.UserUuid,e.SourceVersion,e.Status));
        if(decision==SnapshotDecision.Conflict)throw new InvalidCustomerEvent("CUSTOMER_SCOPE_OR_VERSION_CONFLICT");
        if(decision==SnapshotDecision.Apply)
        {
            var sql=current is null?"""
                INSERT INTO CUSTOMER_REFERENCE(UUID,PROGRAM_UUID,USER_UUID,STATUS,SOURCE_VERSION,LAST_EVENT_UUID,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
                VALUES(@uuid,@program,@user,@status,@version,@event,@actor,UTC_TIMESTAMP(6),@actor,UTC_TIMESTAMP(6))
                """:"""
                UPDATE CUSTOMER_REFERENCE SET STATUS=@status,SOURCE_VERSION=@version,LAST_EVENT_UUID=@event,
                UPDATED_BY_UUID=@actor,UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID=@uuid
                """;
            await using var write=Command(c,tx,sql,("@uuid",e.Key),("@program",e.ProgramUuid.ToString("D")),("@user",e.UserUuid.ToString("D")),
                ("@status",e.Status),("@version",e.SourceVersion),("@event",e.EventUuid.ToString("D")),("@actor",Actor));
            await write.ExecuteNonQueryAsync(ct);
            await Audit(c,tx,e.OperationUuid,e.ProgramUuid,null,null,"CUSTOMER_SNAPSHOT_APPLIED","CUSTOMER_REFERENCE",e.CustomerUuid,
                current is null?"CREATE":"UPDATE",e.Status,e.EventUuid.ToString("D"));
        }
        await using(var inbox=Command(c,tx,"""
            INSERT INTO CUSTOMER_EVENT_INBOX(UUID,CUSTOMER_UUID,SOURCE_VERSION,PAYLOAD_HASH,RESULT_CODE,CREATED_DATETIME)
            VALUES(@uuid,@customer,@version,@hash,@result,UTC_TIMESTAMP(6))
            """,("@uuid",e.EventUuid.ToString("D")),("@customer",e.Key),("@version",e.SourceVersion),("@hash",hash),("@result",decision==SnapshotDecision.Apply?"APPLIED":"STALE")))
            await inbox.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
    private async Task Reject(string topic,int partition,long offset,byte[]? payload,string code,CancellationToken ct)
    {
        await using var c=await Open();await using var tx=await c.BeginTransactionAsync(ct);
        await using(var check=Command(c,tx,"SELECT COUNT(*) FROM CUSTOMER_EVENT_REJECTIONS WHERE TOPIC=@topic AND PARTITION_NUMBER=@partition AND OFFSET_NUMBER=@offset",
            ("@topic",topic),("@partition",partition),("@offset",offset)))
            if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))>0)return;
        var uuid=Guid.NewGuid();
        await using(var reject=Command(c,tx,"""
            INSERT INTO CUSTOMER_EVENT_REJECTIONS(UUID,TOPIC,PARTITION_NUMBER,OFFSET_NUMBER,PAYLOAD_HASH,ERROR_CODE,STATE,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES(@uuid,@topic,@partition,@offset,@hash,@code,'MANUAL_REVIEW',@actor,UTC_TIMESTAMP(6),@actor,UTC_TIMESTAMP(6))
            """,("@uuid",uuid.ToString("D")),("@topic",topic),("@partition",partition),("@offset",offset),("@hash",SHA256.HashData(payload??[])),("@code",code),("@actor",Actor)))
            await reject.ExecuteNonQueryAsync(ct);
        await Audit(c,tx,uuid,null,null,null,"CUSTOMER_EVENT_REJECTED","CUSTOMER_EVENT_REJECTIONS",uuid,"SYNC","REJECTED",uuid.ToString("D"),"DENIED");
        await using(var error=Command(c,tx,"""
            INSERT INTO ERROR_LOGS(UUID,CORRELATION_ID,SERVICE_NAME,ERROR_CODE,ERROR_CATEGORY,SEVERITY,SAFE_MESSAGE,CREATED_DATETIME)
            VALUES(@uuid,@reference,'Transactions',@code,'VALIDATION','ERROR','Customer event quarantined; raw payload excluded.',UTC_TIMESTAMP(6))
            """,("@uuid",Guid.NewGuid().ToString("D")),("@reference",uuid.ToString("D")),("@code",code)))await error.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
