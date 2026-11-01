param([switch]$Browser)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$processes=@()
$previousEnvironment=$env:ASPNETCORE_ENVIRONMENT
try {
 & "$PSScriptRoot/Export-DevelopmentCertificate.ps1" | Out-Null
 $env:ASPNETCORE_ENVIRONMENT='Development'
 $logs=Join-Path $root '.local/login-tests';New-Item -ItemType Directory -Force $logs|Out-Null
 foreach($service in @(@('CORE','Core',5075),@('IDENTITY-MANAGEMENT','IdentityManagement',5076),@('GATEWAY','Gateway',5080))) {
  $client=New-Object Net.Sockets.TcpClient
  try {if($client.ConnectAsync('127.0.0.1',$service[2]).Wait(250)-and $client.Connected){throw "Port $($service[2]) already in use. Stop the existing API before testing."}} catch [AggregateException] {} finally {$client.Dispose()}
  $dir=Join-Path $root "SRC/$($service[0])/CJora.$($service[1]).Api"
  $dll=Join-Path $dir "bin/Debug/net8.0/CJora.$($service[1]).Api.dll"
  $urls="http://localhost:$($service[2])";if($service[1]-eq'Gateway'){$urls+=';https://localhost:7000'}
  $processes+=Start-Process dotnet -ArgumentList @($dll,'--urls',$urls) -WorkingDirectory $dir -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logs/$($service[1]).out.log" -RedirectStandardError "$logs/$($service[1]).err.log"
 }
 foreach($port in @(5075,5076,5080)) {
  $ready=$false
  for($i=0;$i-lt40;$i++){try{Invoke-RestMethod "http://localhost:$port/health/live" -TimeoutSec 1|Out-Null;$ready=$true;break}catch{Start-Sleep -Milliseconds 250}}
  if(!$ready){throw "API on port $port did not start. Inspect the ignored local test logs."}
 }
 $testScript=if($Browser){'Test-Web.mjs'}else{'Test-Login.mjs'}
 & node (Join-Path $PSScriptRoot $testScript)
 if($LASTEXITCODE-ne0){throw 'Login integration test failed. Secrets and authorization URLs are intentionally not printed.'}
} finally {
 foreach($process in $processes){if(!$process.HasExited){Stop-Process -Id $process.Id -Force}}
 $env:ASPNETCORE_ENVIRONMENT=$previousEnvironment
}
