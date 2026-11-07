# Inside Windows Sandbox: the Discord / Vencord life cycle through Mesura's own commands, on a clean Windows with a network. NO account is used: Discord is never signed in.
# The sandbox has no winget, so the first step brings it (App Installer and its two dependencies from Microsoft); if that fails the scenario says so and stops: nothing is faked.
#   1. winget present                      2. states before (Discord, Vencord, Autoruns)
#   3. install Discord (mesura apps install --apply): the exit code, the files, whether the state says Installed, whether Discord is running by itself
#   4. Vencord before Discord has ever been started (the Squirrel stub state), then its install: result, files, what mesura apps list says
#   5. Discord's and Vencord's configuration files: do they exist before the first start; export (backup) of what is there; import into a missing file; import after the first start
#   6. the second start of Mesura (apps list again), repeated install (must not claim a new install), Autoruns install / repeat / state
# The report states what each step really did; a step that could not run is INFO, never PASS.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== discord-vencord $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
$work = 'C:\Host\out\dv'; New-Item -ItemType Directory -Force $work | Out-Null
$localData = $env:LOCALAPPDATA; $roaming = $env:APPDATA
$discordRoot = Join-Path $localData 'Discord'

function Mesura([string[]]$arguments, [int]$timeoutSec = 900) {
  $psi = New-Object Diagnostics.ProcessStartInfo $cli
  $psi.Arguments = (($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')   # Windows PowerShell 5.1: no ArgumentList
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  $p = [Diagnostics.Process]::Start($psi)
  $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($timeoutSec * 1000)) { try { Stop-Process -Id $p.Id -Force } catch { }; return [pscustomobject]@{ Exit = -1; Text = 'TIMEOUT'; Seconds = $timeoutSec } }
  [pscustomobject]@{ Exit = $p.ExitCode; Text = (($o.Result + "`n" + $e.Result).Trim()); Seconds = 0 }
}
function Discord-Folders { @(Get-ChildItem $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue | Sort-Object Name) }
function Discord-Procs { @(Get-Process Discord, Update -ErrorAction SilentlyContinue) }
function StateOf([string]$id) { $t = (Mesura @('apps', 'list')).Text; if ($t -match "\[\s*([a-z -]+?)\s*\]\s+$id\s") { $Matches[1].Trim() } else { 'unreadable' } }

# ---- 1. winget -------------------------------------------------------------------------------------------------------------------------------------------
$wingetExe = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
function Have-Winget { [bool](Get-Command winget -ErrorAction SilentlyContinue) -or (Test-Path $wingetExe) }
if (-not (Have-Winget)) {
  Info 'winget is not in this sandbox: installing App Installer with its dependencies from Microsoft'
  $dl = Join-Path $work 'wg'; New-Item -ItemType Directory -Force $dl | Out-Null
  try {
    $ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    # The release of winget-cli ships the exact dependencies of its own App Installer (VCLibs, UI.Xaml, Windows App Runtime): taking them from there keeps the versions matched.
    Invoke-WebRequest 'https://github.com/microsoft/winget-cli/releases/latest/download/DesktopAppInstaller_Dependencies.zip' -OutFile "$dl\deps.zip" -UseBasicParsing
    Expand-Archive "$dl\deps.zip" "$dl\deps" -Force
    Invoke-WebRequest 'https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile "$dl\winget.msixbundle" -UseBasicParsing
    foreach ($f in (Get-ChildItem "$dl\deps" -Recurse -Filter '*.appx' | Where-Object { $_.FullName -match '\\x64\\' })) { try { Add-AppxPackage $f.FullName } catch { Info "dependency $($f.Name) not added" $_.Exception.Message.Split("`n")[0] } }
    Add-AppxPackage "$dl\winget.msixbundle"
    Start-Sleep 5
  } catch { Info 'bringing winget failed' $_.Exception.Message }
}
$env:PATH += ';' + (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps')
$wv = if (Have-Winget) { (& winget --version 2>&1 | Out-String).Trim() } else { '' }
Check '[1] winget runs in the sandbox' ($wv -match '^v\d') $wv
if ($wv -notmatch '^v\d') { Info 'Without winget nothing about install can be tested here: stopping (a plan for a clean Windows is in docs/discord-vencord-test-plan.md)'; Say "== done $(Get-Date -Format s)"; return }
$null = & winget source update 2>&1

# ---- 2. before --------------------------------------------------------------------------------------------------------------------------------------------
$list0 = Mesura @('apps', 'list')
$list0.Text | Out-File "$work\apps-list-0.txt" -Encoding utf8
Check '[2] mesura apps list runs and reads Discord as not installed on a clean Windows' ((StateOf 'discord') -eq '-') (StateOf 'discord')
Check '[2] Vencord reads as not installed (no Discord to patch)' ((StateOf 'vencord') -eq '-') (StateOf 'vencord')
Info "mesura apps list first lines: $((($list0.Text -split "`n") | Select-Object -First 4) -join ' | ')"

# ---- 3. install Discord ---------------------------------------------------------------------------------------------------------------------------------
$i = Mesura @('apps', 'install', '--id', 'discord', '--apply') 1500
$i.Text | Out-File "$work\install-discord.txt" -Encoding utf8
Info "install discord: exit $($i.Exit)" (($i.Text -split "`n" | Select-Object -Last 6) -join ' | ')
Start-Sleep 20
$folders = Discord-Folders
Info "Discord folder after install: Update.exe $(Test-Path (Join-Path $discordRoot 'Update.exe')); app-* folders: $($folders.Name -join ',')"
Info "Discord processes right after the install: $((Discord-Procs | ForEach-Object { $_.ProcessName }) -join ',')"
$stateD = StateOf 'discord'
Check '[3] after a successful install exit code the state reads Installed (not just the exit code)' ($i.Exit -eq 0 -and $stateD -eq 'installed') "exit $($i.Exit), state '$stateD'"
if ($i.Exit -eq 0 -and $stateD -ne 'installed') { Say 'FAIL  [3] Mesura reported success but the state is not Installed: a false success' }
if ($i.Exit -ne 0) { Info 'Discord did not install in this sandbox: the rest of the life cycle cannot be shown here' }

# ---- 4. Vencord before the first start of Discord ---------------------------------------------------------------------------------------------------
$vBefore = StateOf 'vencord'
Info "Vencord state with Discord installed but never started: '$vBefore'; app-* folders present: $($folders.Count)"
if ($folders.Count -eq 0) { Info 'Discord is the stub (no app-* folder yet): the detector reads Vencord as not installed, which is true' }
$v = Mesura @('apps', 'install', '--id', 'vencord', '--apply') 900
$v.Text | Out-File "$work\install-vencord.txt" -Encoding utf8
Info "install vencord (Discord never started): exit $($v.Exit)" (($v.Text -split "`n" | Select-Object -Last 6) -join ' | ')
$vAfter = StateOf 'vencord'
Check '[4] Mesura does not say Vencord is Installed unless Discord really carries the patch' (($v.Exit -eq 0) -eq ($vAfter -eq 'installed')) "exit $($v.Exit), state '$vAfter'"
$res = if (Discord-Folders) { Join-Path (Discord-Folders | Select-Object -Last 1).FullName 'resources' } else { '' }
if ($res) { Info "resources: $((Get-ChildItem $res -ErrorAction SilentlyContinue | ForEach-Object Name) -join ',')" }

# ---- 5. first start of Discord, no account -----------------------------------------------------------------------------------------------------------
$upd = Join-Path $discordRoot 'Update.exe'
if (Test-Path $upd) {
  Start-Process $upd -ArgumentList '--processStart', 'Discord.exe' | Out-Null
  $deadline = (Get-Date).AddMinutes(4)
  while ((Get-Date) -lt $deadline -and -not ((Discord-Folders).Count -gt 0 -and (Get-Process Discord -ErrorAction SilentlyContinue))) { Start-Sleep 5 }
  Start-Sleep 45   # Discord creates %APPDATA%\discord and downloads its modules at the first start; it stays signed out
  Info "after the first start: app-* $((Discord-Folders).Name -join ','), roaming discord folder $(Test-Path (Join-Path $roaming 'discord')), settings.json $(Test-Path (Join-Path $roaming 'discord\settings.json'))"
  Check '[5] the first start created the app-* folder' ((Discord-Folders).Count -gt 0)
  $vFirst = StateOf 'vencord'
  Info "Vencord state after the first start (installed before the app-* folder existed): '$vFirst'"
  # The window is open and signed out: that is the state "Discord running" for the config tests
  $running = [bool](Get-Process Discord -ErrorAction SilentlyContinue)
  $imp = Mesura @('config', 'list')
  Info "config list: $((($imp.Text -split "`n") | Select-Object -First 12) -join ' | ')"
  $exp = Mesura @('config', 'export', '--out', "$work\bundle-running.json")
  Info "config export while Discord runs: exit $($exp.Exit)" (($exp.Text -split "`n" | Select-Object -Last 4) -join ' | ')
  if (Test-Path "$work\bundle-running.json") { $b = Get-Content -Raw "$work\bundle-running.json"; Info "bundle apps: $(([regex]::Matches($b, '"appId":\s*"([a-z]+)"') | ForEach-Object { $_.Groups[1].Value }) -join ',')" }
  # import while it runs must be skipped, not claimed
  if (Test-Path "$work\bundle-running.json") {
    $ir = Mesura @('config', 'import', '--in', "$work\bundle-running.json", '--apply')
    Check '[5] an import while Discord runs does not claim to have written a Discord file' (-not ($ir.Text -match 'Discord.*applied') -or $ir.Text -match 'running|close') (($ir.Text -split "`n" | Select-Object -First 8) -join ' | ')
  }
  Get-Process Discord -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 5
  Check '[5] Discord is closed for the next steps' (-not (Get-Process Discord -ErrorAction SilentlyContinue))
  $exp2 = Mesura @('config', 'export', '--out', "$work\bundle-closed.json")
  Info "config export with Discord closed: exit $($exp2.Exit)" (($exp2.Text -split "`n" | Select-Object -Last 4) -join ' | ')
  if (Test-Path "$work\bundle-closed.json") { Info "bundle size $((Get-Item "$work\bundle-closed.json").Length) bytes" }
  # a restore on a missing settings file: remove Discord's settings.json, import must report the file as missing, not create it
  $sj = Join-Path $roaming 'discord\settings.json'
  if (Test-Path $sj) {
    $orig = Get-Content -Raw $sj; Move-Item $sj "$work\settings.json.moved" -Force
    if (Test-Path "$work\bundle-closed.json") {
      $im = Mesura @('config', 'import', '--in', "$work\bundle-closed.json", '--apply')
      Check '[5] importing when Discord''s settings file is missing does not create or invent it' (-not (Test-Path $sj)) (($im.Text -split "`n" | Select-Object -First 6) -join ' | ')
    }
    Move-Item "$work\settings.json.moved" $sj -Force
  } else { Info 'Discord has no settings.json after the first start without sign-in (the file is made later): nothing to remove' }
  $again = Mesura @('config', 'import', '--in', "$work\bundle-closed.json", '--apply'); $again2 = Mesura @('config', 'import', '--in', "$work\bundle-closed.json", '--apply')
  Check '[5] a repeated import changes nothing the second time' ($again2.Text -match 'unchanged|nothing|already' -or $again2.Exit -eq 0) (($again2.Text -split "`n" | Select-Object -First 4) -join ' | ')
}

# ---- 5b. a real restore: change values on disk, import the bundle, read the files (the actual result, not the status) -----------------------------------------
$vs = Join-Path $roaming 'Vencord\settings\settings.json'; $ds = Join-Path $roaming 'discord\settings.json'
if ((Test-Path $vs) -and (Test-Path $ds) -and (Test-Path "$work\bundle-closed.json") -and -not (Get-Process Discord -ErrorAction SilentlyContinue)) {
  $v0 = Get-Content -Raw $vs | ConvertFrom-Json; $d0 = Get-Content -Raw $ds | ConvertFrom-Json
  $wantAuto = [bool]$v0.autoUpdate; $wantColor = "$($d0.BACKGROUND_COLOR)"   # keys the real files carry and the bundle holds (autoUpdate, BACKGROUND_COLOR)
  Info "before: Vencord autoUpdate=$wantAuto, Discord BACKGROUND_COLOR=$wantColor"
  # damage what the bundle carries, and add a key of our own that the import must not touch
  $v0 | Add-Member -NotePropertyName zzKeepMe -NotePropertyValue 42 -Force; $v0.autoUpdate = -not $wantAuto
  $d0 | Add-Member -NotePropertyName zzKeepMe -NotePropertyValue 42 -Force; $d0.BACKGROUND_COLOR = '#000000'
  ($v0 | ConvertTo-Json -Depth 50) | Set-Content $vs -Encoding utf8; ($d0 | ConvertTo-Json -Depth 50) | Set-Content $ds -Encoding utf8
  $rt = Mesura @('config', 'import', '--in', "$work\bundle-closed.json", '--apply')
  $rt.Text | Out-File "$work\roundtrip-import.txt" -Encoding utf8
  $v2 = Get-Content -Raw $vs | ConvertFrom-Json; $d2 = Get-Content -Raw $ds | ConvertFrom-Json
  Check '[5b] the Vencord value that was changed is back as it was in the bundle (read from the file)' ([bool]$v2.autoUpdate -eq $wantAuto) "file says $($v2.autoUpdate), bundle had $wantAuto"
  Check '[5b] the Discord value that was changed is back (read from the file)' ("$($d2.BACKGROUND_COLOR)" -eq $wantColor) "file says $($d2.BACKGROUND_COLOR), bundle had $wantColor"
  Check '[5b] a key the bundle does not carry was left alone in both files' ($v2.zzKeepMe -eq 42 -and $d2.zzKeepMe -eq 42)
  $backups = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Mesura\Setup\history') -Recurse -Filter '*' -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'backups' })
  Check '[5b] the files the import replaced were backed up first' ($backups.Count -ge 2) "$($backups.Count) backup file(s)"
  if ($backups.Count -ge 1) { $b0 = $backups | Select-Object -First 1; Info "backup sample: $($b0.Name), $($b0.Length) bytes" }
  Check '[5b] no temporary file is left beside the settings' (@(Get-ChildItem (Split-Path $vs), (Split-Path $ds) -Filter '*.mesura-tmp' -ErrorAction SilentlyContinue).Count -eq 0)
  $rt2 = Mesura @('config', 'import', '--in', "$work\bundle-closed.json", '--apply')
  Check '[5b] a second import after the restore writes nothing' (([regex]::Matches($rt2.Text, 'nothing to write')).Count -ge 2) (($rt2.Text -split "`n" | Select-Object -First 4) -join ' | ')
  # an unreadable (damaged) file must be reported, never overwritten
  'this is not json {' | Set-Content $ds -Encoding ascii
  $bad = Mesura @('config', 'import', '--in', "$work\bundle-closed.json", '--apply')
  Check '[5b] a damaged Discord settings file is reported and left exactly as it was' ((Get-Content -Raw $ds).Trim() -eq 'this is not json {') (($bad.Text -split "`n" | Select-Object -First 5) -join ' | ')
  $d2 | ConvertTo-Json -Depth 50 | Set-Content $ds -Encoding utf8
}

# ---- 5c. a partly installed Discord: the app-* folder is gone (an interrupted update) ---------------------------------------------------------------------
$f = Discord-Folders | Select-Object -Last 1
if ($f) {
  $moved = "$($f.FullName).away"; Move-Item $f.FullName $moved -Force
  $sv = StateOf 'vencord'; $sd = StateOf 'discord'
  Check '[5c] with no app-* folder Vencord reads as not installed (there is nothing patched)' ($sv -eq '-') "vencord '$sv'"
  Info "with no app-* folder Discord reads '$sd' (winget's own record of the install)"
  Move-Item $moved $f.FullName -Force
  Check '[5c] put back: Vencord reads installed again' ((StateOf 'vencord') -eq 'installed')
}

# ---- 5d. a Discord update: Discord delivers a newer app-* folder with its own archive and no Vencord loader (simulated by building exactly that from the real, patched folder) -----
# Not Discord's real updater (that needs its servers and a newer build than the one just installed): the folder an update leaves behind, made from the real one. What is checked is
# Mesura's reading and its re-patch, on the real files.
$f5d = Discord-Folders | Select-Object -Last 1
if ($f5d -and (StateOf 'vencord') -eq 'installed' -and -not (Get-Process Discord -ErrorAction SilentlyContinue)) {
  $ver = [version]($f5d.Name.Substring(4)); $newVer = [version]::new($ver.Major, $ver.Minor, $ver.Build + 1)
  $newDir = Join-Path $discordRoot "app-$newVer"
  Copy-Item $f5d.FullName $newDir -Recurse
  $res5 = Join-Path $newDir 'resources'
  Remove-Item (Join-Path $res5 'app.asar') -Recurse -Force
  Move-Item (Join-Path $res5 '_app.asar') (Join-Path $res5 'app.asar')
  Info "simulated update: $($f5d.Name) -> app-$newVer (original archive back in place, no loader)"
  Check '[5d] after a Discord update (a newer folder without the patch) Vencord reads as not installed' ((StateOf 'vencord') -eq '-') (StateOf 'vencord')
  Check '[5d] Discord itself still reads installed' ((StateOf 'discord') -eq 'installed') (StateOf 'discord')
  $re = Mesura @('apps', 'install', '--id', 'vencord', '--apply') 900
  $re.Text | Out-File "$work\reinstall-vencord.txt" -Encoding utf8
  Info "re-install vencord: exit $($re.Exit)" (($re.Text -split "`n" | Select-Object -Last 6) -join ' | ')
  Check '[5d] installing Vencord again patches the new folder (loader in place, original archive kept)' ((StateOf 'vencord') -eq 'installed' -and (Test-Path (Join-Path $res5 '_app.asar'))) "exit $($re.Exit)"
  $re2 = Mesura @('apps', 'install', '--id', 'vencord', '--apply') 900
  Check '[5d] a second install says it is already there' ($re2.Text -match 'already|nothing|skip') (($re2.Text -split "`n" | Select-Object -Last 3) -join ' | ')
  # the old folder is what Discord's updater would remove
  Remove-Item $f5d.FullName -Recurse -Force -ErrorAction SilentlyContinue
  Check '[5d] with only the newer folder left, Vencord still reads installed' ((StateOf 'vencord') -eq 'installed')
} else { Info '[5d] skipped: Vencord was not installed on a real Discord here, so there is nothing to update' }

# ---- 5e. undo of an app-settings restore through the window (Backups > App settings): cancel, restore, undo; the files read byte for byte ---------------------------------------
# The real window, driven with UI Automation, on the real Discord and Vencord settings files. Restore and Undo are clicked in the sandbox only.
$appExe = Join-Path (Split-Path (Split-Path $cli)) 'Mesura.exe'
if ((Test-Path $appExe) -and (Test-Path $vs) -and (Test-Path $ds) -and -not (Get-Process Discord -ErrorAction SilentlyContinue)) {
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
  $A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
  $S0v = Get-Content -Raw $vs; $S0d = Get-Content -Raw $ds
  $ex = Mesura @('config', 'export', '--out', "$work\bundle-ui.json")
  Check '[5e] a bundle of the current state was exported for the window test' (Test-Path "$work\bundle-ui.json") "exit $($ex.Exit)"
  # state S1: change what the bundle carries
  $v1 = $S0v | ConvertFrom-Json; $d1 = $S0d | ConvertFrom-Json
  $v1.autoUpdate = -not [bool]$v1.autoUpdate; $d1.BACKGROUND_COLOR = '#102030'
  ($v1 | ConvertTo-Json -Depth 50) | Set-Content $vs -Encoding utf8; ($d1 | ConvertTo-Json -Depth 50) | Set-Content $ds -Encoding utf8
  $H1v = (Get-FileHash $vs).Hash; $H1d = (Get-FileHash $ds).Hash
  Remove-Item (Join-Path $env:LOCALAPPDATA 'Mesura\Setup\app-settings.json') -ErrorAction SilentlyContinue
  $proc = Start-Process $appExe -ArgumentList '--no-elevate' -PassThru
  $dl = (Get-Date).AddSeconds(60); while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $dl) { Start-Sleep -Milliseconds 400; $proc.Refresh() }
  Start-Sleep -Seconds 8
  $mainHandle = $proc.MainWindowHandle; $root = $A::FromHandle($mainHandle)
  function UiNamed($scope, [string]$name, $type = $null) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    @($scope.FindAll($TS::Descendants, $c)) | Where-Object { -not $type -or $_.Current.ControlType -eq $type } | Select-Object -First 1
  }
  function UiAll($scope, [string]$name, $type) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    @($scope.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.ControlType -eq $type }
  }
  function UiInvoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  function UiDialog {
    # The confirmation dialog is a window owned by the main one: UI Automation lists it below the main window, not on the desktop (the same was seen with the file dialog).
    $win = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window)
    for ($t = 0; $t -lt 40; $t++) {
      $w = @($root.FindAll($TS::Descendants, $win)) | Select-Object -First 1
      if (-not $w) { $w = @($A::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)))) | Where-Object { $_.Current.NativeWindowHandle -ne 0 -and $_.Current.NativeWindowHandle -ne $mainHandle.ToInt32() } | Select-Object -First 1 }
      if ($w) { return $w }
      Start-Sleep -Milliseconds 300
    }
    $null
  }
  function UiAnswer([bool]$confirm) {
    $d = UiDialog
    if (-not $d) { return 'no dialog' }
    $buttons = @($d.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button))))
    $names = ($buttons | ForEach-Object { $_.Current.Name }) -join ' / '
    $pick = if ($confirm) { $buttons | Where-Object { $_.Current.Name -notmatch '^(Cancel|Close)$' } | Select-Object -First 1 } else { $buttons | Where-Object { $_.Current.Name -match '^Cancel$' } | Select-Object -First 1 }
    if (-not $pick) { return "no button ($names)" }
    UiInvoke $pick; Start-Sleep -Seconds 4
    "buttons: $names"
  }
  function UiTexts { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Text)))) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'restor|Undo|undone|put back|back|changed|saved|wasn' } | Select-Object -Unique }
  try {
    # More > Backups > App settings
    $more = @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'More')))) | Where-Object { $_.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty) } | Select-Object -First 1
    $more.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 800
    $nav = @($A::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Backups')))) | Where-Object { $_.Current.ControlType -eq $CT::RadioButton } | Select-Object -First 1
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 2
    (UiNamed $root 'App settings' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 3
    # the saved settings file: Choose file... and the file dialog (typed, as a person would)
    UiInvoke (UiNamed $root ('Choose file' + [char]0x2026) $CT::Button); Start-Sleep -Seconds 4
    [System.Windows.Forms.SendKeys]::SendWait("$work\bundle-ui.json".Replace('{', '{{}').Replace('}', '{}}').Replace('+', '{+}').Replace('^', '{^}').Replace('%', '{%}').Replace('~', '{~}')); Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep -Seconds 10
    $restore = @(UiAll $root 'Restore saved settings' $CT::Button)
    Check '[5e] the window offers "Restore saved settings" on the cards whose files differ from the saved ones' ($restore.Count -ge 2) "$($restore.Count) button(s)"

    # (1) Restore, then CANCEL in the confirmation: nothing is written
    if ($restore.Count -ge 1) {
      UiInvoke $restore[0]; $r1 = UiAnswer $false
      Check '[5e] cancelling the confirmation of a restore writes nothing (both files byte for byte)' (((Get-FileHash $vs).Hash -eq $H1v) -and ((Get-FileHash $ds).Hash -eq $H1d)) $r1
    }
    # (2) Restore every card that offers it, confirm
    $restore = @(UiAll $root 'Restore saved settings' $CT::Button)
    $answers = @()
    foreach ($b in $restore) { UiInvoke $b; $answers += (UiAnswer $true); Start-Sleep -Seconds 3 }
    Info "[5e] restore confirmations: $($answers -join ' | ')"
    $v2 = Get-Content -Raw $vs | ConvertFrom-Json; $d2 = Get-Content -Raw $ds | ConvertFrom-Json
    $S0vj = $S0v | ConvertFrom-Json; $S0dj = $S0d | ConvertFrom-Json
    Check '[5e] after the restore the Vencord value is the saved one (read from the file)' ([bool]$v2.autoUpdate -eq [bool]$S0vj.autoUpdate) "file says $($v2.autoUpdate)"
    Check '[5e] after the restore the Discord value is the saved one (read from the file)' ("$($d2.BACKGROUND_COLOR)" -eq "$($S0dj.BACKGROUND_COLOR)") "file says $($d2.BACKGROUND_COLOR)"
    Info "[5e] messages after the restore: $((UiTexts) -join ' | ')"
    # (3) Undo last change, confirm: the files are what they were before the restore, byte for byte
    $undo = @(UiAll $root 'Undo last change' $CT::Button)
    Check '[5e] the window offers "Undo last change" after a restore' ($undo.Count -ge 1) "$($undo.Count) button(s)"
    $uanswers = @()
    foreach ($b in $undo) { UiInvoke $b; $uanswers += (UiAnswer $true); Start-Sleep -Seconds 3 }
    Info "[5e] undo confirmations: $($uanswers -join ' | ')"
    Check '[5e] after Undo both files are byte for byte what they were before the restore' (((Get-FileHash $vs).Hash -eq $H1v) -and ((Get-FileHash $ds).Hash -eq $H1d)) "vencord $((Get-FileHash $vs).Hash -eq $H1v), discord $((Get-FileHash $ds).Hash -eq $H1d)"
    Info "[5e] messages after the undo: $((UiTexts) -join ' | ')"
    Check '[5e] no temporary file is left beside the settings after restore and undo' (@(Get-ChildItem (Split-Path $vs), (Split-Path $ds) -Filter '*.mesura-tmp' -ErrorAction SilentlyContinue).Count -eq 0)
  } catch { Say "FAIL  [5e] the window test stopped: $($_.Exception.Message)" }
  finally { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } }
  # put the files back as they were for the steps that follow
  Set-Content $vs $S0v -Encoding utf8 -NoNewline; Set-Content $ds $S0d -Encoding utf8 -NoNewline
} else { Info '[5e] skipped: the window test needs the app beside the CLI, both settings files and Discord closed' }

