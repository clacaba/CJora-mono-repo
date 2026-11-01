[CmdletBinding(DefaultParameterSetName='Save')]
param(
 [Parameter(Mandatory=$true,ParameterSetName='Save')][string]$InputFile,
 [Parameter(Mandatory=$true,ParameterSetName='Reconcile')][switch]$Reconcile
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$previous=$env:CJORA_CORE_CUSTOMER_CONNECTION
$previousEncoding=$OutputEncoding;$OutputEncoding=New-Object Text.UTF8Encoding($false)
Push-Location $root
try{
 $password=$null
 foreach($line in [IO.File]::ReadAllLines((Join-Path $root 'INFRASTRUCTURE/LOCAL/.env'))){
  if($line-match'^CJORA_CORE_CUSTOMER_PASSWORD=([a-f0-9]{64})$'){$password=$Matches[1]}
 }
 if(!$password){throw 'Run Initialize-CustomerSync.ps1 first. Operator secret is not configured.'}
 $env:CJORA_CORE_CUSTOMER_CONNECTION="Server=127.0.0.1;Port=3307;Database=CJORA_CORE;User ID=cjora_core_customer_operator;Password=$password;SslMode=Disabled;Connection Timeout=3"
 if($Reconcile){dotnet run --project SRC/CORE/CJora.Core.Customers -- --reconcile}
 else{[IO.File]::ReadAllText((Resolve-Path -LiteralPath $InputFile).Path)|dotnet run --project SRC/CORE/CJora.Core.Customers -- --save}
 if($LASTEXITCODE-ne0){throw 'Customer command failed; credentials and command content are not displayed.'}
}finally{$env:CJORA_CORE_CUSTOMER_CONNECTION=$previous;$OutputEncoding=$previousEncoding;Pop-Location}
