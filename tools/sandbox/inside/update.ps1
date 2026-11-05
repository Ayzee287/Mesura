# Inside Windows Sandbox: the update from one real build to the next with REAL packages (0.5.5 -> 0.6.0), and what the user keeps.
# The packages folder holds a\ (the older build: build.json, Mesura-<v>-x64.msi, -ru.msi) and b\ (the newer build, plus test-only\Mesura-<v>-x64-FAILTEST.msi), packages.json naming the two MSIs.
#
#   1. tools\e2e-installer.ps1 with -Old a -New b: fresh install, reinstall, uninstall, upgrade of the older build in place, a FAILED update (the older build must be back byte for byte and still
#      uninstallable), a failed first install, the language packages.
#   2. User data: the older build is installed per user into its default folder, makes data (a recorded Windows change, an app-settings.json), the newer package is installed OVER it, and then
#      - the registration, the files and the shortcut are the newer build's, exactly one product is registered, the older product is gone;
#      - every file under %LOCALAPPDATA%\Mesura is byte for byte what it was before the update;
#      - the NEWER command line lists the older build's history run and reverts it (the registry value comes back, read independently);
#      - the newer build's own read-only commands run on this PC (hardware, plan for both levels);
#      - uninstalling the newer build leaves the user's data alone; going back to the older package is refused with the installer's message.
#   Not shown: the window itself (an unattended script cannot open it), the app's own "update available" prompt (needs a published manifest), a real PC's data.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== update $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$old = Split-Path (Package 'older'); $new = Split-Path (Package 'newer')
$oldInfo = Get-Content -Raw (Join-Path $old 'build.json') | ConvertFrom-Json
$newInfo = Get-Content -Raw (Join-Path $new 'build.json') | ConvertFrom-Json
Info 'older build' "$($oldInfo.version) commit $($oldInfo.commit)"
Info 'newer build' "$($newInfo.version) commit $($newInfo.commit)"
Remove-All

# ---- 1. the installer lifecycle between the two real builds ----
# A file skip-installer-lifecycle.txt beside the packages skips this part (a rerun of part 2 alone).
if (-not (Test-Path 'C:\Host\packages\skip-installer-lifecycle.txt')) {
$installerReport = 'C:\Host\out\e2e-installer.txt'   # not $report: variable names are case-insensitive and common.ps1 keeps its report path in $script:Report
Remove-Item $installerReport -ErrorAction SilentlyContinue
$p = Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\Host\tools\e2e-installer.ps1', '-Old', $old, '-New', $new, '-Report', $installerReport) -PassThru `
  -RedirectStandardOutput 'C:\Host\out\e2e-installer-console.txt' -RedirectStandardError 'C:\Host\out\e2e-installer-err.txt' -WindowStyle Hidden
$done = $p.WaitForExit(40 * 60000)
if (-not $done) { try { $p.Kill() } catch { } }
$lines = @(Get-Content $installerReport -ErrorAction SilentlyContinue)
$fail = @($lines | Where-Object { $_ -match '^\s*FAIL' }); $pass = @($lines | Where-Object { $_ -match '^\s*PASS' }); $skip = @($lines | Where-Object { $_ -match '^\s*SKIP' })
Info 'e2e-installer (older -> newer)' "finished: $done; $($pass.Count) PASS, $($fail.Count) FAIL, $($skip.Count) SKIP"
foreach ($f in $fail) { Say "      $f" }
foreach ($s in $skip) { Say "      $s" }
Check 'the installer lifecycle between the two builds has no FAIL and finished' ($done -and $fail.Count -eq 0 -and $pass.Count -gt 0)
} else { Info 'installer lifecycle' 'skipped on request (skip-installer-lifecycle.txt)' }
Remove-All

# ---- 2. what the user keeps ----
function Reg { try { Get-ItemProperty 'HKCU:\Software\Mesura' -ErrorAction Stop } catch { $null } }
function Hashes([string]$root) {
  $map = [ordered]@{}
  foreach ($f in @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName)) { $map[$f.FullName.Substring($root.Length)] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }
  $map
}
function Run-Cli([string]$cli, [string[]]$arguments) {
  $ErrorActionPreference = 'Continue'
  $errFile = Join-Path $env:TEMP 'mesura-update-stderr.txt'
  $text = (& $cli @arguments 2>$errFile | ForEach-Object { "$_" }) -join "`n"
  $code = $LASTEXITCODE
  $err = if (Test-Path $errFile) { Get-Content -Raw $errFile } else { '' }
  [pscustomobject]@{ Text = $text; Err = $err; Exit = $code; Run = $(if (("$text`n$err") -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }) }
}
$user = @('ALLUSERS=2', 'MSIINSTALLPERUSER=1')
$oldMsi = Package 'older'; $newMsi = Package 'newer'
$cliOld = Join-Path $pu 'cli\mesura.exe'
$audio = 'HKCU:\Software\Microsoft\Multimedia\Audio'
function Ducking { (Get-ItemProperty -LiteralPath $audio -ErrorAction SilentlyContinue).UserDuckingPreference }

