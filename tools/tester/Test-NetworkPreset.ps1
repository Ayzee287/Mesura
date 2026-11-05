# A supervised, reversible test of Mesura's per-maker network adapter presets on a REAL adapter. READ THIS BEFORE RUNNING WITH -Apply.
#
#   Without -Apply this script only PREVIEWS. It asks Mesura what each of the five presets would do on this PC (which adapters it finds, which settings the driver of each lists, what each would change)
#   and writes nothing. That is safe to run any time, also on a PC you are using.
#
#   With -Apply (and your typed YES) it applies ONE preset on the real adapter(s) it is for, reads every value back with Windows' own cmdlets (Get-NetAdapterAdvancedProperty, not Mesura's reading),
#   optionally waits while you look at the connection, then undoes the run and reads the values back again: they must equal what they were. Why this exists: the adapter writes of Mesura have run only against
#   stand-ins and in Windows Sandbox (a virtual adapter), never on a real adapter. This is the one real check, and it needs your separate permission, which is why it asks.
#
# What can go wrong, in plain words: each value written restarts the adapter, so THE NETWORK DROPS FOR A FEW SECONDS PER VALUE (a remote session over that adapter would cut itself off: run it at the PC);
# a driver may refuse a value (the run then fails and what was changed is put back), may accept it and report the old value again (the read-back catches it), or may not bring the adapter back up (reported;
# the undo tries to put things back, and "mesura network restore latest --apply" from the snapshot this script takes first is the second way back; disabling and enabling the adapter in Windows is the third).
# Nothing here says the preset makes anything faster: that is not measured (tools\tester\Measure-NetworkLatency.ps1 measures it, on a wired link).
#
#   powershell -ExecutionPolicy Bypass -File Test-NetworkPreset.ps1                                  (previews only)
#   powershell -ExecutionPolicy Bypass -File Test-NetworkPreset.ps1 -Apply                           (the real cycle for the first preset that applies here; asks you to type YES; needs administrator rights)
#   powershell -ExecutionPolicy Bypass -File Test-NetworkPreset.ps1 -Apply -Preset network.preset-intel-wifi
param(
  [switch]$Apply,
  [string]$Preset = '',
  [switch]$HoldBeforeUndo,
  [string]$Cli = '',
  [string]$Out = '',
  [string]$Confirmation = '',
  [scriptblock]$ReadProperty = $null,   # tests hand in a stand-in for Get-NetAdapterAdvancedProperty: (adapter name, keyword) -> registry value text
  [scriptblock]$ReadStatus = $null,     # and one for Get-NetAdapter: (adapter name) -> 'Up' or something else
  [switch]$AssumeAdmin                 # tests only: skips the administrator check when the command line and the readers are stand-ins
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
if (-not $Cli) { Write-Host 'mesura.exe was not found (put it beside this script or pass -Cli).'; exit 3 }
$ids = 'network.preset-realtek-ethernet', 'network.preset-intel-ethernet', 'network.preset-killer-ethernet', 'network.preset-intel-wifi', 'network.preset-realtek-wifi'
$lines = New-Object System.Collections.Generic.List[string]
$failed = $false
function Say([string]$kind, [string]$text) { $line = ('{0,-5} {1}' -f $kind, $text); $lines.Add($line); Write-Host $line -ForegroundColor $(switch ($kind) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Gray' } }) }
function Check([string]$what, [bool]$ok) { if (-not $ok) { $script:failed = $true }; Say $(if ($ok) { 'PASS' } else { 'FAIL' }) $what }
function Run([string[]]$arguments) { $t = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Text = $t; Exit = $LASTEXITCODE } }
function Save { $lines | Out-File (Join-Path $Out 'network-preset-test.txt') -Encoding utf8 }
if (-not $ReadProperty) { $ReadProperty = { param($name, $keyword) @((Get-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $keyword -ErrorAction SilentlyContinue).RegistryValue) -join ',' } }
if (-not $ReadStatus) { $ReadStatus = { param($name) "$((Get-NetAdapter -Name $name -ErrorAction SilentlyContinue).Status)" } }
function IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
# "<description> (<name>): <keyword>: <from> -> <to>" is how the preview names one change of one adapter's driver property.
$changePattern = '^\s+(?<desc>.+) \((?<name>[^()]+)\): (?<key>[^:]+): (?<from>.*) -> (?<to>.*)$'
function Changes([string]$text) { @($text -split "`n" | ForEach-Object { if ($_ -match $changePattern -and $Matches.key -ne 'allowTurnOff') { [pscustomobject]@{ Adapter = $Matches.name; Keyword = $Matches.key; From = $Matches.from.Trim(); To = $Matches.to.Trim() } } }) }

Say 'INFO' "Network preset test, $(Get-Date -Format s), mode: $(if ($Apply) { 'REAL APPLY AND UNDO' } else { 'preview only' })"
$inventory = Run @('network')
$inventory.Text -split "`n" | Where-Object { $_ -match '^\s{2}\S|driver ' } | Select-Object -First 30 | ForEach-Object { Say 'INFO' "  $($_.TrimEnd())" }
$found = @()
foreach ($id in $ids) {
  $r = Run @('windows', 'apply', '--id', $id)
  $c = @(Changes $r.Text)   # one change is not an array until it is made one
  $rows = @($r.Text -split "`n")   # the reason is the line under "not applicable" (or "needs admin")
  $at = @(0..($rows.Count - 1) | Where-Object { $rows[$_] -match '^\s+(not applicable|needs admin|unknown|blocked)\s' }) | Select-Object -First 1
  $why = @(if ($null -ne $at -and $at + 1 -lt $rows.Count) { $rows[$at + 1].Trim() })
  if ($c.Count -gt 0) { Say 'INFO' "preview $id : $($c.Count) change(s)"; $c | ForEach-Object { Say 'INFO' "    $($_.Adapter): $($_.Keyword): $($_.From) -> $($_.To)" }; $found += [pscustomobject]@{ Id = $id; Changes = $c } }
  else { Say 'INFO' "preview $id : nothing to change here ($(if ($why) { $why[0] } else { 'already in place or not for this PC' }))" }
}
if (-not $Apply) { Say 'INFO' 'Preview only: nothing was changed. Add -Apply for the real cycle on the first preset that applies here (or -Preset <id>).'; Save; exit 0 }

# ---- the real cycle ---------------------------------------------------------------------------------------------------------------------------------------
if (-not ($AssumeAdmin -or (IsAdmin))) { Say 'FAIL' 'Writing adapter settings needs administrator rights: start this script as administrator.'; Save; exit 3 }
$pick = if ($Preset) { $found | Where-Object { $_.Id -eq $Preset } | Select-Object -First 1 } else { $found | Select-Object -First 1 }
if (-not $pick) { Say 'INFO' 'No preset has anything to change on this PC (no matching adapter, or its driver lists none of the settings, or they are already in place): nothing to test.'; Save; exit 0 }
$cs = @($pick.Changes)
$names = $cs | ForEach-Object { $_.Adapter } | Select-Object -Unique
Say 'INFO' "Test: $($pick.Id) on adapter(s): $($names -join ', ')"
$before = @{}
foreach ($c in $cs) { $before["$($c.Adapter)|$($c.Keyword)"] = & $ReadProperty $c.Adapter $c.Keyword }
foreach ($c in $cs) { Check "before: $($c.Adapter) $($c.Keyword) reads $($before["$($c.Adapter)|$($c.Keyword)"]) in Windows (the preview said $($c.From))" ($before["$($c.Adapter)|$($c.Keyword)"] -eq $c.From) }
if ($failed) { Say 'FAIL' 'Windows and Mesura disagree about the starting values: nothing was written.'; Save; exit 1 }
$snapshot = Run @('network', 'snapshot', '--name', 'before-preset-test')
$snapshotId = if ($snapshot.Text -match 'Saved snapshot (\S+):') { $Matches[1] } else { $null }
Say 'INFO' "Snapshot of every adapter setting before the test: $snapshotId  (second way back: mesura network restore $snapshotId --apply)"
if (-not $snapshotId) { Say 'FAIL' 'The snapshot could not be taken: nothing was written.'; Save; exit 1 }

Write-Host ''
Write-Host "About to apply $($pick.Id) on the REAL adapter(s) $($names -join ', '), then undo it. The network drops for a few seconds per value." -ForegroundColor Yellow
$answer = if ($Confirmation) { $Confirmation } else { Read-Host 'Type YES to write (anything else cancels)' }
if ($answer -cne 'YES') { Say 'INFO' 'Cancelled: nothing was written.'; Save; exit 0 }

$history = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\history'
$known = @(Get-ChildItem $history -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
$runId = $null
try {
  $applied = Run @('windows', 'apply', '--id', $pick.Id, '--apply')
  $applied.Text -split "`n" | ForEach-Object { Say 'INFO' "  apply: $_" }
  if ($applied.Text -match 'history run ([0-9A-Za-z-]+)') { $runId = $Matches[1] }
  else {
    # The command ended without telling the run's id (a crash). The run is recorded before it ends: find it, so the undo does not depend on the output.
    $new = @(Get-ChildItem $history -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } | Where-Object { $known -notcontains $_ -and $_ -match 'windows-apply$' } | Sort-Object)
    Check "the command ended with an answer (exit code $($applied.Exit))" ($applied.Exit -eq 0)
    if ($new.Count -gt 0) { $runId = $new[-1]; Say 'INFO' "A run was recorded before the command ended: $runId (it will be undone below)." }
  }
  Say 'INFO' "Run: $runId, exit code $($applied.Exit)"
  foreach ($n in $names) { Check "after: adapter $n is up again" ((& $ReadStatus $n) -eq 'Up') }
  foreach ($c in $cs) {
    $now = & $ReadProperty $c.Adapter $c.Keyword
    if ($applied.Exit -eq 0) { Check "independent read: $($c.Adapter) $($c.Keyword) is $($c.To) in Windows (Get-NetAdapterAdvancedProperty)" ($now -eq $c.To) }
    else { Say 'INFO' "independent read after a run that did not succeed: $($c.Adapter) $($c.Keyword) reads $now (before: $($before["$($c.Adapter)|$($c.Keyword)"]))" }
  }
  if ($applied.Exit -ne 0) { Say 'INFO' 'The run did not succeed. Mesura puts back what a failed run changed; the values below show whether it did.' }
  if ($HoldBeforeUndo) { Read-Host 'The preset is in place. Look at the connection if you want, then press Enter to undo' | Out-Null }
}
finally {
  if ($runId) {
    $undo = Run @('windows', 'revert', '--run', $runId, '--apply')
    $undo.Text -split "`n" | ForEach-Object { Say 'INFO' "  undo: $_" }
    foreach ($n in $names) { Check "after the undo: adapter $n is up" ((& $ReadStatus $n) -eq 'Up') }
    foreach ($c in $cs) { Check "after the undo: $($c.Adapter) $($c.Keyword) reads $($before["$($c.Adapter)|$($c.Keyword)"]) again in Windows (the value from before)" ((& $ReadProperty $c.Adapter $c.Keyword) -eq $before["$($c.Adapter)|$($c.Keyword)"]) }
    $diff = Run @('network', 'diff', $snapshotId)
    Check 'Mesura compares the adapters with the snapshot taken before the test: no difference' ($diff.Text -match 'Summary: 0 would be written')
  }
  else {
    Say 'INFO' 'No run was recorded, so there is nothing to undo through Mesura.'
    $stuck = @($cs | Where-Object { (& $ReadProperty $_.Adapter $_.Keyword) -ne $before["$($_.Adapter)|$($_.Keyword)"] })
    if ($stuck.Count -gt 0) { Check "the settings are back as they were (they are NOT: run  mesura network restore $snapshotId --apply  or disable and enable the adapter in Windows)" $false }
  }
}
Save
Say 'INFO' "Wrote $(Join-Path $Out 'network-preset-test.txt')"
if ($failed) { exit 1 } else { exit 0 }
