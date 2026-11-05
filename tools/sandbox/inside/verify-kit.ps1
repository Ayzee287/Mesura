# Inside Windows Sandbox: does the read-only verifier of the owner's kit (tools\tester\Verify-MesuraApplied.ps1) tell a profile that is applied from one that is not, and does it notice a value that was
# changed afterwards? Four situations on a real Windows, each judged by the verifier's own PASS/FAIL lines:
#   1. nothing applied            -> the verifier must report that Maximum performance still has things to apply (a FAIL)
#   2. Maximum performance applied -> the recorded values read back from Windows must all match, and nothing may be left to apply (no FAIL)
#   3. one recorded value changed by hand after the run -> the verifier must name it (a FAIL)
#   4. the run reverted           -> the verifier must say the run's values are no longer in place (a FAIL)
# It applies and reverts only what the profile applies; the sandbox is thrown away anyway.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== verify-kit $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
$verifier = 'C:\Host\tools\tester\Verify-MesuraApplied.ps1'
New-Item -ItemType Directory -Force 'C:\Host\out\verify' | Out-Null

function Verify([string]$label, [string[]]$more = @()) {
  $file = "C:\Host\out\verify\$label.txt"
  $text = (& powershell -NoProfile -ExecutionPolicy Bypass -File $verifier -Cli $cli -Out 'C:\Host\out\verify' @more 2>&1 | ForEach-Object { "$_" })
  $text | Out-File $file -Encoding utf8
  $summary = ($text | Where-Object { $_ -match 'Summary: PASS (\d+), FAIL (\d+)' } | Select-Object -Last 1)
  $fails = @($text | Where-Object { $_ -match '^FAIL ' })
  [pscustomobject]@{ Text = $text; Fails = $fails; Summary = "$summary".Trim() }
}
function Mesura([string[]]$arguments) {
  $ErrorActionPreference = 'Continue'
  $text = (& $cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Run = $(if ($text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }); Exit = $LASTEXITCODE }
}

# 1. nothing applied
$v1 = Verify '1-before'
Check '[1 nothing applied] the verifier reports that the level still has things to apply' (@($v1.Fails | Where-Object { $_ -match 'nothing is left to apply' }).Count -gt 0) $v1.Summary
Check '[1 nothing applied] and says there is no run to read back' (@($v1.Text | Where-Object { $_ -match 'No run with recorded Windows changes' }).Count -gt 0)

# 2. applied
$apply = Mesura @('windows', 'apply', '--profile', 'maximum-performance', '--apply')
Info 'Maximum performance applied' "run $($apply.Run), exit $($apply.Exit)"
$apply.Text | Out-File 'C:/Host/out/verify/apply-output.txt' -Encoding utf8   # what the apply said about every operation, to explain any entry the plan still lists afterwards
$v2 = Verify '2-applied' @('-Run', "$($apply.Run)")
foreach ($f in $v2.Fails) { Say "      $f" }
Check '[2 applied] the verifier finds no FAIL: the run''s recorded values read back from Windows match and nothing is left to apply' ($v2.Fails.Count -eq 0) $v2.Summary
Check '[2 applied] it read back at least one value' (@($v2.Text | Where-Object { $_ -match '^PASS\s+([1-9]\d*) value\(s\) the run recorded' }).Count -gt 0)

# 3. a value changed by hand afterwards
$record = Get-Content -Raw -Encoding UTF8 (Join-Path $env:LOCALAPPDATA "Mesura\Setup\history\$($apply.Run)\run.json") | ConvertFrom-Json
$target = $null
foreach ($op in $record.windows) { foreach ($c in $op.changes) { if (-not $target -and $c.kind -eq 'Registry' -and $c.registryKind -eq 'DWord' -and $c.hive -eq 'HKCU' -and $null -ne $c.after) { $target = $c } } }
if ($target) {
  $key = "HKCU:\$($target.path)"
  $changed = if ("$($target.after)" -eq '7') { 8 } else { 7 }
  Set-ItemProperty -LiteralPath $key -Name $target.name -Value $changed -Type DWord
  $v3 = Verify '3-tampered' @('-Run', "$($apply.Run)")
  Check "[3 tampered] the verifier names the value changed after the run ($($target.target))" (@($v3.Fails | Where-Object { $_ -match 'read back from Windows still equal' }).Count -gt 0) $v3.Summary
  Set-ItemProperty -LiteralPath $key -Name $target.name -Value ([int]$target.after) -Type DWord   # put it back
} else { Info '[3 tampered] skipped' 'the run recorded no HKCU DWord to change' }

# 4. reverted
$revert = Mesura @('windows', 'revert', '--run', "$($apply.Run)", '--apply')
Info 'Run reverted' "exit $($revert.Exit)"
$v4 = Verify '4-reverted' @('-Run', "$($apply.Run)")
Check '[4 reverted] the verifier says the run''s values are no longer in place' (@($v4.Fails | Where-Object { $_ -match 'read back from Windows still equal' }).Count -gt 0) $v4.Summary
Say "== verify-kit done $(Get-Date -Format s)"
