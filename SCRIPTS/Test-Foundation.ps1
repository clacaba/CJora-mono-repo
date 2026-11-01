$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$processes = @()
$certificatePath = & (Join-Path $PSScriptRoot 'Export-DevelopmentCertificate.ps1')
$services = @(
 @('CORE','Core','cor',5075), @('IDENTITY-MANAGEMENT','IdentityManagement','idm',5076),
 @('THIRD-PARTY-PROVIDER','Tpp','tpp',5077), @('PAYMENTS','Payments','pay',5078),
 @('TRANSACTIONS','Transactions','txn',5079), @('GATEWAY','Gateway','gtw',5080),
 @('NOTIFICATIONS','Notifications','ntc',5081), @('DOCUMENTS','Documents','doc',5082)
)
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
function Assert-NotFound([string]$url) {
 if ($url.StartsWith('https://')) {
  $status = & curl.exe --silent --show-error --noproxy localhost,127.0.0.1 --cacert $certificatePath --output NUL --write-out '%{http_code}' $url
  if ($LASTEXITCODE -ne 0 -or $status -ne '404') { throw "Expected 404: $url" }
  return
 }
 try { Invoke-WebRequest $url -UseBasicParsing -ErrorAction Stop | Out-Null; throw "Unexpected available business route: $url" }
 catch [System.Net.WebException] { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
}
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
 foreach ($service in $services) { Assert-FreePort $service[3] }
 Assert-FreePort 7000
 $env:ASPNETCORE_ENVIRONMENT = 'Development'
 $logs = Join-Path $repository '.local/smoke'
 New-Item -ItemType Directory -Path $logs -Force | Out-Null
 foreach ($service in $services) {
  $name = "CJora.$($service[1]).Api"
  $directory = Join-Path $repository "SRC/$($service[0])/$name"
  $dll = Join-Path $directory "bin/Debug/net8.0/$name.dll"
  if (-not (Test-Path $dll)) { throw 'Build the solution before running smoke tests.' }
  $urls = "http://localhost:$($service[3])"
  if ($service[1] -eq 'Gateway') { $urls += ';https://localhost:7000' }
  $processes += Start-Process dotnet -ArgumentList @($dll,'--urls',$urls) -WorkingDirectory $directory -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logs/$name.out.log" -RedirectStandardError "$logs/$name.err.log"
 }
 foreach ($service in $services) {
  $baseUrl = "http://localhost:$($service[3])"
  $health = Wait-Response "$baseUrl/health/live"
  if ($health.service -ne $service[1] -or $health.status -ne 'alive') { throw "Wrong health response for $($service[1])" }
  $healthRoot = Invoke-RestMethod "$baseUrl/health"
  if ($healthRoot.service -ne $service[1]) { throw 'Health root route mismatch' }
  $weather = Invoke-RestMethod "$baseUrl/WeatherForecast"
  if ($weather.Count -ne 5) { throw "Expected five sample forecasts for $($service[1])" }
  foreach ($forecast in $weather) {
   if (-not $forecast.date -or -not $forecast.summary -or $null -eq $forecast.temperatureC -or $null -eq $forecast.temperatureF) { throw 'Invalid weather payload' }
  }
  $swagger = Invoke-RestMethod "$baseUrl/swagger/v1/swagger.json"
  if (-not $swagger.paths.'/WeatherForecast' -or -not $swagger.paths.'/health/live') { throw "Controllers missing from OpenAPI for $($service[1])" }
  $swaggerUi = Invoke-WebRequest "$baseUrl/swagger/index.html" -UseBasicParsing
  if ($swaggerUi.StatusCode -ne 200) { throw 'Swagger UI unavailable' }
  if ($service[1] -ne 'Gateway') {
   $alias = Invoke-RestMethod "$baseUrl/api/v1/$($service[2])/health/live"
   if ($alias.service -ne $service[1]) { throw 'Versioned health alias mismatch' }
  }
  Write-Output "PASS: $($service[1]) health, five sample forecasts, Swagger UI and OpenAPI controllers."
 }
 $forwarded = (& curl.exe --silent --show-error --fail --noproxy localhost,127.0.0.1 --cacert $certificatePath 'https://localhost:7000/api/v1/cor/health/live') | ConvertFrom-Json
 if ($forwarded.service -ne 'Core') { throw 'Gateway is not forwarding to Core' }
 $loginMethodStatus = & curl.exe --silent --show-error --noproxy localhost,127.0.0.1 --cacert $certificatePath --output NUL --write-out '%{http_code}' 'https://localhost:7000/api/v1/idm/sessions'
 if ($LASTEXITCODE -ne 0 -or $loginMethodStatus -ne '405') { throw 'Login start must reject GET; it requires POST and CSRF protection.' }
 Assert-NotFound 'https://localhost:7000/api/v1/idm/health/live'
 Write-Output 'PASS: Gateway still forwards versioned API requests only to Core; login requires POST and direct Identity Management health proxy remains unavailable.'
}
finally {
 foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
 $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}