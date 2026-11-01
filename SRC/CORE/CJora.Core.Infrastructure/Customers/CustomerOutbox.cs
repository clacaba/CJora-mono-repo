using CJora.Kafka;
using MySqlConnector;
using System.Text;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.Core.Infrastructure.Customers;

public sealed class CustomerOutbox(string connectionString):IEventOutbox
{
    private const string Actor="00000000-0000-0000-0000-000000000001";
    private async Task<MySqlConnection> Open()
    {
        if(new MySqlConnectionStringBuilder(connectionString).Database!="CJORA_CORE")throw new InvalidOperationException("Wrong outbox database.");
        var c=new MySqlConnection(connectionString);try{await c.OpenAsync();return c;}catch{await c.DisposeAsync();throw;}
    }
    public async Task<OutboundEvent?> ClaimAsync(CancellationToken ct)
    {
        await using var c=await Open();await using var tx=await c.BeginTransactionAsync(ct);
        await using(var exhausted=Command(c,tx,"""
            UPDATE CUSTOMER_EVENT_OUTBOX SET STATE='MANUAL_REVIEW',LEASE_OWNER_UUID=NULL,LEASE_EXPIRES_DATETIME=NULL,
            UPDATED_BY_UUID=@actor,UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1
            WHERE ATTEMPT_COUNT>=10 AND (STATE='PENDING' OR (STATE='LEASED' AND LEASE_EXPIRES_DATETIME<UTC_TIMESTAMP(6)))
            """,("@actor",Actor)))await exhausted.ExecuteNonQueryAsync(ct);
        Guid uuid;string key,payload;
        await using(var select=Command(c,tx,"""
            SELECT UUID,CUSTOMER_UUID,CAST(PAYLOAD AS CHAR) FROM CUSTOMER_EVENT_OUTBOX
            WHERE ATTEMPT_COUNT<10 AND ((STATE='PENDING' AND NEXT_ATTEMPT_DATETIME<=UTC_TIMESTAMP(6)) OR (STATE='LEASED' AND LEASE_EXPIRES_DATETIME<UTC_TIMESTAMP(6)))
            ORDER BY ID LIMIT 1 FOR UPDATE SKIP LOCKED
            """))
        await using(var r=await select.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct)){await r.DisposeAsync();await tx.CommitAsync(ct);return null;}
            uuid=r.GetGuid(0);key=r.GetGuid(1).ToString("D");payload=r.GetString(2);
        }
        var owner=Guid.NewGuid();
        await using(var claim=Command(c,tx,"""
            UPDATE CUSTOMER_EVENT_OUTBOX SET STATE='LEASED',LEASE_OWNER_UUID=@owner,LEASE_EXPIRES_DATETIME=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 SECOND),
            ATTEMPT_COUNT=ATTEMPT_COUNT+1,UPDATED_BY_UUID=@actor,UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID=@uuid
            """,("@owner",owner.ToString("D")),("@uuid",uuid.ToString("D")),("@actor",Actor)))await claim.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);return new(uuid,owner,key,Encoding.UTF8.GetBytes(payload));
    }
    public async Task CompleteAsync(OutboundEvent message,bool published,CancellationToken ct)
    {
        await using var c=await Open();
        await using var command=Command(c,null,"""
            UPDATE CUSTOMER_EVENT_OUTBOX SET STATE=IF(@success,'PUBLISHED',IF(ATTEMPT_COUNT>=10,'MANUAL_REVIEW','PENDING')),
            PUBLISHED_DATETIME=IF(@success,UTC_TIMESTAMP(6),NULL),NEXT_ATTEMPT_DATETIME=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 5 SECOND),
            LEASE_OWNER_UUID=NULL,LEASE_EXPIRES_DATETIME=NULL,UPDATED_BY_UUID=@actor,UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1
            WHERE UUID=@uuid AND STATE='LEASED' AND LEASE_OWNER_UUID=@owner
            """,("@success",published),("@actor",Actor),("@uuid",message.EventUuid.ToString("D")),("@owner",message.LeaseOwner.ToString("D")));
        await command.ExecuteNonQueryAsync(ct);
    }
}
