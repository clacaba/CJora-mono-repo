import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import assert from 'node:assert/strict';
const root=new URL('../',import.meta.url);
const pem=fs.readFileSync(new URL('.local/certificates/localhost-public.pem',root));
const secretFile=fs.readFileSync(new URL('INFRASTRUCTURE/LOCAL/.env',root),'utf8');
const password=secretFile.match(/^CJORA_DEMO_USER_PASSWORD=(.+)$/m)?.[1].trim();
assert(password,'Run Initialize-Identity.ps1 first.');
const jar=new Map();
let finalCookie='';
async function request(address,{method='GET',body,headers={},cookies=true}={}) {
 const url=new URL(address,'https://localhost:7000');
 const all={...headers};
 if(cookies) {
  const saved=[...jar.values()].filter(c=>c.host===url.hostname && (!c.secure||url.protocol==='https:'||(url.origin==='http://localhost:8080'&&!c.pair.startsWith('__Host-cjora-'))) && url.pathname.startsWith(c.path));
  if(saved.length) all.Cookie=saved.map(c=>c.pair).join('; ');
 }
 if(body) all['Content-Length']=Buffer.byteLength(body);
 return new Promise((resolve,reject)=>{
  const req=(url.protocol==='https:'?https:http).request(url,{method,headers:all,ca:pem,timeout:20000},res=>{
   const chunks=[];
   for(const line of res.headers['set-cookie']??[]) {
    const parts=line.split(';').map(p=>p.trim());const pair=parts[0];const name=pair.slice(0,pair.indexOf('='));
    const path=parts.find(p=>p.toLowerCase().startsWith('path='))?.slice(5)??'/';
    const key=url.hostname+path+name;
    if(pair.endsWith('=')||parts.some(p=>p.toLowerCase()==='max-age=0')) jar.delete(key);
    else jar.set(key,{host:url.hostname,path,pair,secure:parts.some(p=>p.toLowerCase()==='secure')});
    if(name==='__Host-cjora-session' && !pair.endsWith('=')) {
     assert(parts.some(p=>p.toLowerCase()==='secure'));assert(parts.some(p=>p.toLowerCase()==='httponly'));
     assert(parts.some(p=>p.toLowerCase()==='samesite=lax'));assert.equal(path,'/');finalCookie=pair;
    }
   }
   res.on('data',c=>chunks.push(c));res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,body:Buffer.concat(chunks).toString('utf8')}));
  });
  req.on('error',()=>reject(new Error('Local HTTP/TLS request failed.')));req.on('timeout',()=>req.destroy(new Error('timeout')));
  if(body)req.write(body);req.end();
 });
}
const json=r=>JSON.parse(r.body);
const pass=message=>console.log('PASS: '+message);
function sql(statement) {
 const result=spawnSync('docker',['compose','--env-file','INFRASTRUCTURE/LOCAL/.env','-f','INFRASTRUCTURE/LOCAL/compose.yaml','exec','-T','mysql','sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names'],{cwd:fileURLToPath(root),input:statement,encoding:'utf8',windowsHide:true});
 assert.equal(result.status,0,'Local test database operation failed; output suppressed.');
 return result.stdout.trim();
}
function membership(enabled) {
 const state=enabled?'ACTIVE':'SUSPENDED';
 sql(`USE CJORA_IDENTITY_MANAGEMENT; START TRANSACTION;
 UPDATE MEMBERSHIP SET STATUS='${state}',UPDATED_DATETIME=UTC_TIMESTAMP(6),ROW_VERSION=ROW_VERSION+1 WHERE UUID='c6a8a500-0000-4000-8000-000000000004';
 INSERT INTO API_LOG (UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CREATED_DATETIME)
 VALUES(UUID(),'WORKLOAD','00000000-0000-0000-0000-000000000001','login-integration-fixture',UUID(),'TEST_MEMBERSHIP_CHANGED','MEMBERSHIP','c6a8a500-0000-4000-8000-000000000004','UPDATE','SUCCEEDED',UTC_TIMESTAMP(6));COMMIT;`);
}
const origin='https://localhost:7000';
async function csrf(){const r=await request('/api/v1/idm/csrf');assert.equal(r.status,200,'CSRF endpoint');return json(r).requestToken;}
async function begin(returnPath='/api/v1/cor/customers/me'){
 const token=await csrf();
 return request('/api/v1/idm/sessions',{method:'POST',headers:{Origin:origin,'X-CSRF-TOKEN':token,'Content-Type':'application/json'},body:JSON.stringify({programKey:'cjora-demo',email:'alex@example.test',returnPath})});
}
try {
 let r=await request('http://localhost:5075/INTERNAL/sessions/revoke',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({authSessionUuid:'00000000-0000-4000-8000-000000000099',operationUuid:'00000000-0000-4000-8000-000000000098'}),cookies:false});
 assert.equal(r.status,403);pass('mixed-case internal Core route requires workload authentication');
 r=await request('http://localhost:5076/internal/sessions/validate',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}',cookies:false});
 assert.equal(r.status,403);pass('direct Identity Management access rejected');
 r=await request('http://localhost:5075/API/v1/cor/customers/me',{cookies:false});assert.equal(r.status,403);pass('direct Core profile requires Gateway');
 r=await request('/api/v1/cor/customers/me');assert.equal(r.status,401);pass('unauthenticated profile rejected');
 r=await request('/api/v1/idm/sessions',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json'},body:JSON.stringify({programKey:'cjora-demo',email:'alex@example.test',returnPath:'/api/v1/cor/customers/me'})});
 assert.equal(r.status,403);pass('missing CSRF token rejected');
 r=await request('/api/v1/idm/sessions',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json'},body:'{broken'});
 assert.equal(r.status,400);pass('malformed JSON receives a safe client error');
 r=await begin('https://attacker.example/');assert.equal(r.status,400);pass('open return URL rejected');
 r=await begin();assert.equal(r.status,200,'login start');
 const authorization=new URL(json(r).authorizationUrl);
 assert.equal(authorization.origin,'http://localhost:8080');assert.equal(authorization.searchParams.get('code_challenge_method'),'S256');
 assert.equal(authorization.searchParams.get('response_type'),'code');
 r=await request(authorization);
 for(let i=0;i<5&&r.headers.location;i++)r=await request(new URL(r.headers.location,authorization));
 assert.equal(r.status,200,'Keycloak login form');
 const action=r.body.match(/<form[^>]+action="([^"]+)"/)?.[1]?.replaceAll('&amp;','&');
 assert(action,'Keycloak form action'); const values=new URLSearchParams({username:'alex@example.test',password,credentialId:''});
 for(const input of r.body.matchAll(/<input\b[^>]*>/g)){
  if(!/type="hidden"/.test(input[0]))continue;
  const name=input[0].match(/name="([^"]+)"/)?.[1];const value=input[0].match(/value="([^"]*)"/)?.[1]??'';
  if(name)values.set(name,value.replaceAll('&amp;','&'));
 }
 r=await request(action,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:values.toString()});
 if(!r.headers.location){console.error('Provider form status: '+r.status+'; required action: '+(/required-action/.test(r.body))+'; invalid credentials: '+(/Invalid username or password/.test(r.body))+'; profile update: '+(/Update Account Information/.test(r.body))+'; missing cookie: '+(/Cookie not found/.test(r.body))+'; invalid code: '+(/Invalid code/.test(r.body))+'; expired: '+(/expired/i.test(r.body)));} assert(r.headers.location,'Keycloak successful login redirect');
 let callback;let current=new URL(action);
 for(let i=0;i<8&&r.headers.location;i++){
  const location=new URL(r.headers.location,current);current=location;
  if(location.pathname==='/api/v1/idm/sessions/callback')callback=location.href;
  r=await request(location);
 }
 assert.equal(r.status,200,'authenticated profile response');
 assert.equal(json(r).email,'alex@example.test');assert(finalCookie,'Core cookie issued');pass('Keycloak code + PKCE login creates a linked session and own profile');
 const hash=createHash('sha256').update(finalCookie.slice(finalCookie.indexOf('=')+1)).digest('hex');
 const linked=sql(`SELECT COUNT(*) FROM CJORA_CORE.CORE_SESSION C JOIN CJORA_IDENTITY_MANAGEMENT.AUTH_SESSION A ON A.UUID=C.AUTH_SESSION_UUID AND A.CORE_SESSION_UUID=C.UUID AND A.OPERATION_UUID=C.OPERATION_UUID AND A.PROGRAM_UUID='c6a8a500-0000-4000-8000-000000000001' WHERE C.SESSION_SECRET_HASH=UNHEX('${hash}') AND C.STATE='ACTIVE' AND A.STATE='ACTIVE';`);
 assert.equal(linked,'1');pass('database sessions share operation, Program and reciprocal session UUIDs');
 membership(false);
 try {r=await request('/api/v1/cor/customers/me');assert.equal(r.status,403);pass('membership suspension immediately denies an existing session');}
 finally {membership(true);}
 r=await request('/api/v1/cor/customers/me',{headers:{'X-Program-UUID':'00000000-0000-4000-8000-000000000099'}});assert.equal(r.status,403);pass('cross-Program scope rejected');
 r=await request(callback);assert.equal(r.status,303);assert.equal(r.headers.location,'/app/login-failed');pass('callback replay rejected');
 r=await request('/api/v1/cor/customers/me');assert.equal(r.status,200);pass('callback replay does not revoke the successful session');
 const token=await csrf();
 r=await request('/api/v1/idm/sessions/current',{method:'DELETE',headers:{Origin:'https://attacker.example','X-CSRF-TOKEN':token}});assert.equal(r.status,403);pass('foreign Origin logout rejected');
 r=await request('/api/v1/idm/sessions/current',{method:'DELETE',headers:{Origin:origin,'X-CSRF-TOKEN':token}});
 assert.equal(r.status,204,'logout');pass('logout revokes both linked sessions and clears the browser cookie');
 r=await request('/api/v1/cor/customers/me',{cookies:false,headers:{Cookie:finalCookie}});assert.equal(r.status,401);pass('replayed old cookie denied after logout');
 assert.equal(sql(`SELECT COUNT(*) FROM CJORA_CORE.CORE_SESSION C JOIN CJORA_IDENTITY_MANAGEMENT.AUTH_SESSION A ON A.UUID=C.AUTH_SESSION_UUID WHERE C.SESSION_SECRET_HASH=UNHEX('${hash}') AND C.STATE='REVOKED' AND A.STATE='REVOKED';`),'1');
 assert.equal(sql(`SELECT COUNT(*) FROM CJORA_CORE.API_LOG L JOIN CJORA_CORE.CORE_SESSION C ON L.ENTITY_UUID=C.UUID WHERE C.SESSION_SECRET_HASH=UNHEX('${hash}') AND L.OPERATION_NAME IN ('CORE_SESSION_CREATED','CORE_SESSION_ACTIVATED','CORE_SESSION_REVOKED');`),'3');
 assert.equal(sql(`SELECT COUNT(*) FROM CJORA_IDENTITY_MANAGEMENT.API_LOG L JOIN CJORA_IDENTITY_MANAGEMENT.AUTH_SESSION A ON L.ENTITY_UUID=A.UUID JOIN CJORA_CORE.CORE_SESSION C ON C.AUTH_SESSION_UUID=A.UUID WHERE C.SESSION_SECRET_HASH=UNHEX('${hash}') AND L.OPERATION_NAME IN ('AUTH_SESSION_CREATED','AUTH_SESSION_ACTIVATED','AUTH_SESSION_REVOKED');`),'3');
 pass('creation, activation and revocation are audited in each owning database');
 const second=await csrf();r=await request('/api/v1/idm/sessions/current',{method:'DELETE',headers:{Origin:origin,'X-CSRF-TOKEN':second}});assert.equal(r.status,204);pass('logout is idempotent');
 // Simulate a lost acknowledgement after revocation. No session is reactivated.
 sql(`INSERT INTO CJORA_CORE.SESSION_REVOCATION_OUTBOX
 (UUID,CORE_SESSION_ID,AUTH_SESSION_UUID,OPERATION_UUID,STATE,NEXT_ATTEMPT_DATETIME,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
 SELECT UUID(),ID,AUTH_SESSION_UUID,UUID(),'PENDING',UTC_TIMESTAMP(6),'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6),'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6)
 FROM CJORA_CORE.CORE_SESSION WHERE SESSION_SECRET_HASH=UNHEX('${hash}');`);
 let recovered=false;
 for(let attempt=0;attempt<30;attempt++){
  const count=sql(`SELECT COUNT(*) FROM CJORA_CORE.SESSION_REVOCATION_OUTBOX O JOIN CJORA_CORE.CORE_SESSION C ON C.ID=O.CORE_SESSION_ID
   JOIN CJORA_IDENTITY_MANAGEMENT.AUTH_SESSION A ON A.UUID=C.AUTH_SESSION_UUID
   JOIN CJORA_IDENTITY_MANAGEMENT.SESSION_WORKFLOW W ON W.AUTH_SESSION_ID=A.ID AND W.ACTION_CODE='REVOKE'
   WHERE C.SESSION_SECRET_HASH=UNHEX('${hash}') AND O.STATE='COMPLETED' AND W.STATE='COMPLETED' AND C.STATE='REVOKED' AND A.STATE='REVOKED';`);
  if(count==='1'){recovered=true;break;}
  await new Promise(resolve=>setTimeout(resolve,1000));
 }
 assert(recovered,'Both durable revocation workers must complete their pending work.');
 assert.equal(sql(`SELECT COUNT(*) FROM CJORA_CORE.API_LOG L JOIN CJORA_CORE.CORE_SESSION C ON L.ENTITY_UUID=C.UUID WHERE C.SESSION_SECRET_HASH=UNHEX('${hash}') AND L.OPERATION_NAME='CORE_SESSION_REVOKED';`),'1');
 pass('durable Core and Identity Management revocation workers recover idempotently');
} catch(error) {
 console.error('FAIL: '+error.message);
 process.exitCode=1;
}
