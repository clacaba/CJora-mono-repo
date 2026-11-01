param(
 [ValidateSet('USD','PHP','SGD','JPY','KWD')][string]$DemoCurrency,
 [long]$MaxTransferMinor=0,
 [long]$DemoFundingMinor=0
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$previousEncoding=$OutputEncoding;$OutputEncoding=New-Object Text.UTF8Encoding($false)
Push-Location $root
$envNames=@('CJORA_FINANCE_VALIDATION_ADMIN','CJORA_TX_FINANCE_ADMIN','CJORA_CORE_CUSTOMER_CONNECTION','CJORA_CORE_OUTBOX_CONNECTION','CJORA_TX_SYNC_CONNECTION')
$previous=@{};foreach($name in $envNames){$previous[$name]=[Environment]::GetEnvironmentVariable($name)}
try{
 if($DemoCurrency-and($MaxTransferMinor-le0-or$DemoFundingMinor-le0)){throw 'Explicit positive MaxTransferMinor and DemoFundingMinor are required with DemoCurrency. Amounts are fictional minor units.'}
 $path=Join-Path $root 'INFRASTRUCTURE/LOCAL/.env'
 $content=[IO.File]::ReadAllText($path);$secrets=@{}
 foreach($line in [IO.File]::ReadAllLines($path)){if($line-match'^([A-Z_]+)=(.*)$'){$secrets[$Matches[1]]=$Matches[2]}}
 foreach($required in @('MYSQL_ROOT_PASSWORD','CJORA_CORE_CUSTOMER_PASSWORD','CJORA_CORE_OUTBOX_PASSWORD','CJORA_TRANSACTIONS_RUNTIME_PASSWORD','CJORA_TRANSACTIONS_MIGRATION_PASSWORD')){
  if(!$secrets[$required]){throw 'Complete local identity and customer synchronization initialization first.'}
 }
 foreach($name in @('CJORA_CORE_TRANSACTIONS_TOKEN','CJORA_TRANSACTIONS_FINANCE_PASSWORD')){
  if(!$secrets.ContainsKey($name)){
   $bytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
   $secrets[$name]=[BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant();$content=$content.TrimEnd()+"`n$name=$($secrets[$name])`n"
  }
  if($secrets[$name]-notmatch'^[a-f0-9]{64}$'){throw 'Finance secrets must be independently generated 64-character hex strings.'}
 }
 [IO.File]::WriteAllText($path,$content,(New-Object Text.UTF8Encoding($false)))
 function Sql([string]$query){
  $output=$query|docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
  if($LASTEXITCODE-ne0){throw 'Finance provisioning SQL failed. Credential-bearing output suppressed.'}
 }
 Sql "CREATE USER IF NOT EXISTS 'cjora_tx_finance_definer'@'%' ACCOUNT LOCK;"
 $env:CJORA_FINANCE_VALIDATION_ADMIN="Server=127.0.0.1;Port=3307;Database=mysql;User ID=root;Password=$($secrets['MYSQL_ROOT_PASSWORD']);SslMode=Disabled;Allow User Variables=true"
 dotnet run --project SCRIPTS/CJora.Finance.Checks -- --validate-schema
 if($LASTEXITCODE-ne0){throw 'Financial schema validation failed; no service migration applied.'}
 & "$PSScriptRoot/Initialize-CustomerSync.ps1"
 $password=$secrets['CJORA_TRANSACTIONS_FINANCE_PASSWORD']
 Sql "CREATE USER IF NOT EXISTS 'cjora_tx_finance'@'%' IDENTIFIED BY '$password';"
 foreach($table in @('ACCOUNT','TRANSFER_INTENT','FINANCIAL_REQUEST','TRANSFER','LEDGER_JOURNAL','LEDGER_ENTRY')){
  Sql "GRANT SELECT ON CJORA_TRANSACTIONS.$table TO 'cjora_tx_finance'@'%';"
 }
 foreach($table in @('API_LOG','ERROR_LOGS')){Sql "GRANT INSERT ON CJORA_TRANSACTIONS.$table TO 'cjora_tx_finance'@'%';"}
 foreach($routine in @('FINANCE_PREPARE','FINANCE_CONFIRM','FINANCE_RECONCILE')){
  Sql "GRANT EXECUTE ON PROCEDURE CJORA_TRANSACTIONS.$routine TO 'cjora_tx_finance'@'%';"
 }
 $runtime="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_finance;Password=$password;SslMode=Disabled;Connection Timeout=3;Default Command Timeout=10"
 @{'Finance:Enabled'='true';'Finance:CallerToken'=$secrets['CJORA_CORE_TRANSACTIONS_TOKEN']}|ConvertTo-Json|dotnet user-secrets set --project SRC/CORE/CJora.Core.Api
 if($LASTEXITCODE-ne0){throw 'Core finance User Secrets configuration failed.'}
 @{'Finance:Enabled'='true';'Finance:CallerToken'=$secrets['CJORA_CORE_TRANSACTIONS_TOKEN'];'ConnectionStrings:TransactionsFinance'=$runtime}|ConvertTo-Json|dotnet user-secrets set --project SRC/TRANSACTIONS/CJora.Transactions.Api
 if($LASTEXITCODE-ne0){throw 'Transactions finance User Secrets configuration failed.'}
 if($DemoCurrency){
  $env:CJORA_CORE_CUSTOMER_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_customer_operator;Password=$($secrets['CJORA_CORE_CUSTOMER_PASSWORD']);SslMode=Disabled;Connection Timeout=3"
  $env:CJORA_CORE_OUTBOX_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_outbox;Password=$($secrets['CJORA_CORE_OUTBOX_PASSWORD']);SslMode=Disabled;Connection Timeout=3"
  $env:CJORA_TX_SYNC_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_sync;Password=$($secrets['CJORA_TRANSACTIONS_RUNTIME_PASSWORD']);SslMode=Disabled;Connection Timeout=3"
  $env:CJORA_TX_FINANCE_ADMIN="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_migrator;Password=$($secrets['CJORA_TRANSACTIONS_MIGRATION_PASSWORD']);SslMode=Disabled;Connection Timeout=3"
  dotnet run --project SCRIPTS/CJora.Finance.Checks -- --bootstrap-demo $DemoCurrency "$MaxTransferMinor" "$DemoFundingMinor"
  if($LASTEXITCODE-ne0){throw 'Explicit fictional finance fixture provisioning failed.'}
  foreach($permission in @('ACCOUNTS.READ','TRANSFERS.CREATE','TRANSACTIONS.READ')){
   Sql @"
USE CJORA_IDENTITY_MANAGEMENT;
START TRANSACTION;
SET @permission_uuid=UUID();
INSERT INTO MEMBERSHIP_PERMISSION(UUID,MEMBERSHIP_ID,PERMISSION_CODE,IS_ENABLED,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
SELECT @permission_uuid,ID,'$permission',1,'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6),'00000000-0000-0000-0000-000000000001',UTC_TIMESTAMP(6)
FROM MEMBERSHIP M WHERE M.UUID='c6a8a500-0000-4000-8000-000000000004' AND M.STATUS='ACTIVE'
AND NOT EXISTS(SELECT 1 FROM MEMBERSHIP_PERMISSION P WHERE P.MEMBERSHIP_ID=M.ID AND P.PERMISSION_CODE='$permission');
SET @changed=ROW_COUNT();
INSERT INTO API_LOG(UUID,PROGRAM_UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CREATED_DATETIME)
SELECT UUID(),'c6a8a500-0000-4000-8000-000000000001','WORKLOAD','00000000-0000-0000-0000-000000000001','explicit-demo-finance',UUID(),'DEMO_FINANCIAL_PERMISSION_GRANTED','MEMBERSHIP_PERMISSION',@permission_uuid,'CREATE','SUCCEEDED',UTC_TIMESTAMP(6) WHERE @changed=1;
COMMIT;
"@
  }
 }
 Write-Output 'Development financial service configured. Monetary mutation is routine-only. Demo policy, permissions and fictional funds are created only when explicitly requested.'
}finally{
 foreach($name in $envNames){[Environment]::SetEnvironmentVariable($name,$previous[$name])}
 $OutputEncoding=$previousEncoding;Pop-Location
}
