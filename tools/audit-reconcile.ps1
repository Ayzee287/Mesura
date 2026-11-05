# Reconciles the AdamX audit table (research\adamx-audit-2026-10\adamx-vs-mesura.tsv) with the CURRENT Windows catalog, read only:
#   1. operations the table names that the catalog has retired (the table's row is stale),
#   2. operations the table names that exist in neither the catalog nor the retired list (a wrong or renamed id),
#   3. operations in the catalog that the table never mentions (added after the audit, not reviewed against AdamX).
# Operation ids have the form category.name; collection and profile ids have no dot and are ignored.
#
#   powershell -ExecutionPolicy Bypass -File tools\audit-reconcile.ps1 [-Tsv <path>]
param([string]$Tsv = (Join-Path $PSScriptRoot '..\..\research\adamx-audit-2026-10\adamx-vs-mesura.tsv'))
$ErrorActionPreference = 'Stop'
$core = Join-Path $PSScriptRoot '..\src\Mesura.Core\Windows'
function Ids([string]$file) { $set = New-Object 'System.Collections.Generic.HashSet[string]'; foreach ($m in [regex]::Matches([IO.File]::ReadAllText($file), '"id"\s*:\s*"([a-z0-9-]+\.[a-z0-9.-]+)"')) { [void]$set.Add($m.Groups[1].Value) }; ,$set }
$live = Ids (Join-Path $core 'windows.catalog.json')
$dead = Ids (Join-Path $core 'windows.retired.json')
$rows = Import-Csv -Delimiter "`t" -Path $Tsv -Encoding UTF8
$referenced = New-Object 'System.Collections.Generic.HashSet[string]'
$retired = @(); $unknown = @()
foreach ($r in $rows) {
  foreach ($id in ($r.mesura -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[a-z0-9-]+\.[a-z0-9.-]+$' })) {
    [void]$referenced.Add($id)
    if ($live.Contains($id)) { continue }
    $line = "{0}  <-  AdamX '{1}' (class {2})" -f $id, $r.adamx, $r.class
    if ($dead.Contains($id)) { $retired += $line } else { $unknown += $line }
  }
}
$unmentioned = @($live | Where-Object { -not $referenced.Contains($_) } | Sort-Object)
Write-Host "Catalog: $($live.Count) live operations, $($dead.Count) retired. Table: $($rows.Count) rows."
Write-Host "`n1. Named by the table but retired ($($retired.Count)):"; $retired | ForEach-Object { Write-Host "   $_" }
Write-Host "`n2. Named by the table, in neither list ($($unknown.Count)):"; $unknown | ForEach-Object { Write-Host "   $_" }
Write-Host "`n3. In the catalog, never mentioned by the table ($($unmentioned.Count)):"
$unmentioned | Group-Object { $_.Split('.')[0] } | Sort-Object Name | ForEach-Object { Write-Host ("   {0} ({1}): {2}" -f $_.Name, $_.Count, ($_.Group -join ', ')) }
