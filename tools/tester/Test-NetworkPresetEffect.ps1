# Does a network adapter preset change the round-trip time to your router on THIS PC? A supervised, reversible A/B/A/B test on the real adapter.
#
#   Each cycle: preset OFF (the driver's own values) -> capture latency -> apply the preset -> capture latency -> undo it. The cycles alternate (A B A B A B), the first cycle is a warm-up and is dropped, and
#   tools\tester\Measure-NetworkLatency.ps1 -Compare says whether B differs from A by more than the run-to-run noise (the rule of docs/gpu-measurement-g7.md). Every apply and undo goes through Mesura
#   (windows apply --id ... --apply, windows revert --run ...), and before AND after the whole test the driver values are read back with Windows' own cmdlets and must equal what they were at the start.
#
# What to expect, in plain words: the adapter restarts on every value written, so the connection drops for a few seconds, several times per cycle (this script waits for it to come back and for the router to answer).
# On Wi-Fi the spread between runs is far larger than the microseconds these settings act on, so "no measurable difference" is the likely, and a valid, answer; a wired link resolves more. Nothing here says
# a preset makes anything faster unless this measurement shows it on this PC.
#
#   powershell -ExecutionPolicy Bypass -File Test-NetworkPresetEffect.ps1 -Preset network.preset-intel-wifi [-Cycles 4] [-Count 500]        (from an elevated terminal; asks you to type YES)
param(
  [string]$Preset = '',
  [int]$Cycles = 4,
  [int]$Count = 500,
  [int]$IntervalMs = 20,
  [int]$SettleSeconds = 12,
  [string]$Cli = '',
  [string]$Out = '',
  [string]$Confirmation = '',
  [scriptblock]$ReadProperty = $null,
  [scriptblock]$ReadStatus = $null,
  [scriptblock]$CaptureHook = $null,    # tests only: (label, run, folder) -> writes <label>-<run>.netlat.json
  [switch]$AssumeAdmin                  # tests only
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'network-preset-effect' }
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
if (-not $Cli) { Write-Host 'mesura.exe was not found (put it beside this script or pass -Cli).'; exit 3 }
$measure = Join-Path $PSScriptRoot 'Measure-NetworkLatency.ps1'
if (-not $CaptureHook -and -not (Test-Path $measure)) { Write-Host 'Measure-NetworkLatency.ps1 must be beside this script.'; exit 3 }
$lines = New-Object System.Collections.Generic.List[string]; $failed = $false
function Say([string]$kind, [string]$text) { $line = ('{0,-5} {1}' -f $kind, $text); $lines.Add($line); Write-Host $line -ForegroundColor $(switch ($kind) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Gray' } }) }
function Check([string]$what, [bool]$ok) { if (-not $ok) { $script:failed = $true }; Say $(if ($ok) { 'PASS' } else { 'FAIL' }) $what }
function Run([string[]]$arguments) { $t = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Text = $t; Exit = $LASTEXITCODE } }
function Save { $lines | Out-File (Join-Path $Out 'network-preset-effect.txt') -Encoding utf8 }
if (-not $ReadProperty) { $ReadProperty = { param($name, $keyword) @((Get-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $keyword -ErrorAction SilentlyContinue).RegistryValue) -join ',' } }
if (-not $ReadStatus) { $ReadStatus = { param($name) "$((Get-NetAdapter -Name $name -ErrorAction SilentlyContinue).Status)" } }
function IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
$changePattern = '^\s+(?<desc>.+) \((?<name>[^()]+)\): (?<key>[^:]+): (?<from>.*) -> (?<to>.*)$'
function Changes([string]$text) { @($text -split "`n" | ForEach-Object { if ($_ -match $changePattern -and $Matches.key -ne 'allowTurnOff') { [pscustomobject]@{ Adapter = $Matches.name; Keyword = $Matches.key; From = $Matches.from.Trim(); To = $Matches.to.Trim() } } }) }
function Gateway { $r = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1; if ($r) { $r.NextHop } else { $null } }
function WaitOnline([string[]]$adapters) {
  # the adapter up again AND the router answering, then a settling time so DHCP, roaming scans and the radio have calmed down
  $deadline = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $deadline) {
    $up = @($adapters | Where-Object { (& $ReadStatus $_) -eq 'Up' }).Count -eq $adapters.Count
    $gw = Gateway
    if ($up -and $gw -and (Test-Connection -ComputerName $gw -Count 1 -Quiet -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds $SettleSeconds; return $true }
    if ($CaptureHook) { return $true }
    Start-Sleep -Seconds 2
  }
  $false
}
function Capture([string]$label, [int]$run) {
  if ($CaptureHook) { & $CaptureHook $label $run $Out; return }
  & $measure -Capture -Label $label -Run $run -Count $Count -IntervalMs $IntervalMs -Out $Out | ForEach-Object { Say 'INFO' "  $_" }
}

Say 'INFO' "Network preset effect test, $(Get-Date -Format s), $Cycles cycle(s) of $Count probes"
# 1. which preset, on which adapter
$ids = if ($Preset) { @($Preset) } else { 'network.preset-realtek-ethernet', 'network.preset-intel-ethernet', 'network.preset-killer-ethernet', 'network.preset-intel-wifi', 'network.preset-realtek-wifi' }
$pick = $null
foreach ($id in $ids) { $c = @(Changes (Run @('windows', 'apply', '--id', $id)).Text); if ($c.Count -gt 0) { $pick = [pscustomobject]@{ Id = $id; Changes = $c }; break } }
if (-not $pick) { Say 'INFO' 'No preset has anything to change on this PC: nothing to test.'; Save; exit 0 }
$cs = @($pick.Changes); $names = @($cs | ForEach-Object { $_.Adapter } | Select-Object -Unique)
Say 'INFO' "Preset $($pick.Id) on adapter(s) $($names -join ', '):"; $cs | ForEach-Object { Say 'INFO' "    $($_.Keyword): $($_.From) -> $($_.To)" }
if ($Cycles -lt 3) { Say 'FAIL' 'At least 3 cycles are needed (the first is a warm-up and at least 3 runs per label are needed for a noise band).'; Save; exit 2 }
if (-not ($AssumeAdmin -or (IsAdmin))) { Say 'FAIL' 'Writing adapter settings needs administrator rights: start this script from an elevated terminal.'; Save; exit 3 }
if (-not $CaptureHook -and -not (Gateway)) { Say 'FAIL' 'No default gateway: the test measures the round trip to your router.'; Save; exit 3 }

# 2. the starting values, in Windows' own words, and the second way back
$start = @{}; foreach ($c in $cs) { $start["$($c.Adapter)|$($c.Keyword)"] = & $ReadProperty $c.Adapter $c.Keyword }
foreach ($c in $cs) { Check "start: $($c.Adapter) $($c.Keyword) reads $($start["$($c.Adapter)|$($c.Keyword)"]) in Windows (Mesura's preview said $($c.From))" ($start["$($c.Adapter)|$($c.Keyword)"] -eq $c.From) }
if ($failed) { Say 'FAIL' 'Windows and Mesura disagree about the starting values (or the preset is already on): nothing was written.'; Save; exit 1 }
$snap = Run @('network', 'snapshot', '--name', 'before-effect-test'); $snapId = if ($snap.Text -match 'Saved snapshot (\S+):') { $Matches[1] } else { $null }
if (-not $snapId) { Say 'FAIL' 'The snapshot could not be taken: nothing was written.'; Save; exit 1 }
Say 'INFO' "Snapshot before the test: $snapId  (second way back: mesura network restore $snapId --apply)"

Write-Host ''
Write-Host "About to apply and undo $($pick.Id) $Cycles times on the REAL adapter(s) $($names -join ', '), measuring after each. The connection drops for a few seconds several times per cycle." -ForegroundColor Yellow
$answer = if ($Confirmation) { $Confirmation } else { Read-Host 'Type YES to start (anything else cancels)' }
if ($answer -cne 'YES') { Say 'INFO' 'Cancelled: nothing was written.'; Save; exit 0 }

$history = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\history'
$runId = $null
try {
  for ($i = 1; $i -le $Cycles; $i++) {
    Say 'INFO' "== cycle $i of $Cycles"
    Check "cycle ${i}: A (preset off): the values are the starting ones" (@($cs | Where-Object { (& $ReadProperty $_.Adapter $_.Keyword) -ne $start["$($_.Adapter)|$($_.Keyword)"] }).Count -eq 0)
    if (-not (WaitOnline $names)) { Check "cycle ${i}: the router answers before the A capture" $false; break }
    Capture 'preset-off' $i
    $known = @(Get-ChildItem $history -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $applied = Run @('windows', 'apply', '--id', $pick.Id, '--apply')
    $runId = if ($applied.Text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { @(Get-ChildItem $history -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } | Where-Object { $known -notcontains $_ -and $_ -match 'windows-apply$' } | Sort-Object) | Select-Object -Last 1 }
    Check "cycle ${i}: apply ended with an answer (exit $($applied.Exit)) and the run is $runId" ($applied.Exit -eq 0 -and $runId)
    if ($applied.Exit -ne 0) { $applied.Text -split "`n" | Select-Object -First 8 | ForEach-Object { Say 'INFO' "  apply: $_" } }
    foreach ($c in $cs) { if ($applied.Exit -eq 0) { Check "cycle ${i}: independent read: $($c.Adapter) $($c.Keyword) is $($c.To)" ((& $ReadProperty $c.Adapter $c.Keyword) -eq $c.To) } }
    if ($applied.Exit -ne 0) { break }
    if (-not (WaitOnline $names)) { Check "cycle ${i}: the router answers after the preset is applied" $false }
    else { Capture 'preset-on' $i }
    $undo = Run @('windows', 'revert', '--run', $runId, '--apply'); $runId = $null
    Check "cycle ${i}: the undo ran (exit $($undo.Exit))" ($undo.Exit -eq 0)
    foreach ($c in $cs) { Check "cycle ${i}: after the undo $($c.Adapter) $($c.Keyword) is $($start["$($c.Adapter)|$($c.Keyword)"]) again" ((& $ReadProperty $c.Adapter $c.Keyword) -eq $start["$($c.Adapter)|$($c.Keyword)"]) }
  }
}
finally {
  if ($runId) { $u = Run @('windows', 'revert', '--run', $runId, '--apply'); Say 'INFO' "the run left open was undone (exit $($u.Exit))" }
  foreach ($n in $names) { Check "end: adapter $n is up" ((& $ReadStatus $n) -eq 'Up') }
  foreach ($c in $cs) { Check "end: $($c.Adapter) $($c.Keyword) reads the starting value $($start["$($c.Adapter)|$($c.Keyword)"])" ((& $ReadProperty $c.Adapter $c.Keyword) -eq $start["$($c.Adapter)|$($c.Keyword)"]) }
  $diff = Run @('network', 'diff', $snapId)
  Check 'end: Mesura compares the adapters with the snapshot taken before the test: no difference' ($diff.Text -match 'Summary: 0 would be written')
}

# 3. the verdict, by the A B A B rule, without the warm-up cycle
foreach ($label in 'preset-off', 'preset-on') { Remove-Item (Join-Path $Out "$label-1.netlat.json") -ErrorAction SilentlyContinue }
Say 'INFO' 'Comparison (cycle 1 is the warm-up and is left out):'
$cmp = if ($CaptureHook) { @('(no comparison in the stand-in test)') } else { @(& $measure -Compare $Out -A preset-off -B preset-on *>&1 | ForEach-Object { "$_" }) }
$cmp | ForEach-Object { Say 'INFO' "  $_" }
Save
Say 'INFO' "Wrote $(Join-Path $Out 'network-preset-effect.txt')"
if ($failed) { exit 1 } else { exit 0 }
