# Inside Windows Sandbox: undo of an app-settings restore through the window (Backups > App settings), on its own and fast. NO Discord is installed and no account is used:
# Discord's and Vencord's settings files are written by hand in the shape the real ones have (the real files, from a real Discord, are the subject of the discord-vencord scenario's part 5e).
# What it shows: Restore asks first (cancelling writes nothing), Restore writes the saved values, Undo puts the files back byte for byte, no temporary file is left.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== config-undo-ui $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
$work = 'C:\Host\out\cu'; New-Item -ItemType Directory -Force $work | Out-Null
$roaming = $env:APPDATA
function Mesura([string[]]$arguments, [int]$timeoutSec = 300) {
  $psi = New-Object Diagnostics.ProcessStartInfo $cli
  $psi.Arguments = (($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $p = [Diagnostics.Process]::Start($psi)
  $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($timeoutSec * 1000)) { try { Stop-Process -Id $p.Id -Force } catch { }; return [pscustomobject]@{ Exit = -1; Text = 'TIMEOUT' } }
  [pscustomobject]@{ Exit = $p.ExitCode; Text = (($o.Result + "`n" + $e.Result).Trim()) }
}
$vs = Join-Path $roaming 'Vencord\settings\settings.json'; $ds = Join-Path $roaming 'discord\settings.json'
New-Item -ItemType Directory -Force (Split-Path $vs), (Split-Path $ds) | Out-Null
'{ "autoUpdate": true, "autoUpdateNotification": true, "useQuickCss": true, "frameless": false, "plugins": { "ChatInputButtonAPI": { "enabled": true } } }' | Set-Content $vs -Encoding utf8
'{ "BACKGROUND_COLOR": "#121214", "IS_MAXIMIZED": false, "IS_MINIMIZED": false, "OPEN_ON_STARTUP": false, "MIN_WIDTH": 940, "MIN_HEIGHT": 500 }' | Set-Content $ds -Encoding utf8
Info 'files made by hand' "$vs ; $ds"

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

Say "== done $(Get-Date -Format s)"
