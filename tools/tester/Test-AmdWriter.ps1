# The AMD switch writer (Anti-Lag level 0, SmartAccess Memory, FreeSync per display) on a REAL Radeon: a supervised, reversible cycle. READ THIS BEFORE RUNNING WITH -Apply.
#
#   Without -Apply this script only PREVIEWS: it asks Mesura what each switch would do and writes nothing. That is safe to run any time.
#   With -Apply (and your typed confirmation) it writes ONE switch on the real card, reads it back in a separate process, optionally waits while you try a game, then undoes it and reads that back too.
#   It exists because the writer has only ever been tested against a fake driver (docs/gpu-g1-g7-report.md): this is the one real check, and it needs your separate permission, which is why it asks.
#
# What can go wrong, in plain words: the driver may refuse (nothing changes), may answer "ok" and not change anything (the read-back catches it and the script reports it), may need a restart for
# SmartAccess Memory (the script says so and does not call it applied), or may leave Chill turned off after Anti-Lag is switched off (the undo puts it back and the script checks). Mesura writes the state from
# before to disk first (%LOCALAPPDATA%\Mesura\Setup\gpu\<id>.json), so `mesura gpu amd undo <id> --apply` always works even if this script is interrupted. A restart returns the driver to its saved configuration.
#
#   powershell -ExecutionPolicy Bypass -File Test-AmdWriter.ps1                                   (previews only)
#   powershell -ExecutionPolicy Bypass -File Test-AmdWriter.ps1 -Apply -Feature antilag           (the real cycle for Anti-Lag; asks you to type YES)
#   powershell -ExecutionPolicy Bypass -File Test-AmdWriter.ps1 -Apply -Feature sam
#   powershell -ExecutionPolicy Bypass -File Test-AmdWriter.ps1 -Apply -Feature freesync -Display 0
param(
  [switch]$Apply,
  [ValidateSet('antilag', 'sam', 'freesync')][string]$Feature = 'antilag',
  [int]$Display = -1,
  [int]$Gpu = 0,
  [switch]$HoldBeforeUndo,
  [string]$Cli = '',
  [string]$Out = '',
  [string]$Confirmation = ''
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
if (-not $Cli) { Write-Host 'mesura.exe was not found (put it beside this script or pass -Cli).'; exit 3 }
$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$kind, [string]$text) { $line = ('{0,-5} {1}' -f $kind, $text); $lines.Add($line); Write-Host $line -ForegroundColor $(switch ($kind) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Gray' } }) }
$failed = $false
function Check([string]$what, [bool]$ok) { if (-not $ok) { $script:failed = $true }; Say $(if ($ok) { 'PASS' } else { 'FAIL' }) $what }
function Run([string[]]$arguments) { $t = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Text = $t; Exit = $LASTEXITCODE } }
function Snapshot {   # what the driver says now, read by a fresh process (its own ADLX session)
  $r = Run @('gpu', 'amd', '--json')
  if ($r.Exit -ne 0) { return $null }
  try { $r.Text | ConvertFrom-Json } catch { $null }
}
function ChangeIds {   # the recorded GPU changes (mesura writes one BEFORE it touches the driver), so a change can be found even when the command that made it died
  $r = Run @('gpu', 'amd', 'changes')
  @($r.Text -split "`n" | ForEach-Object { if ($_ -match '^(\d{8}T\d+Z-gpu-\S+)') { $Matches[1] } })
}
function SwitchState($snap, [string]$feature, [int]$display) {
  $g = $snap.Gpus[$Gpu]
  switch ($feature) {
    'antilag' { [pscustomobject]@{ Enabled = $g.AntiLag.Enabled; Level = $g.AntiLagLevel; Chill = $g.Chill.Enabled; Boost = $g.Boost.Enabled } }
    'sam' { [pscustomobject]@{ Enabled = $g.SmartAccessMemory.Enabled; Level = $null; Chill = $null; Boost = $null } }
    'freesync' { $d = @($snap.Displays | Where-Object { $_.Index -eq $display })[0]; [pscustomobject]@{ Enabled = $d.FreeSync.Enabled; Level = $null; Chill = $null; Boost = $null } }
  }
}
function Same($a, $b) { "$($a.Enabled)/$($a.Level)/$($a.Chill)/$($a.Boost)" -eq "$($b.Enabled)/$($b.Level)/$($b.Chill)/$($b.Boost)" }

Say 'INFO' "AMD writer test, $(Get-Date -Format s), mode: $(if ($Apply) { 'REAL WRITE CYCLE' } else { 'preview only' })"
$before = Snapshot
if (-not $before -or -not $before.Gpus) { Say 'INFO' 'ADLX is not available on this PC (no AMD driver with ADLX) or it would not start. Nothing to test.'; $lines | Out-File (Join-Path $Out "amd-writer-test-$Feature.txt") -Encoding utf8; exit 3 }
Say 'INFO' "GPU $Gpu`: $($before.Gpus[$Gpu].Name); driver ADLX $($before.DllVersion)"

if (-not $Apply) {
  Say 'INFO' 'Previews (nothing is written):'
  foreach ($f in @(@('antilag', ''), @('sam', ''))) { $r = Run (@('gpu', 'amd', 'set', $f[0], 'on', '--gpu', "$Gpu")); $r.Text -split "`n" | ForEach-Object { Say 'INFO' "  $_" } }
  foreach ($d in @($before.Displays | Where-Object { $_.Index -ge 0 })) { $r = Run @('gpu', 'amd', 'set', 'freesync', 'on', '--display', "$($d.Index)"); $r.Text -split "`n" | ForEach-Object { Say 'INFO' "  [display $($d.Index) $($d.Name)] $_" } }
  Say 'INFO' 'To run the real cycle for one switch: add -Apply -Feature antilag|sam|freesync (and -Display N for freesync).'
  $lines | Out-File (Join-Path $Out "amd-writer-test-$Feature.txt") -Encoding utf8
  exit 0
}

# ---- the real cycle ----------------------------------------------------------------------------------------------------------------------------
if ($Feature -eq 'freesync' -and $Display -lt 0) { Say 'FAIL' 'FreeSync needs -Display N (the numbers of "mesura gpu amd").'; exit 2 }
$pre = SwitchState $before $Feature $Display
Say 'INFO' "State before: enabled=$($pre.Enabled)$(if ($Feature -eq 'antilag') { ", level=$($pre.Level), Chill=$($pre.Chill), Boost=$($pre.Boost)" })"
$want = -not [bool]$pre.Enabled   # the cycle turns the switch to the opposite of what it is now, then back
$word = if ($want) { 'on' } else { 'off' }
$setArgs = @('gpu', 'amd', 'set', $Feature, $word, '--gpu', "$Gpu") + $(if ($Feature -eq 'freesync') { @('--display', "$Display") } else { @() })
$preview = Run $setArgs
$preview.Text -split "`n" | ForEach-Object { Say 'INFO' "  preview: $_" }
if ($preview.Text -match 'not supported|does not offer|Anti-Lag Next|could not be read|ADLX is not available') { Say 'FAIL' 'The preview says this switch cannot be written here; nothing was written.'; $lines | Out-File (Join-Path $Out "amd-writer-test-$Feature.txt") -Encoding utf8; exit 1 }

Write-Host ''
Write-Host "About to turn $Feature $word on the REAL card and then back. Mesura records the state first; an undo is always available." -ForegroundColor Yellow
$answer = if ($Confirmation) { $Confirmation } else { Read-Host 'Type YES to write (anything else cancels)' }
if ($answer -cne 'YES') { Say 'INFO' 'Cancelled: nothing was written.'; $lines | Out-File (Join-Path $Out "amd-writer-test-$Feature.txt") -Encoding utf8; exit 0 }

$id = $null
$knownIds = ChangeIds
try {
  $applied = Run ($setArgs + '--apply')
  $applied.Text -split "`n" | ForEach-Object { Say 'INFO' "  apply: $_" }
  if ($applied.Text -match 'Change (\S+): (\w+)') { $id = $Matches[1]; $status = $Matches[2] } else { $status = '' }
  if (-not $id) {
    # The command ended without an answer (2026-11-04: it crashed inside amdadlx64.dll AFTER the write). The change was recorded first, so look for the new record: the undo must not depend on the output.
    $new = @(ChangeIds | Where-Object { $knownIds -notcontains $_ })
    Check "the command ended with an answer (exit code $($applied.Exit))" $false
    if ($new.Count -gt 0) { $id = $new[-1]; Say 'INFO' "A change was recorded before the command ended: $id (it will be undone below)." }
  }
  Say 'INFO' "Change id: $id, Mesura says: $status"
  # the independent reading: a different process, a different ADLX session
  $now = Snapshot
  $afterState = SwitchState $now $Feature $Display
  if ($status -eq 'Applied') { Check "the switch reads $word in a separate process" ([bool]$afterState.Enabled -eq $want) }
  elseif ($status -eq 'RestartNeeded') { Say 'INFO' 'The driver took it but needs a restart before it is in effect: not counted as applied.' }
  else { Check "Mesura reported '$status': it must not claim Applied unless the separate reading agrees" ([bool]$afterState.Enabled -ne $want -or $status -eq 'Applied') }
  if ($Feature -eq 'antilag') { Check 'Anti-Lag is still the original (level 0), never Anti-Lag Next' ($afterState.Level -eq 0) }
  if ($HoldBeforeUndo) { Read-Host 'The switch is in its new state. Try a game if you want, then press Enter to undo' | Out-Null }
}
finally {
  if ($id) {
    $undo = Run @('gpu', 'amd', 'undo', $id, '--apply')
    $undo.Text -split "`n" | ForEach-Object { Say 'INFO' "  undo: $_" }
    $final = Snapshot
    $back = SwitchState $final $Feature $Display
    Check 'after the undo a separate process reads the same state as before (the switch, and Chill and Boost for Anti-Lag)' (Same $pre $back)
  } else {
    Say 'INFO' 'No change was recorded, so there is nothing to undo.'
    $now2 = Snapshot
    if ($now2) {
      $leftState = SwitchState $now2 $Feature $Display
      if (-not (Same $pre $leftState)) { Check 'the switch is back in its original state (it is NOT: turn it back in Adrenalin, or restart Windows, which returns the driver to its saved configuration)' $false }
    }
  }
}
$lines | Out-File (Join-Path $Out "amd-writer-test-$Feature.txt") -Encoding utf8
Say 'INFO' "Wrote $(Join-Path $Out "amd-writer-test-$Feature.txt")"
if ($failed) { exit 1 } else { exit 0 }
