# Inside Windows Sandbox: the app's own update flow between two REAL builds (tools\e2e-update.ps1 -Staged), driven through the window with UI Automation:
# the older build (a\) is installed per user into a folder with spaces and Cyrillic, started against a local update server that serves the SIGNED manifest and the newer package (b\)
# staged on the host by tools\e2e-update-stage.ps1 (folders ok, tamper, fail beside the packages as staged\), and
#   happy      "View update", "Install and restart", the installer runs, the newer build is installed and registered, the data is kept;
#   tampered   a manifest edited after signing is refused and nothing is installed;
#   corrupt    a damaged download fails the SHA-256 check and nothing is installed;
#   offline    no network: the app says so and stays as it was;
#   rollback   a package whose install fails after copying: the older build comes back.
# The Sandbox desktop is elevated (UAC off); the update is per user, so no prompt is involved. Everything is thrown away with the Sandbox.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== update-flow $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$old = Split-Path (Package 'older'); $new = Split-Path (Package 'newer')
Remove-All
$report = 'C:\Host\out\e2e-update.txt'
Remove-Item $report -ErrorAction SilentlyContinue
$work = 'C:\Work\update-flow'
New-Item -ItemType Directory -Force $work | Out-Null
# e2e-update.ps1 finds the window's texts under <repo>\src\Mesura.App\Localization, and only tools\ is shared with the Sandbox: a working copy of tools\ with the two texts beside it
# (the packages folder carries them as strings\en.json and strings\ru.json).
$repoCopy = 'C:\Work\repo'
New-Item -ItemType Directory -Force "$repoCopy\src\Mesura.App\Localization" | Out-Null
Copy-Item 'C:\Host\tools' "$repoCopy\tools" -Recurse -Force
Copy-Item 'C:\Host\packages\strings\*.json' "$repoCopy\src\Mesura.App\Localization" -Force
Check 'the window texts were provided (en.json and ru.json)' ((Test-Path "$repoCopy\src\Mesura.App\Localization\en.json") -and (Test-Path "$repoCopy\src\Mesura.App\Localization\ru.json")) ''
# A file scenarios.txt beside the packages names the scenarios to run (comma separated); without it all five run.
$scenarios = @('happy', 'tampered', 'corrupt', 'offline', 'rollback')
$only = @(Get-Content 'C:\Host\packages\scenarios.txt' -ErrorAction SilentlyContinue | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($only.Count -gt 0) { $scenarios = $only }
# The script runs through an encoded command that writes the error AND its position when it dies (a bare "null-valued expression" says nothing about where).
$code = @"
try { & '$repoCopy\tools\e2e-update.ps1' -Old '$old' -New '$new' -Staged 'C:\Host\packages\staged' -Work '$work' -Report '$report' -Scenario $(($scenarios | ForEach-Object { "'$_'" }) -join ',') }
catch { (`$_ | Out-String), `$_.InvocationInfo.PositionMessage, `$_.ScriptStackTrace | Add-Content 'C:\Host\out\e2e-update-trace.txt' }
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
$p = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -PassThru -RedirectStandardOutput 'C:\Host\out\e2e-update-console.txt' -RedirectStandardError 'C:\Host\out\e2e-update-err.txt' -WindowStyle Hidden
$done = $p.WaitForExit(40 * 60000)
if (-not $done) { try { $p.Kill() } catch { } }
$lines = @(Get-Content $report -ErrorAction SilentlyContinue)
$fail = @($lines | Where-Object { $_ -match '^\s*FAIL' }); $pass = @($lines | Where-Object { $_ -match '^\s*PASS' }); $skip = @($lines | Where-Object { $_ -match '^\s*SKIP' })
Info 'e2e-update' ("finished: $done; $($pass.Count) PASS, $($fail.Count) FAIL, $($skip.Count) SKIP")
foreach ($f in $fail) { Say "      $f" }
Check 'e2e-update finished' $done ''
Check 'e2e-update has no FAIL' ($fail.Count -eq 0) ''
foreach ($s in $scenarios) { Check "scenario '$s' ran and passed (at least one PASS line carrying its name)" (@($pass | Where-Object { $_ -match "\[$s\]" }).Count -gt 0) '' }
Check 'the script did not die half way (no trace file)' (-not (Test-Path 'C:\Host\out\e2e-update-trace.txt')) ''
Say "== done $(Get-Date -Format s)"
