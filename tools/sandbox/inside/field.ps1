# Inside Windows Sandbox: the field kit (tools\field) end to end on a real Windows 11 that is thrown away afterwards: an ELEVATED survey (the paths a normal PC takes when started as administrator, and the
# virtual-machine paths), then the real scenarios the kit offers (profile-cycle for both profiles with an undo, the three-phase reboot test up to the point where a restart is needed, and the scenarios
# that must say "not available here"), then the merger over everything. The sandbox has no GPU, no battery, one virtual network adapter and no restart: those parts are covered by the owner's PCs.
# Packages folder = artifacts\field-kit (it holds MesuraField\).
$ErrorActionPreference = 'Continue'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This scenario changes Windows settings: it runs only inside Windows Sandbox (user WDAGUtilityAccount).' }
New-Item -ItemType Directory -Force 'C:\Host\out' | Out-Null
try { Start-Transcript -Path 'C:\Host\out\transcript-field.txt' -Force | Out-Null } catch { }
$report = 'C:\Host\out\report.txt'
function Say([string]$line) { Write-Host $line; Add-Content -Path $report -Value $line -Encoding utf8 }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
function Info([string]$what, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f 'INFO', $what, $(if ($detail) { "  ($detail)" } else { '' })) }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say "== field kit $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $admin)"

$kit = 'C:\kit\MesuraField'
New-Item -ItemType Directory -Force 'C:\kit' | Out-Null
Copy-Item 'C:\Host\packages\MesuraField' 'C:\kit' -Recurse -Force
$results = 'C:\Host\out\field-results'
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Run-Kit([string[]]$kitArgs) {
  $log = 'C:\Host\out\kit-' + (Get-Date -Format 'HHmmss') + '.log'
  $p = Start-Process $ps -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$kit\Start-MesuraField.ps1`"", '-NoElevate', '-Results', "`"$results`"") + $kitArgs) -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
  [pscustomobject]@{ Exit = $p.ExitCode; Log = $log }
}

# 1. the survey, elevated, full
$r = Run-Kit @('-Mode', 'survey', '-Label', 'sandbox')
Check 'survey: exits 0 (its own format check passed)' ($r.Exit -eq 0) "exit $($r.Exit)"
$run = Get-ChildItem $results -Directory | Where-Object { $_.Name -like 'PC-*' } | ForEach-Object { Get-ChildItem $_.FullName -Directory } | Sort-Object Name | Select-Object -First 1
Check 'survey: one run folder and one zip' ($null -ne $run -and (Get-ChildItem $results -Filter '*.zip').Count -eq 1)

# 2. real scenarios
$r = Run-Kit @('-Mode', 'scenario', '-Scenario', 'profile-cycle', '-Level', 'both', '-ProfileCycleScope', 'any', '-Confirmation', 'YES')
Check 'profile-cycle (both profiles): exits 0' ($r.Exit -eq 0) "exit $($r.Exit)"
$r = Run-Kit @('-Mode', 'scenario', '-Scenario', 'profile-cycle', '-Level', 'maximum-performance', '-OnlyGroup', 'gpu', '-ProfileCycleScope', 'any', '-Confirmation', 'YES')
Check 'profile-cycle (one group, gpu): exits 0' ($r.Exit -eq 0) "exit $($r.Exit)"
$r = Run-Kit @('-Mode', 'scenario', '-Scenario', 'profile-cycle', '-Level', 'recommended-gaming', '-ProfileCycleScope', 'any', '-Confirmation', 'no')
Check 'profile-cycle: anything but YES cancels and writes nothing' ($r.Exit -eq 0)
$r = Run-Kit @('-Mode', 'scenario', '-Scenario', 'profile-cycle', '-Level', 'both', '-Confirmation', 'YES')
Check 'profile-cycle without the test switch: refused on a PC that is not one of the two permitted (nothing written)' ($r.Exit -eq 0)
foreach ($s in 'network-preset', 'network-effect', 'amd-writer', 'gpu-idle', 'nvpi-check') {
  $r = Run-Kit @('-Mode', 'scenario', '-Scenario', $s, '-Confirmation', 'YES')
  Check "$s : says it is not available here, without an error" ($r.Exit -eq 0) "exit $($r.Exit)"
}
# the reboot test: phase 1 really applies one operation; phase 2 without a restart must notice that no restart happened; then undo by hand
$r = Run-Kit @('-Mode', 'scenario', '-Scenario', 'reboot-test', '-Operation', 'system.long-paths-on', '-Confirmation', 'YES')
Check 'reboot-test phase 1: exits 0' ($r.Exit -eq 0) "exit $($r.Exit)"
$pending = Get-ChildItem (Join-Path $results 'pending') -Filter '*-reboot-test.json' -ErrorAction SilentlyContinue
Check 'reboot-test phase 1: the pending file exists' (@($pending).Count -eq 1)
$r = Run-Kit @('-Mode', 'scenario', '-Scenario', 'reboot-test', '-Confirmation', 'YES')
Check 'reboot-test phase 2 without a restart: exits 0 and says no restart happened' ($r.Exit -eq 0) "exit $($r.Exit)"
if (@($pending).Count -eq 1) {
  $st = Get-Content $pending[0].FullName -Raw | ConvertFrom-Json
  $cli = 'C:\kit\MesuraField\bin\mesura.exe'
  $rev = (& $cli windows revert --run $st.applyRunId --apply 2>&1 | ForEach-Object { "$_" }) -join "`n"
  Check 'reboot-test: the operation applied by phase 1 is undone by hand' ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
}

