# Inside Windows Sandbox: the real-Windows lifecycle and operation checks that exist as scripts (tools\e2e-*.ps1) and could not be run on the development PC, each as its own PowerShell process with a time limit
# and its own report. Everything it changes it undoes, and the sandbox is thrown away anyway.
#   per-user install lifecycle with a failed update (e2e-installer.ps1), all-users install / update / uninstall (e2e-installer-allusers.ps1), per-user registry operations (e2e-user-ops.ps1),
#   the machine-wide operations with the heaviest consequences (e2e-admin-ops.ps1: services, hibernation, NetBIOS, DISM feature, power setting, bcdedit, memory, TCP, policy, NTFS),
#   the two power plans on a real powercfg (e2e-power-plans.ps1 -TryImport).
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== stable-ops $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
$older = Package 'older'; $newer = Package 'newer'
$tools = 'C:\Host\tools'
New-Item -ItemType Directory -Force 'C:\Host\out\ops' | Out-Null

function Run-Script([string]$name, [string[]]$arguments, [int]$minutes = 25) {
  $report = "C:\Host\out\ops\$name.txt"
  $stdout = "C:\Host\out\ops\$name-console.txt"
  Remove-Item $report, $stdout -ErrorAction SilentlyContinue
  $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$tools\$name.ps1") + $arguments + @('-Report', $report)
  $p = Start-Process powershell -ArgumentList $argList -PassThru -RedirectStandardOutput $stdout -RedirectStandardError "C:\Host\out\ops\$name-err.txt" -WindowStyle Hidden
  $done = $p.WaitForExit($minutes * 60000)
  if (-not $done) { try { $p.Kill() } catch { } }
  $lines = @(Get-Content $report -ErrorAction SilentlyContinue)
  $fail = @($lines | Where-Object { $_ -match '^\s*FAIL' })
  $pass = @($lines | Where-Object { $_ -match '^\s*PASS' })
  $skip = @($lines | Where-Object { $_ -match '^\s*SKIP' })
  Info "$name" ("finished: $done; exit $(if ($done) { $p.ExitCode } else { 'killed' }); $($pass.Count) PASS, $($fail.Count) FAIL, $($skip.Count) SKIP")
  foreach ($f in $fail) { Say "      $f" }
  Check "$name has no FAIL and finished" ($done -and $fail.Count -eq 0) ''
}

# the failing package is built beside the newer one by tools\sandbox\make-test-packages.ps1
$new = Split-Path (Package 'newer')
$old = Split-Path (Package 'older')
# A file stable-ops-only.txt beside the packages names the scripts to run (one per line); without it all of them run.
$only = @(Get-Content 'C:\Host\packages\stable-ops-only.txt' -ErrorAction SilentlyContinue | Where-Object { $_ })
function Wanted([string]$name) { $only.Count -eq 0 -or $only -contains $name }
if (Wanted 'e2e-installer') { Run-Script 'e2e-installer' @('-Old', $old, '-New', $new) 30 }
if (Wanted 'e2e-installer-allusers') { Run-Script 'e2e-installer-allusers' @('-Msi', $older, '-NewerMsi', $newer) 30 }
if (Wanted 'e2e-user-ops') { Run-Script 'e2e-user-ops' @('-Cli', $cli) 15 }
if (Wanted 'e2e-admin-ops') { Run-Script 'e2e-admin-ops' @('-Cli', $cli) 30 }
if (Wanted 'e2e-power-plans') { Run-Script 'e2e-power-plans' @('-Cli', $cli, '-TryImport') 15 }
Say "== done $(Get-Date -Format s)"