# ---- 6. second start of Mesura, repeat, Autoruns -----------------------------------------------------------------------------------------------------
$s2 = StateOf 'discord'; Check '[6] a new Mesura process still reads Discord as installed' ($s2 -eq 'installed') $s2
$r = Mesura @('apps', 'install', '--id', 'discord', '--apply') 900
Check '[6] installing Discord again does not say it installed it again' ($r.Text -match 'already|nothing|skip' -or $r.Exit -eq 0 -and $r.Text -notmatch 'installed Discord') (($r.Text -split "`n" | Select-Object -Last 4) -join ' | ')
$a0 = StateOf 'autoruns'
$a = Mesura @('apps', 'install', '--id', 'autoruns', '--apply') 900
$a.Text | Out-File "$work\install-autoruns.txt" -Encoding utf8
$a1 = StateOf 'autoruns'
Info "autoruns: before '$a0', install exit $($a.Exit), after '$a1'"
Check '[6] Autoruns: success is claimed only when the state reads installed' (($a.Exit -eq 0) -eq ($a1 -eq 'installed')) "exit $($a.Exit), state '$a1'"
$aru = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Recurse -Filter 'autoruns*.exe' -ErrorAction SilentlyContinue | Select-Object -First 2
Info "autoruns files: $(($aru | ForEach-Object FullName) -join '; ')"
$a2 = Mesura @('apps', 'install', '--id', 'autoruns', '--apply') 900
Check '[6] Autoruns: installing again reports it already there' ($a2.Text -match 'already|nothing|skip' -or $a2.Exit -eq 0) (($a2.Text -split "`n" | Select-Object -Last 3) -join ' | ')
# AnyDesk (0.7): the same checks as Autoruns; it is an installer (exe), so its files and its state are read after the install, and a repeat must be "already installed"
$ad0 = StateOf 'anydesk'
$ad = Mesura @('apps', 'install', '--id', 'anydesk', '--apply') 900
$ad.Text | Out-File "$work\install-anydesk.txt" -Encoding utf8
$ad1 = StateOf 'anydesk'
Info "anydesk: before '$ad0', install exit $($ad.Exit), after '$ad1'" (($ad.Text -split "`n" | Select-Object -Last 4) -join ' | ')
Check '[6] AnyDesk: success is claimed only when the state reads installed' (($ad.Exit -eq 0) -eq ($ad1 -eq 'installed')) "exit $($ad.Exit), state '$ad1'"
$adFiles = @(Get-ChildItem 'C:\Program Files (x86)\AnyDesk', 'C:\Program Files\AnyDesk', (Join-Path $env:APPDATA 'AnyDesk') -Filter 'AnyDesk*.exe' -ErrorAction SilentlyContinue | Select-Object -First 2)
Info "anydesk files: $(($adFiles | ForEach-Object FullName) -join '; ')"
if ($ad1 -eq 'installed') { Check '[6] AnyDesk: an installed state comes with a program on disk' ($adFiles.Count -gt 0) }
$ad2 = Mesura @('apps', 'install', '--id', 'anydesk', '--apply') 900
Check '[6] AnyDesk: installing again reports it already there' ($ad2.Text -match 'already|nothing|skip' -or $ad2.Exit -eq 0) (($ad2.Text -split "`n" | Select-Object -Last 3) -join ' | ')
Say "== done $(Get-Date -Format s)"
