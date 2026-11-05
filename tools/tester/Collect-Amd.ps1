# One read-only collection for a PC with a Radeon (the RX 9070 XT PC): what a Mesura profile left in place, what AMD's driver says about the card, the displays and Resizable BAR / SmartAccess Memory,
# gathered into one folder and one zip on the Desktop. It changes NOTHING (no setting, no profile, no driver, no GPU switch), uses no network and records no serial number.
#
# Inside: the verification report (Verify-MesuraApplied.ps1), Mesura's hardware profile and the plan of both levels, the run list, Mesura's ADLX reader (every feature, tuning ranges, metrics, displays),
# variable refresh per display and Resizable BAR by layer, the ADLX probe (second, independent reader), the active power plan, the display facts, and what Windows says about GPU scheduling and Game Mode.
#
#   powershell -ExecutionPolicy Bypass -File Collect-Amd.ps1 [-Cli <mesura.exe>] [-Out <folder>]
param(
  [string]$Cli = '',
  [string]$Out = ''
)
$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format yyyyMMdd-HHmmss
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) "mesura-radeon-$stamp" }
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
$steps = New-Object System.Collections.Generic.List[string]
function Step([string]$name, [scriptblock]$body) {
  $t = Get-Date
  try { & $body; $steps.Add(('ok     {0,5:0.0}s  {1}' -f ((Get-Date) - $t).TotalSeconds, $name)) } catch { $steps.Add(('FAILED {0,5:0.0}s  {1}: {2}' -f ((Get-Date) - $t).TotalSeconds, $name, $_.Exception.Message)) }
  Write-Host $steps[-1]
}
function Mesura([string]$file, [string[]]$arguments) {
  if (-not $Cli) { throw 'mesura.exe not found (put it beside this script or pass -Cli)' }
  $err = Join-Path $env:TEMP 'mesura-collect-stderr.txt'
  & $Cli @arguments 2> $err | Out-File (Join-Path $Out $file) -Encoding utf8
  if (Test-Path $err) { $e = Get-Content -Raw $err; if ($e) { Add-Content (Join-Path $Out $file) "`n[stderr] $e" -Encoding utf8 } }
}

Write-Host "Collecting into $Out (read only)"
Step 'verification report' { & (Join-Path $PSScriptRoot 'Verify-MesuraApplied.ps1') -Cli $Cli -Out $Out | Out-Null }
Step 'hardware profile' { Mesura 'mesura-hardware.txt' @('hardware') }
Step 'plan: Maximum performance' { Mesura 'plan-maximum-performance.txt' @('windows', 'plan', '--profile', 'maximum-performance') }
Step 'plan: Maximum performance (json)' { Mesura 'plan-maximum-performance.json' @('windows', 'plan', '--profile', 'maximum-performance', '--json') }
Step 'plan: Recommended' { Mesura 'plan-recommended-gaming.txt' @('windows', 'plan', '--profile', 'recommended-gaming') }
Step 'run list' { Mesura 'history-list.txt' @('history', 'list') }
Step 'Mesura AMD reader (json)' { Mesura 'mesura-gpu-amd.json' @('gpu', 'amd', '--json') }
Step 'Mesura AMD reader (text)' { Mesura 'mesura-gpu-amd.txt' @('gpu', 'amd') }
Step 'GPU switch changes recorded by Mesura' { Mesura 'mesura-gpu-amd-changes.txt' @('gpu', 'amd', 'changes') }
Step 'variable refresh per display' { Mesura 'mesura-gpu-vrr.txt' @('gpu', 'vrr') }
Step 'Resizable BAR layers' { Mesura 'mesura-gpu-bar.txt' @('gpu', 'bar') }
Step 'ADLX probe (second reader)' {
  $runner = Join-Path $PSScriptRoot 'run-amd-probe.ps1'
  if (-not (Test-Path (Join-Path $PSScriptRoot 'adlx_probe.exe')) -or -not (Test-Path $runner)) { throw 'adlx_probe.exe or run-amd-probe.ps1 is not beside this script' }
  & $runner -Kind amd -Out (Join-Path $Out 'amd-probe.json') | Out-Null
}
Step 'power plans' { (powercfg /list) + '' + (powercfg /getactivescheme) + '' + (powercfg /query SCHEME_CURRENT) | Out-File (Join-Path $Out 'powercfg.txt') -Encoding utf8 }
Step 'display facts' { & (Join-Path $PSScriptRoot 'collect-display-facts.ps1') -Out (Join-Path $Out 'display-facts.txt') | Out-Null }
Step 'Windows graphics state' {
  foreach ($id in 'gpu.hardware-scheduling-on', 'display.graphics-defaults', 'gaming.game-mode-on') { Mesura "windows-$id.txt" @('windows', 'show', '--id', $id) }
}
$steps | Out-File (Join-Path $Out 'steps.txt') -Encoding utf8
$zip = "$Out.zip"
try { Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $zip -Force; Write-Host "Wrote $zip" } catch { Write-Host "Could not zip ($($_.Exception.Message)); send the folder $Out" }
Write-Host 'Done. Nothing was changed. Send the zip.'
