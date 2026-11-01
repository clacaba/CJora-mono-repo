using System.Globalization;
using System.Text.Json;
using CJora.Finance.Contracts;
using CJora.Transactions.Application.Finance;
using CJora.Transactions.Infrastructure.Finance;
using MySqlConnector;
using static CJora.Routing.Persistence.SessionSql;

internal static class DatabaseFinanceChecks
{
    private static string Admin=>DemoFinance.Env("CJORA_TX_FINANCE_ADMIN");
    private static string Runtime=>DemoFinance.Env("CJORA_TX_FINANCE_CONNECTION");
    private static void Check(bool condition,string code){if(!condition)throw new InvalidOperationException(code);}
    private static void Pass(string message)=>Console.WriteLine("PASS: "+message);
    private static async Task<object?> Sql(string sql,params (string,object?)[] values)
    {await using var c=new MySqlConnection(Admin);await c.OpenAsync();await using var command=Command(c,null,sql,values);return await command.ExecuteScalarAsync();}
    private static async Task Trigger(string sql)
    {
        await using var c=new MySqlConnection(DemoFinance.Env("CJORA_FINANCE_TRIGGER_ADMIN"));await c.OpenAsync();
        await using var command=new MySqlCommand(sql,c){CommandTimeout=10};await command.ExecuteNonQueryAsync();
    }
    private static FinancialAuthorityContext Authority(FinancialSessionScope scope)=>new(scope,[FinancialPermissions.TransfersCreate],DateTime.UtcNow,DateTime.UtcNow.AddHours(1));
    private static FinancialRequestScope RequestScope(FinancialSessionScope scope,FinancialOperation operation,Guid? key=null)=>FinancialRequestScope.Create(scope,operation,(key??Guid.NewGuid()).ToString("D"));
    private static async Task Denied(string sql,int expected)
    {
        await using var c=new MySqlConnection(Runtime);await c.OpenAsync();
        try{await using var cmd=new MySqlCommand(sql,c);await cmd.ExecuteNonQueryAsync();}
        catch(MySqlException e)when(e.Number==expected){return;}
        throw new InvalidOperationException("EXPECTED_DATABASE_PRIVILEGE_DENIAL");
    }
    internal static async Task<int> Run()
    {
        var program=Guid.NewGuid();var sourceCustomer=Guid.NewGuid();var destinationCustomer=Guid.NewGuid();var sourceUser=Guid.NewGuid();var destinationUser=Guid.NewGuid();
        foreach(var row in new[]{(sourceCustomer,sourceUser),(destinationCustomer,destinationUser)})
            await Sql("""
                INSERT INTO CUSTOMER_REFERENCE(UUID,PROGRAM_UUID,USER_UUID,STATUS,SOURCE_VERSION,LAST_EVENT_UUID,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
                VALUES(@uuid,@program,@user,'ACTIVE',1,@event,'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6),'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6))
                """,("@uuid",row.Item1.ToString("D")),("@program",program.ToString("D")),("@user",row.Item2.ToString("D")),("@event",Guid.NewGuid().ToString("D")));
        var accounts=await DemoFinance.SeedAccounts(Admin,program,sourceCustomer,destinationCustomer,"USD",1000,1000);
        var source=new FinancialSessionScope(program,sourceCustomer,sourceUser,Guid.NewGuid(),Guid.NewGuid());
        var destination=new FinancialSessionScope(program,destinationCustomer,destinationUser,Guid.NewGuid(),Guid.NewGuid());
        var store=new FinancialStore(Runtime);
        async Task<IntentRecord> Prepare(FinancialSessionScope owner,Guid from,Guid to,string amount)
        {
            var request=new CreateTransferIntentRequest(from,to,new("USD",amount),"Database fixture");
            var scope=RequestScope(owner,FinancialOperation.PrepareTransfer);
            var result=await store.PrepareAsync(Authority(owner),scope,RequestFingerprint.Prepare(scope,request),request,default);
            Check(result.Status==201,"DATABASE_PREPARATION_FAILED");
            return await store.IntentAsync(owner,result.Body.GetProperty("intentUuid").GetGuid(),default);
        }
        Task<FinancialResult> Confirm(FinancialSessionScope owner,IntentRecord intent,Guid? key=null)
        {
            var scope=RequestScope(owner,FinancialOperation.ConfirmTransfer,key);
            return store.ConfirmAsync(Authority(owner),scope,RequestFingerprint.Confirm(scope,intent.View.IntentUuid,new(1)),intent,1,default);
        }
        async Task<long> Balance(Guid account)=>Convert.ToInt64(await Sql("SELECT POSTED_MINOR FROM ACCOUNT WHERE UUID=@uuid",("@uuid",account.ToString("D"))));
        var first=await Prepare(source,accounts.Source,accounts.Destination,"700");
        var second=await Prepare(source,accounts.Source,accounts.Destination,"700");
        var firstKey=Guid.NewGuid();var secondKey=Guid.NewGuid();
        var competing=await Task.WhenAll(Confirm(source,first,firstKey),Confirm(source,second,secondKey));
        Check(competing.Count(r=>r.Status==201)==1&&competing.Count(r=>r.Status==422)==1,"CONCURRENT_OVERSPEND_NOT_PREVENTED");
        Check(await Balance(accounts.Source)==300&&await Balance(accounts.Destination)==700,"CONCURRENT_BALANCE_WRONG");
        Pass("concurrent confirmations cannot overspend; the losing intent has no journal");
        var rejected=competing[0].Status==422?first:second;var rejectedKey=competing[0].Status==422?firstKey:secondKey;
        var reverse=await Prepare(destination,accounts.Destination,accounts.Source,"700");
        Check((await Confirm(destination,reverse)).Status==201,"REVERSE_FIXTURE_FAILED");
        Check((await Confirm(source,rejected,rejectedKey)).Status==422&&(await Confirm(source,rejected)).Status==422,"REJECTED_INTENT_REVIVED");
        Check(await Balance(accounts.Source)==1000,"REJECTION_REPLAY_MOVED_FUNDS");
        Pass("terminal insufficient-funds rejection stays rejected after funds become available");
        var differentKeys=await Prepare(source,accounts.Source,accounts.Destination,"100");
        var results=await Task.WhenAll(Confirm(source,differentKeys),Confirm(source,differentKeys));
        Check(results.Count(r=>r.Status==201)==1&&results.Count(r=>r.Status==200)==1,"INTENT_DUPLICATE_KEY_GUARD_FAILED");
        Check(results[0].Body.GetProperty("transferUuid").GetGuid()==results[1].Body.GetProperty("transferUuid").GetGuid(),"DUPLICATE_TRANSFER_RECEIPT");
        var sameKeyIntent=await Prepare(source,accounts.Source,accounts.Destination,"10");var sameKey=Guid.NewGuid();
        results=await Task.WhenAll(Confirm(source,sameKeyIntent,sameKey),Confirm(source,sameKeyIntent,sameKey));
        Check(results.Count(r=>r.Status==201)==1&&results.Count(r=>r.Status==200)==1,"SAME_KEY_RACE_FAILED");
        Pass("same-key and different-key concurrent confirmations create one transfer per intent");
        var forward=await Prepare(source,accounts.Source,accounts.Destination,"40");var back=await Prepare(destination,accounts.Destination,accounts.Source,"30");
        results=await Task.WhenAll(Confirm(source,forward),Confirm(destination,back));
        Check(results.All(r=>r.Status==201),"OPPOSING_TRANSFER_LOCK_ORDER_FAILED");
        Check(await Balance(accounts.Source)+await Balance(accounts.Destination)==1000,"MONEY_CONSERVATION_FAILED");
        Pass("opposing transfers acquire accounts consistently and conserve total customer value");

        async Task<string> Snapshot()
        {
            var values=new List<string>{(await Balance(accounts.Source)).ToString(),(await Balance(accounts.Destination)).ToString()};
            foreach(var table in new[]{"TRANSFER","LEDGER_JOURNAL","LEDGER_ENTRY","FINANCIAL_EVENT_OUTBOX","API_LOG"})
                values.Add(Convert.ToString(await Sql($"SELECT COUNT(*) FROM {table} WHERE PROGRAM_UUID=@program",("@program",program.ToString("D"))))!);
            return string.Join(":",values);
        }
        foreach(var faultTable in new[]{"LEDGER_ENTRY","API_LOG"})
        {
            var intent=await Prepare(source,accounts.Source,accounts.Destination,"10");var key=Guid.NewGuid();var before=await Snapshot();
            var trigger="CJORA_FINANCE_TEST_"+faultTable;
            // Fixed own-fixture trigger, removed in finally; no effect on any other Program.
            await Trigger($"CREATE TRIGGER {trigger} BEFORE INSERT ON {faultTable} FOR EACH ROW BEGIN IF NEW.PROGRAM_UUID='{program:D}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='TEST_FINANCIAL_ROLLBACK'; END IF; END");
            try
            {
                bool failed=false;
                try{await Confirm(source,intent,key);}catch(FinanceFailure e)when(e.Status==503){failed=true;}
                Check(failed,"ROLLBACK_FAULT_NOT_TRIGGERED");
                Check(await Snapshot()==before,"PARTIAL_FINANCIAL_STATE_SURVIVED_ROLLBACK");
                Check(Convert.ToInt64(await Sql("SELECT COUNT(*) FROM FINANCIAL_REQUEST WHERE PROGRAM_UUID=@program AND OPERATION_CODE='CONFIRM' AND IDEMPOTENCY_KEY=@key",("@program",program.ToString("D")),("@key",key.ToString("D"))))==0,"ROLLED_BACK_KEY_RETAINED");
                Check((await store.IntentAsync(source,intent.View.IntentUuid,default)).View.State=="PREPARED","ROLLED_BACK_INTENT_CHANGED");
            }
            finally{await Trigger($"DROP TRIGGER {trigger}");}
            Check((await Confirm(source,intent,key)).Status==201,"SAME_KEY_RETRY_AFTER_ROLLBACK_FAILED");
        }
        Pass("ledger-insert and audit-insert faults roll back balances, entries, transfer, outbox, intent and idempotency together");

        var lost=await Prepare(source,accounts.Source,accounts.Destination,"10");var lostKey=Guid.NewGuid();
        await Confirm(source,lost,lostKey); // Deliberately discard the committed response; resolve using original operation.
        var replay=await Confirm(source,lost,lostKey);
        Check(replay.Status==200&&(await store.IntentAsync(source,lost.View.IntentUuid,default)).View.State=="POSTED","COMMITTED_OUTCOME_RECOVERY_FAILED");
        Pass("an abandoned committed outcome resolves through the original intent/key without a second posting");

        await Denied("UPDATE ACCOUNT SET POSTED_MINOR=0 WHERE ID=0",1142);
        await Denied("DELETE FROM LEDGER_ENTRY WHERE ID=0",1142);
        await Denied("INSERT INTO LEDGER_ENTRY(UUID) VALUES(UUID())",1142);
        await Denied("SELECT ID FROM CJORA_CORE.CUSTOMER LIMIT 0",1142);
        await Denied("CALL FINANCE_INTENT_BODY(0,@body)",1370);
        Pass("actual posting credential cannot alter money directly, invoke private helpers or read Core");
        var journals=Convert.ToInt64(await Sql("""
            SELECT COUNT(*) FROM LEDGER_JOURNAL J WHERE J.PROGRAM_UUID=@program AND
            ((SELECT COUNT(*) FROM LEDGER_ENTRY E WHERE E.JOURNAL_ID=J.ID)<>2 OR
             (SELECT SUM(AMOUNT_MINOR) FROM LEDGER_ENTRY E WHERE E.JOURNAL_ID=J.ID AND SIDE='DEBIT')<>J.DEBIT_TOTAL_MINOR OR
             (SELECT SUM(AMOUNT_MINOR) FROM LEDGER_ENTRY E WHERE E.JOURNAL_ID=J.ID AND SIDE='CREDIT')<>J.CREDIT_TOTAL_MINOR)
            """,("@program",program.ToString("D"))));
        Check(journals==0,"JOURNAL_RECONCILIATION_FAILED");
        await store.ReconcileAsync(accounts.Source,default);await store.ReconcileAsync(accounts.Destination,default);
        Check((string?)await Sql("SELECT STATUS FROM ACCOUNT WHERE UUID=@uuid",("@uuid",accounts.Source.ToString("D")))=="ACTIVE","VALID_ACCOUNT_RESTRICTED");
        var correctBalance=await Balance(accounts.Source);
        async Task CorruptCache(Guid account,bool alreadyRestricted)
        {
            await Sql("""
                START TRANSACTION;
                UPDATE ACCOUNT SET POSTED_MINOR=POSTED_MINOR+1,STATUS=IF(@restricted,'RESTRICTED',STATUS),
                  UPDATED_BY_UUID='00000000-0000-0000-0000-000000000001',UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID=@uuid;
                INSERT INTO API_LOG(UUID,PROGRAM_UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CREATED_DATETIME)
                VALUES(UUID(),@program,'WORKLOAD','00000000-0000-0000-0000-000000000001','fixture-corruption-check',UUID(),'TEST_ACCOUNT_CACHE_CORRUPTED','ACCOUNT',@uuid,'UPDATE','SUCCEEDED',UTC_TIMESTAMP(6));
                COMMIT;
                """,("@uuid",account.ToString("D")),("@restricted",alreadyRestricted),("@program",program.ToString("D")));
        }
        await CorruptCache(accounts.Source,false);
        await store.ReconcileAsync(accounts.Source,default);await store.ReconcileAsync(accounts.Source,default);
        Check((string?)await Sql("SELECT STATUS FROM ACCOUNT WHERE UUID=@uuid",("@uuid",accounts.Source.ToString("D")))=="RESTRICTED","DRIFT_NOT_RESTRICTED");
        Check(await Balance(accounts.Source)==correctBalance+1,"DRIFT_SILENTLY_REPAIRED");
        Check(Convert.ToInt64(await Sql("SELECT COUNT(*) FROM API_LOG WHERE ENTITY_UUID=@uuid AND OPERATION_NAME='ACCOUNT_RECONCILIATION_RESTRICTED'",("@uuid",accounts.Source.ToString("D"))))==1,"RECONCILIATION_AUDIT_NOT_IDEMPOTENT");
        Pass("valid ledgers reconcile; cache drift restricts the affected account once and is never silently repaired");
        await CorruptCache(accounts.Destination,true);
        await store.ReconcileAsync(accounts.Destination,default);
        Check(Convert.ToInt64(await Sql("SELECT COUNT(*) FROM ACCOUNT_INTEGRITY_ALERT A JOIN ACCOUNT C ON C.ID=A.ACCOUNT_ID WHERE C.UUID=@uuid AND A.STATE='MANUAL_REVIEW'",("@uuid",accounts.Destination.ToString("D"))))==1,"PREEXISTING_RESTRICTION_HID_INTEGRITY_FAILURE");
        Pass("an already-restricted account still gets a durable integrity alert and independent error record");
        return 0;
    }
}
