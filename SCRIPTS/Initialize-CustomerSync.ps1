$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$previousEncoding=$OutputEncoding;$OutputEncoding=New-Object Text.UTF8Encoding($false)
Push-Location $root
try {
 & "$PSScriptRoot/Initialize-CoreDatabase.ps1" -ApplyMigrations
 $path=Join-Path $root 'INFRASTRUCTURE/LOCAL/.env';$content=[IO.File]::ReadAllText($path);$secrets=@{}
 foreach($line in [IO.File]::ReadAllLines($path)){if($line -match '^([A-Z_]+)=(.*)$'){$secrets[$Matches[1]]=$Matches[2]}}
 foreach($name in @('CJORA_CORE_OUTBOX_PASSWORD','CJORA_CORE_CUSTOMER_PASSWORD','CJORA_TRANSACTIONS_MIGRATION_PASSWORD','CJORA_TRANSACTIONS_RUNTIME_PASSWORD')){
  if(!$secrets.ContainsKey($name)){
   $bytes=New-Object byte[] 32;$random=[Security.Cryptography.RandomNumberGenerator]::Create();try{$random.GetBytes($bytes)}finally{$random.Dispose()}
   $secrets[$name]=[BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant();$content=$content.TrimEnd()+"`n$name=$($secrets[$name])`n"
  }
  if($secrets[$name] -notmatch '^[a-f0-9]{64}$'){throw 'Generated synchronization credentials must be 64 hex characters. Values suppressed.'}
 }
 [IO.File]::WriteAllText($path,$content,(New-Object Text.UTF8Encoding($false)))
 function Sql([string]$query){
  $result=$query | docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
  if($LASTEXITCODE-ne0){throw 'Synchronization SQL provisioning failed. Output suppressed.'}
 }
 $txMigration=$secrets['CJORA_TRANSACTIONS_MIGRATION_PASSWORD'];$txRuntime=$secrets['CJORA_TRANSACTIONS_RUNTIME_PASSWORD'];$outbox=$secrets['CJORA_CORE_OUTBOX_PASSWORD'];$writer=$secrets['CJORA_CORE_CUSTOMER_PASSWORD']
 Sql @"
CREATE DATABASE IF NOT EXISTS CJORA_TRANSACTIONS CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
CREATE USER IF NOT EXISTS 'cjora_tx_migrator'@'%' IDENTIFIED BY '$txMigration';
GRANT SELECT,INSERT,UPDATE,DELETE,CREATE,ALTER,DROP,INDEX,REFERENCES ON CJORA_TRANSACTIONS.* TO 'cjora_tx_migrator'@'%';
CREATE USER IF NOT EXISTS 'cjora_tx_sync'@'%' IDENTIFIED BY '$txRuntime';
CREATE USER IF NOT EXISTS 'cjora_core_outbox'@'%' IDENTIFIED BY '$outbox';
CREATE USER IF NOT EXISTS 'cjora_core_customer_operator'@'%' IDENTIFIED BY '$writer';
"@
 $previous=$env:CJORA_TRANSACTIONS_MIGRATION_CONNECTION
 try {
  $env:CJORA_TRANSACTIONS_MIGRATION_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_migrator;Password=$txMigration;SslMode=Disabled;Connection Timeout=5"
  dotnet run --project SRC/TRANSACTIONS/CJora.Transactions.Migrations -- --apply
  if($LASTEXITCODE-ne0){throw 'Transactions migration failed; inspect interrupted DDL before retrying.'}
 } finally {$env:CJORA_TRANSACTIONS_MIGRATION_CONNECTION=$previous}
 Sql @"
GRANT SELECT,INSERT,UPDATE ON CJORA_TRANSACTIONS.CUSTOMER_REFERENCE TO 'cjora_tx_sync'@'%';
GRANT SELECT,INSERT ON CJORA_TRANSACTIONS.CUSTOMER_EVENT_INBOX TO 'cjora_tx_sync'@'%';
GRANT SELECT,INSERT ON CJORA_TRANSACTIONS.CUSTOMER_EVENT_REJECTIONS TO 'cjora_tx_sync'@'%';
GRANT INSERT ON CJORA_TRANSACTIONS.API_LOG TO 'cjora_tx_sync'@'%';
GRANT INSERT ON CJORA_TRANSACTIONS.ERROR_LOGS TO 'cjora_tx_sync'@'%';
GRANT SELECT,UPDATE ON CJORA_CORE.CUSTOMER_EVENT_OUTBOX TO 'cjora_core_outbox'@'%';
GRANT SELECT ON CJORA_CORE.PROGRAM TO 'cjora_core_customer_operator'@'%';
GRANT SELECT,INSERT,UPDATE ON CJORA_CORE.CUSTOMER TO 'cjora_core_customer_operator'@'%';
GRANT SELECT,INSERT ON CJORA_CORE.CUSTOMER_EVENT_OUTBOX TO 'cjora_core_customer_operator'@'%';
GRANT INSERT ON CJORA_CORE.API_LOG TO 'cjora_core_customer_operator'@'%';
GRANT INSERT ON CJORA_CORE.ERROR_LOGS TO 'cjora_core_customer_operator'@'%';
"@
 $coreConnection="Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_outbox;Password=$outbox;SslMode=Disabled;Connection Timeout=3"
 $txConnection="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_sync;Password=$txRuntime;SslMode=Disabled;Connection Timeout=3"
 @{'CustomerSync:Enabled'='true';'ConnectionStrings:CoreOutbox'=$coreConnection}|ConvertTo-Json|dotnet user-secrets set --project SRC/CORE/CJora.Core.Api
 if($LASTEXITCODE-ne0){throw 'Could not configure Core outbox User Secrets.'}
 @{'CustomerSync:Enabled'='true';'ConnectionStrings:TransactionsSync'=$txConnection}|ConvertTo-Json|dotnet user-secrets set --project SRC/TRANSACTIONS/CJora.Transactions.Api
 if($LASTEXITCODE-ne0){throw 'Could not configure Transactions User Secrets.'}
 docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml up -d kafka
 if($LASTEXITCODE-ne0){throw 'Could not start Kafka.'}
 $ready=$false
 for($i=0;$i-lt30;$i++){
  $status=docker inspect --format '{{.State.Health.Status}}' cjora-local-kafka-1
  if($status-eq'healthy'){$ready=$true;break}
  Start-Sleep -Seconds 1
 }
 if(!$ready){throw 'Kafka is not ready yet. Inspect the local container health.'}
 docker exec cjora-local-kafka-1 /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic cjora.core.customer-snapshots.v1 --partitions 3 --replication-factor 1 --config retention.ms=604800000 --config max.message.bytes=16384 --config cleanup.policy=delete
 if($LASTEXITCODE-ne0){throw 'Could not create the customer topic.'}
 $previous=$env:CJORA_CORE_CUSTOMER_CONNECTION
 try{
  $env:CJORA_CORE_CUSTOMER_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_customer_operator;Password=$writer;SslMode=Disabled;Connection Timeout=3"
  dotnet run --project SRC/CORE/CJora.Core.Customers -- --reconcile
  if($LASTEXITCODE-ne0){throw 'Customer snapshot reconciliation failed.'}
 }finally{$env:CJORA_CORE_CUSTOMER_CONNECTION=$previous}
 Write-Output 'Customer synchronization configured: local Kafka, isolated service databases/accounts, User Secrets and existing-customer snapshot reconciliation. Secrets not displayed.'
} finally {$OutputEncoding=$previousEncoding;Pop-Location}
