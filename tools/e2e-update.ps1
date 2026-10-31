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
  [string[]]$Scenario = @('happy', 'tampered', 'corrupt', 'offline', 'rollback'),   # also: resume (the server cuts the download once), killed (the installer is killed midway)
  [string]$Work = '',
  [string]$Report = '',
  # Use the real update source (GitHub releases, the Preview channel) instead of the local test server. Only the happy path runs, and only after the owner has
  # published a Preview release of the build in -New (an external step: docs/updates.md); tools\verify-published.ps1 checks that publication first.
  [switch]$RealSource,
  # Run as ANOTHER Windows user (a throwaway local account) so a real installation of Mesura on this PC is never involved: that account has no access to the signing key, so
  # the signed update folders are made beforehand by the key holder with tools\e2e-update-stage.ps1 and handed over here (folders ok, tamper, fail).
  [string]$Staged = '',
  # Machine-wide install (Program Files, HKLM): the update then goes through the elevated stage and updater (docs/security.md). Run as a throwaway local ADMINISTRATOR
  # (docs/testing-lifecycle.md): elevation then needs no password, and the first user's per-user installation is still never seen. The window itself stays unelevated.
  [switch]$AllUsers,
  # Writes everything the script says to this file as well (a run as another user has no console to read afterwards).
  [string]$Transcript = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if ($Transcript) { Start-Transcript -Path $Transcript | Out-Null }
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' })   # -File passes 'a,b' as one string
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$Old = (Resolve-Path $Old).Path; $New = (Resolve-Path $New).Path
$oldInfo = Get-Content -Raw (Join-Path $Old 'build.json') | ConvertFrom-Json; $newInfo = Get-Content -Raw (Join-Path $New 'build.json') | ConvertFrom-Json
$oldMsi = Join-Path $Old "Mesura-$($oldInfo.version)-x64.msi"; $newMsi = Join-Path $New "Mesura-$($newInfo.version)-x64.msi"
$failMsi = Join-Path $New "test-only\Mesura-$($newInfo.version)-x64-FAILTEST.msi"
if (-not (Test-Path $failMsi)) { $failMsi = Join-Path $New "Mesura-$($newInfo.version)-x64-FAILTEST.msi" }   # builds before rc2 kept it beside the installers
$key = Join-Path $env:USERPROFILE '.mesura-release\update-signing.pk8'
if ($Staged) { $Staged = (Resolve-Path $Staged).Path }
if (-not $Work) { $Work = Join-Path ([IO.Path]::GetTempPath()) ('mesura-e2e-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$cyrillic = 'Программы'   # Cyrillic on purpose; this file is saved with a BOM so Windows PowerShell 5.1 reads it as UTF-8
$install = if ($AllUsers) { Join-Path $env:ProgramFiles "$cyrillic Mesura E2E" } else { Join-Path $Work "$cyrillic Mesura" }    # spaces and Cyrillic on purpose
$regKey = if ($AllUsers) { 'HKLM:\Software\Mesura' } else { 'HKCU:\Software\Mesura' }
$productsKey = if ($AllUsers) { 'HKLM:\SOFTWARE\Classes\Installer\Products' } else { 'HKCU:\Software\Microsoft\Installer\Products' }
$elev = @{}; if ($AllUsers) { $elev.Verb = 'RunAs' }
$scopeArgs = if ($AllUsers) { 'ALLUSERS=1' } else { '' }
$payloadExe = Join-Path (Join-Path $New 'Mesura') 'Mesura.exe'
$data = Join-Path $Work 'data'; $serve = Join-Path $Work 'server'
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mesura.lnk'
$results = New-Object System.Collections.Generic.List[string]; $failed = $false; $startedAt = Get-Date
function Check([string]$what, [bool]$ok, [string]$detail = '') { $what = $what.Replace('[happy]', "[$script:scenarioName]"); $script:failed = $script:failed -or -not $ok; $line = "{0,-5} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $detail; $results.Add($line); Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' }) }
function Msi([string]$verb, [string]$pkg, [string]$extra = '') { $p = Start-Process msiexec -ArgumentList "/$verb `"$pkg`" /qn $extra /l*v `"$Work\msi-$verb.log`"" -Wait -PassThru @elev; $p.ExitCode }
function Reg-Version { try { (Get-ItemProperty $script:regKey -ErrorAction Stop).Version } catch { $null } }
# The Windows Installer products named Mesura that this user has registered (their product codes), read from the registry.
function Mesura-Products {
  $list = @()
  foreach ($k in Get-ChildItem $script:productsKey -ErrorAction SilentlyContinue) {
    if ((Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).ProductName -eq 'Mesura') {
      $c = $k.PSChildName; $r = { param($x) -join $x[($x.Length - 1)..0] }
      $pairs = ($c.Substring(16) -split '(..)' | Where-Object { $_ }) | ForEach-Object { -join $_[1..0] }
      $list += "{0}-{1}-{2}-{3}-{4}" -f (& $r $c.Substring(0, 8)), (& $r $c.Substring(8, 4)), (& $r $c.Substring(12, 4)), ($pairs[0] + $pairs[1]), ($pairs[2..7] -join '')
    }
  }
  $list
}
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Tok {
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int c, out int info, int len, out int ret);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  // 1 elevated, 0 not elevated, -1 the token could not be opened (a process of higher integrity than the caller's: elevated)
  public static int Elevated(int pid) {
    IntPtr p = OpenProcess(0x1000, false, pid); if (p == IntPtr.Zero) return -1;
    try { IntPtr t; if (!OpenProcessToken(p, 8, out t)) return -1; try { int e, r; return GetTokenInformation(t, 20, out e, 4, out r) ? e : -1; } finally { CloseHandle(t); } } finally { CloseHandle(p); }
  }
}
"@
# Mesura processes that run from the install folder under test (never anyone else's Mesura), by the process table so elevated ones are listed too.
function Test-Mesura {
  # An elevated process shows no path to this (unelevated) script: it is counted when it started after this script did and is the only kind left (the machine-wide install).
  @(Get-CimInstance Win32_Process -Filter "Name='Mesura.exe'" -ErrorAction SilentlyContinue | Where-Object {
      ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($install, [StringComparison]::OrdinalIgnoreCase)) -or ($AllUsers -and -not $_.ExecutablePath -and $_.CreationDate -gt $script:startedAt)
    })
}
# Runs a script block in an elevated PowerShell and waits (the update's installer, started by the elevated updater, owns windows and processes this script cannot touch).
function Invoke-Elevated([string]$code) {
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
  Start-Process powershell -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded -Verb RunAs -Wait -WindowStyle Hidden
}
function Kill-Pid([int]$procId) { try { Stop-Process -Id $procId -Force -ErrorAction Stop } catch { if ($AllUsers) { Start-Process taskkill -ArgumentList "/F /T /PID $procId" -Verb RunAs -Wait -WindowStyle Hidden } } }
function Kill-Mesura { foreach ($m in Test-Mesura) { Kill-Pid $m.ProcessId }; Start-Sleep -Milliseconds 500 }
function Remove-Install { if (Test-Path $install) { if ($AllUsers) { Start-Process cmd -ArgumentList "/c rmdir /s /q `"$install`"" -Verb RunAs -Wait -WindowStyle Hidden } else { Remove-Item -Recurse -Force $install -ErrorAction SilentlyContinue } } }
function Hash([string]$f) { if (Test-Path -LiteralPath $f) { (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash } else { '' } }
function Wait-For([scriptblock]$cond, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }
function Prepare-Server([string]$msiToServe, [switch]$Tamper) {
  if (Test-Path $serve) { Remove-Item -Recurse -Force $serve }
  New-Item -ItemType Directory -Force $serve | Out-Null
  if ($Staged) {
    $kind = if ($Tamper) { 'tamper' } elseif ($msiToServe -eq $failMsi) { 'fail' } else { 'ok' }
    Copy-Item (Join-Path $Staged "$kind\*") $serve -Recurse -Force
    return
  }
  Copy-Item $msiToServe (Join-Path $serve "Mesura-$($newInfo.version)-x64.msi")
  Set-Content (Join-Path $Work 'notes-en.txt') "- End-to-end test notes.`n- Second line." -Encoding UTF8
  Copy-Item (Join-Path $New 'build.json') (Join-Path $serve 'build.json')
  dotnet run --project (Join-Path $repo 'tools\Mesura.ReleaseTool') -c Release -- manifest --channel preview --version $newInfo.version --msi (Join-Path $serve "Mesura-$($newInfo.version)-x64.msi") --url "http://127.0.0.1:8097/Mesura-$($newInfo.version)-x64.msi" --notes-en (Join-Path $Work 'notes-en.txt') --key $key --out $serve | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'signing the test manifest failed' }
  if ($Tamper) { $m = Join-Path $serve 'update-preview.json'; (Get-Content -Raw $m).Replace($newInfo.version, '9.9.9') | Set-Content $m -NoNewline }
}
function Start-Server([string]$mode = 'ok') {
  $script:serverLog = Join-Path $Work ("server-" + [Guid]::NewGuid().ToString('N').Substring(0, 6) + ".log")
  Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$repo\tools\update-test-server.ps1`" -Dir `"$serve`" -Port 8097 -Mode $mode" -WindowStyle Hidden -RedirectStandardOutput $script:serverLog | Out-Null
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
# Since 0.4 Settings sits in the dock's "More" menu, a popup whose entries are not in the window's tree until it is open: open it, then pick Settings from the desktop's tree.
function Goto-Settings($root) {
  $more = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $strings.'Nav.More'))))[0]
  $more.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 700
  $n = $null; $end = (Get-Date).AddSeconds(8)
  while (-not $n -and (Get-Date) -lt $end) { $n = @($A::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $strings.'Nav.Settings'))) | Where-Object { $_.Current.ControlType -eq $CT::RadioButton })[0]; if (-not $n) { Start-Sleep -Milliseconds 300 } }
  $n.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800
}
function Texts($root) { (@($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Text)))) | ForEach-Object { $_.Current.Name }) -join ' | ' }
# The rollback scenario installs a package that is built to fail, so Windows Installer shows "There is a problem with this Windows Installer package. A program run
# as part of the setup did not finish as expected" (error 1722) in a message box. That box is the test working, not a fault of the product: this closes it (OK) so nobody
# has to, and so it is never mistaken for a real update failing.
function Dismiss-InstallerError {
  if ($AllUsers) {
    # The box belongs to the elevated installer: an elevated helper presses OK (at most once every few seconds, an elevated start takes a moment).
    if (((Get-Date) - $script:lastElevatedDismiss).TotalSeconds -lt 4) { return }
    $script:lastElevatedDismiss = Get-Date
    Invoke-Elevated @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
foreach ($w in $A::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Mesura')))) {
  if ($w.Current.ClassName -ne '#32770') { continue }
  $text = (@($w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) | ForEach-Object { $_.Current.Name }) -join ' '
  $ok = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'OK')))
  if ($ok -and $text -match 'Windows Installer package') { $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
}
'@
    $script:dismissed = $true
    return
  }
  $desktop = $A::RootElement
  foreach ($w in $desktop.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Mesura')))) {
    if ($w.Current.ClassName -ne '#32770') { continue }
    $ok = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'OK')))
    $text = (@($w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Text)))) | ForEach-Object { $_.Current.Name }) -join ' '
    if ($ok -and $text -match 'Windows Installer package') { $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $script:dismissed = $true }
  }
}
$script:lastElevatedDismiss = [datetime]::MinValue
function Result-File { $f = Join-Path $data 'updates\update-result.json'; if (Test-Path $f) { Get-Content -Raw $f | ConvertFrom-Json } }
# The installed product is removed with the package of ITS version (the old package cannot remove a newer product, which is what the previous scenario leaves behind).
function Fresh-Install { if (Reg-Version) { Msi 'x' $(if ((Reg-Version) -eq $newInfo.version) { $newMsi } else { $oldMsi }) | Out-Null }; Kill-Mesura; Remove-Install; $code = Msi 'i' $oldMsi "INSTALLFOLDER=`"$install`" $scopeArgs"; if ($code -ne 0) { throw "installing the old build failed ($code); see $Work\msi-i.log" } }

if ($RealSource) { $Scenario = @('happy') }
New-Item -ItemType Directory -Force $Work | Out-Null
$lnkBackup = Join-Path $Work 'Mesura.lnk.bak'; $hadLnk = Test-Path $lnk; if ($hadLnk) { Copy-Item $lnk $lnkBackup -Force }
$oldRunning = Get-Process Mesura -ErrorAction SilentlyContinue; if ($oldRunning) { throw 'Mesura is running; close it first.' }
if (Reg-Version) { throw "Mesura is already installed ($regKey); this check will not touch a real installation." }
Write-Host "Old $($oldInfo.version) -> new $($newInfo.version); work folder $Work"
try {
  foreach ($name in $Scenario) {
    $script:scenarioName = $name
    Write-Host "`n== $name" -ForegroundColor Cyan
    Stop-Server; Fresh-Install
    $installedHash = Hash (Join-Path $install 'Mesura.exe')
    Check "[$name] the old build installs $(if ($AllUsers) { 'for all users into Program Files' } else { 'per user' }) into a folder with spaces and Cyrillic" ((Reg-Version) -eq $oldInfo.version -and (Test-Path (Join-Path $install 'cli\mesura.exe'))) "registry version $(Reg-Version)"
    switch ($name) {
      { $_ -in 'happy', 'resume' } {
        if (-not $RealSource) { Prepare-Server $newMsi; Start-Server $(if ($name -eq 'resume') { 'drop-once' } else { 'ok' }) }
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
        Check '[happy] the installed Mesura.exe is the new build byte for byte' ((Hash (Join-Path $install 'Mesura.exe')) -eq (Hash $payloadExe) -and (Hash (Join-Path $install 'Mesura.exe')) -ne $installedHash)
        $running = Wait-For { Test-Mesura | Select-Object -First 1 } 60
        Check '[happy] Mesura started again from the installed folder' ([bool]$running) "$($running.ExecutablePath)"
        if ($running) { $level = [Tok]::Elevated([int]$running.ProcessId); $results.Add("      INFO [$name] the restarted Mesura is $(if ($level -eq 0) { 'NOT elevated' } elseif ($level -eq 1) { 'ELEVATED' } else { 'of higher integrity than this script (elevated)' })") }
        $again = if ($running) { Get-Process -Id $running.ProcessId -ErrorAction SilentlyContinue } else { $null }
        if ($AllUsers -and $running) { Check "[$name] the restarted Mesura is the signed-in user's, not elevated (the updater was)" ($level -eq 0) "token level $level" }
        if ($again -and $level -eq 0 -and -not $AllUsers) {
          $again.Refresh(); $d = (Get-Date).AddSeconds(30); while ($again.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $again.Refresh() }
          Start-Sleep 4; $root2 = $A::FromHandle($again.MainWindowHandle)
          $says = Wait-For { (Texts $root2) -match [regex]::Escape(($strings.'Update.Done' -replace '\{0\}', $newInfo.version)) } 20
          Check '[happy] the restarted app confirms "Updated to <version>"' ([bool]$says)
        }
        Check '[happy] the user data folder was left as it was (settings file still holds the language)' ((Get-Content -Raw (Join-Path $data 'Setup\app-settings.json')) -match '"Language"')
        if ($name -eq 'resume') {
          $log = Get-Content -Raw $script:serverLog
          Check '[resume] the server cut the download a third of the way in' ($log -match 'dropped Mesura-\S+\.msi after a third')
          Check '[resume] the app asked for the rest with a Range request and got it (206)' ($log -match '206 Mesura-\S+\.msi')
          Check '[resume] the package was not fetched whole a second time' (-not ($log -match '(?s)dropped.*200 Mesura-\S+\.msi'))
        }
      }
      'killed' {
        # The installer's client process (msiexec started by the updater) is killed a few seconds into the install. What may happen is up to Windows Installer; what must not
        # is a half-installed Mesura, a second ghost product, an app that does not come back, or a recorded result that contradicts what is installed.
        $marker = Join-Path $Work 'killed.txt'
        if ($AllUsers) {
          # The installer of a machine-wide update is elevated: its command line is not readable from here, and an elevated start takes longer than the install, so a watcher is started first.
          $watch = "`$m = '$marker'; `$end = (Get-Date).AddSeconds(300); while ((Get-Date) -lt `$end) { `$v = Get-CimInstance Win32_Process -Filter ""Name='msiexec.exe'"" | Where-Object { `$_.CommandLine -match '/qb!' } | Select-Object -First 1; if (`$v) { Start-Sleep -Milliseconds 700; Stop-Process -Id `$v.ProcessId -Force; Set-Content `$m `$v.CommandLine; break }; Start-Sleep -Milliseconds 50 }"
          Start-Process powershell -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($watch))) -Verb RunAs -WindowStyle Hidden
        }
        Prepare-Server $newMsi; Start-Server
        $p = Start-App; $root = $A::FromHandle($p.MainWindowHandle)
        Wait-For { (Texts $root) -match [regex]::Escape(($strings.'Update.Available' -replace '\{0\}', $newInfo.version)) } 40 | Out-Null
        Goto-Settings $root; (Find-Button $root $strings.'Update.ViewUpdate').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2
        $dlg = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' }) | Select-Object -First 1
        (Find-Button $dlg $strings.'Update.InstallRestart').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $confirm = Wait-For { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window))) | Where-Object { $_.Current.ClassName -ne 'Popup' -and (Find-Button $_ $strings.'Update.InstallRestart') -and (Texts $_) -match 'Restart Mesura' }) | Select-Object -First 1 } 90
        (Find-Button $confirm $strings.'Update.InstallRestart').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $victim = $null; $until = (Get-Date).AddSeconds($(if ($AllUsers) { 0 } else { 180 })); while (-not $victim -and (Get-Date) -lt $until) { $victim = Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | Where-Object { $_.CommandLine -match '/qb!' } | Select-Object -First 1; if (-not $victim) { Start-Sleep -Milliseconds 50 } }
        if ($AllUsers) { $victim = Wait-For { if (Test-Path $marker) { [pscustomobject]@{ ProcessId = 0; CommandLine = (Get-Content $marker -Raw) } } } 240 }
        Check '[killed] the updater started the installer' ([bool]$victim)
        if ($victim -and $victim.ProcessId -gt 0) { Start-Sleep -Milliseconds 700; Kill-Pid $victim.ProcessId; $results.Add("      INFO [killed] killed msiexec $($victim.ProcessId): $($victim.CommandLine)") }
        $done = Wait-For { Result-File } 240
        Start-Sleep 45   # the Windows Installer service may still be finishing or rolling back
        $reg = Reg-Version; $now = Hash (Join-Path $install 'Mesura.exe')
        $isOld = ($reg -eq $oldInfo.version -and $now -eq $installedHash); $isNew = ($reg -eq $newInfo.version -and $now -eq (Hash $payloadExe))
        Check '[killed] what is installed is a whole old Mesura or a whole new one, never a mix' ($isOld -or $isNew) "registry $reg; Mesura.exe is $(if ($isOld) { 'the old build' } elseif ($isNew) { 'the new build' } else { 'neither' })"
        $products = @(Mesura-Products); Check '[killed] exactly one Mesura product is registered with Windows Installer' ($products.Count -eq 1) ($products -join ', ')
        Check '[killed] the updater recorded a result' ([bool]$done) "$($done.status) $($done.reason) code $($done.exitCode)"
        Check '[killed] the recorded result agrees with what is installed' ($done -and (($done.status -eq 'succeeded' -and $isNew) -or ($done.status -ne 'succeeded' -and $isOld))) "$($done.status) / installed $reg"
        $back = Wait-For { Test-Mesura | Select-Object -First 1 } 60
        Check '[killed] Mesura runs again from the installed folder' ([bool]$back)
        if ($back) { Start-Sleep 5; $level = [Tok]::Elevated([int]$back.ProcessId); if ($level -eq 0) { $again = Get-Process -Id $back.ProcessId; $again.Refresh(); $root2 = $A::FromHandle($again.MainWindowHandle); $results.Add("      INFO [killed] the restarted app says: " + ((Texts $root2) -split ' \| ' | Where-Object { $_ -match 'update|Updat' } | Select-Object -First 3) -join ' / ') } }
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
        $again = Wait-For { Test-Mesura | Select-Object -First 1 } 60
        $products = @(Mesura-Products)
        Check '[rollback] exactly one Mesura product is registered with Windows Installer afterwards (no ghost of the failed package)' ($products.Count -eq 1) ($products -join ', ')
        Check '[rollback] Mesura is running again from the installed folder' ([bool]$again)
        if ($AllUsers -and $again) { Check "[rollback] the restarted Mesura is the signed-in user's, not elevated" ([Tok]::Elevated([int]$again.ProcessId) -eq 0) }
        if ($again -and -not $AllUsers -and [Tok]::Elevated([int]$again.ProcessId) -eq 0) { Start-Sleep 5; $again = Get-Process -Id $again.ProcessId; $again.Refresh(); $root2 = $A::FromHandle($again.MainWindowHandle); Check '[rollback] the app says the update did not finish' ((Texts $root2) -match [regex]::Escape($strings.'Update.FailedTitle')) }
      }
    }
    Stop-Server; Kill-Mesura
  }
}
finally {
  Stop-Server; Kill-Mesura
  if ($AllUsers) { Invoke-Elevated "Get-CimInstance Win32_Process | Where-Object { `$_.Name -eq 'msiexec.exe' -and `$_.CommandLine -match '/qb!' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue }; Get-CimInstance Win32_Process | Where-Object { `$_.ExecutablePath -like 'C:\ProgramData\MesuraUpdates\*' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue }"; Start-Sleep 10 }
  Remove-Item Env:MESURA_DATA_DIR, Env:MESURA_UPDATE_BASE -ErrorAction SilentlyContinue
  if (Reg-Version) {
    $latest = if ((Reg-Version) -eq $newInfo.version) { $newMsi } else { $oldMsi }
    $code = Msi 'x' $latest; $results.Add("      cleanup: uninstall exit $code; registry now '$(Reg-Version)'; install folder exists: $(Test-Path $install)")
    if ((Reg-Version)) { $code = Msi 'x' $(if ($latest -eq $newMsi) { $oldMsi } else { $newMsi }); $results.Add("      cleanup (second attempt): exit $code") }
  }
  Remove-Install
  if ($hadLnk) { Copy-Item $lnkBackup $lnk -Force } elseif (Test-Path $lnk) { Remove-Item $lnk -Force }
  Write-Host "`nStart menu shortcut restored: $(if ($hadLnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } else { 'none before, none now' })"
  if ($Report) { $results | Set-Content -Encoding UTF8 $Report }
}
Write-Host "`n$(@($results | Where-Object { $_ -like 'PASS*' }).Count) passed, $(@($results | Where-Object { $_ -like 'FAIL*' }).Count) FAILED. Work folder: $Work"
if ($failed) { exit 1 }