# 3. the merger over everything
$merged = 'C:\Host\out\merged'
$m = & (Join-Path 'C:\Host\tools\field' 'Merge-FieldResults.ps1') -From $results -Out $merged 6>$null | Select-Object -Last 1
Check 'merge: every run accepted, none rejected' ($m.Rejected -eq 0 -and $m.Accepted -ge 9) "accepted $($m.Accepted), rejected $($m.Rejected)"
$machines = @(Import-Csv (Join-Path $merged 'machines.csv'))
Check 'merge: one machine, recognised as a virtual machine, surveyed elevated' ($machines.Count -eq 1 -and $machines[0].virtual -eq 'True' -and $machines[0].elevatedSurvey -eq 'True') "$($machines[0].form) virtual=$($machines[0].virtual) elevated=$($machines[0].elevatedSurvey)"
$ke = @(Import-Csv (Join-Path $merged 'kit-errors.csv') -ErrorAction SilentlyContinue)
Check 'no step of the kit itself failed in any run' ($ke.Count -eq 0) (($ke | ForEach-Object { "$($_.module)/$($_.step): $($_.message)" }) -join ' | ')
$sc = @(Import-Csv (Join-Path $merged 'scenarios.csv'))
$pc = @($sc | Where-Object { $_.scenario -eq 'profile-cycle' -and $_.status -eq 'ok' })
Check 'profile-cycle runs have no FAIL check' ($pc.Count -ge 2 -and @($pc | Where-Object { [int]$_.fail -gt 0 }).Count -eq 0) (($pc | ForEach-Object { "pass $($_.pass) fail $($_.fail) $($_.failedChecks)" }) -join ' || ')
$cyc = @(Import-Csv (Join-Path $merged 'profile-cycles.csv'))
Check 'rollback is reported complete for every cycle that applied something' (@($cyc | Where-Object { [int]$_.planned -gt 0 -and $_.rollbackComplete -ne 'True' }).Count -eq 0) (($cyc | ForEach-Object { "$($_.level)=$($_.rollbackComplete)" }) -join ' ')
$refused = @($sc | Where-Object { $_.scenario -eq 'profile-cycle' -and $_.status -eq 'notapplicable' })
Check 'the refused profile-cycle is recorded as notapplicable' ($refused.Count -eq 1)
Check 'profile-cycles.csv: Reference, Maximum and the one-group cycle are all there' ($cyc.Count -ge 3) (($cyc | ForEach-Object { "$($_.level)/$($_.onlyGroup) planned=$($_.planned) fail=$($_.failures) mismatches=$($_.mismatchesAfterApply)/$($_.mismatchesAfterRevert) diff=$($_.differencesAfterRevert)" }) -join ' ; ')
foreach ($s in 'network-preset', 'network-effect', 'amd-writer', 'gpu-idle', 'nvpi-check') { Check "$s is recorded as notapplicable" (@($sc | Where-Object { $_.scenario -eq $s -and $_.status -eq 'notapplicable' }).Count -eq 1) }
$rb = @(Import-Csv (Join-Path $merged 'reboot-tests.csv'))
Check 'reboot-tests.csv: phase 1 and the phase 2 that found no restart' ($rb.Count -eq 2 -and ($rb | Where-Object { $_.phase -eq '2' }).restarted -eq 'False') (($rb | ForEach-Object { "phase $($_.phase) op=$($_.operation) restarted=$($_.restarted)" }) -join ' ; ')
foreach ($f in 'report.md', 'machines.csv', 'operation-states.csv', 'network-adapters.csv', 'adamx.csv', 'registry-state.csv', 'power-settings-all.csv') { Check "merged output: $f exists" (Test-Path (Join-Path $merged $f)) }
$uns = @(Import-Csv (Join-Path $merged 'unsupported-summary.csv'))
Info 'what the kit could not read in the sandbox (kind: what)' (($uns | Select-Object -First 12 | ForEach-Object { "$($_.kind): $($_.what)" }) -join ' | ')

Say "== field kit done $(Get-Date -Format s)"
try { Stop-Transcript | Out-Null } catch { }
