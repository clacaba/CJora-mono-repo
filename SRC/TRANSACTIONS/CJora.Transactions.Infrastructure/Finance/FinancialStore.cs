using System.Globalization;
using System.Text.Json;
using CJora.Finance.Contracts;
using CJora.Transactions.Application.Finance;
using CJora.Transactions.Domain.Ledger;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

namespace CJora.Transactions.Infrastructure.Finance;

public sealed record FinancialResult(int Status,JsonElement Body,bool IsReplay);
public sealed record AccountView(Guid AccountUuid,string Label,string Currency,string Status,string PostedMinor,string AvailableMinor);
public sealed record FinanceAmount(string Currency,string MinorUnits);
public sealed record IntentView(Guid IntentUuid,string State,int ReviewVersion,Guid SourceAccountUuid,Guid DestinationAccountUuid,string DestinationLabel,
    FinanceAmount Amount,string FeeMinor,string TotalDebitMinor,string Reference,DateTime ExpiresAtUtc,Guid? TransferUuid,string? FailureCode);
public sealed record IntentRecord(long Id,FinancialSessionScope Owner,IntentView View,Guid RecipientCustomerUuid,string TermsHash)
{
    public RequestFingerprint VerifiedTerms()
    {
        var scope=new FinancialRequestScope(Owner.ProgramUuid,Owner.CustomerUuid,FinancialOperation.PrepareTransfer,Guid.NewGuid());
        var fingerprint=RequestFingerprint.Prepare(scope,new(View.SourceAccountUuid,View.DestinationAccountUuid,new(View.Amount.Currency,View.Amount.MinorUnits),View.Reference));
        if(fingerprint.Hex!=TermsHash)throw new FinanceFailure(409,"REVIEW_TERMS_CHANGED");
        return fingerprint;
    }
}
public sealed record HistoryView(Guid TransferUuid,Guid AccountUuid,string Direction,FinanceAmount Amount,string CounterpartyLabel,string Reference,DateTime PostedAtUtc);
public sealed record AccountRow(long Id,AccountView View);
public sealed record HistoryRow(long Sequence,HistoryView View);

