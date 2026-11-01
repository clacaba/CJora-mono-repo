$ErrorActionPreference='Stop'
$previousOutputEncoding=$OutputEncoding
$OutputEncoding=New-Object System.Text.UTF8Encoding($false)
$root=Split-Path $PSScriptRoot -Parent
function Invoke-LocalSql([string]$sql) {
 $output=$sql | docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
 if ($LASTEXITCODE -ne 0) { throw 'Local Gateway database setup failed; raw output suppressed. Inspect securely before retrying.' }
 return $output
}
Push-Location $root
try {
 # Archive the immutable historical Core registry before moving its live ownership to Gateway.
 & (Join-Path $PSScriptRoot 'Initialize-CoreDatabase.ps1') -ApplyMigrations
 if (-not $?) { throw 'Core migration prerequisite failed.' }
 $envPath=Join-Path $root 'INFRASTRUCTURE/LOCAL/.env'
 $content=[System.IO.File]::ReadAllText($envPath)
 $values=@{}
 foreach ($line in $content -split '\r?\n') { if ($line -match '^([A-Z_]+)=(.*)$') { $values[$Matches[1]]=$Matches[2] } }
 foreach ($name in @('CJORA_GATEWAY_MIGRATION_PASSWORD','CJORA_GATEWAY_RUNTIME_PASSWORD','CJORA_GATEWAY_CORE_TOKEN')) {
  if (-not $values.ContainsKey($name)) {
   $bytes=New-Object byte[] 32
   $random=[Security.Cryptography.RandomNumberGenerator]::Create()
   try { $random.GetBytes($bytes) } finally { $random.Dispose() }
   $values[$name]=[BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()
   $content=$content.TrimEnd()+"`n$name=$($values[$name])`n"
  }
  if ($values[$name] -notmatch '^[a-f0-9]{64}$') { throw 'Gateway credentials must be generated 64-character hex values; no secrets displayed.' }
 }
 [System.IO.File]::WriteAllText($envPath,$content,(New-Object System.Text.UTF8Encoding($false)))
 $migrationPassword=$values['CJORA_GATEWAY_MIGRATION_PASSWORD']
 $runtimePassword=$values['CJORA_GATEWAY_RUNTIME_PASSWORD']
 $token=$values['CJORA_GATEWAY_CORE_TOKEN']
 $create=@"
CREATE DATABASE IF NOT EXISTS CJORA_GATEWAY CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
CREATE USER IF NOT EXISTS 'cjora_gateway_migrator'@'%' IDENTIFIED BY '$migrationPassword';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON CJORA_GATEWAY.* TO 'cjora_gateway_migrator'@'%';
CREATE USER IF NOT EXISTS 'cjora_gateway_runtime'@'%' IDENTIFIED BY '$runtimePassword';
"@
 Invoke-LocalSql $create | Out-Null
 $previousConnection=$env:CJORA_GATEWAY_MIGRATION_CONNECTION
 try {
  $env:CJORA_GATEWAY_MIGRATION_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_GATEWAY;User ID=cjora_gateway_migrator;Password=$migrationPassword;SslMode=Disabled;Connection Timeout=5"
  dotnet run --project SRC/GATEWAY/CJora.Gateway.Migrations -- --apply
  if ($LASTEXITCODE -ne 0) { throw 'Gateway migration failed. Do not retry an interrupted DDL script without inspection.' }
 } finally { $env:CJORA_GATEWAY_MIGRATION_CONNECTION=$previousConnection }
 $operation=[Guid]::NewGuid().ToString('D')
 $columns='UUID,SERVICE_NAME,METHOD_NAME,METHOD_HOST,METHOD_PATH,METHOD_DESCRIPTION,METHOD_STATUS,METHOD_REQUEST_TYPE,IS_DATATYPE_SENSITIVE,IS_TESTED,AUTHORIZATION_POLICY,DEVELOPMENT_ONLY,BODY_TYPE,MAX_REQUEST_BYTES,TIMEOUT_MS,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME,ROW_VERSION'
 $selected=($columns -split ',' | ForEach-Object { 'L.'+$_ }) -join ','
 # This one-time operator import preserves UUIDs/metadata but regenerates Gateway-local numeric IDs.
 # Existing Gateway rows are never overwritten; a conflicting alias with another UUID fails atomically.
 $import=@"
START TRANSACTION;
INSERT INTO CJORA_GATEWAY.API_LOG
(UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CHANGE_DETAILS,CREATED_DATETIME)
SELECT UUID(),'WORKLOAD','00000000-0000-0000-0000-000000000001','gateway-registry-handoff','$operation','IMPORT_METHOD_REGISTRY','SERVICE_METHOD',L.UUID,'CREATE','SUCCEEDED',JSON_OBJECT('source','Core legacy archive','service',L.SERVICE_NAME,'method',L.METHOD_NAME,'verb',L.METHOD_REQUEST_TYPE),UTC_TIMESTAMP(6)
FROM CJORA_CORE.SERVICE_METHOD_ARCHIVE L
WHERE NOT EXISTS (SELECT 1 FROM CJORA_GATEWAY.SERVICE_METHOD G WHERE G.UUID=L.UUID);
INSERT INTO CJORA_GATEWAY.SERVICE_METHOD ($columns)
SELECT $selected FROM CJORA_CORE.SERVICE_METHOD_ARCHIVE L
WHERE NOT EXISTS (SELECT 1 FROM CJORA_GATEWAY.SERVICE_METHOD G WHERE G.UUID=L.UUID);
COMMIT;
GRANT SELECT ON CJORA_GATEWAY.SERVICE_METHOD TO 'cjora_gateway_runtime'@'%';
GRANT INSERT ON CJORA_GATEWAY.ERROR_LOGS TO 'cjora_gateway_runtime'@'%';
"@
 Invoke-LocalSql $import | Out-Null
 $missing=Invoke-LocalSql 'SELECT COUNT(*) FROM CJORA_CORE.SERVICE_METHOD_ARCHIVE L LEFT JOIN CJORA_GATEWAY.SERVICE_METHOD G ON G.UUID=L.UUID WHERE G.UUID IS NULL;'
 if ([int]($missing | Select-Object -Last 1) -ne 0) { throw 'Gateway registry import is incomplete.' }
 $oldAccount=Invoke-LocalSql "SELECT COUNT(*) FROM mysql.user WHERE User='cjora_core_registry' AND Host='%';"
 if ([int]($oldAccount | Select-Object -Last 1) -gt 0) {
  Invoke-LocalSql "REVOKE ALL PRIVILEGES, GRANT OPTION FROM 'cjora_core_registry'@'%'; ALTER USER 'cjora_core_registry'@'%' ACCOUNT LOCK;" | Out-Null
 }
 $runtime="Server=127.0.0.1;Port=3307;Database=CJORA_GATEWAY;User ID=cjora_gateway_runtime;Password=$runtimePassword;SslMode=Disabled;Connection Timeout=3"
 @{ 'ConnectionStrings:GatewayRegistry'=$runtime; 'GatewayBridge:Token'=$token } | ConvertTo-Json | dotnet user-secrets set --project SRC/GATEWAY/CJora.Gateway.Api
 if ($LASTEXITCODE -ne 0) { throw 'Could not configure Gateway User Secrets.' }
 @{ 'GatewayBridge:Token'=$token } | ConvertTo-Json | dotnet user-secrets set --project SRC/CORE/CJora.Core.Api
 if ($LASTEXITCODE -ne 0) { throw 'Could not configure the local Core caller credential.' }
 $removedLegacySetting = dotnet user-secrets remove 'ConnectionStrings:CoreRegistry' --project SRC/CORE/CJora.Core.Api
 Write-Output 'Gateway owns CJORA_GATEWAY.SERVICE_METHOD with independent migrations and runtime credentials. Core receives authenticated local route context and cannot read the registry. No secrets displayed.'
} finally { $OutputEncoding=$previousOutputEncoding; Pop-Location }