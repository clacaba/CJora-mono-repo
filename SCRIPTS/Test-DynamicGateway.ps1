$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$certificate = & (Join-Path $PSScriptRoot 'Export-DevelopmentCertificate.ps1')
$processes = @()
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$alias = 'health-probe-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$probeUuid = [Guid]::NewGuid().ToString('D')
$operationUuid = [Guid]::NewGuid().ToString('D')
$probeCreated = $false
$actor = '00000000-0000-0000-0000-000000000001'
$local = Join-Path $root '.local/routing-smoke'
New-Item -ItemType Directory -Path $local -Force | Out-Null
function Invoke-GatewaySql([string]$sql) {
 $output = $sql | docker compose --env-file INFRASTRUCTURE/LOCAL/.env -f INFRASTRUCTURE/LOCAL/compose.yaml exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names' 2>&1
 if ($LASTEXITCODE -ne 0) { throw 'Local routing fixture database operation failed; raw output suppressed.' }
 return $output
}
function Assert-FreePort([int]$port) {
 $client = New-Object System.Net.Sockets.TcpClient
 try { if ($client.ConnectAsync('127.0.0.1',$port).Wait(250) -and $client.Connected) { throw "Port $port is occupied; stop the existing CJora service first." } }
 catch [System.AggregateException] { }
 finally { $client.Dispose() }
}
function Invoke-Route([string]$path,[string[]]$extra = @()) {
 $outputFile = Join-Path $local ('response-' + [Guid]::NewGuid().ToString('N') + '.json')
 $status = & curl.exe --silent --show-error --noproxy localhost,127.0.0.1 --cacert $certificate --max-time 30 --output $outputFile --write-out '%{http_code}' @extra "https://localhost:7000$path"
 if ($LASTEXITCODE -ne 0) { throw "HTTPS routing request failed: $path" }
 $raw = [System.IO.File]::ReadAllText($outputFile)
 $body = if ($raw) { $raw | ConvertFrom-Json } else { $null }
 return [pscustomobject]@{Status=[int]$status; Body=$body}
}
function Assert-Status($response,[int]$expected) {
 if ($response.Status -ne $expected) { throw "Expected HTTP $expected, received $($response.Status): $($response.Body.code)" }
}
function Set-Probe([string]$field,[string]$value) {
 if ($field -notin @('METHOD_STATUS','METHOD_HOST','AUTHORIZATION_POLICY','IS_TESTED')) { throw 'Fixture field not approved' }
 if ($value.Contains("'")) { throw 'Fixture value contains an unsupported character' }
 $sql = @"
START TRANSACTION;
UPDATE CJORA_GATEWAY.SERVICE_METHOD SET $field='$value',ROW_VERSION=ROW_VERSION+1,UPDATED_BY_UUID='$actor',UPDATED_DATETIME=UTC_TIMESTAMP(6) WHERE UUID='$probeUuid';
INSERT INTO CJORA_GATEWAY.API_LOG (UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CHANGE_DETAILS,CREATED_DATETIME)
VALUES (UUID(),'WORKLOAD','$actor','routing-smoke','$operationUuid','TEST_REGISTRY_CHANGE','SERVICE_METHOD','$probeUuid','UPDATE','SUCCEEDED',JSON_OBJECT('field','$field','value','$value'),UTC_TIMESTAMP(6));
COMMIT;
"@
 Invoke-GatewaySql $sql | Out-Null
}
Push-Location $root
try {
 foreach ($port in @(5075,5080,5082,7000)) { Assert-FreePort $port }
 $env:ASPNETCORE_ENVIRONMENT = 'Development'
 foreach ($service in @(@('CORE','Core','http://localhost:5075'),@('DOCUMENTS','Documents','http://localhost:5082'),@('GATEWAY','Gateway','http://localhost:5080;https://localhost:7000'))) {
  $name = "CJora.$($service[1]).Api"
  $directory = Join-Path $root "SRC/$($service[0])/$name"
  $dll = Join-Path $directory "bin/Debug/net8.0/$name.dll"
  if (-not (Test-Path -LiteralPath $dll)) { throw 'Build CJora.sln before running this check.' }
  $processes += Start-Process dotnet -ArgumentList @($dll,'--urls',$service[2]) -WorkingDirectory $directory -WindowStyle Hidden -PassThru -RedirectStandardOutput "$local/$name.out.log" -RedirectStandardError "$local/$name.err.log"
 }
 for ($attempt=0; $attempt -lt 40; $attempt++) {
  try { $ready = Invoke-RestMethod 'http://localhost:5080/health/live' -TimeoutSec 1; if ($ready.service -eq 'Gateway') { break } } catch { }
  Start-Sleep -Milliseconds 250
 }
 $health = Invoke-Route '/core/health'
 Assert-Status $health 200
 if ($health.Body.service -ne 'Core') { throw 'Dynamic health did not reach Core' }
 $redirect = & curl.exe --silent --show-error --noproxy localhost,127.0.0.1 --output NUL --write-out '%{http_code}' 'http://localhost:5080/core/health'
 if ($redirect -ne '307') { throw 'HTTP dynamic route did not redirect to HTTPS' }
 Write-Output 'PASS: trusted-certificate HTTPS and HTTP redirect; Gateway-owned database registration reaches Core health.'
 Assert-Status (Invoke-Route '/api/v1/cor/dispatch/core/health' @('--header','X-CJora-Gateway-Token: forged','--header','X-CJora-Route: forged')) 403
 $directOutput = Join-Path $local 'direct-core-rejection.json'
 $directStatus = & curl.exe --silent --show-error --noproxy localhost,127.0.0.1 --output $directOutput --write-out '%{http_code}' --header 'X-CJora-Gateway-Token: forged' --header 'X-CJora-Route: forged' 'http://localhost:5075/api/v1/cor/dispatch/core/health'
 if ($directStatus -ne '403') { throw 'Core accepted forged local routing metadata.' }
 Write-Output 'PASS: client-supplied route metadata is rejected through the versioned path and directly at Core.'

 $jsonPath = Join-Path $local 'request.json'
 [System.IO.File]::WriteAllText($jsonPath,'{"message":"transport-check","nested":{"amount":123},"items":[1,2]}',(New-Object System.Text.UTF8Encoding($false)))
 $echo = Invoke-Route '/core/echo?reference=a%20b' @('--request','POST','--header','Content-Type: application/json','--header','Authorization: Bearer synthetic-test-only','--header','Cookie: session=synthetic-test-only','--data-binary',"@$jsonPath")
 Assert-Status $echo 200
 if ($echo.Body.payload.nested.amount -ne 123 -or $echo.Body.credentialHeadersPresent -or $echo.Body.routingHeadersPresent) { throw 'JSON payload changed or credentials leaked downstream' }
 Write-Output 'PASS: JSON POST forwarding preserves payload and strips browser credentials.'

 $binaryPath = Join-Path $local 'sample.bin'
 $binary = New-Object byte[] 4096
 for ($index=0;$index -lt $binary.Length;$index++) { $binary[$index] = $index % 256 }
 [System.IO.File]::WriteAllBytes($binaryPath,$binary)
 $textPath = Join-Path $local 'sample.txt'
 [System.IO.File]::WriteAllText($textPath,'fictional document transport sample')
 $uploadArgs = @('--request','POST','--form',"file=@$binaryPath;type=application/octet-stream",'--form',"support=@$textPath;type=text/plain",'--form','note=transport-check','--form','tag=one','--form','tag=two')
 $upload = Invoke-Route '/documents/upload-check/' $uploadArgs
 Assert-Status $upload 200
 $file = @($upload.Body.files | Where-Object { $_.fieldName -eq 'file' })
 if ($upload.Body.files.Count -ne 2 -or $file.Count -ne 1 -or $file[0].length -ne 4096 -or $file[0].sha256 -ne (Get-FileHash $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant() -or $upload.Body.fields.note[0] -ne 'transport-check' -or $upload.Body.fields.tag.Count -ne 2) { throw 'Multipart files, binary bytes or repeated fields changed' }
 Write-Output 'PASS: multipart POST preserves binary SHA256, multiple files and repeated form fields; files are not persisted by the service.'

 Assert-Status (Invoke-Route '/core/unregistered') 404
 Assert-Status (Invoke-Route '/core/health' @('--request','POST','--header','Content-Type: application/json','--data-binary',"@$jsonPath")) 404
 Assert-Status (Invoke-Route '/core/echo' @('--request','POST','--header','Content-Type: text/plain','--data-binary',"@$jsonPath")) 415
 Assert-Status (Invoke-Route '/documents/upload-check' @('--request','POST','--header','Content-Type: multipart/form-data','--data-binary',"@$jsonPath")) 415
 $largePath = Join-Path $local 'oversized.bin'
 [System.IO.File]::WriteAllBytes($largePath,(New-Object byte[] (2*1024*1024)))
 Assert-Status (Invoke-Route '/documents/upload-check' @('--request','POST','--form',"file=@$largePath")) 413
 $chunked = Invoke-Route '/documents/upload-check' @('--request','POST','--header','Transfer-Encoding: chunked','--form',"file=@$largePath")
 Assert-Status $chunked 413
 Write-Output 'PASS: missing registration, wrong verb, unsupported media, missing boundary, fixed-length and chunked oversized bodies are rejected.'

 $create = @"
START TRANSACTION;
INSERT INTO CJORA_GATEWAY.SERVICE_METHOD
(UUID,SERVICE_NAME,METHOD_NAME,METHOD_HOST,METHOD_PATH,METHOD_DESCRIPTION,METHOD_STATUS,METHOD_REQUEST_TYPE,IS_DATATYPE_SENSITIVE,IS_TESTED,AUTHORIZATION_POLICY,DEVELOPMENT_ONLY,BODY_TYPE,MAX_REQUEST_BYTES,TIMEOUT_MS,CREATED_BY_UUID,CREATED_DATETIME,UPDATED_BY_UUID,UPDATED_DATETIME)
VALUES ('$probeUuid','core','$alias','http://localhost:5075','/health/live','Temporary routing smoke fixture','A','GET',0,1,'PUBLIC_DIAGNOSTIC',1,'NONE',8192,10000,'$actor',UTC_TIMESTAMP(6),'$actor',UTC_TIMESTAMP(6));
INSERT INTO CJORA_GATEWAY.API_LOG (UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CHANGE_DETAILS,CREATED_DATETIME)
VALUES (UUID(),'WORKLOAD','$actor','routing-smoke','$operationUuid','TEST_REGISTRY_CREATE','SERVICE_METHOD','$probeUuid','CREATE','SUCCEEDED',JSON_OBJECT('method','$alias'),UTC_TIMESTAMP(6));
COMMIT;
"@
 Invoke-GatewaySql $create | Out-Null
 $probeCreated = $true
 Assert-Status (Invoke-Route "/core/$alias") 200
 Set-Probe 'METHOD_STATUS' 'D'
 Assert-Status (Invoke-Route "/core/$alias") 404
 Set-Probe 'METHOD_STATUS' 'A'
 Set-Probe 'IS_TESTED' '0'
 Assert-Status (Invoke-Route "/core/$alias") 404
 Set-Probe 'IS_TESTED' '1'
 Set-Probe 'METHOD_HOST' 'http://169.254.169.254'
 Assert-Status (Invoke-Route "/core/$alias") 503
 Set-Probe 'METHOD_HOST' 'http://localhost:5080'
 Assert-Status (Invoke-Route "/core/$alias") 503
 Set-Probe 'METHOD_HOST' 'http://localhost:5075'
 Set-Probe 'AUTHORIZATION_POLICY' 'SESSION'
 Assert-Status (Invoke-Route "/core/$alias") 503
 Set-Probe 'AUTHORIZATION_POLICY' 'PUBLIC_DIAGNOSTIC'
 Assert-Status (Invoke-Route "/core/$alias") 200
 $errorCount = Invoke-GatewaySql "SELECT COUNT(*) FROM CJORA_GATEWAY.ERROR_LOGS WHERE ERROR_CODE IN ('METHOD_NOT_FOUND','DESTINATION_NOT_APPROVED','SESSION_AUTHORIZATION_NOT_READY');"
 if ([int]($errorCount | Select-Object -Last 1) -lt 3) { throw 'Expected routing errors were not persisted' }
 Write-Output 'PASS: database-only registration/disable changes activate without source edits; metadata/Gateway destinations and session-policy bypasses fail closed; errors persist in ERROR_LOGS.'
 $limited = $false
 for ($attempt=0; $attempt -lt 125; $attempt++) {
  $status = & curl.exe --silent --show-error --noproxy localhost,127.0.0.1 --output NUL --write-out '%{http_code}' 'http://localhost:5080/health/live'
  if ($status -eq '429') { $limited = $true; break }
 }
 if (-not $limited) { throw 'Gateway rate limit did not activate.' }
 Write-Output 'PASS: Gateway rejects excess requests with HTTP 429.'
}
finally {
 if ($probeCreated) {
  $cleanup = @"
START TRANSACTION;
INSERT INTO CJORA_GATEWAY.API_LOG (UUID,ACTOR_TYPE,ACTOR_UUID,CORRELATION_ID,OPERATION_UUID,OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CHANGE_DETAILS,CREATED_DATETIME)
VALUES (UUID(),'WORKLOAD','$actor','routing-smoke','$operationUuid','TEST_REGISTRY_REMOVE','SERVICE_METHOD','$probeUuid','DELETE','SUCCEEDED',JSON_OBJECT('method','$alias'),UTC_TIMESTAMP(6));
DELETE FROM CJORA_GATEWAY.SERVICE_METHOD WHERE UUID='$probeUuid';
COMMIT;
"@
  try { Invoke-GatewaySql $cleanup | Out-Null } catch { Write-Warning 'Temporary routing fixture cleanup failed; inspect it securely.' }
 }
 foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
 $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
 Pop-Location
}