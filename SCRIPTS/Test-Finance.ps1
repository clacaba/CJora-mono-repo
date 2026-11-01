param([switch]$Api)
$ErrorActionPreference='Stop'
if($Api){
 & "$PSScriptRoot/Test-Login.ps1" -Finance
 return
}
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
$names=@('CJORA_TX_FINANCE_ADMIN','CJORA_TX_FINANCE_CONNECTION','CJORA_FINANCE_TRIGGER_ADMIN')
$previous=@{};foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name)}
try{
 $secrets=@{}
 foreach($line in [IO.File]::ReadAllLines((Join-Path $root 'INFRASTRUCTURE/LOCAL/.env'))){if($line-match'^([A-Z_]+)=(.*)$'){$secrets[$Matches[1]]=$Matches[2]}}
 $env:CJORA_TX_FINANCE_ADMIN="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_migrator;Password=$($secrets['CJORA_TRANSACTIONS_MIGRATION_PASSWORD']);SslMode=Disabled;Connection Timeout=3;Allow User Variables=true"
 $env:CJORA_TX_FINANCE_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=cjora_tx_finance;Password=$($secrets['CJORA_TRANSACTIONS_FINANCE_PASSWORD']);SslMode=Disabled;Connection Timeout=3;Allow User Variables=true"
 # Used only by the fixture harness for temporary Program-scoped fault triggers, removed in finally.
 $env:CJORA_FINANCE_TRIGGER_ADMIN="Server=127.0.0.1;Port=3307;Database=CJORA_TRANSACTIONS;User ID=root;Password=$($secrets['MYSQL_ROOT_PASSWORD']);SslMode=Disabled;Connection Timeout=3"
 dotnet run --project SCRIPTS/CJora.Finance.Checks -- --database
 if($LASTEXITCODE-ne0){throw 'Financial database integration check failed; sensitive details suppressed.'}
}finally{foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name])};Pop-Location}