public sealed class FinancialStore(string connectionString)
{
    private async Task<MySqlConnection> Open(CancellationToken ct)
    {
        if(new MySqlConnectionStringBuilder(connectionString).Database!="CJORA_TRANSACTIONS")throw new FinanceFailure(503,"FINANCE_DATABASE_UNAVAILABLE");
        var c=new MySqlConnection(connectionString);try{await c.OpenAsync(ct);return c;}catch{await c.DisposeAsync();throw;}
    }
    private static (string,object?)[] Scope(FinancialSessionScope scope)=>[("@program",scope.ProgramUuid.ToString("D")),("@customer",scope.CustomerUuid.ToString("D"))];
    public async Task<Guid> RecipientAsync(FinancialSessionScope scope,Guid source,Guid destination,CancellationToken ct)
    {
        await using var c=await Open(ct);
        await using var query=Command(c,null,"""
            SELECT D.CUSTOMER_UUID FROM ACCOUNT D WHERE D.UUID=@destination AND D.PROGRAM_UUID=@program AND D.ACCOUNT_TYPE='CUSTOMER_LIABILITY'
            AND EXISTS(SELECT 1 FROM ACCOUNT S WHERE S.UUID=@source AND S.PROGRAM_UUID=@program AND S.CUSTOMER_UUID=@customer AND S.ACCOUNT_TYPE='CUSTOMER_LIABILITY')
            """,[..Scope(scope),("@source",source.ToString("D")),("@destination",destination.ToString("D"))]);
        var value=await query.ExecuteScalarAsync(ct);
        return value is Guid uuid?uuid:throw new FinanceFailure(404,"RESOURCE_NOT_AVAILABLE");
    }
    public async Task<IntentRecord> IntentAsync(FinancialSessionScope scope,Guid intent,CancellationToken ct)
    {
        await using var c=await Open(ct);
        await using var query=Command(c,null,"""
            SELECT I.ID,I.UUID,I.STATE,I.REVIEW_VERSION,S.UUID,D.UUID,D.LABEL,I.CURRENCY,I.AMOUNT_MINOR,I.REFERENCE_TEXT,I.EXPIRES_DATETIME,T.UUID,I.FAILURE_CODE,
             D.CUSTOMER_UUID,I.TERMS_HASH,I.USER_UUID,I.CORE_SESSION_UUID,I.AUTH_SESSION_UUID
            FROM TRANSFER_INTENT I JOIN ACCOUNT S ON S.ID=I.SOURCE_ACCOUNT_ID JOIN ACCOUNT D ON D.ID=I.DESTINATION_ACCOUNT_ID
            LEFT JOIN TRANSFER T ON T.TRANSFER_INTENT_ID=I.ID
            WHERE I.UUID=@intent AND I.PROGRAM_UUID=@program AND I.CUSTOMER_UUID=@customer AND I.USER_UUID=@user
            """,[..Scope(scope),("@intent",intent.ToString("D")),("@user",scope.UserUuid.ToString("D"))]);
        await using var r=await query.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))throw new FinanceFailure(404,"RESOURCE_NOT_AVAILABLE");
        var amount=r.GetInt64(8).ToString(CultureInfo.InvariantCulture);
        var view=new IntentView(r.GetGuid(1),r.GetString(2),r.GetInt32(3),r.GetGuid(4),r.GetGuid(5),r.GetString(6),
            new(r.GetString(7),amount),"0",amount,r.GetString(9),Utc(r,10),r.IsDBNull(11)?null:r.GetGuid(11),r.IsDBNull(12)?null:r.GetString(12));
        return new(r.GetInt64(0),scope with{UserUuid=r.GetGuid(15),CoreSessionUuid=r.GetGuid(16),AuthSessionUuid=r.GetGuid(17)},view,r.GetGuid(13),Convert.ToHexString((byte[])r[14]));
    }
    private static (string,object?)[] Authority(FinancialAuthorityContext auth,FinancialRequestScope scope,RequestFingerprint fingerprint)=>[
        ("@program",auth.Scope.ProgramUuid.ToString("D")),("@customer",auth.Scope.CustomerUuid.ToString("D")),("@user",auth.Scope.UserUuid.ToString("D")),
        ("@core",auth.Scope.CoreSessionUuid.ToString("D")),("@auth",auth.Scope.AuthSessionUuid.ToString("D")),("@authorized",auth.ValidatedAtUtc),
        ("@expires",auth.SessionExpiresAtUtc),("@key",scope.Key.ToString("D")),("@hash",Convert.FromHexString(fingerprint.Hex))];
    public Task<FinancialResult> PrepareAsync(FinancialAuthorityContext auth,FinancialRequestScope scope,RequestFingerprint fingerprint,CreateTransferIntentRequest request,CancellationToken ct)
    {
        FinancialAuthorization.Require(auth,FinancialPermissions.TransfersCreate,DateTime.UtcNow);
        FinancialIdempotency.Decide(scope,fingerprint,null);
        if(scope.ProgramUuid!=auth.Scope.ProgramUuid||scope.CustomerUuid!=auth.Scope.CustomerUuid)throw new FinanceFailure(403,"FINANCIAL_ACCESS_DENIED");
        var amount=Money.FromMinorUnits(request.Amount.Currency,request.Amount.MinorUnits);
        return Call("CALL FINANCE_PREPARE(@program,@customer,@user,@core,@auth,@authorized,@expires,@key,@hash,@intent,@source,@destination,@currency,@amount,@reference)",
            [..Authority(auth,scope,fingerprint),("@intent",Guid.NewGuid().ToString("D")),("@source",request.SourceAccountUuid.ToString("D")),("@destination",request.DestinationAccountUuid.ToString("D")),
            ("@currency",amount.Currency.Code),("@amount",amount.MinorUnits),("@reference",request.Reference??"")],ct);
    }
    public Task<FinancialResult> ConfirmAsync(FinancialAuthorityContext auth,FinancialRequestScope scope,RequestFingerprint fingerprint,IntentRecord intent,int review,CancellationToken ct)
    {
        FinancialAuthorization.Require(auth,FinancialPermissions.TransfersCreate,DateTime.UtcNow);
        FinancialIdempotency.Decide(scope,fingerprint,null);
        if(scope.ProgramUuid!=auth.Scope.ProgramUuid||scope.CustomerUuid!=auth.Scope.CustomerUuid)throw new FinanceFailure(403,"FINANCIAL_ACCESS_DENIED");
        return Call("CALL FINANCE_CONFIRM(@program,@customer,@user,@core,@auth,@authorized,@expires,@key,@hash,@intent,@terms,@review)",
            [..Authority(auth,scope,fingerprint),("@intent",intent.View.IntentUuid.ToString("D")),("@terms",Convert.FromHexString(intent.VerifiedTerms().Hex)),("@review",review)],ct);
    }
    private async Task<FinancialResult> Call(string sql,(string,object?)[] values,CancellationToken ct)
    {
        await using var c=await Open(ct);
        await using var command=Command(c,null,sql,values);command.CommandTimeout=10;
        try
        {
            await using var r=await command.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))throw new FinanceFailure(503,"FINANCE_DATABASE_UNAVAILABLE");
            using var document=JsonDocument.Parse(r.GetString(1));
            return new(r.GetInt32(0),document.RootElement.Clone(),r.GetBoolean(2));
        }
        catch(MySqlException e)when(e.SqlState=="45000")
        {
            var code=e.Message;
            var status=code switch{
                "RESOURCE_NOT_AVAILABLE"=>404,"IDEMPOTENCY_CONFLICT" or "REVIEW_SESSION_CHANGED" or "REVIEW_TERMS_CHANGED"=>409,
                "INVALID_TRANSFER_REQUEST" or "INVALID_REVIEW_VERSION"=>400,"FINANCIAL_AUTHORITY_EXPIRED"=>403,_=>503};
            throw new FinanceFailure(status,status==503?"FINANCE_DATABASE_UNAVAILABLE":code);
        }
    }
    public async Task<(IReadOnlyList<AccountRow> Rows,long Upper)> AccountsAsync(FinancialSessionScope scope,long? upper,long before,int limit,CancellationToken ct)
    {
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        long bound=upper??0;
        if(upper is null)
        {
            await using var max=Command(c,tx,"SELECT COALESCE(MAX(ID),0) FROM ACCOUNT WHERE PROGRAM_UUID=@program AND CUSTOMER_UUID=@customer AND ACCOUNT_TYPE='CUSTOMER_LIABILITY'",Scope(scope));
            bound=Convert.ToInt64(await max.ExecuteScalarAsync(ct));
        }
        await using var query=Command(c,tx,"""
            SELECT ID,UUID,LABEL,CURRENCY,STATUS,POSTED_MINOR FROM ACCOUNT
            WHERE PROGRAM_UUID=@program AND CUSTOMER_UUID=@customer AND ACCOUNT_TYPE='CUSTOMER_LIABILITY' AND ID<=@upper AND ID<@before ORDER BY ID DESC LIMIT @limit
            """,[..Scope(scope),("@upper",bound),("@before",before),("@limit",limit)]);
        var rows=new List<AccountRow>();
        await using(var r=await query.ExecuteReaderAsync(ct))
            while(await r.ReadAsync(ct)){var balance=r.GetInt64(5).ToString(CultureInfo.InvariantCulture);rows.Add(new(r.GetInt64(0),new(r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),balance,balance)));}
        await tx.CommitAsync(ct);return(rows,bound);
    }
    public async Task<(IReadOnlyList<HistoryRow> Rows,long Upper)> HistoryAsync(FinancialSessionScope scope,Guid account,long? upper,long before,int limit,Guid? transfer,CancellationToken ct)
    {
        await using var c=await Open(ct);await using var tx=await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        long id,sequence;
        await using(var owned=Command(c,tx,"SELECT ID,LAST_ENTRY_SEQUENCE FROM ACCOUNT WHERE UUID=@account AND PROGRAM_UUID=@program AND CUSTOMER_UUID=@customer AND ACCOUNT_TYPE='CUSTOMER_LIABILITY'",[..Scope(scope),("@account",account.ToString("D"))]))
        await using(var r=await owned.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct))throw new FinanceFailure(404,"RESOURCE_NOT_AVAILABLE");
            id=r.GetInt64(0);sequence=r.GetInt64(1);
        }
        var bound=upper??sequence;
        await using var query=Command(c,tx,"""
            SELECT E.ENTRY_SEQUENCE,T.UUID,E.SIDE,T.CURRENCY,T.AMOUNT_MINOR,
              IF(T.SOURCE_ACCOUNT_ID=E.ACCOUNT_ID,D.LABEL,S.LABEL),T.REFERENCE_TEXT,T.POSTED_DATETIME
            FROM LEDGER_ENTRY E JOIN LEDGER_JOURNAL J ON J.ID=E.JOURNAL_ID JOIN TRANSFER T ON T.ID=J.TRANSFER_ID
            JOIN ACCOUNT S ON S.ID=T.SOURCE_ACCOUNT_ID JOIN ACCOUNT D ON D.ID=T.DESTINATION_ACCOUNT_ID
            WHERE E.ACCOUNT_ID=@id AND E.ENTRY_SEQUENCE<=@upper AND E.ENTRY_SEQUENCE<@before
              AND (@transfer IS NULL OR T.UUID=@transfer) ORDER BY E.ENTRY_SEQUENCE DESC LIMIT @limit
            """,("@id",id),("@upper",bound),("@before",before),("@transfer",transfer?.ToString("D")),("@limit",limit));
        var rows=new List<HistoryRow>();
        await using(var r=await query.ExecuteReaderAsync(ct))
            while(await r.ReadAsync(ct))rows.Add(new(r.GetInt64(0),new(r.GetGuid(1),account,r.GetString(2),new(r.GetString(3),r.GetInt64(4).ToString(CultureInfo.InvariantCulture)),r.GetString(5),r.GetString(6),Utc(r,7))));
        await tx.CommitAsync(ct);return(rows,bound);
    }
    public async Task<IReadOnlyList<(long Id,Guid Uuid)>> ReconciliationBatchAsync(long after,CancellationToken ct)
    {
        await using var c=await Open(ct);await using var command=Command(c,null,"SELECT ID,UUID FROM ACCOUNT WHERE ID>@after ORDER BY ID LIMIT 100",("@after",after));
        await using var r=await command.ExecuteReaderAsync(ct);var rows=new List<(long,Guid)>();
        while(await r.ReadAsync(ct))rows.Add((r.GetInt64(0),r.GetGuid(1)));return rows;
    }
    public async Task ReconcileAsync(Guid account,CancellationToken ct)
    {
        await using var c=await Open(ct);await using var command=Command(c,null,"CALL FINANCE_RECONCILE(@account)",("@account",account.ToString("D")));
        command.CommandTimeout=10;await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<FinancialResult?> CachedAsync(FinancialRequestScope scope,RequestFingerprint fingerprint,CancellationToken ct)
    {
        FinancialIdempotency.Decide(scope,fingerprint,null);
        await using var c=await Open(ct);
        await using var query=Command(c,null,"""
            SELECT PAYLOAD_HASH,HTTP_STATUS,RESPONSE_BODY,STATE FROM FINANCIAL_REQUEST
            WHERE PROGRAM_UUID=@program AND CUSTOMER_UUID=@customer AND OPERATION_CODE=@op AND IDEMPOTENCY_KEY=@key
            """,[("@program",scope.ProgramUuid.ToString("D")),("@customer",scope.CustomerUuid.ToString("D")),
            ("@op",scope.Operation==FinancialOperation.PrepareTransfer?"PREPARE":"CONFIRM"),("@key",scope.Key.ToString("D"))]);
        await using var r=await query.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;
        if(Convert.ToHexString((byte[])r[0])!=fingerprint.Hex)throw new FinanceFailure(409,"IDEMPOTENCY_CONFLICT");
        if(r.GetString(3)!="COMPLETED")throw new FinanceFailure(503,"FINANCE_OUTCOME_PENDING");
        using var body=JsonDocument.Parse(r.GetString(2));var status=r.GetInt32(1);
        return new(status==201?200:status,body.RootElement.Clone(),true);
    }}
