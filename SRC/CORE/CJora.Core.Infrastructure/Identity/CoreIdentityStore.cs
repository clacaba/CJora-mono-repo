using System.Security.Cryptography;
using CJora.Core.Application.Identity;
using CJora.Identity.Contracts;
using CJora.Routing.Persistence;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.Core.Infrastructure.Identity;

public sealed class CoreIdentityStore(string connectionString,TimeProvider clock) : ICoreIdentityStore
{
    private async Task<MySqlConnection> Open()
    {
        var settings=new MySqlConnectionStringBuilder(connectionString);
        if (settings.Database!="CJORA_CORE") throw new IdentityFailure(503,"CORE_DATABASE_UNAVAILABLE");
        var connection=new MySqlConnection(connectionString);
        try {await connection.OpenAsync();return connection;}
        catch {await connection.DisposeAsync();throw;}
    }
    private DateTime Now=>clock.GetUtcNow().UtcDateTime;
    public async Task<ProgramInfo?> ResolveProgramAsync(string key)
    {
        await using var connection=await Open();
        await using var command=Command(connection,null,"SELECT UUID,PROGRAM_KEY,DISPLAY_NAME FROM PROGRAM WHERE PROGRAM_KEY=@key AND IS_ENABLED=1",("@key",key));
        await using var reader=await command.ExecuteReaderAsync();
        return await reader.ReadAsync()?new ProgramInfo(reader.GetGuid(0),reader.GetString(1),reader.GetString(2)):null;
    }
    public async Task<CoreSessionReference> PrepareAsync(PrepareSession request,string correlation)
    {
        var now=Now;
        if (request.AuthSessionUuid==Guid.Empty || request.OperationUuid==Guid.Empty || request.ExpiresAtUtc<=now || request.ExpiresAtUtc>now.Add(IdentityProtocol.AbsoluteTimeout))
            throw new IdentityFailure(400,"INVALID_SESSION_REQUEST");
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        long customerId,programId;
        await using (var select=Command(connection,transaction,"""
            SELECT C.ID,P.ID FROM CUSTOMER C JOIN PROGRAM P ON P.ID=C.PROGRAM_ID
            WHERE P.UUID=@program AND P.IS_ENABLED=1 AND C.USER_UUID=@user AND C.STATUS='ACTIVE'
            """,("@program",request.ProgramUuid.ToString("D")),("@user",request.UserUuid.ToString("D"))))
        await using (var reader=await select.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new IdentityFailure(403,"CUSTOMER_ACCESS_DENIED");
            customerId=reader.GetInt64(0); programId=reader.GetInt64(1);
        }
        await using (var existing=Command(connection,transaction,"SELECT UUID,AUTH_SESSION_UUID FROM CORE_SESSION WHERE OPERATION_UUID=@op",("@op",request.OperationUuid.ToString("D"))))
        await using (var reader=await existing.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                if (reader.GetGuid(1)!=request.AuthSessionUuid) throw new IdentityFailure(409,"SESSION_OPERATION_CONFLICT");
                return new CoreSessionReference(reader.GetGuid(0));
            }
        }
        var uuid=Guid.NewGuid();
        await using var insert=Command(connection,transaction,"""
            INSERT INTO CORE_SESSION (UUID,PROGRAM_ID,CUSTOMER_ID,USER_UUID,AUTH_SESSION_UUID,OPERATION_UUID,STATE,LAST_ACTIVITY_DATETIME,EXPIRES_DATETIME,
                CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES (@uuid,@program,@customer,@user,@auth,@op,'PENDING',@now,@expires,@user,@now,@user,@now)
            """,("@uuid",uuid.ToString("D")),("@program",programId),("@customer",customerId),("@user",request.UserUuid.ToString("D")),
            ("@auth",request.AuthSessionUuid.ToString("D")),("@op",request.OperationUuid.ToString("D")),("@now",now),("@expires",request.ExpiresAtUtc));
        await insert.ExecuteNonQueryAsync();
        await Audit(connection,transaction,request.OperationUuid,request.ProgramUuid,request.UserUuid,uuid,"CORE_SESSION_CREATED","CORE_SESSION",uuid,"CREATE","PENDING",correlation);
        await transaction.CommitAsync(); return new CoreSessionReference(uuid);
    }
    private const string ScopeSql="""
        SELECT S.UUID,S.AUTH_SESSION_UUID,S.USER_UUID,P.UUID,C.UUID,C.DISPLAY_NAME,C.EMAIL,S.STATE,P.IS_ENABLED,C.STATUS,S.LAST_ACTIVITY_DATETIME,S.EXPIRES_DATETIME
        FROM CORE_SESSION S JOIN PROGRAM P ON P.ID=S.PROGRAM_ID JOIN CUSTOMER C ON C.ID=S.CUSTOMER_ID AND C.PROGRAM_ID=S.PROGRAM_ID AND C.USER_UUID=S.USER_UUID
        """;
    private static CoreIdentitySession Scope(MySqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetGuid(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetBoolean(8),r.GetString(9),Utc(r,10),Utc(r,11));
    public async Task<CoreIdentitySession?> ReadAsync(byte[] secretHash)
    {
        await using var connection=await Open(); await using var command=Command(connection,null,ScopeSql+" WHERE S.SESSION_SECRET_HASH=@hash",("@hash",secretHash));
        await using var reader=await command.ExecuteReaderAsync(); return await reader.ReadAsync()?Scope(reader):null;
    }
    public async Task<CoreIdentitySession?> ReadByUuidAsync(Guid uuid)
    {
        await using var connection=await Open(); await using var command=Command(connection,null,ScopeSql+" WHERE S.UUID=@uuid",("@uuid",uuid.ToString("D")));
        await using var reader=await command.ExecuteReaderAsync(); return await reader.ReadAsync()?Scope(reader):null;
    }
    public async Task<SessionCookie> ActivateAsync(ActivateSession request,string correlation)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        CoreIdentitySession session;
        await using (var select=Command(connection,transaction,ScopeSql+" WHERE S.UUID=@uuid AND S.OPERATION_UUID=@op FOR UPDATE OF S",("@uuid",request.CoreSessionUuid.ToString("D")),("@op",request.OperationUuid.ToString("D"))))
        await using (var reader=await select.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new IdentityFailure(403,"SESSION_LINK_INVALID");
            session=Scope(reader);
        }
        if (session.AuthSessionUuid!=request.AuthSessionUuid || Now>=session.ExpiresAtUtc) throw new IdentityFailure(403,"SESSION_LINK_INVALID");
        if (session.State=="ACTIVE") return new SessionCookie(session.SessionUuid,null,session.ExpiresAtUtc);
        if (session.State!="PENDING") throw new IdentityFailure(401,"SESSION_REVOKED");
        var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); var now=Now;
        await using var update=Command(connection,transaction,"""
            UPDATE CORE_SESSION SET STATE='ACTIVE',SESSION_SECRET_HASH=@hash,ACTIVATED_DATETIME=@now,LAST_ACTIVITY_DATETIME=@now,
            UPDATED_DATETIME=@now,UPDATED_BY_UUID=@user,ROW_VERSION=ROW_VERSION+1 WHERE UUID=@uuid AND STATE='PENDING'
            """,("@hash",SessionSecrets.Hash(secret)),("@now",now),("@user",session.UserUuid.ToString("D")),("@uuid",session.SessionUuid.ToString("D")));
        if (await update.ExecuteNonQueryAsync()!=1) throw new IdentityFailure(409,"SESSION_OPERATION_CONFLICT");
        await Audit(connection,transaction,request.OperationUuid,session.ProgramUuid,session.UserUuid,session.SessionUuid,"CORE_SESSION_ACTIVATED","CORE_SESSION",session.SessionUuid,"UPDATE","ACTIVE",correlation);
        await transaction.CommitAsync(); return new SessionCookie(session.SessionUuid,secret,session.ExpiresAtUtc);
    }
    public async Task TouchAsync(Guid sessionUuid)
    {
        await using var connection=await Open(); await using var command=Command(connection,null,"UPDATE CORE_SESSION SET LAST_ACTIVITY_DATETIME=@now WHERE UUID=@uuid AND STATE='ACTIVE' AND EXPIRES_DATETIME>@now",("@now",Now),("@uuid",sessionUuid.ToString("D")));
        await command.ExecuteNonQueryAsync();
    }
    public async Task RevokeAsync(Guid authSessionUuid,Guid operation,string correlation,bool queueIdentity)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        long id; Guid uuid,user,program; string state;
        await using (var select=Command(connection,transaction,"""
            SELECT S.ID,S.UUID,S.USER_UUID,P.UUID,S.STATE FROM CORE_SESSION S JOIN PROGRAM P ON P.ID=S.PROGRAM_ID WHERE S.AUTH_SESSION_UUID=@auth FOR UPDATE OF S
            """,("@auth",authSessionUuid.ToString("D"))))
        await using (var reader=await select.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return;
            id=reader.GetInt64(0); uuid=reader.GetGuid(1); user=reader.GetGuid(2); program=reader.GetGuid(3); state=reader.GetString(4);
        }
        var now=Now;
        if (state!="REVOKED")
        {
            await using var update=Command(connection,transaction,"""
                UPDATE CORE_SESSION SET STATE='REVOKED',REVOKED_DATETIME=@now,REVOCATION_REASON_CODE='LOGOUT_OR_RECOVERY',UPDATED_DATETIME=@now,UPDATED_BY_UUID=@user,ROW_VERSION=ROW_VERSION+1 WHERE ID=@id
                """,("@now",now),("@user",user.ToString("D")),("@id",id));
            await update.ExecuteNonQueryAsync();
            await Audit(connection,transaction,operation,program,user,uuid,"CORE_SESSION_REVOKED","CORE_SESSION",uuid,"UPDATE","REVOKED",correlation);
        }
        if (queueIdentity)
        {
            await using var queue=Command(connection,transaction,"""
                INSERT INTO SESSION_REVOCATION_OUTBOX (UUID,CORE_SESSION_ID,AUTH_SESSION_UUID,OPERATION_UUID,STATE,NEXT_ATTEMPT_DATETIME,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
                VALUES (@uuid,@id,@auth,@op,'PENDING',@now,@user,@now,@user,@now)
                ON DUPLICATE KEY UPDATE STATE='PENDING',NEXT_ATTEMPT_DATETIME=@now
                """,("@uuid",Guid.NewGuid().ToString("D")),("@id",id),("@auth",authSessionUuid.ToString("D")),("@op",operation.ToString("D")),("@now",now),("@user",user.ToString("D")));
            await queue.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
    public async Task<IReadOnlyList<PendingRevocation>> PendingRevocationsAsync()
    {
        await using var connection=await Open(); await using var command=Command(connection,null,"""
            SELECT O.UUID,O.AUTH_SESSION_UUID,S.UUID,O.OPERATION_UUID FROM SESSION_REVOCATION_OUTBOX O JOIN CORE_SESSION S ON S.ID=O.CORE_SESSION_ID
            WHERE O.STATE='PENDING' AND O.NEXT_ATTEMPT_DATETIME<=UTC_TIMESTAMP(6) LIMIT 20
            """);
        await using var reader=await command.ExecuteReaderAsync(); var result=new List<PendingRevocation>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3)));
        return result;
    }
    public async Task FinishRevocationAsync(Guid uuid,bool success)
    {
        await using var connection=await Open(); await using var command=Command(connection,null,"""
            UPDATE SESSION_REVOCATION_OUTBOX SET STATE=IF(@success,'COMPLETED',IF(ATTEMPT_COUNT>=9,'MANUAL_REVIEW','PENDING')),
            ATTEMPT_COUNT=ATTEMPT_COUNT+1,NEXT_ATTEMPT_DATETIME=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 15 SECOND),UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID=@uuid
            """,("@success",success),("@uuid",uuid.ToString("D")));
        await command.ExecuteNonQueryAsync();
    }
    public async Task AuditSecurityAsync(string operation,Guid operationUuid,string correlation)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        await Audit(connection,transaction,operationUuid,null,null,null,operation,"SECURITY",null,"CHECK",operation,correlation,operation=="LOGIN_STARTED"?"SUCCEEDED":"DENIED");
        await transaction.CommitAsync();
    }
}