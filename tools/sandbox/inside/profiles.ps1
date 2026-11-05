# Inside Windows Sandbox: the 0.6 optimization levels on a real Windows (tools\e2e-profiles.ps1): the hardware profile against what Windows says, each level planned, applied through the
# operations the app applies, read back independently (registry, powercfg), planned again, applied twice, reverted and read back again. Everything it changes it reverts, and the sandbox is thrown away anyway.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== profiles $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
New-Item -ItemType Directory -Force 'C:\Host\out\ops' | Out-Null
$report = 'C:\Host\out\ops\e2e-profiles.txt'
$stdout = 'C:\Host\out\ops\e2e-profiles-console.txt'
Remove-Item $report, $stdout -ErrorAction SilentlyContinue
$p = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\Host\tools\e2e-profiles.ps1', '-Cli', $cli, '-Report', $report) -PassThru `
  -RedirectStandardOutput $stdout -RedirectStandardError 'C:\Host\out\ops\e2e-profiles-err.txt' -WindowStyle Hidden
$done = $p.WaitForExit(30 * 60000)
if (-not $done) { try { $p.Kill() } catch { } }
$lines = @(Get-Content $report -ErrorAction SilentlyContinue)
$fail = @($lines | Where-Object { $_ -match '^\s*FAIL' })
$pass = @($lines | Where-Object { $_ -match '^\s*PASS' })
Info 'e2e-profiles' "finished: $done; exit $(if ($done) { $p.ExitCode } else { 'killed' }); $($pass.Count) PASS, $($fail.Count) FAIL"
foreach ($f in $fail) { Say "      $f" }
Check 'e2e-profiles has no FAIL and finished' ($done -and $fail.Count -eq 0) ''
Say "== done $(Get-Date -Format s)"
