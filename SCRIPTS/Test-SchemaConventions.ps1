$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scripts = Get-ChildItem (Join-Path $root 'SRC') -Recurse -File -Filter *.sql | Where-Object { $_.FullName -match '\\.Migrations\\Scripts\\' -or $_.FullName -match '\.Migrations\\Scripts\\' }
$tableCount = 0
foreach ($script in $scripts) {
 $sql = Get-Content -LiteralPath $script.FullName -Raw
 $tables = [regex]::Matches($sql, '(?s)CREATE TABLE ([A-Z][A-Z0-9_]*)\s*\((.*?)\)\s*ENGINE=InnoDB[^;]*COMMENT=''([^'']+)'';')
 $createCount = [regex]::Matches($sql,'(?i)CREATE\s+TABLE\b').Count
 if ($tables.Count -ne $createCount) { throw "Missing table comment, uppercase name or InnoDB declaration in $($script.Name)" }
 foreach ($table in $tables) {
  $name = $table.Groups[1].Value
  $columnLines = @($table.Groups[2].Value -split '\r?\n' | Where-Object { $_ -match '^\s*[A-Z][A-Z0-9_]*\s+(?:BIGINT|INT|SMALLINT|CHAR|VARCHAR|BINARY|BOOLEAN|DATETIME|JSON|TEXT)\b' })
  if ($columnLines.Count -lt 2 -or $columnLines[0] -notmatch '^\s*ID BIGINT NOT NULL AUTO_INCREMENT' -or $columnLines[1] -notmatch '^\s*UUID CHAR\(36\)') { throw "$name must start with numeric ID followed by UUID" }
  foreach ($line in $columnLines) { if ($line -notmatch "COMMENT '[^']+'") { throw "$name contains an undocumented column" } }
  if ($table.Groups[2].Value -notmatch 'PRIMARY KEY \(ID\)' -or $table.Groups[2].Value -notmatch 'UNIQUE KEY [A-Z0-9_]+ \(UUID\)') { throw "$name requires ID primary key and unique UUID" }
  if ($table.Groups[2].Value -match '(?i)REFERENCES\s+[A-Z0-9_]+\.') { throw "$name contains a cross-database foreign key" }
  $tableCount++
 }
}
if ($scripts.Count -lt 10) { throw "Expected service SQL scripts including the method registry." }
Write-Output "PASS: $tableCount table definitions across $($scripts.Count) scripts have numeric ID first, UUID second, unique UUIDs, documented columns/table names and local database boundaries."
Write-Output 'This is a convention check, not MySQL execution or a proof of authorization.'