# A supervised, reversible test of Mesura's rollback ON THIS PC: undo the newest Windows apply run, prove the undo with Windows' own tools, prove the verifier notices, then apply EXACTLY the same operations
# again and prove that too. READ THIS BEFORE RUNNING: it really changes this PC (the operations of that run are undone and applied again: power plan, policies, registry values). It needs administrator rights
# when the run changed machine-wide settings, and it asks you to type YES.
#
# Why: apply, read-back and undo of a profile have so far been proven on a real Windows only inside Windows Sandbox. This is the same cycle on your real PC, where the first apply already happened.
#
# The steps, each judged by the verifier's independent reading of the registry and powercfg (not by Mesura):
#   1. the newest Windows apply run reads back as in place                         (the starting state)
#   2. `mesura windows revert --run <id> --apply`
#   3. the revert run's recorded values read back as restored
#   4. the ORIGINAL run's values are NOT in place any more                         (a negative control: the verifier must be able to say no)
#   5. the plan lists things to apply again
#   6. the same operations are applied again (`mesura windows apply --id ... --apply`)
#   7. the new run reads back as in place and none of its operations is still listed to apply
# The list of operations is saved to <Out>\profile-cycle-operations.txt first, so the state can be re-created by hand (`mesura windows apply --id <operation> --apply`) if anything stops half way.
# What stays pending after a restart (hardware scheduling, policies) is reported as pending, not as applied.
#
#   powershell -ExecutionPolicy Bypass -File Test-ProfileCycle.ps1            (preview only: shows what it would undo, changes nothing)
#   powershell -ExecutionPolicy Bypass -File Test-ProfileCycle.ps1 -Apply     (the real cycle; asks you to type YES)
param(
  [switch]$Apply,
  [string]$Run = '',
  [string]$Level = 'maximum-performance',
  [string]$Cli = '',
  [string]$Out = '',
  [string]$Confirmation = ''
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
if (-not $Cli) { Write-Host 'mesura.exe was not found (put it beside this script or pass -Cli).'; exit 3 }
$verifier = Join-Path $PSScriptRoot 'Verify-MesuraApplied.ps1'
if (-not (Test-Path $verifier)) { Write-Host 'Verify-MesuraApplied.ps1 must be beside this script.'; exit 3 }
$history = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\history'
$lines = New-Object System.Collections.Generic.List[string]
$failed = $false
function Say([string]$kind, [string]$text) { $line = ('{0,-5} {1}' -f $kind, $text); $lines.Add($line); Write-Host $line -ForegroundColor $(switch ($kind) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Gray' } }) }
function Check([string]$what, [bool]$ok) { if (-not $ok) { $script:failed = $true }; Say $(if ($ok) { 'PASS' } else { 'FAIL' }) $what }
function Run-Cli([string[]]$arguments) { $t = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Text = $t; Exit = $LASTEXITCODE } }
function IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
function Verify([string]$runId) {
  $tmp = Join-Path $env:TEMP 'mesura-cycle-verify'; New-Item -ItemType Directory -Force $tmp | Out-Null
  $text = (& powershell -NoProfile -ExecutionPolicy Bypass -File $verifier -Cli $Cli -Out $tmp -Level $Level -Run $runId 2>&1 | ForEach-Object { "$_" })
  [pscustomobject]@{
    ReadBackOk = @($text | Where-Object { $_ -match '^PASS\s+\d+ value\(s\) the run recorded read back' }).Count -gt 0
    ReadBackFailed = @($text | Where-Object { $_ -match '^FAIL\s+\d+ value\(s\) the run recorded read back' }).Count -gt 0
    NothingLeft = @($text | Where-Object { $_ -match '^PASS\s+nothing is left to apply' }).Count -gt 0
    SomethingLeft = @($text | Where-Object { $_ -match '^FAIL\s+nothing is left to apply' }).Count -gt 0
    StillToApply = @($text | ForEach-Object { if ($_ -match '^\s+still to apply:\s+(\S+)') { $Matches[1] } })
    Text = $text
  }
}
function RunIds { @(Get-ChildItem $history -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }

Say 'INFO' "Profile rollback cycle, $(Get-Date -Format s), mode: $(if ($Apply) { 'REAL CYCLE' } else { 'preview only' }), level $Level"
if (-not $Run) { $Run = RunIds | Where-Object { $_ -match 'windows-apply$' } | Sort-Object -Descending | Select-Object -First 1 }
if (-not $Run) { Say 'INFO' 'No recorded Windows apply run was found: nothing to undo. Apply a profile first.'; exit 0 }
$record = Get-Content -Raw -Encoding UTF8 (Join-Path $history "$Run\run.json") | ConvertFrom-Json
$ops = @($record.windows | Where-Object { $null -ne $_.changes -and @($_.changes | Where-Object { $null -ne $_ }).Count -gt 0 } | ForEach-Object { $_.operationId })
Say 'INFO' "Run $Run changed $($ops.Count) operation(s): $($ops -join ', ')"
$machineWide = @($record.windows | ForEach-Object { $_.changes } | Where-Object { $_.hive -eq 'HKLM' -or $_.kind -in 'PowerSetting', 'PowerScheme' }).Count -gt 0
if ($machineWide -and -not (IsAdmin)) { Say 'FAIL' 'The run changed machine-wide settings: start this script as administrator.'; exit 3 }
$preview = Run-Cli @('windows', 'revert', '--run', $Run)
$preview.Text -split "`n" | Select-Object -First 25 | ForEach-Object { Say 'INFO' "  undo preview: $_" }
if (-not $Apply) { Say 'INFO' 'Preview only: nothing was changed. Add -Apply for the real cycle.'; $lines | Out-File (Join-Path $Out 'profile-cycle-test.txt') -Encoding utf8; exit 0 }

# The cycle must be able to put things back: every operation of the run has to exist in THIS build, or the re-apply step cannot work and the PC would be left undone (2026-11-04: the run had used
# network.interrupt-moderation-off, retired in 0.5, so the undo worked and the re-apply was refused with "Unknown operation"). Previewing the apply writes nothing.
$gone = @($ops | Where-Object { (Run-Cli @('windows', 'apply', '--id', $_)).Text -match 'Unknown operation' })
if ($gone.Count -gt 0) {
  Say 'FAIL' "This build no longer has: $($gone -join ', ') (retired or withheld). The cycle could not apply it again, so nothing was changed. Use a build that has it, or a run that used current operations."
  $lines | Out-File (Join-Path $Out 'profile-cycle-test.txt') -Encoding utf8; exit 3
}

Write-Host ''
Write-Host "This UNDOES run $Run on this PC and then APPLIES the same operations again. Both steps are recorded and undoable." -ForegroundColor Yellow
$answer = if ($Confirmation) { $Confirmation } else { Read-Host 'Type YES to run the cycle (anything else cancels)' }
if ($answer -cne 'YES') { Say 'INFO' 'Cancelled: nothing was changed.'; $lines | Out-File (Join-Path $Out 'profile-cycle-test.txt') -Encoding utf8; exit 0 }
$ops | Out-File (Join-Path $Out 'profile-cycle-operations.txt') -Encoding ascii
Say 'INFO' "The operation list was saved to $(Join-Path $Out 'profile-cycle-operations.txt')"

# 1. the starting state
$v1 = Verify $Run
Check '[1 start] the run''s recorded values read back from Windows as in place' $v1.ReadBackOk
if ($v1.ReadBackFailed) { Say 'INFO' 'Something differs before the test starts; the lines of the verification say what.'; $v1.Text | Where-Object { $_ -match '^\s+\S' } | Select-Object -First 8 | ForEach-Object { Say 'INFO' "  $_" } }

# 2. undo
$known = RunIds
$undo = Run-Cli @('windows', 'revert', '--run', $Run, '--apply')
Say 'INFO' "revert exit code $($undo.Exit)"
$revertRun = RunIds | Where-Object { $known -notcontains $_ -and $_ -match 'windows-revert$' } | Sort-Object -Descending | Select-Object -First 1
Say 'INFO' "revert run: $revertRun"

# 3 and 4. the undo proven, and the verifier able to say no
if ($revertRun) { $v3 = Verify $revertRun; Check '[2 undone] the revert run''s recorded values read back as restored' $v3.ReadBackOk }
else { Check '[2 undone] a revert run was recorded' $false }
$v4 = Verify $Run
Check '[2 undone] the original run''s values are no longer in place (the negative control: the verifier can say no)' $v4.ReadBackFailed

# 5. the plan sees it
$plan = Run-Cli @('windows', 'plan', '--profile', $Level, '--json')
$json = $null; try { $json = ($plan.Text -split "`n" | Where-Object { $_ -notmatch '^Windows catalog:' }) -join "`n" | ConvertFrom-Json } catch { }
$left = if ($json) { @($json.groups | Where-Object { $_.enabled } | ForEach-Object { $_.entries } | Where-Object { $_.state -eq 'ToApply' -and $_.role -ne 'Optional' }).Count } else { -1 }
Check "[2 undone] the plan lists things to apply again ($left)" ($left -gt 0)

# 6. the same operations again
$idArgs = @($ops | ForEach-Object { @('--id', $_) })
$again = Run-Cli (@('windows', 'apply') + $idArgs + '--apply')
Say 'INFO' "re-apply exit code $($again.Exit)"
if ($again.Exit -ne 0) { $again.Text -split "`n" | Select-Object -First 15 | ForEach-Object { Say 'INFO' "  re-apply: $_" } }
$newRun = if ($again.Text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { RunIds | Where-Object { $known -notcontains $_ -and $_ -match 'windows-apply$' } | Sort-Object -Descending | Select-Object -First 1 }
Say 'INFO' "new apply run: $newRun"

# 7. the end state
if ($newRun) {
  $v7 = Verify $newRun
  Check '[3 applied again] the new run''s recorded values read back from Windows as in place' $v7.ReadBackOk
  # Judged on the operations of the run, not on the whole level: a run that applied one operation leaves the level's other operations to apply, which is not a failure (2026-11-04).
  $stillOurs = @($ops | Where-Object { $v7.StillToApply -contains $_ })
  Check "[3 applied again] none of the run's operations is listed as still to apply$(if ($stillOurs.Count) { ': ' + ($stillOurs -join ', ') })" ($stillOurs.Count -eq 0)
} else { Check '[3 applied again] a new apply run was recorded' $false }
$notes = @($again.Text -split "`n" | Where-Object { $_ -match 'Restart Windows|Sign out|Restart Explorer' })
foreach ($n in $notes) { Say 'INFO' "pending: $($n.Trim())" }
Say 'INFO' 'Operations that need a restart or a sign-out are reported as pending until you do it; run the verifier again afterwards.'
$lines | Out-File (Join-Path $Out 'profile-cycle-test.txt') -Encoding utf8
Say 'INFO' "Wrote $(Join-Path $Out 'profile-cycle-test.txt')"
if ($failed) { exit 1 } else { exit 0 }
