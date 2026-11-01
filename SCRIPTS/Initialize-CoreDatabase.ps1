param([switch]$ApplyMigrations)
$ErrorActionPreference = 'Stop'
$previousOutputEncoding = $OutputEncoding
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 $envPath = Join-Path $root 'INFRASTRUCTURE/LOCAL/.env'
 if (-not (Test-Path -LiteralPath $envPath)) { throw 'Configure the ignored local .env and start MySQL first.' }
 $content = [System.IO.File]::ReadAllText($envPath)
 $secrets = @{}
 foreach ($line in $content -split '\r?\n') {
  if ($line -match '^([A-Z_]+)=(.*)$') { $secrets[$Matches[1]] = $Matches[2] }
 }
 foreach ($name in @('CJORA_CORE_MIGRATION_PASSWORD','CJORA_CORE_RUNTIME_PASSWORD')) {
  if (-not $secrets.ContainsKey($name)) {
   $bytes = New-Object byte[] 32
   $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
   try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
   $secrets[$name] = [BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()
   $content = $content.TrimEnd() + "`n$name=$($secrets[$name])`n"
  }
  if ($secrets[$name] -notmatch '^[a-f0-9]{64}$') { throw 'Core provisioning passwords must be generated 64-character hex strings; no credentials displayed.' }
 }
 [System.IO.File]::WriteAllText($envPath,$content,(New-Object System.Text.UTF8Encoding($false)))
 $migrationPassword = $secrets['CJORA_CORE_MIGRATION_PASSWORD']
 $runtimePassword = $secrets['CJORA_CORE_RUNTIME_PASSWORD']
 $sql = @"
CREATE DATABASE IF NOT EXISTS CJORA_CORE CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
CREATE USER IF NOT EXISTS 'cjora_core_migrator'@'%' IDENTIFIED BY '$migrationPassword';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON CJORA_CORE.* TO 'cjora_core_migrator'@'%';
CREATE USER IF NOT EXISTS 'cjora_core_runtime'@'%' IDENTIFIED BY '$runtimePassword';
"@
 # Secrets travel only over stdin to the local container, never in process arguments or output.
 $mysqlOutput = $sql | docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
 if ($LASTEXITCODE -ne 0) { throw 'Core provisioning failed; inspect the local MySQL service securely. Credentials/output suppressed.' }
 if ($ApplyMigrations) {
  $previousConnection = $env:CJORA_CORE_MIGRATION_CONNECTION
  try {
   $env:CJORA_CORE_MIGRATION_CONNECTION = "Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_migrator;Password=$migrationPassword;SslMode=Disabled;Connection Timeout=5"
   dotnet run --project SRC/CORE/CJora.Core.Migrations -- --apply
   if ($LASTEXITCODE -ne 0) { throw 'Core migration failed. Do not blindly retry an interrupted DDL script.' }
  } finally { $env:CJORA_CORE_MIGRATION_CONNECTION = $previousConnection }
 }
 $grants = @"
GRANT INSERT ON CJORA_CORE.ERROR_LOGS TO 'cjora_core_runtime'@'%';
"@
 $grantOutput = $grants | docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
 if ($LASTEXITCODE -ne 0) { throw 'Runtime grants failed. Apply Core migrations before assigning error-log privileges.' }
 $runtime = "Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_runtime;Password=$runtimePassword;SslMode=Disabled;Connection Timeout=3"
 @{ 'ConnectionStrings:CoreErrors' = $runtime } | ConvertTo-Json | dotnet user-secrets set --project SRC/CORE/CJora.Core.Api
 if ($LASTEXITCODE -ne 0) { throw 'Could not configure the Core runtime User Secret.' }
 Write-Output 'Core database, isolated migrator and INSERT-only error-log runtime account configured. Runtime connection stored in User Secrets; credentials not displayed.'
} finally { $OutputEncoding = $previousOutputEncoding; Pop-Location }