$code = Msi (@('/i', "`"$oldMsi`"") + $user)
Check "[older] the older build installs per user into its default folder" ($code -eq 0 -and (Reg).Version -eq $oldInfo.version -and (Test-Path $cliOld)) "exit $code, $((Reg).Version)"
if (Test-Path $cliOld) {
  if (-not (Test-Path $audio)) { New-Item -Path $audio -Force | Out-Null }
  Set-ItemProperty -LiteralPath $audio -Name UserDuckingPreference -Value 1 -Type DWord
  $apply = Run-Cli $cliOld @('windows', 'apply', '--id', 'audio.no-ducking', '--apply')
  Check '[older] the older command line applies a per-user operation and records a run' ([bool]$apply.Run -and (Ducking) -eq 3) "run $($apply.Run), value $(Ducking), exit $($apply.Exit)"
  $settings = Join-Path $data 'Setup\app-settings.json'
  New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
  [IO.File]::WriteAllText($settings, '{ "Language": "ru", "UpdateChannel": "preview", "CheckForUpdates": false, "SkippedUpdate": "0.5.4", "UpdateHighestSequence": 7, "ConfigureAfterInstall": "defaults" }', (New-Object Text.UTF8Encoding($false)))
  $before = Hashes $data
  Info 'user data before the update' "$($before.Count) files under $data"
  Check '[older] there is data to keep (the run and the preferences)' ($before.Count -ge 2)

  $code = Msi (@('/i', "`"$newMsi`"") + $user)
  $products = @(Products)
  Check '[update] the newer package installs over the older one' ($code -eq 0 -and (Reg).Version -eq $newInfo.version) "exit $code, $((Reg).Version)"
  Check '[update] exactly one Mesura product is registered, and it is the newer one' ($products.Count -eq 1 -and $products[0].Version -match [regex]::Escape(($newInfo.version -replace '-.*$', ''))) "$($products | ForEach-Object { $_.Version })"
  $cliNew = Join-Path $pu 'cli\mesura.exe'
  Check '[update] the installed program is the newer build (file hash equals the build record)' ((Test-Path (Join-Path $pu 'Mesura.exe')) -and (Get-FileHash (Join-Path $pu 'Mesura.exe') -Algorithm SHA256).Hash -eq $newInfo.sha256.app -and (Get-FileHash $cliNew -Algorithm SHA256).Hash -eq $newInfo.sha256.cli) ''
  Check '[update] the Start menu shortcut is still there' (Test-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mesura.lnk'))
  $after = Hashes $data
  $changed = @($before.Keys | Where-Object { -not $after.Contains($_) -or $after[$_] -ne $before[$_] })
  Check '[data] every file under the data folder is byte for byte what it was before the update' ($changed.Count -eq 0) "changed or missing: $($changed -join ', ')"

  $list = Run-Cli $cliNew @('history', 'list')
  Check '[data] the newer command line lists the run the older build recorded' ($apply.Run -and $list.Text -like "*$($apply.Run)*") "exit $($list.Exit)"
  $revert = Run-Cli $cliNew @('windows', 'revert', '--run', $apply.Run, '--apply')
  Check '[data] the newer command line reverts that run: the value is back (read from the registry)' ((Ducking) -eq 1) "value $(Ducking), exit $($revert.Exit)"
  $hw = Run-Cli $cliNew @('hardware')
  Check '[newer] mesura hardware runs' ($hw.Exit -eq 0 -and $hw.Text.Length -gt 40) "exit $($hw.Exit)"
  foreach ($level in 'recommended-gaming', 'maximum-performance') {
    $plan = Run-Cli $cliNew @('windows', 'plan', '--profile', $level, '--json')
    $json = try { $plan.Text | ConvertFrom-Json } catch { $null }
    Check "[newer] windows plan --profile $level answers with valid JSON" ($plan.Exit -eq 0 -and $null -ne $json) "exit $($plan.Exit)"
  }

  $code = Msi @('/x', "`"$newMsi`"")
  $afterRemove = Hashes $data
  $lost = @($before.Keys | Where-Object { $_ -notlike '*\Setup\history*' -and -not $afterRemove.Contains($_) })
  Check '[uninstall] uninstalling the newer build removes the program and keeps the data' ($code -eq 0 -and -not (Reg) -and -not (Test-Path (Join-Path $pu 'Mesura.exe')) -and $lost.Count -eq 0) "exit $code, lost: $($lost -join ', ')"
  Check '[uninstall] the preferences file is untouched' ((Test-Path $settings) -and (Get-FileHash $settings -Algorithm SHA256).Hash -eq $before['\Setup\app-settings.json'])

  # Going back: the newer build installed, then the older package. The installer must refuse and leave the newer build in place.
  [void](Msi (@('/i', "`"$newMsi`"") + $user))
  $code = Msi (@('/i', "`"$oldMsi`"") + $user)
  Check '[downgrade] installing the older package over the newer one is refused and leaves the newer build' ($code -ne 0 -and (Reg).Version -eq $newInfo.version) "exit $code, $((Reg).Version)"
}
Remove-All
Remove-ItemProperty -LiteralPath $audio -Name UserDuckingPreference -ErrorAction SilentlyContinue
Say "== update done $(Get-Date -Format s)"
