$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$processes = @()
function Assert-FreePort([int]$port) {
 $client = New-Object System.Net.Sockets.TcpClient
 try { if ($client.ConnectAsync('127.0.0.1',$port).Wait(250) -and $client.Connected) { throw "Port $port is already in use. Stop the existing service before running this test." } }
 catch [System.AggregateException] { }
 finally { $client.Dispose() }
}
function Wait-Response([string]$url) {
 for ($attempt = 0; $attempt -lt 40; $attempt++) {
  try { return Invoke-RestMethod $url -TimeoutSec 1 } catch { Start-Sleep -Milliseconds 250 }
 }
 throw "Service did not become ready: $url"
}
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
 foreach ($port in @(5075,5076,5080)) { Assert-FreePort $port }
 $env:ASPNETCORE_ENVIRONMENT = 'Development'
 $logs = Join-Path $repository '.local/smoke'
 New-Item -ItemType Directory -Path $logs -Force | Out-Null
 foreach ($item in @(
  @('CORE/CJora.Core.Api','CJora.Core.Api'),
  @('USER-ACCOUNTS/CJora.UserAccounts.Api','CJora.UserAccounts.Api'),
  @('GATEWAY/CJora.Gateway.Api','CJora.Gateway.Api')
 )) {
  $directory = Join-Path $repository "SRC/$($item[0])"
  $dll = Join-Path $directory "bin/Debug/net8.0/$($item[1]).dll"
  if (-not (Test-Path $dll)) { throw 'Build the solution before running smoke tests.' }
  $processes += Start-Process dotnet -ArgumentList @($dll,'--urls', $(switch ($item[1]) { 'CJora.Core.Api' { 'http://localhost:5075' } 'CJora.UserAccounts.Api' { 'http://localhost:5076' } default { 'http://localhost:5080' } })) -WorkingDirectory $directory -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logs/$($item[1]).out.log" -RedirectStandardError "$logs/$($item[1]).err.log"
 }
 $core = Wait-Response 'http://localhost:5075/api/v1/cor/health/live'
 $gateway = Wait-Response 'http://localhost:5080/health/live'
 $accounts = Wait-Response 'http://localhost:5076/health/live'
 $forwarded = Wait-Response 'http://localhost:5080/api/v1/cor/health/live'
 if ($core.service -ne 'Core' -or $gateway.service -ne 'Gateway' -or $accounts.service -ne 'UserAccounts' -or $forwarded.service -ne 'Core') { throw 'Unexpected routing identity' }
 $swagger = Invoke-WebRequest 'http://localhost:5075/swagger/v1/swagger.json' -UseBasicParsing
 if ($swagger.StatusCode -ne 200) { throw 'Swagger unavailable' }
 foreach ($path in @('/swagger/index.html','/WeatherForecast','/api/v1/usr/sessions')) {
  try { Invoke-WebRequest "http://localhost:5080$path" -UseBasicParsing -ErrorAction Stop | Out-Null; throw "Unexpected public route: $path" }
  catch [System.Net.WebException] { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
 }
 Write-Output 'PASS: Core, Gateway and User Accounts liveness; Gateway forwards to Core; Core Swagger; non-API/sample/login routes are unavailable through Gateway.'
}
finally {
 foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
 $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}