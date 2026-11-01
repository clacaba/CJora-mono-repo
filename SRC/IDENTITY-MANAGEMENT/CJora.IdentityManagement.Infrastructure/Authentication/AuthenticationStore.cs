using CJora.Identity.Contracts;
using CJora.IdentityManagement.Application.Authentication;
using CJora.IdentityManagement.Domain.Authentication;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.IdentityManagement.Infrastructure.Authentication;

public sealed class AuthenticationStore(string connectionString,TimeProvider clock) : IAuthenticationStore, IFinancialPermissionStore
{
    private DateTime Now=>clock.GetUtcNow().UtcDateTime;
    private async Task<MySqlConnection> Open()
    {
        if (new MySqlConnectionStringBuilder(connectionString).Database!="CJORA_IDENTITY_MANAGEMENT") throw new IdentityFailure(503,"IDENTITY_DATABASE_UNAVAILABLE");
        var connection=new MySqlConnection(connectionString);
        try {await connection.OpenAsync();return connection;}
        catch {await connection.DisposeAsync();throw;}
    }
    public async Task<AuthRecord> CreateAsync(VerifiedIdentity identity,Guid program,Guid operation,string correlation)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        long userId,memberId; Guid user;
        await using (var query=Command(connection,transaction,"""
            SELECT U.ID,M.ID,U.UUID FROM USER_ACCOUNT U JOIN MEMBERSHIP M ON M.USER_ACCOUNT_ID=U.ID
            WHERE U.ISSUER=@issuer AND U.SUBJECT=@subject AND U.IS_ENABLED=1 AND U.EMAIL_VERIFIED=1 AND M.PROGRAM_UUID=@program AND M.STATUS='ACTIVE'
            AND EXISTS (SELECT 1 FROM MEMBERSHIP_PERMISSION P WHERE P.MEMBERSHIP_ID=M.ID AND P.PERMISSION_CODE=@permission AND P.IS_ENABLED=1)
            """,("@issuer",identity.Issuer),("@subject",identity.Subject),("@program",program.ToString("D")),("@permission",IdentityProtocol.ProfilePermission)))
        await using (var reader=await query.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new IdentityFailure(403,"MEMBERSHIP_DENIED");
            userId=reader.GetInt64(0); memberId=reader.GetInt64(1); user=reader.GetGuid(2);
        }
        var now=Now; var expires=now.Add(IdentityProtocol.AbsoluteTimeout); var auth=Guid.NewGuid();
        await using (var existing=Command(connection,transaction,"SELECT COUNT(*) FROM AUTH_SESSION WHERE OPERATION_UUID=@op",("@op",operation.ToString("D"))))
            if (Convert.ToInt64(await existing.ExecuteScalarAsync())!=0) throw new IdentityFailure(401,"LOGIN_ALREADY_USED");
        await using var insert=Command(connection,transaction,"""
            INSERT INTO AUTH_SESSION (UUID,MEMBERSHIP_ID,PROGRAM_UUID,USER_ACCOUNT_ID,OPERATION_UUID,STATE,AUTHENTICATED_DATETIME,LAST_ACTIVITY_DATETIME,EXPIRES_DATETIME,
                CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES (@uuid,@membership,@program,@userId,@op,'PENDING',@authenticated,@now,@expires,@actor,@now,@actor,@now)
            """,("@uuid",auth.ToString("D")),("@membership",memberId),("@program",program.ToString("D")),("@userId",userId),
            ("@op",operation.ToString("D")),("@authenticated",identity.AuthenticatedUtc>now?now:identity.AuthenticatedUtc),("@now",now),("@expires",expires),("@actor",user.ToString("D")));
        try { await insert.ExecuteNonQueryAsync(); }
        catch (MySqlException ex) when (ex.Number==1062) { throw new IdentityFailure(401,"LOGIN_ALREADY_USED"); }
        var id=insert.LastInsertedId;
        await using var workflow=Command(connection,transaction,"""
            INSERT INTO SESSION_WORKFLOW (UUID,AUTH_SESSION_ID,OPERATION_UUID,ACTION_CODE,STATE,NEXT_ATTEMPT_DATETIME,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES (@uuid,@id,@op,'ACTIVATE','PENDING',DATE_ADD(@now,INTERVAL 2 MINUTE),@actor,@now,@actor,@now)
            """,("@uuid",Guid.NewGuid().ToString("D")),("@id",id),("@op",operation.ToString("D")),("@now",now),("@actor",user.ToString("D")));
        await workflow.ExecuteNonQueryAsync();
        await Audit(connection,transaction,operation,program,user,auth,"AUTH_SESSION_CREATED","AUTH_SESSION",auth,"CREATE","PENDING",correlation);
        await transaction.CommitAsync(); return new AuthRecord(auth,operation,user,program,expires);
    }
    public async Task ActivateAsync(AuthRecord session,Guid coreSession,string correlation)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        await using var update=Command(connection,transaction,"""
            UPDATE AUTH_SESSION S JOIN MEMBERSHIP M ON M.ID=S.MEMBERSHIP_ID JOIN USER_ACCOUNT U ON U.ID=S.USER_ACCOUNT_ID
            SET S.STATE='ACTIVE',S.CORE_SESSION_UUID=@core,S.ACTIVATED_DATETIME=@now,S.UPDATED_DATETIME=@now,S.ROW_VERSION=S.ROW_VERSION+1
            WHERE S.UUID=@uuid AND S.STATE='PENDING' AND S.EXPIRES_DATETIME>@now AND M.STATUS='ACTIVE' AND U.IS_ENABLED=1
            """,("@core",coreSession.ToString("D")),("@now",Now),("@uuid",session.AuthSessionUuid.ToString("D")));
        if (await update.ExecuteNonQueryAsync()!=1) throw new IdentityFailure(403,"SESSION_ACTIVATION_DENIED");
        await Audit(connection,transaction,session.OperationUuid,session.ProgramUuid,session.UserUuid,session.AuthSessionUuid,"AUTH_SESSION_ACTIVATED","AUTH_SESSION",session.AuthSessionUuid,"UPDATE","ACTIVE",correlation);
        await transaction.CommitAsync();
    }
    public async Task CompleteActivationAsync(Guid authSession)
    {
        await using var connection=await Open(); await using var command=Command(connection,null,"""
            UPDATE SESSION_WORKFLOW W JOIN AUTH_SESSION S ON S.ID=W.AUTH_SESSION_ID
            SET W.STATE='COMPLETED',W.UPDATED_DATETIME=UTC_TIMESTAMP(6),W.ROW_VERSION=W.ROW_VERSION+1 WHERE S.UUID=@uuid AND W.ACTION_CODE='ACTIVATE'
            """,("@uuid",authSession.ToString("D")));
        await command.ExecuteNonQueryAsync();
    }
    public async Task<ValidatedIdentity> ValidateAsync(ValidateSession request)
    {
        await using var connection=await Open(); DateTime expires,last; string state,membership; bool enabled,verified,permission;
        await using (var query=Command(connection,null,"""
            SELECT S.EXPIRES_DATETIME,S.LAST_ACTIVITY_DATETIME,S.STATE,M.STATUS,U.IS_ENABLED,U.EMAIL_VERIFIED,
              EXISTS(SELECT 1 FROM MEMBERSHIP_PERMISSION P WHERE P.MEMBERSHIP_ID=M.ID AND P.PERMISSION_CODE=@permission AND P.IS_ENABLED=1)
            FROM AUTH_SESSION S JOIN MEMBERSHIP M ON M.ID=S.MEMBERSHIP_ID JOIN USER_ACCOUNT U ON U.ID=S.USER_ACCOUNT_ID
            WHERE S.UUID=@auth AND S.CORE_SESSION_UUID=@core AND S.PROGRAM_UUID=@program AND U.UUID=@user
            """,("@auth",request.AuthSessionUuid.ToString("D")),("@core",request.CoreSessionUuid.ToString("D")),("@program",request.ProgramUuid.ToString("D")),("@user",request.UserUuid.ToString("D")),("@permission",IdentityProtocol.ProfilePermission)))
        await using (var reader=await query.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new IdentityFailure(401,"AUTH_SESSION_INVALID");
            expires=Utc(reader,0); last=Utc(reader,1); state=reader.GetString(2); membership=reader.GetString(3); enabled=reader.GetBoolean(4); verified=reader.GetBoolean(5); permission=reader.GetBoolean(6);
        }
        if (state!="ACTIVE" || IdentityProtocol.IsExpired(Now,last,expires)) throw new IdentityFailure(401,"AUTH_SESSION_EXPIRED");
        if (membership!="ACTIVE" || !enabled || !verified || !permission) throw new IdentityFailure(403,"MEMBERSHIP_DENIED");
        await using var touch=Command(connection,null,"UPDATE AUTH_SESSION SET LAST_ACTIVITY_DATETIME=@now WHERE UUID=@uuid AND STATE='ACTIVE' AND EXPIRES_DATETIME>@now",("@now",Now),("@uuid",request.AuthSessionUuid.ToString("D")));
        await touch.ExecuteNonQueryAsync();
        return new ValidatedIdentity(request.UserUuid,request.ProgramUuid,request.AuthSessionUuid,request.CoreSessionUuid,expires);
    }
    public async Task RevokeAsync(RevokeSession request,string correlation)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        long id; Guid user,program; Guid? core; string state;
        await using (var query=Command(connection,transaction,"""
            SELECT S.ID,U.UUID,S.PROGRAM_UUID,S.CORE_SESSION_UUID,S.STATE FROM AUTH_SESSION S JOIN USER_ACCOUNT U ON U.ID=S.USER_ACCOUNT_ID WHERE S.UUID=@uuid FOR UPDATE OF S
            """,("@uuid",request.AuthSessionUuid.ToString("D"))))
        await using (var reader=await query.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return;
            id=reader.GetInt64(0); user=reader.GetGuid(1); program=reader.GetGuid(2); core=reader.IsDBNull(3)?null:reader.GetGuid(3); state=reader.GetString(4);
        }
        if (request.CoreSessionUuid.HasValue && core.HasValue && request.CoreSessionUuid!=core) throw new IdentityFailure(403,"SESSION_LINK_INVALID");
        var now=Now;
        if (state!="REVOKED")
        {
            await using var update=Command(connection,transaction,"""
                UPDATE AUTH_SESSION SET STATE='REVOKED',REVOKED_DATETIME=@now,REVOCATION_REASON_CODE='LOGOUT_OR_RECOVERY',UPDATED_DATETIME=@now,ROW_VERSION=ROW_VERSION+1 WHERE ID=@id
                """,("@now",now),("@id",id)); await update.ExecuteNonQueryAsync();
            await Audit(connection,transaction,request.OperationUuid,program,user,request.AuthSessionUuid,"AUTH_SESSION_REVOKED","AUTH_SESSION",request.AuthSessionUuid,"UPDATE","REVOKED",correlation);
        }
        await using var queue=Command(connection,transaction,"""
            INSERT INTO SESSION_WORKFLOW (UUID,AUTH_SESSION_ID,OPERATION_UUID,ACTION_CODE,STATE,NEXT_ATTEMPT_DATETIME,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
            VALUES (@uuid,@id,@op,'REVOKE','PENDING',@now,@actor,@now,@actor,@now)
            ON DUPLICATE KEY UPDATE AUTH_SESSION_ID=AUTH_SESSION_ID
            """,("@uuid",Guid.NewGuid().ToString("D")),("@id",id),("@op",request.OperationUuid.ToString("D")),("@now",now),("@actor",user.ToString("D")));
        await queue.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
    public async Task<IReadOnlyList<RecoveryWork>> RecoveryAsync()
    {
        await using var connection=await Open(); await using var command=Command(connection,null,"""
            SELECT W.UUID,S.UUID,S.CORE_SESSION_UUID,W.OPERATION_UUID,W.ACTION_CODE FROM SESSION_WORKFLOW W JOIN AUTH_SESSION S ON S.ID=W.AUTH_SESSION_ID
            WHERE W.STATE='PENDING' AND W.NEXT_ATTEMPT_DATETIME<=UTC_TIMESTAMP(6) LIMIT 20
            """);
        await using var reader=await command.ExecuteReaderAsync(); var result=new List<RecoveryWork>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetGuid(3),reader.GetString(4)));
        return result;
    }
    public async Task FinishRecoveryAsync(Guid uuid,bool success)
    {
        await using var connection=await Open(); await using var command=Command(connection,null,"""
            UPDATE SESSION_WORKFLOW SET STATE=IF(@success,'COMPLETED',IF(ATTEMPT_COUNT>=9,'MANUAL_REVIEW','PENDING')),ATTEMPT_COUNT=ATTEMPT_COUNT+1,
            NEXT_ATTEMPT_DATETIME=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 15 SECOND),UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID=@uuid
            """,("@success",success),("@uuid",uuid.ToString("D"))); await command.ExecuteNonQueryAsync();
    }
    public async Task AuditSecurityAsync(string operation,string correlation)
    {
        await using var connection=await Open(); await using var transaction=await connection.BeginTransactionAsync();
        await Audit(connection,transaction,Guid.NewGuid(),null,null,null,operation,"SECURITY",null,"CHECK",operation,correlation,operation=="LOGIN_STARTED"?"SUCCEEDED":"DENIED");
        await transaction.CommitAsync();
    }
    public async Task<ValidatedIdentity> AuthorizePermissionAsync(AuthorizeSessionPermission request)
    {
        if(request.Permission is not ("ACCOUNTS.READ" or "TRANSFERS.CREATE" or "TRANSACTIONS.READ"))
            throw new IdentityFailure(403,"FINANCIAL_PERMISSION_DENIED");
        var validated=await ValidateAsync(request.Session);
        await using var connection=await Open();
        await using var query=Command(connection,null,"""
            SELECT COUNT(*) FROM AUTH_SESSION S JOIN MEMBERSHIP M ON M.ID=S.MEMBERSHIP_ID
            JOIN USER_ACCOUNT U ON U.ID=S.USER_ACCOUNT_ID
            JOIN MEMBERSHIP_PERMISSION P ON P.MEMBERSHIP_ID=M.ID
            WHERE S.UUID=@auth AND S.CORE_SESSION_UUID=@core AND S.PROGRAM_UUID=@program AND U.UUID=@user
              AND S.STATE='ACTIVE' AND S.EXPIRES_DATETIME>@now AND M.STATUS='ACTIVE'
              AND U.IS_ENABLED=1 AND U.EMAIL_VERIFIED=1 AND P.IS_ENABLED=1 AND P.PERMISSION_CODE=@permission
            """,("@auth",request.Session.AuthSessionUuid.ToString("D")),("@core",request.Session.CoreSessionUuid.ToString("D")),
            ("@program",request.Session.ProgramUuid.ToString("D")),("@user",request.Session.UserUuid.ToString("D")),
            ("@now",Now),("@permission",request.Permission));
        if(Convert.ToInt64(await query.ExecuteScalarAsync())!=1)throw new IdentityFailure(403,"FINANCIAL_PERMISSION_DENIED");
        return validated;
    }}