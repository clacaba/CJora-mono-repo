param([switch]$Browser,[switch]$Finance)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$processes=@()
$previousEnvironment=$env:ASPNETCORE_ENVIRONMENT
try {
 & "$PSScriptRoot/Export-DevelopmentCertificate.ps1" | Out-Null
 $env:ASPNETCORE_ENVIRONMENT='Development'
 $logs=Join-Path $root '.local/login-tests';New-Item -ItemType Directory -Force $logs|Out-Null
 $services=@(@('CORE','Core',5075),@('IDENTITY-MANAGEMENT','IdentityManagement',5076),@('GATEWAY','Gateway',5080))
 if($Finance){$services+=,@('TRANSACTIONS','Transactions',5079)}
 foreach($service in $services) {
  $client=New-Object Net.Sockets.TcpClient
  try {if($client.ConnectAsync('127.0.0.1',$service[2]).Wait(250)-and $client.Connected){throw "Port $($service[2]) already in use. Stop the existing API before testing."}} catch [AggregateException] {} finally {$client.Dispose()}
  $dir=Join-Path $root "SRC/$($service[0])/CJora.$($service[1]).Api"
  $dll=Join-Path $dir "bin/Debug/net8.0/CJora.$($service[1]).Api.dll"
  $urls="http://localhost:$($service[2])";if($service[1]-eq'Gateway'){$urls+=';https://localhost:7000'}
  $previousSync=$env:CustomerSync__Enabled
  try{
   if($Finance-and$service[1]-eq'Transactions'){$env:CustomerSync__Enabled='false'}
   $processes+=Start-Process dotnet -ArgumentList @($dll,'--urls',$urls) -WorkingDirectory $dir -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logs/$($service[1]).out.log" -RedirectStandardError "$logs/$($service[1]).err.log"
  }finally{$env:CustomerSync__Enabled=$previousSync}
 }
 foreach($port in @($services | ForEach-Object { $_[2] })) {
  $ready=$false
  for($i=0;$i-lt40;$i++){try{Invoke-RestMethod "http://localhost:$port/health/live" -TimeoutSec 1|Out-Null;$ready=$true;break}catch{Start-Sleep -Milliseconds 250}}
  if(!$ready){throw "API on port $port did not start. Inspect the ignored local test logs."}
 }
 $testScript=if($Finance){'Test-Finance.mjs'}elseif($Browser){'Test-Web.mjs'}else{'Test-Login.mjs'}
 & node (Join-Path $PSScriptRoot $testScript)
 if($LASTEXITCODE-ne0){throw 'Login integration test failed. Secrets and authorization URLs are intentionally not printed.'}
} finally {
 foreach($process in $processes){if(!$process.HasExited){Stop-Process -Id $process.Id -Force}}
 $env:ASPNETCORE_ENVIRONMENT=$previousEnvironment
}
