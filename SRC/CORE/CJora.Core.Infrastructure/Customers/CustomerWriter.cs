using System.Security.Cryptography;
using System.Text.Json;
using CJora.Core.Application.Customers;
using CJora.CustomerEvents;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.Core.Infrastructure.Customers;

public sealed class CustomerWriter(string connectionString):ICustomerWriter
{
    private const string Actor="00000000-0000-0000-0000-000000000001";
    private async Task<MySqlConnection> Open()
    {
        if(new MySqlConnectionStringBuilder(connectionString).Database!="CJORA_CORE")throw new CustomerWriteRejected("WRONG_DATABASE");
        var c=new MySqlConnection(connectionString);try{await c.OpenAsync();return c;}catch{await c.DisposeAsync();throw;}
    }
    public async Task<long> SaveAsync(CustomerChange change,CancellationToken cancellationToken)
    {
        if(!change.IsValid())throw new CustomerWriteRejected("INVALID_CUSTOMER_CHANGE");
        var fingerprint=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(change,CustomerSnapshot.Json));
        await using var c=await Open();await using var tx=await c.BeginTransactionAsync(cancellationToken);
        await using(var check=Command(c,tx,"SELECT SOURCE_VERSION,OPERATION_HASH FROM CUSTOMER_EVENT_OUTBOX WHERE OPERATION_UUID=@op",("@op",change.OperationUuid.ToString("D"))))
        await using(var reader=await check.ExecuteReaderAsync(cancellationToken))
        {
            if(await reader.ReadAsync(cancellationToken))
            {
                if(!CryptographicOperations.FixedTimeEquals((byte[])reader[1],fingerprint))throw new CustomerWriteRejected("OPERATION_CONFLICT");
                return reader.GetInt64(0);
            }
        }
        long programId;
        await using(var program=Command(c,tx,"SELECT ID FROM PROGRAM WHERE UUID=@uuid AND IS_ENABLED=1",("@uuid",change.ProgramUuid.ToString("D"))))
            programId=Convert.ToInt64(await program.ExecuteScalarAsync(cancellationToken)??throw new CustomerWriteRejected("PROGRAM_UNAVAILABLE"));
        long version=0;Guid? programUuid=null,userUuid=null;
        await using(var select=Command(c,tx,"SELECT P.UUID,C.USER_UUID,C.ROW_VERSION FROM CUSTOMER C JOIN PROGRAM P ON P.ID=C.PROGRAM_ID WHERE C.UUID=@uuid FOR UPDATE OF C",("@uuid",change.CustomerUuid.ToString("D"))))
        await using(var reader=await select.ExecuteReaderAsync(cancellationToken))
        {
            if(await reader.ReadAsync(cancellationToken)){programUuid=reader.GetGuid(0);userUuid=reader.GetGuid(1);version=reader.GetInt64(2);}
        }
        if(version!=change.ExpectedVersion)throw new CustomerWriteRejected("CUSTOMER_VERSION_CONFLICT");
        if(version>0&&(programUuid!=change.ProgramUuid||userUuid!=change.UserUuid))throw new CustomerWriteRejected("CUSTOMER_SCOPE_IMMUTABLE");
        var now=DateTime.UtcNow;var next=checked(version+1);
        var sql=version==0?"""
            INSERT INTO CUSTOMER(UUID,PROGRAM_ID,USER_UUID,DISPLAY_NAME,EMAIL,STATUS,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES(@uuid,@program,@user,@name,@email,@status,@actor,@now,@actor,@now)
            """:"""
            UPDATE CUSTOMER SET DISPLAY_NAME=@name,EMAIL=@email,STATUS=@status,UPDATED_BY_UUID=@actor,UPDATED_DATETIME=@now,ROW_VERSION=ROW_VERSION+1
            WHERE UUID=@uuid AND ROW_VERSION=@version
            """;
        await using(var command=Command(c,tx,sql,("@uuid",change.CustomerUuid.ToString("D")),("@program",programId),("@user",change.UserUuid.ToString("D")),
            ("@name",change.DisplayName),("@email",change.Email),("@status",change.Status),("@actor",Actor),("@now",now),("@version",version)))
        {
            try {if(await command.ExecuteNonQueryAsync(cancellationToken)!=1)throw new CustomerWriteRejected("CUSTOMER_VERSION_CONFLICT");}
            catch(MySqlException e)when(e.Number==1062){throw new CustomerWriteRejected("CUSTOMER_CONFLICT");}
        }
        var snapshot=new CustomerSnapshot(Guid.NewGuid(),change.OperationUuid,1,"core.customer.snapshot.v1",change.CustomerUuid,change.ProgramUuid,change.UserUuid,next,change.Status,now);
        await Enqueue(c,tx,snapshot,fingerprint,cancellationToken);
        await Audit(c,tx,change.OperationUuid,change.ProgramUuid,change.UserUuid,null,version==0?"CUSTOMER_CREATED":"CUSTOMER_UPDATED","CUSTOMER",change.CustomerUuid,
            version==0?"CREATE":"UPDATE",change.Status,change.OperationUuid.ToString("D"),workloadActor:Guid.Parse(Actor));
        await tx.CommitAsync(cancellationToken);return next;
    }
    private static async Task Enqueue(MySqlConnection c,MySqlTransaction tx,CustomerSnapshot e,byte[] fingerprint,CancellationToken ct)
    {
        await using var command=Command(c,tx,"""
            INSERT INTO CUSTOMER_EVENT_OUTBOX(UUID,CUSTOMER_UUID,SOURCE_VERSION,OPERATION_UUID,OPERATION_HASH,PAYLOAD,STATE,NEXT_ATTEMPT_DATETIME,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES(@uuid,@customer,@version,@op,@hash,@payload,'PENDING',@now,@actor,@now,@actor,@now)
            """,("@uuid",e.EventUuid.ToString("D")),("@customer",e.Key),("@version",e.SourceVersion),("@op",e.OperationUuid.ToString("D")),
            ("@hash",fingerprint),("@payload",JsonSerializer.Serialize(e,CustomerSnapshot.Json)),("@now",DateTime.UtcNow),("@actor",Actor));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        // Bounded batches; rerunning never duplicates the current customer/version.
        int count=0;
        while(true)
        {
            await using var c=await Open();await using var tx=await c.BeginTransactionAsync(ct);
            var list=new List<CustomerSnapshot>();
            await using(var command=Command(c,tx,"""
                SELECT C.UUID,P.UUID,C.USER_UUID,C.ROW_VERSION,C.STATUS FROM CUSTOMER C JOIN PROGRAM P ON P.ID=C.PROGRAM_ID
                WHERE NOT EXISTS(SELECT 1 FROM CUSTOMER_EVENT_OUTBOX O WHERE O.CUSTOMER_UUID=C.UUID AND O.SOURCE_VERSION=C.ROW_VERSION)
                LIMIT 100 FOR UPDATE OF C SKIP LOCKED
                """))
            await using(var reader=await command.ExecuteReaderAsync(ct))
                while(await reader.ReadAsync(ct))list.Add(new(Guid.NewGuid(),Guid.NewGuid(),1,"core.customer.snapshot.v1",reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetInt64(3),reader.GetString(4),DateTime.UtcNow));
            foreach(var e in list)
            {
                await Enqueue(c,tx,e,SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e,CustomerSnapshot.Json)),ct);
                await Audit(c,tx,e.OperationUuid,e.ProgramUuid,null,null,"CUSTOMER_SNAPSHOT_QUEUED","CUSTOMER",e.CustomerUuid,"SYNC",e.Status,e.OperationUuid.ToString("D"));
            }
            await tx.CommitAsync(ct);count+=list.Count;if(list.Count<100)return count;
        }
    }
}
