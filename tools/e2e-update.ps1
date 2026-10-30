# End-to-end check of installing and updating, without publishing anything (docs/testing-0.3.md). It installs an OLD build for the current user into a
# throwaway folder (spaces and Cyrillic in the path), starts it against a local update server and the NEW build, drives the window with UI Automation
# (View update, Install and restart, confirm), lets the updater and the Windows Installer run, and reads the result back independently: the registry, the
# files' hashes, the update result file, the running process. Failure scenarios do the same with a tampered manifest, a corrupted download, no network and
# an installer that fails (the previous version must come back).
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-update.ps1 -Old <folder of the old build> -New <folder of the new build> [-Scenario happy,tampered,corrupt,offline,rollback]
#
# Needs: both build folders from tools\publish.ps1 + tools\build-installer.ps1 (the new one also tools\make-release.ps1's key), a signing key, and, for
# `rollback`, a Mesura-<v>-x64-FAILTEST.msi in the new folder (tools\build-installer.ps1 -FailInstall). It uses an isolated data folder (MESURA_DATA_DIR) and the
# throwaway install folder; it backs up and restores your Start menu Mesura shortcut (the installer takes it over, as it should in real use), and uninstalls
# what it installed. It never touches %LOCALAPPDATA%\Programs\Mesura, per-machine locations or your real settings. No UAC prompt appears (per-user installs).
param(
  [Parameter(Mandatory = $true)][string]$Old,
  [Parameter(Mandatory = $true)][string]$New,
  [string[]]$Scenario = @('happy', 'tampered', 'corrupt', 'offline', 'rollback'),
  [string]$Work = '',
  [string]$Report = '',
  # Use the real update source (GitHub releases, the Preview channel) instead of the local test server. Only the happy path runs, and only after the owner has
  # published a Preview release of the build in -New (an external step: docs/updates.md); tools\verify-published.ps1 checks that publication first.
  [switch]$RealSource
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$Old = (Resolve-Path $Old).Path; $New = (Resolve-Path $New).Path
$oldInfo = Get-Content -Raw (Join-Path $Old 'build.json') | ConvertFrom-Json; $newInfo = Get-Content -Raw (Join-Path $New 'build.json') | ConvertFrom-Json
$oldMsi = Join-Path $Old "Mesura-$($oldInfo.version)-x64.msi"; $newMsi = Join-Path $New "Mesura-$($newInfo.version)-x64.msi"
$failMsi = Join-Path $New "test-only\Mesura-$($newInfo.version)-x64-FAILTEST.msi"
if (-not (Test-Path $failMsi)) { $failMsi = Join-Path $New "Mesura-$($newInfo.version)-x64-FAILTEST.msi" }   # builds before rc2 kept it beside the installers
$key = Join-Path $env:USERPROFILE '.mesura-release\update-signing.pk8'
if (-not $Work) { $Work = Join-Path ([IO.Path]::GetTempPath()) ('mesura-e2e-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$cyrillic = 'Программы'   # Cyrillic on purpose; this file is saved with a BOM so Windows PowerShell 5.1 reads it as UTF-8
$install = Join-Path $Work "$cyrillic Mesura"    # spaces and Cyrillic on purpose
$data = Join-Path $Work 'data'; $serve = Join-Path $Work 'server'
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mesura.lnk'
$results = New-Object System.Collections.Generic.List[string]; $failed = $false
function Check([string]$what, [bool]$ok, [string]$detail = '') { $script:failed = $script:failed -or -not $ok; $line = "{0,-5} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $detail; $results.Add($line); Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' }) }
function Msi([string]$verb, [string]$pkg, [string]$extra = '') { $p = Start-Process msiexec -ArgumentList "/$verb `"$pkg`" /qn $extra /l*v `"$Work\msi-$verb.log`"" -Wait -PassThru; $p.ExitCode }
function Reg-Version { try { (Get-ItemProperty 'HKCU:\Software\Mesura' -ErrorAction Stop).Version } catch { $null } }
# The Windows Installer products named Mesura that this user has registered (their product codes), read from the registry.
function Mesura-Products {
  $list = @()
  foreach ($k in Get-ChildItem 'HKCU:\Software\Microsoft\Installer\Products' -ErrorAction SilentlyContinue) {
    if ((Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).ProductName -eq 'Mesura') {
      $c = $k.PSChildName; $r = { param($x) -join $x[($x.Length - 1)..0] }
      $pairs = ($c.Substring(16) -split '(..)' | Where-Object { $_ }) | ForEach-Object { -join $_[1..0] }
      $list += "{0}-{1}-{2}-{3}-{4}" -f (& $r $c.Substring(0, 8)), (& $r $c.Substring(8, 4)), (& $r $c.Substring(12, 4)), ($pairs[0] + $pairs[1]), ($pairs[2..7] -join '')
    }
  }
  $list
}
function Hash([string]$f) { if (Test-Path -LiteralPath $f) { (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash } else { '' } }
function Wait-For([scriptblock]$cond, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }
function Prepare-Server([string]$msiToServe, [switch]$Tamper) {
  if (Test-Path $serve) { Remove-Item -Recurse -Force $serve }
  New-Item -ItemType Directory -Force $serve | Out-Null
  Copy-Item $msiToServe (Join-Path $serve "Mesura-$($newInfo.version)-x64.msi")
  Set-Content (Join-Path $Work 'notes-en.txt') "- End-to-end test notes.`n- Second line." -Encoding UTF8
  Copy-Item (Join-Path $New 'build.json') (Join-Path $serve 'build.json')
  dotnet run --project (Join-Path $repo 'tools\Mesura.ReleaseTool') -c Release -- manifest --channel preview --version $newInfo.version --msi (Join-Path $serve "Mesura-$($newInfo.version)-x64.msi") --url "http://127.0.0.1:8097/Mesura-$($newInfo.version)-x64.msi" --notes-en (Join-Path $Work 'notes-en.txt') --key $key --out $serve | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'signing the test manifest failed' }
  if ($Tamper) { $m = Join-Path $serve 'update-preview.json'; (Get-Content -Raw $m).Replace($newInfo.version, '9.9.9') | Set-Content $m -NoNewline }
}
function Start-Server([string]$mode = 'ok') {
  Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$repo\tools\update-test-server.ps1`" -Dir `"$serve`" -Port 8097 -Mode $mode" -WindowStyle Hidden | Out-Null
  Start-Sleep 2
}
function Stop-Server { Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'update-test-server' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }
function Start-App([bool]$online = $true) {
  if (Test-Path $data) { Remove-Item -Recurse -Force $data }
  New-Item -ItemType Directory -Force (Join-Path $data 'Setup') | Out-Null
  '{ "Language": "en", "UpdateChannel": "preview" }' | Set-Content (Join-Path $data 'Setup\app-settings.json')   # the window's language is fixed, so the controls can be found by name
  $env:MESURA_DATA_DIR = $data
  if ($RealSource) { Remove-Item Env:MESURA_UPDATE_BASE -ErrorAction SilentlyContinue } else { $env:MESURA_UPDATE_BASE = $(if ($online) { 'http://127.0.0.1:8097' } else { 'http://127.0.0.1:8098' }) }
  $p = Start-Process (Join-Path $install 'Mesura.exe') -ArgumentList '--no-elevate' -PassThru
  $d = (Get-Date).AddSeconds(40); while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  $p
}
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'src\Mesura.App\Localization\en.json') | ConvertFrom-Json
function Find-Button($scope, [string]$name) { @($scope.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) | Where-Object { $_.Current.ControlType -eq $CT::Button }) | Select-Object -First 1 }
function Goto-Settings($root) { $n = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $strings.'Nav.Settings'))) | Where-Object { $_.Current.ControlType -eq $CT::RadioButton })[0]; $n.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
function Texts($root) { (@($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Text)))) | ForEach-Object { $_.Current.Name }) -join ' | ' }
# The rollback scenario installs a package that is built to fail, so Windows Installer shows "There is a problem with this Windows Installer package. A program run
# as part of the setup did not finish as expected" (error 1722) in a message box. That box is the test working, not a fault of the product: this closes it (OK) so nobody
# has to, and so it is never mistaken for a real update failing.
function Dismiss-InstallerError {
  $desktop = $A::RootElement
  foreach ($w in $desktop.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Mesura')))) {
    if ($w.Current.ClassName -ne '#32770') { continue }
    $ok = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'OK')))
    $text = (@($w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Text)))) | ForEach-Object { $_.Current.Name }) -join ' '
    if ($ok -and $text -match 'Windows Installer package') { $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $script:dismissed = $true }
  }
}
function Result-File { $f = Join-Path $data 'updates\update-result.json'; if (Test-Path $f) { Get-Content -Raw $f | ConvertFrom-Json } }
function Fresh-Install { if (Reg-Version) { Msi 'x' $oldMsi | Out-Null }; Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force; if (Test-Path $install) { Remove-Item -Recurse -Force $install -ErrorAction SilentlyContinue }; $code = Msi 'i' $oldMsi "INSTALLFOLDER=`"$install`""; if ($code -ne 0) { throw "installing the old build failed ($code); see $Work\msi-i.log" } }

if ($RealSource) { $Scenario = @('happy') }
New-Item -ItemType Directory -Force $Work | Out-Null
$lnkBackup = Join-Path $Work 'Mesura.lnk.bak'; $hadLnk = Test-Path $lnk; if ($hadLnk) { Copy-Item $lnk $lnkBackup -Force }
$oldRunning = Get-Process Mesura -ErrorAction SilentlyContinue; if ($oldRunning) { throw 'Mesura is running; close it first.' }
if (Reg-Version) { throw 'Mesura is already installed for this user (HKCU\Software\Mesura); this check will not touch a real installation.' }
Write-Host "Old $($oldInfo.version) -> new $($newInfo.version); work folder $Work"
try {
  foreach ($name in $Scenario) {
    Write-Host "`n== $name" -ForegroundColor Cyan
    Stop-Server; Fresh-Install
    $installedHash = Hash (Join-Path $install 'Mesura.exe')
    Check "[$name] the old build installs per user into a folder with spaces and Cyrillic" ((Reg-Version) -eq $oldInfo.version -and (Test-Path (Join-Path $install 'cli\mesura.exe'))) "registry version $(Reg-Version)"
    switch ($name) {
      'happy' {
        if (-not $RealSource) { Prepare-Server $newMsi; Start-Server }
        # The migration case: the Start menu shortcut points at another copy of Mesura (here your own, restored from the backup); the installed copy must point it at itself.
        if ($hadLnk) { Copy-Item $lnkBackup $lnk -Force }
        $p = Start-App; $root = $A::FromHandle($p.MainWindowHandle)
        if ($hadLnk) {
          $repointed = Wait-For { [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($lnk)).Contains($cyrillic) } 30
          Check '[happy] the old shortcut was pointed at the installed copy (a Cyrillic folder; Unicode-safe)' ([bool]$repointed)
        }
        $card = Wait-For { (Texts $root) -match [regex]::Escape(($strings.'Update.Available' -replace '\{0\}', $newInfo.version)) } 40
        Check '[happy] the sidebar offers the new version after the start-up check' ([bool]$card)
        Goto-Settings $root
        $view = Wait-For { Find-Button $root $strings.'Update.ViewUpdate' } 10
        $view.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2
        $dlg = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' }) | Select-Object -First 1
        Check '[happy] the dialog shows the release notes' ($dlg -and ($RealSource -or ((Texts $dlg) -match 'End-to-end test notes')))
        $go = Find-Button $dlg $strings.'Update.InstallRestart'
        $go.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $confirm = Wait-For { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' -and (Find-Button $_ $strings.'Update.InstallRestart') -and (Texts $_) -match 'Restart Mesura' }) | Select-Object -First 1 } 60
        Check '[happy] after the verified download Mesura asks to restart' ([bool]$confirm)
        (Find-Button $confirm $strings.'Update.InstallRestart').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $closed = Wait-For { $p.Refresh(); $p.HasExited } 30
        Check '[happy] the app closes itself so the installer can replace it' ([bool]$closed)
        $done = Wait-For { (Result-File) } 240
        Check '[happy] the updater recorded a result' ([bool]$done) "$($done.status) $($done.reason)"
        Check '[happy] the result is success' ($done.status -eq 'succeeded')
        Check '[happy] the registry now names the new version (read independently)' ((Reg-Version) -eq $newInfo.version) "registry $(Reg-Version)"
        $payloadExe = Join-Path (Join-Path $New 'Mesura') 'Mesura.exe'
        Check '[happy] the installed Mesura.exe is the new build byte for byte' ((Hash (Join-Path $install 'Mesura.exe')) -eq (Hash $payloadExe) -and (Hash (Join-Path $install 'Mesura.exe')) -ne $installedHash)
        $again = Wait-For { Get-Process Mesura -ErrorAction SilentlyContinue | Select-Object -First 1 } 60
        Check '[happy] Mesura started again from the installed folder' ($again -and ($again.Path.EndsWith("$cyrillic Mesura\Mesura.exe",[StringComparison]::OrdinalIgnoreCase))) "$($again.Path)"
        if ($again) {
          $again.Refresh(); $d = (Get-Date).AddSeconds(30); while ($again.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $again.Refresh() }
          Start-Sleep 4; $root2 = $A::FromHandle($again.MainWindowHandle)
          $says = Wait-For { (Texts $root2) -match [regex]::Escape(($strings.'Update.Done' -replace '\{0\}', $newInfo.version)) } 20
          Check '[happy] the restarted app confirms "Updated to <version>"' ([bool]$says)
        }
        Check '[happy] the user data folder was left as it was (settings file still holds the language)' ((Get-Content -Raw (Join-Path $data 'Setup\app-settings.json')) -match '"Language"')
      }
      'tampered' {
        Prepare-Server $newMsi -Tamper; Start-Server
        $p = Start-App; $root = $A::FromHandle($p.MainWindowHandle); Start-Sleep 14
        $t = Texts $root
        Check '[tampered] a manifest changed after signing is NOT offered' (-not ($t -match [regex]::Escape($strings.'Update.AvailableHint')) -and -not ($t -match '9\.9\.9'))
        Check '[tampered] the installed version is untouched' ((Reg-Version) -eq $oldInfo.version -and (Hash (Join-Path $install 'Mesura.exe')) -eq $installedHash)
        Goto-Settings $root; (Find-Button $root $strings.'Update.CheckNow').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4
        Check '[tampered] Check now says the information failed verification' ((Texts $root) -match [regex]::Escape($strings.'Update.Refused'))
      }
      'corrupt' {
        Prepare-Server $newMsi; Start-Server 'corrupt-msi'
        $p = Start-App; $root = $A::FromHandle($p.MainWindowHandle)
        Wait-For { (Texts $root) -match [regex]::Escape(($strings.'Update.Available' -replace '\{0\}', $newInfo.version)) } 40 | Out-Null
        Goto-Settings $root; (Find-Button $root $strings.'Update.ViewUpdate').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2
        $dlg = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' }) | Select-Object -First 1
        (Find-Button $dlg $strings.'Update.InstallRestart').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $bad = Wait-For { (Texts $dlg) -match [regex]::Escape($strings.'Update.Corrupt') } 90
        Check '[corrupt] a damaged download is detected and reported' ([bool]$bad)
        Check '[corrupt] the damaged file was deleted, nothing was installed' (-not (Test-Path (Join-Path $data "updates\Mesura-$($newInfo.version)-x64.msi")) -and (Reg-Version) -eq $oldInfo.version)
        Check '[corrupt] the app is still running' (-not $p.HasExited)
      }
      'offline' {
        $p = Start-App -online:$false; $root = $A::FromHandle($p.MainWindowHandle); Start-Sleep 12
        Check '[offline] the app starts and keeps working with the update server unreachable' (-not $p.HasExited -and $p.MainWindowHandle -ne 0)
        Goto-Settings $root; (Find-Button $root $strings.'Update.CheckNow').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4
        Check '[offline] Check now says it could not reach the server' ((Texts $root) -match [regex]::Escape($strings.'Update.Offline'))
      }
      'rollback' {
        if (-not (Test-Path $failMsi)) { Write-Host 'no FAILTEST package: skipped'; $results.Add('SKIP  [rollback] no FAILTEST package in the new folder'); continue }
        Prepare-Server $failMsi; Start-Server
        $p = Start-App; $root = $A::FromHandle($p.MainWindowHandle)
        Wait-For { (Texts $root) -match [regex]::Escape(($strings.'Update.Available' -replace '\{0\}', $newInfo.version)) } 40 | Out-Null
        Goto-Settings $root; (Find-Button $root $strings.'Update.ViewUpdate').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2
        $dlg = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' }) | Select-Object -First 1
        (Find-Button $dlg $strings.'Update.InstallRestart').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $confirm = Wait-For { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' -and (Find-Button $_ $strings.'Update.InstallRestart') -and (Texts $_) -match 'Restart Mesura' }) | Select-Object -First 1 } 60
        (Find-Button $confirm $strings.'Update.InstallRestart').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $done = Wait-For { Dismiss-InstallerError; (Result-File) } 240
        Check '[rollback] the failed install is recorded as failed' ($done.status -eq 'failed') "$($done.status) $($done.reason) code $($done.exitCode)"
        Check '[rollback] the previous version is back: registry (read independently)' ((Reg-Version) -eq $oldInfo.version) "registry $(Reg-Version)"
        Check '[rollback] the previous Mesura.exe is back byte for byte' ((Hash (Join-Path $install 'Mesura.exe')) -eq $installedHash)
        $again = Wait-For { Get-Process Mesura -ErrorAction SilentlyContinue | Select-Object -First 1 } 60
        $products = @(Mesura-Products)
        Check '[rollback] exactly one Mesura product is registered with Windows Installer afterwards (no ghost of the failed package)' ($products.Count -eq 1) ($products -join ', ')
        Check '[rollback] Mesura is running again from the installed folder' ([bool]$again)
        if ($again) { Start-Sleep 5; $again.Refresh(); $root2 = $A::FromHandle($again.MainWindowHandle); Check '[rollback] the app says the update did not finish' ((Texts $root2) -match [regex]::Escape($strings.'Update.FailedTitle')) }
      }
    }
    Stop-Server; Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force
  }
}
finally {
  Stop-Server; Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-Item Env:MESURA_DATA_DIR, Env:MESURA_UPDATE_BASE -ErrorAction SilentlyContinue
  if (Reg-Version) {
    $latest = if ((Reg-Version) -eq $newInfo.version) { $newMsi } else { $oldMsi }
    $code = Msi 'x' $latest; $results.Add("      cleanup: uninstall exit $code; registry now '$(Reg-Version)'; install folder exists: $(Test-Path $install)")
    if ((Reg-Version)) { $code = Msi 'x' $(if ($latest -eq $newMsi) { $oldMsi } else { $newMsi }); $results.Add("      cleanup (second attempt): exit $code") }
  }
  if ($hadLnk) { Copy-Item $lnkBackup $lnk -Force } elseif (Test-Path $lnk) { Remove-Item $lnk -Force }
  Write-Host "`nStart menu shortcut restored: $(if ($hadLnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } else { 'none before, none now' })"
  if ($Report) { $results | Set-Content -Encoding UTF8 $Report }
}
Write-Host "`n$(@($results | Where-Object { $_ -like 'PASS*' }).Count) passed, $(@($results | Where-Object { $_ -like 'FAIL*' }).Count) FAILED. Work folder: $Work"
if ($failed) { exit 1 }
