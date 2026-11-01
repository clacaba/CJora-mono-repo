import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import https from 'node:https';
import {chromium,expect} from '@playwright/test';
import fs from 'node:fs';
import {X509Certificate,createHash,randomUUID} from 'node:crypto';
const root=new URL('../',import.meta.url);
const fixture=JSON.parse(fs.readFileSync(new URL('.local/finance-fixture.json',root),'utf8'));
const envText=fs.readFileSync(new URL('INFRASTRUCTURE/LOCAL/.env',root),'utf8');
const secrets=Object.fromEntries(envText.split(/\r?\n/).filter(line=>/^[A-Z_]+=/.test(line)).map(line=>[line.slice(0,line.indexOf('=')),line.slice(line.indexOf('=')+1)]));
const password=fs.readFileSync(new URL('INFRASTRUCTURE/LOCAL/.env',root),'utf8').match(/^CJORA_DEMO_USER_PASSWORD=(.+)$/m)?.[1].trim();
const certificate=new X509Certificate(fs.readFileSync(new URL('.local/certificates/localhost-public.pem',root)));
const pin=createHash('sha256').update(certificate.publicKey.export({format:'der',type:'spki'})).digest('base64');
function sql(statement){
 const result=spawnSync('docker',['compose','--env-file','INFRASTRUCTURE/LOCAL/.env','-f','INFRASTRUCTURE/LOCAL/compose.yaml','exec','-T','mysql','sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names'],{cwd:fileURLToPath(root),input:statement,encoding:'utf8',windowsHide:true});
 check(result.status===0,'FIXTURE_SQL_FAILED');return result.stdout.trim();
}
function permission(enabled){
 sql(`USE CJORA_IDENTITY_MANAGEMENT;START TRANSACTION;
 UPDATE MEMBERSHIP_PERMISSION P JOIN MEMBERSHIP M ON M.ID=P.MEMBERSHIP_ID
 SET P.IS_ENABLED=${enabled?1:0},P.UPDATED_DATETIME=UTC_TIMESTAMP(6),P.ROW_VERSION=P.ROW_VERSION+1
 WHERE M.UUID='c6a8a500-0000-4000-8000-000000000004' AND P.PERMISSION_CODE='TRANSFERS.CREATE';
 INSERT INTO API_LOG(UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ACTION_CODE,RESULT_CODE,CREATED_DATETIME)
 VALUES(UUID(),'WORKLOAD','00000000-0000-0000-0000-000000000001','financial-api-check',UUID(),'TEST_FINANCIAL_PERMISSION_CHANGED','MEMBERSHIP_PERMISSION','UPDATE','SUCCEEDED',UTC_TIMESTAMP(6));COMMIT;`);
}
function recipient(status){
 const version=Number(sql(`SELECT ROW_VERSION FROM CJORA_CORE.CUSTOMER WHERE UUID='${fixture.destinationCustomerUuid}';`));
 const command={customerUuid:fixture.destinationCustomerUuid,programUuid:fixture.programUuid,userUuid:'c6a8a500-0000-4000-8000-000000000102',
 displayName:'Fictional recipient',email:'recipient@example.test',status,expectedVersion:version,operationUuid:randomUUID()};
 const result=spawnSync('dotnet',['run','--no-build','--project','SRC/CORE/CJora.Core.Customers','--','--save'],{
  cwd:fileURLToPath(root),input:JSON.stringify(command),encoding:'utf8',windowsHide:true,
  env:{...process.env,CJORA_CORE_CUSTOMER_CONNECTION:'Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_customer_operator;Password='+secrets.CJORA_CORE_CUSTOMER_PASSWORD+';SslMode=Disabled'}});
 check(result.status===0,'RECIPIENT_FIXTURE_CHANGE_FAILED');
}
let browser;let stage='launch';
function check(value,code){if(!value)throw new Error(code);}
function pass(message){console.log('PASS: '+message);}
try{
 browser=await chromium.launch({channel:'msedge',headless:true,args:['--ignore-certificate-errors-spki-list='+pin]});
 const context=await browser.newContext();const page=await context.newPage();
 stage='login';await page.goto('https://localhost:7000/app');
 await page.getByLabel('Email address',{exact:true}).fill('alex@example.test');
 await page.getByRole('button',{name:'Continue to sign in'}).click();
 await page.locator('input[name=password]').fill(password);await page.locator('#kc-login').click();
 await expect(page.getByRole('heading',{name:'Hello, Alex Demo.'})).toBeVisible();
 async function api(path,method='GET',body,key,csrf=true){
  return page.evaluate(async({path,method,body,key,csrf})=>{
   const headers={};
   if(method==='POST'){
    headers['Content-Type']='application/json';
    if(key!==undefined)headers['Idempotency-Key']=key;
    if(csrf){
     const token=await fetch('/api/v1/idm/csrf',{cache:'no-store'}).then(r=>r.json());
     headers['X-CSRF-TOKEN']=token.requestToken;
    }
   }
   const response=await fetch(path,{method,headers,cache:'no-store',credentials:'same-origin',...(body===undefined?{}:{body:JSON.stringify(body)})});
   return {status:response.status,body:await response.json(),cache:response.headers.get('Cache-Control')};
  },{path,method,body,key,csrf});
 }
 function status(response,expected){
  if(response.status!==expected){
   const code=/^[A-Z0-9_]{1,64}$/.test(response.body?.code??'')?response.body.code:'UNCLASSIFIED';
   throw new Error('HTTP_'+response.status+'_EXPECTED_'+expected+'_'+code);
  }
 }
 stage='accounts';
 let response=await api('/api/v1/txn/accounts');status(response,200);
 check(response.cache==='no-store','PRIVATE_CACHE');
 check(response.body.items.some(a=>a.accountUuid===fixture.sourceAccountUuid),'OWN_ACCOUNT_MISSING');
 check(!response.body.items.some(a=>a.accountUuid===fixture.destinationAccountUuid),'RECIPIENT_ACCOUNT_LEAK');
 const before=response.body.items.find(a=>a.accountUuid===fixture.sourceAccountUuid);
 check(typeof before.postedMinor==='string'&&before.availableMinor===before.postedMinor,'AMOUNT_PRECISION');
 pass('financial permissions expose only owned fictional accounts and string-valued balances');
 const request={sourceAccountUuid:fixture.sourceAccountUuid,destinationAccountUuid:fixture.destinationAccountUuid,amount:{currency:fixture.currency,minorUnits:'123'},reference:'Fictional API check'};
 stage='input-protection';
 status(await api('/api/v1/txn/transfer-intents','POST',request,randomUUID(),false),403);
 status(await api('/api/v1/txn/transfer-intents','POST',request,undefined),400);
 status(await api('/api/v1/txn/transfer-intents','POST',{...request,programUuid:fixture.programUuid},randomUUID()),400);
 status(await api('/api/v1/txn/transfer-intents','POST',{...request,amount:{currency:fixture.currency,minorUnits:123}},randomUUID()),400);
 pass('CSRF, idempotency and strict amount/identity request contracts are enforced');
 stage='prepare';
 const prepareKey=randomUUID();response=await api('/api/v1/txn/transfer-intents','POST',request,prepareKey);status(response,201);
 const intent=response.body;check(intent.state==='PREPARED'&&intent.amount.minorUnits==='123'&&intent.feeMinor==='0','REVIEW_SHAPE');
 const replay=await api('/api/v1/txn/transfer-intents','POST',request,prepareKey);status(replay,200);
 check(replay.body.intentUuid===intent.intentUuid&&replay.body.expiresAtUtc===intent.expiresAtUtc,'PREPARE_REPLAY');
 status(await api('/api/v1/txn/transfer-intents','POST',{...request,amount:{currency:fixture.currency,minorUnits:'124'}},prepareKey),409);
 pass('preparation returns an immutable five-minute review; request replay and payload conflicts behave correctly');
 stage='confirm';
 const confirmPath='/api/v1/txn/transfer-intents/'+intent.intentUuid+'/confirm';
 status(await api(confirmPath,'POST',{reviewVersion:1,amount:{currency:fixture.currency,minorUnits:'999'}},randomUUID()),400);
 const confirmKey=randomUUID();
 response=await api(confirmPath,'POST',{reviewVersion:1},confirmKey);status(response,201);
 const receipt=response.body;check(receipt.state==='POSTED'&&receipt.intentUuid===intent.intentUuid&&receipt.amount.minorUnits==='123','RECEIPT_SHAPE');
 const repeat=await api(confirmPath,'POST',{reviewVersion:1},confirmKey);status(repeat,200);
 check(repeat.body.transferUuid===receipt.transferUuid,'CONFIRM_REPLAY');
 const anotherKey=await api(confirmPath,'POST',{reviewVersion:1},randomUUID());status(anotherKey,200);
 check(anotherKey.body.transferUuid===receipt.transferUuid,'MULTIPLE_KEY_DOUBLE_POST');
 response=await api('/api/v1/txn/accounts');status(response,200);
 const after=response.body.items.find(a=>a.accountUuid===fixture.sourceAccountUuid);
 check(BigInt(before.postedMinor)-BigInt(after.postedMinor)===123n,'DOUBLE_DEBIT');
 pass('confirmation posts once; retries and different confirmation keys cannot debit the same intent twice');
 stage='status-and-history';
 response=await api('/api/v1/txn/transfer-intents/'+intent.intentUuid);status(response,200);check(response.body.state==='POSTED','STATUS_RECOVERY');
 response=await api('/api/v1/txn/accounts/'+fixture.sourceAccountUuid+'/transactions?limit=1');status(response,200);
 check(response.body.items.some(t=>t.transferUuid===receipt.transferUuid&&t.direction==='DEBIT'),'HISTORY_MISSING');
 response=await api('/api/v1/txn/accounts/'+fixture.sourceAccountUuid+'/transactions/'+receipt.transferUuid);status(response,200);
 check(response.body.amount.minorUnits==='123','DETAIL_AMOUNT');
 status(await api('/api/v1/txn/accounts/'+fixture.destinationAccountUuid+'/transactions'),404);
 pass('original intent resolves outcome; account history/detail remain scoped to the authenticated customer');
 stage='live-permission';
 permission(false);
 try{status(await api('/api/v1/txn/transfer-intents','POST',request,randomUUID()),403);}
 finally{permission(true);}
 pass('removing the financial permission immediately denies the active session');
 stage='live-recipient';
 const held=await api('/api/v1/txn/transfer-intents','POST',request,randomUUID());status(held,201);
 recipient('RESTRICTED');
 try{
  check(sql(`SELECT STATUS FROM CJORA_TRANSACTIONS.CUSTOMER_REFERENCE WHERE UUID='${fixture.destinationCustomerUuid}';`)==='ACTIVE','STALE_PROJECTION_FIXTURE_NOT_ACTIVE');
  status(await api('/api/v1/txn/transfer-intents/'+held.body.intentUuid+'/confirm','POST',{reviewVersion:1},randomUUID()),404);
 }finally{recipient('ACTIVE');}
 pass('live Core eligibility blocks execution even while Transactions has a stale ACTIVE projection');
 async function smallTransfer(minor='1'){
  const prepared=await api('/api/v1/txn/transfer-intents','POST',{...request,amount:{currency:fixture.currency,minorUnits:minor}},randomUUID());status(prepared,201);
  const posted=await api('/api/v1/txn/transfer-intents/'+prepared.body.intentUuid+'/confirm','POST',{reviewVersion:1},randomUUID());status(posted,201);
  return posted.body;
 }
 stage='history-cursor';
 await smallTransfer();await smallTransfer();
 const initialPage=await api('/api/v1/txn/accounts/'+fixture.sourceAccountUuid+'/transactions?limit=1');status(initialPage,200);
 check(typeof initialPage.body.nextCursor==='string','HISTORY_CURSOR_MISSING');
 const newer=await smallTransfer();
 const older=await api('/api/v1/txn/accounts/'+fixture.sourceAccountUuid+'/transactions?cursor='+encodeURIComponent(initialPage.body.nextCursor));status(older,200);
 check(older.body.items.every(item=>item.transferUuid!==newer.transferUuid&&item.transferUuid!==initialPage.body.items[0].transferUuid),'HISTORY_CURSOR_UNSTABLE');
 status(await api('/api/v1/txn/accounts/'+fixture.sourceAccountUuid+'/transactions?cursor='+encodeURIComponent(initialPage.body.nextCursor+'x')),400);
 status(await api('/api/v1/txn/accounts/'+fixture.destinationAccountUuid+'/transactions?cursor='+encodeURIComponent(initialPage.body.nextCursor)),400);
 status(await api('/api/v1/txn/accounts/'+fixture.sourceAccountUuid+'/transactions?limit=51'),400);
 pass('protected account-history cursors stay scoped and stable across a concurrent posting');
 stage='lost-response';
 const abandoned=await api('/api/v1/txn/transfer-intents','POST',{...request,amount:{currency:fixture.currency,minorUnits:'2'}},randomUUID());status(abandoned,201);
 const abandonedKey=randomUUID();
 const csrfToken=await page.evaluate(async()=>(await fetch('/api/v1/idm/csrf').then(r=>r.json())).requestToken);
 const cookieHeader=(await context.cookies('https://localhost:7000')).map(c=>c.name+'='+c.value).join('; ');
 const payload=JSON.stringify({reviewVersion:1});
 await new Promise((resolve,reject)=>{
  const req=https.request('https://localhost:7000/api/v1/txn/transfer-intents/'+abandoned.body.intentUuid+'/confirm',{
   method:'POST',ca:fs.readFileSync(new URL('.local/certificates/localhost-public.pem',root)),headers:{Cookie:cookieHeader,Origin:'https://localhost:7000','Content-Type':'application/json',
   'X-CSRF-TOKEN':csrfToken,'Idempotency-Key':abandonedKey,'Content-Length':Buffer.byteLength(payload)}},res=>{
    // Discard the response body after the committed operation reaches HTTP headers.
    const ok=res.statusCode===201;res.destroy();ok?resolve():reject(new Error('ABANDONED_POST_FAILED'));
   });
  req.on('error',()=>reject(new Error('ABANDONED_CONNECTION_FAILED')));req.end(payload);
 });
 const resolved=await api('/api/v1/txn/transfer-intents/'+abandoned.body.intentUuid);status(resolved,200);check(resolved.body.state==='POSTED','UNKNOWN_OUTCOME_NOT_RESOLVED');
 const recovered=await api('/api/v1/txn/transfer-intents/'+abandoned.body.intentUuid+'/confirm','POST',{reviewVersion:1},abandonedKey);status(recovered,200);
 check(recovered.body.transferUuid===resolved.body.transferUuid,'UNKNOWN_OUTCOME_POSTED_TWICE');
 pass('a discarded HTTP receipt resolves through the original intent/key without another transfer');
 stage='expired-review';
 const expires=await api('/api/v1/txn/transfer-intents','POST',request,randomUUID());status(expires,201);
 check(/^[0-9a-f-]{36}$/.test(expires.body.intentUuid),'FIXTURE_INTENT_INVALID');
 sql(`USE CJORA_TRANSACTIONS;START TRANSACTION;UPDATE TRANSFER_INTENT SET CREATED_DATETIME=DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 6 MINUTE),EXPIRES_DATETIME=DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 1 MINUTE),UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID='${expires.body.intentUuid}';
 INSERT INTO API_LOG(UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CREATED_DATETIME)
 VALUES(UUID(),'WORKLOAD','00000000-0000-0000-0000-000000000001','financial-api-check',UUID(),'TEST_REVIEW_DEADLINE_CHANGED','TRANSFER_INTENT','${expires.body.intentUuid}','UPDATE','SUCCEEDED',UTC_TIMESTAMP(6));COMMIT;`);
 const expired=await api('/api/v1/txn/transfer-intents/'+expires.body.intentUuid+'/confirm','POST',{reviewVersion:1},randomUUID());status(expired,409);
 const expiredState=await api('/api/v1/txn/transfer-intents/'+expires.body.intentUuid);status(expiredState,200);check(expiredState.body.state==='EXPIRED','EXPIRED_INTENT_STATE');
 pass('expired reviews become terminal without monetary execution');
 const previousSessionReview=await api('/api/v1/txn/transfer-intents','POST',request,randomUUID());status(previousSessionReview,201);
 stage='logout';
 const token=await page.evaluate(async()=>(await fetch('/api/v1/idm/csrf').then(r=>r.json())).requestToken);
 const logout=await page.evaluate(async token=>(await fetch('/api/v1/idm/sessions/current',{method:'DELETE',headers:{'X-CSRF-TOKEN':token}})).status,token);
 check(logout===204,'LOGOUT_FAILED');status(await api('/api/v1/txn/accounts'),401);
 pass('logout immediately prevents further financial access');
 await context.clearCookies();
 await page.goto('https://localhost:7000/app');
 await page.getByLabel('Email address',{exact:true}).fill('alex@example.test');
 await page.getByRole('button',{name:'Continue to sign in'}).click();
 await page.locator('input[name=password]').fill(password);await page.locator('#kc-login').click();
 await expect(page.getByRole('heading',{name:'Hello, Alex Demo.'})).toBeVisible();
 status(await api('/api/v1/txn/transfer-intents/'+previousSessionReview.body.intentUuid+'/confirm','POST',{reviewVersion:1},randomUUID()),409);
 pass('reauthentication cannot execute a review bound to the previous session pair');
 const finalToken=await page.evaluate(async()=>(await fetch('/api/v1/idm/csrf').then(r=>r.json())).requestToken);
 check(await page.evaluate(async token=>(await fetch('/api/v1/idm/sessions/current',{method:'DELETE',headers:{'X-CSRF-TOKEN':token}})).status,finalToken)===204,'FINAL_LOGOUT_FAILED');
}catch(error){
 console.error('FAIL: finance browser/API check at '+stage+': '+(/^[A-Z0-9_]+$/.test(error.message)?error.message:error.name)+'. Sensitive data suppressed.');
 process.exitCode=1;
}finally{await browser?.close();}
