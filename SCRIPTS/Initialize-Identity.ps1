# Development only: operator provisioning, never a customer password-grant login.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$previousEncoding=$OutputEncoding
$OutputEncoding=New-Object System.Text.UTF8Encoding($false)
Push-Location $root
try {
 & "$PSScriptRoot/Initialize-CoreDatabase.ps1" -ApplyMigrations
 $path=Join-Path $root 'INFRASTRUCTURE/LOCAL/.env'
 $content=[IO.File]::ReadAllText($path)
 $secrets=@{}
 foreach($line in $content -split '?
') {if($line -match '^([A-Z_]+)=(.*)$') {$secrets[$Matches[1]]=$Matches[2]}}
 foreach($name in @('CJORA_IDENTITY_MANAGEMENT_MIGRATION_PASSWORD','CJORA_IDENTITY_MANAGEMENT_RUNTIME_PASSWORD','CJORA_CORE_IDENTITY_PASSWORD','CJORA_CORE_IDENTITY_TOKEN','CJORA_DEMO_USER_PASSWORD')) {
  if(!$secrets.ContainsKey($name)) {
   $bytes=New-Object byte[] 32;$generator=[Security.Cryptography.RandomNumberGenerator]::Create()
   try {$generator.GetBytes($bytes)} finally {$generator.Dispose()}
   $secrets[$name]=[BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()
   $content=$content.TrimEnd()+"`n$name=$($secrets[$name])`n"
  }
  if($secrets[$name] -notmatch '^[a-f0-9]{64}$') {throw 'Generated identity secrets must be 64 hex characters. Values suppressed.'}
 }
 [IO.File]::WriteAllText($path,$content,(New-Object System.Text.UTF8Encoding($false)))
 function Invoke-LocalSql([string]$sql,[switch]$Read) {
  $result=$sql | docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
  if($LASTEXITCODE -ne 0) {throw 'Local identity SQL failed. Output suppressed to protect credentials.'}
  if($Read){return $result}
 }
 $migrator=$secrets['CJORA_IDENTITY_MANAGEMENT_MIGRATION_PASSWORD'];$idmPassword=$secrets['CJORA_IDENTITY_MANAGEMENT_RUNTIME_PASSWORD'];$corePassword=$secrets['CJORA_CORE_IDENTITY_PASSWORD']
 Invoke-LocalSql @"
CREATE DATABASE IF NOT EXISTS CJORA_IDENTITY_MANAGEMENT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
CREATE USER IF NOT EXISTS 'cjora_idm_migrator'@'%' IDENTIFIED BY '$migrator';
GRANT SELECT,INSERT,UPDATE,DELETE,CREATE,ALTER,DROP,INDEX,REFERENCES ON CJORA_IDENTITY_MANAGEMENT.* TO 'cjora_idm_migrator'@'%';
CREATE USER IF NOT EXISTS 'cjora_idm_runtime'@'%' IDENTIFIED BY '$idmPassword';
CREATE USER IF NOT EXISTS 'cjora_core_identity'@'%' IDENTIFIED BY '$corePassword';
"@
 $previousConnection=$env:CJORA_IDENTITY_MANAGEMENT_MIGRATION_CONNECTION
 try {
  $env:CJORA_IDENTITY_MANAGEMENT_MIGRATION_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_IDENTITY_MANAGEMENT;User ID=cjora_idm_migrator;Password=$migrator;SslMode=Disabled"
  dotnet run --project SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.Migrations -- --apply
  if($LASTEXITCODE -ne 0) {throw 'Identity Management migrations failed; investigate before retrying interrupted DDL.'}
 } finally {$env:CJORA_IDENTITY_MANAGEMENT_MIGRATION_CONNECTION=$previousConnection}
 Invoke-LocalSql @"
GRANT SELECT ON CJORA_IDENTITY_MANAGEMENT.USER_ACCOUNT TO 'cjora_idm_runtime'@'%';
GRANT SELECT ON CJORA_IDENTITY_MANAGEMENT.MEMBERSHIP TO 'cjora_idm_runtime'@'%';
GRANT SELECT ON CJORA_IDENTITY_MANAGEMENT.MEMBERSHIP_PERMISSION TO 'cjora_idm_runtime'@'%';
GRANT SELECT,INSERT,UPDATE ON CJORA_IDENTITY_MANAGEMENT.AUTH_SESSION TO 'cjora_idm_runtime'@'%';
GRANT SELECT,INSERT,UPDATE ON CJORA_IDENTITY_MANAGEMENT.SESSION_WORKFLOW TO 'cjora_idm_runtime'@'%';
GRANT INSERT ON CJORA_IDENTITY_MANAGEMENT.API_LOG TO 'cjora_idm_runtime'@'%';
GRANT INSERT ON CJORA_IDENTITY_MANAGEMENT.ERROR_LOGS TO 'cjora_idm_runtime'@'%';
GRANT SELECT ON CJORA_CORE.PROGRAM TO 'cjora_core_identity'@'%';
GRANT SELECT ON CJORA_CORE.CUSTOMER TO 'cjora_core_identity'@'%';
GRANT SELECT,INSERT,UPDATE ON CJORA_CORE.CORE_SESSION TO 'cjora_core_identity'@'%';
GRANT SELECT,INSERT,UPDATE ON CJORA_CORE.SESSION_REVOCATION_OUTBOX TO 'cjora_core_identity'@'%';
GRANT INSERT ON CJORA_CORE.API_LOG TO 'cjora_core_identity'@'%';
GRANT INSERT ON CJORA_CORE.ERROR_LOGS TO 'cjora_core_identity'@'%';
"@
 $base='http://localhost:8080'
 try {
  $stage='admin-token';$token=Invoke-RestMethod "$base/realms/master/protocol/openid-connect/token" -Method Post -Body @{grant_type='password';client_id='admin-cli';username=$secrets['KEYCLOAK_ADMIN_USERNAME'];password=$secrets['KEYCLOAK_ADMIN_PASSWORD']}
  $headers=@{Authorization="Bearer $($token.access_token)"}
  $admin="$base/admin/realms/cjora"
  $stage='client-lookup';$clients=@(Invoke-RestMethod "$admin/clients?clientId=cjora-web" -Headers $headers | ForEach-Object { $_ })
  $client=@{clientId='cjora-web';name='CJora local browser login';enabled=$true;publicClient=$false;standardFlowEnabled=$true;directAccessGrantsEnabled=$false;implicitFlowEnabled=$false;serviceAccountsEnabled=$false;redirectUris=@('https://localhost:7000/api/v1/idm/sessions/callback');webOrigins=@('https://localhost:7000');attributes=@{'pkce.code.challenge.method'='S256'}}
  if($clients.Count -eq 0) {
   Invoke-RestMethod "$admin/clients" -Method Post -Headers $headers -ContentType 'application/json' -Body ($client|ConvertTo-Json -Depth 8) | Out-Null
   $stage='client-lookup';$clients=@(Invoke-RestMethod "$admin/clients?clientId=cjora-web" -Headers $headers | ForEach-Object { $_ })
  } else {
   Invoke-RestMethod "$admin/clients/$($clients[0].id)" -Method Put -Headers $headers -ContentType 'application/json' -Body ($client|ConvertTo-Json -Depth 8) | Out-Null
  }
  $clientSecret=(Invoke-RestMethod "$admin/clients/$($clients[0].id)/client-secret" -Headers $headers | ForEach-Object { $_ }).value
  $stage='user-lookup';$users=@(Invoke-RestMethod "$admin/users?username=alex%40example.test&exact=true" -Headers $headers | ForEach-Object { $_ })
  if($users.Count -eq 0) {
   $user=@{username='alex@example.test';email='alex@example.test';emailVerified=$true;enabled=$true;firstName='Alex';lastName='Demo';credentials=@(@{type='password';value=$secrets['CJORA_DEMO_USER_PASSWORD'];temporary=$false})}
   Invoke-RestMethod "$admin/users" -Method Post -Headers $headers -ContentType 'application/json' -Body ($user|ConvertTo-Json -Depth 8) | Out-Null
   $stage='user-lookup';$users=@(Invoke-RestMethod "$admin/users?username=alex%40example.test&exact=true" -Headers $headers | ForEach-Object { $_ })
  }
  $subject=$users[0].id
  if($subject -notmatch '^[0-9a-f-]{36}$') {throw 'Unexpected provider subject.'}
 } catch { $status=if($_.Exception.Response){[int]$_.Exception.Response.StatusCode}else{0};throw "Keycloak operator provisioning failed at $stage (HTTP $status). Provider response suppressed." }
 # Known fictional fixtures only; identity linkage is explicit issuer/subject, never email matching.
 $program='c6a8a500-0000-4000-8000-000000000001';$userUuid='c6a8a500-0000-4000-8000-000000000002';$customer='c6a8a500-0000-4000-8000-000000000003'
 $actor='00000000-0000-0000-0000-000000000001'
 function Audit-Seed([string]$entity,[string]$uuid) {
  return "INSERT INTO API_LOG (UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CREATED_DATETIME) SELECT UUID(),'WORKLOAD','$actor','local-identity-fixture',UUID(),'LOCAL_FIXTURE_CREATED','$entity','$uuid','CREATE','SUCCEEDED',UTC_TIMESTAMP(6) WHERE @changed=1;"
 }
 $sql=@"
USE CJORA_CORE;
START TRANSACTION;
INSERT INTO PROGRAM (UUID,PROGRAM_KEY,DISPLAY_NAME,IS_ENABLED,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
 SELECT '$program','cjora-demo','CJora Demo',1,'$actor',UTC_TIMESTAMP(6),'$actor',UTC_TIMESTAMP(6) WHERE NOT EXISTS(SELECT 1 FROM PROGRAM WHERE UUID='$program');
SET @changed=ROW_COUNT();
$(Audit-Seed 'PROGRAM' $program)
INSERT INTO CUSTOMER (UUID,PROGRAM_ID,USER_UUID,DISPLAY_NAME,EMAIL,STATUS,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
 SELECT '$customer',ID,'$userUuid','Alex Demo','alex@example.test','ACTIVE','$actor',UTC_TIMESTAMP(6),'$actor',UTC_TIMESTAMP(6) FROM PROGRAM
 WHERE UUID='$program' AND NOT EXISTS(SELECT 1 FROM CUSTOMER WHERE UUID='$customer');
SET @changed=ROW_COUNT();
$(Audit-Seed 'CUSTOMER' $customer)
COMMIT;
USE CJORA_IDENTITY_MANAGEMENT;
START TRANSACTION;
INSERT INTO USER_ACCOUNT (UUID,ISSUER,SUBJECT,EMAIL,EMAIL_VERIFIED,IS_ENABLED,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
 SELECT '$userUuid','http://localhost:8080/realms/cjora','$subject','alex@example.test',1,1,'$actor',UTC_TIMESTAMP(6),'$actor',UTC_TIMESTAMP(6) WHERE NOT EXISTS(SELECT 1 FROM USER_ACCOUNT WHERE UUID='$userUuid');
SET @changed=ROW_COUNT();
$(Audit-Seed 'USER_ACCOUNT' $userUuid)
INSERT INTO MEMBERSHIP (UUID,USER_ACCOUNT_ID,PROGRAM_UUID,STATUS,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
 SELECT 'c6a8a500-0000-4000-8000-000000000004',ID,'$program','ACTIVE','$actor',UTC_TIMESTAMP(6),'$actor',UTC_TIMESTAMP(6) FROM USER_ACCOUNT
 WHERE UUID='$userUuid' AND NOT EXISTS(SELECT 1 FROM MEMBERSHIP WHERE PROGRAM_UUID='$program' AND USER_ACCOUNT_ID=USER_ACCOUNT.ID);
SET @changed=ROW_COUNT();
$(Audit-Seed 'MEMBERSHIP' 'c6a8a500-0000-4000-8000-000000000004')
INSERT INTO MEMBERSHIP_PERMISSION (UUID,MEMBERSHIP_ID,PERMISSION_CODE,IS_ENABLED,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
 SELECT 'c6a8a500-0000-4000-8000-000000000005',ID,'CUSTOMERS.PROFILE.READ',1,'$actor',UTC_TIMESTAMP(6),'$actor',UTC_TIMESTAMP(6) FROM MEMBERSHIP
 WHERE UUID='c6a8a500-0000-4000-8000-000000000004' AND NOT EXISTS(SELECT 1 FROM MEMBERSHIP_PERMISSION WHERE MEMBERSHIP_ID=MEMBERSHIP.ID AND PERMISSION_CODE='CUSTOMERS.PROFILE.READ');
SET @changed=ROW_COUNT();
$(Audit-Seed 'MEMBERSHIP_PERMISSION' 'c6a8a500-0000-4000-8000-000000000005')
COMMIT;
"@
 Invoke-LocalSql $sql
 $linked=Invoke-LocalSql "SELECT COUNT(*) FROM CJORA_IDENTITY_MANAGEMENT.USER_ACCOUNT WHERE UUID='$userUuid' AND ISSUER='http://localhost:8080/realms/cjora' AND SUBJECT='$subject';" -Read
 if("$linked" -ne '1'){throw 'Existing demo identity links to a different provider subject. Investigate explicitly; no automatic relinking by email is allowed.'}
 $coreConnection="Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_identity;Password=$corePassword;SslMode=Disabled;Connection Timeout=3"
 $idmConnection="Server=127.0.0.1;Port=3307;Database=CJORA_IDENTITY_MANAGEMENT;User ID=cjora_idm_runtime;Password=$idmPassword;SslMode=Disabled;Connection Timeout=3"
 @{'Identity:Enabled'='true';'Identity:Authority'="$base/realms/cjora";'Identity:PublicOrigin'='https://localhost:7000';'Identity:ManagementAddress'='http://localhost:5076/';'Identity:CallerToken'=$secrets['CJORA_CORE_IDENTITY_TOKEN'];'ConnectionStrings:CoreIdentity'=$coreConnection} | ConvertTo-Json | dotnet user-secrets set --project SRC/CORE/CJora.Core.Api
 if($LASTEXITCODE -ne 0) {throw 'Could not configure Core User Secrets.'}
 @{'Identity:Enabled'='true';'Identity:Authority'="$base/realms/cjora";'Identity:PublicOrigin'='https://localhost:7000';'Identity:CoreAddress'='http://localhost:5075/';'Identity:CallerToken'=$secrets['CJORA_CORE_IDENTITY_TOKEN'];'Identity:ClientId'='cjora-web';'Identity:ClientSecret'=$clientSecret;'ConnectionStrings:Identity'=$idmConnection} | ConvertTo-Json | dotnet user-secrets set --project SRC/IDENTITY-MANAGEMENT/CJora.IdentityManagement.Api
 if($LASTEXITCODE -ne 0) {throw 'Could not configure Identity Management User Secrets.'}
 Write-Output 'Local identity configured. Fictional Program: cjora-demo. Demo email: alex@example.test. Password remains in ignored .env under CJORA_DEMO_USER_PASSWORD.'
} finally {$OutputEncoding=$previousEncoding;Pop-Location}
