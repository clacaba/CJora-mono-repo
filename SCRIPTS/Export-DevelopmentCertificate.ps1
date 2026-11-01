param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
if (-not $OutputPath) { $OutputPath = Join-Path (Split-Path $PSScriptRoot -Parent) '.local/certificates/localhost-public.pem' }
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
 [System.Security.Cryptography.X509Certificates.StoreName]::My,
 [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
try {
 $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
 $certificate = $store.Certificates | Where-Object {
  $_.Subject -eq 'CN=localhost' -and $_.NotAfter -gt (Get-Date) -and $_.NotBefore -lt (Get-Date) -and
  @($_.Extensions | Where-Object { $_.Oid.Value -eq '1.3.6.1.4.1.311.84.1.1' }).Count -gt 0
 } | Sort-Object NotAfter -Descending | Select-Object -First 1
 if (-not $certificate) { throw 'No valid ASP.NET HTTPS development certificate. Run dotnet dev-certs https first.' }
 $publicBytes = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
 $base64 = [Convert]::ToBase64String($publicBytes,[Base64FormattingOptions]::InsertLineBreaks)
 [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
 [System.IO.File]::WriteAllText($OutputPath,"-----BEGIN CERTIFICATE-----`n$base64`n-----END CERTIFICATE-----`n",(New-Object System.Text.UTF8Encoding($false)))
 Write-Output $OutputPath
} finally { $store.Close() }