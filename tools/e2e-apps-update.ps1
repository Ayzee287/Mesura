# End-to-end check of updating an app from the Apps page, with the real winget and the real window. It needs an app the PC does not
# have, so nothing of the owner's is touched: it installs an OLDER Notepad++ (8.9.7, MSI, machine-wide), starts Mesura with its own
# data folder, waits for the page to offer "Update Notepad++ to <version>", presses it, confirms, and checks
#   - the button appears only for that app (not for apps that update themselves),
#   - the confirmation lists the app with both versions,
#   - the result banner says "Updated 1 app",
#   - winget AND the file on disk say the new version,
#   - the update button is gone afterwards,
#   - the run is in the history as apps-update with a "Succeeded" outcome.
# Then it removes Notepad++ again. It refuses to run if Notepad++ is already installed (that would be the owner's) or Mesura is open.
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-apps-update.ps1 -Exe <Mesura.exe> [-Lang en|ru]
#
# Installing and removing the MSI needs administrator rights; winget asks for them itself (a UAC prompt unless the PC's policy allows
# silent elevation). The updated app is closed by its installer if it is open; the script does not start it.
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [ValidateSet('en', 'ru')][string]$Lang = 'en',
  [string]$OldVersion = '8.9.7',
  [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class MW2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$id = 'Notepad++.Notepad++'
$results = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') { $results.Add(('{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' }))); Write-Host $results[-1] }
function Installed-Version { $o = winget list --id $id --exact --disable-interactivity 2>$null | Out-String; if ($o -match [regex]::Escape($id) + '\s+(\S+)') { $Matches[1] } else { $null } }
function Remove-App { $p = Start-Process winget -ArgumentList 'uninstall', '--id', $id, '--exact', '--silent', '--disable-interactivity' -Verb RunAs -Wait -PassThru -WindowStyle Hidden; $p.ExitCode }

if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
if (Installed-Version) { throw 'Notepad++ is already installed on this PC; this check only works on a PC without it (it would remove it at the end).' }
if ($Out) { New-Item -ItemType Directory -Force -Path $Out | Out-Null }

$data = Join-Path $env:TEMP ('mesura-appsupd-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $data | Out-Null
$proc = $null
try {
  winget install --id $id --exact --version $OldVersion --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
  Check "older Notepad++ $OldVersion installed" ((Installed-Version) -eq $OldVersion) (Installed-Version)

  $env:MESURA_DATA_DIR = $data; $env:MESURA_PREVIEW_LANGUAGE = $Lang
  $proc = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
  Remove-Item Env:MESURA_DATA_DIR, Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
  $d = (Get-Date).AddSeconds(40); while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $proc.Refresh() }
  $h = $proc.MainWindowHandle
  Start-Sleep -Seconds 5
  $root = $A::FromHandle($h)
  function Shot([string]$name) {
    if (-not $Out) { return }
    $r = New-Object MW2+RECT; [MW2]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [MW2]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  }
  function Find([System.Windows.Automation.AutomationElement]$scope, [string]$name, $type = $null) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    @($scope.FindAll($TS::Descendants, $c)) | Where-Object { -not $type -or $_.Current.ControlType -eq $type } | Select-Object -First 1
  }
  function Wait-For([scriptblock]$what, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $what; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; $null }

  $nav = Find $root $strings.'Nav.Apps' $CT::RadioButton
  $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

  $button = Wait-For { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)))) | Where-Object { $_.Current.Name -like (($strings.'Apps.UpdateAutomationTo' -replace '\{0\}', 'Notepad++' -replace '\{1\}', '*')) } | Select-Object -First 1 } 120
  Check 'the page offers an update for Notepad++' ($null -ne $button) $(if ($button) { $button.Current.Name })
  Shot '1-update-offered'
  # Every per-app update button (its name carries the app and the new version); the owner's own apps may have updates too, so the check is
  # that only apps Mesura updates ever get one, never the self-updating ones.
  $perApp = @(@($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)))) | Where-Object { $_.Current.Name -like ($strings.'Apps.UpdateAutomationTo' -replace '\{0\}', '*' -replace '\{1\}', '*') })
  $names = ($perApp | ForEach-Object { $_.Current.Name }) -join ' | '
  $selfUpdating = @($perApp | Where-Object { $_.Current.Name -match 'Brave|Chrome|Discord|Spotify|Telegram|AyuGram|Steam|Medal|Vencord' })
  Check 'no update button for an app that updates itself' ($selfUpdating.Count -eq 0) $names
  $all = Find $root ([string]::Format($strings.'Apps.UpdateAll', $perApp.Count)) $CT::Button
  Check "'Update all' in the dock counts the $($perApp.Count) buttons" ($null -ne $all)

  if ($button) {
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    # The confirmation is a window owned by the main one, so UI Automation lists it below the main window, not at the top level.
    $dialog = Wait-For { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window)))) | Where-Object { $_.Current.ClassName -ne 'Popup' } | Select-Object -First 1 } 15
    Check 'a confirmation dialog opens' ($null -ne $dialog)
    Check 'nothing is updated before it is confirmed' ((Installed-Version) -eq $OldVersion) (Installed-Version)
    if ($dialog) {
      $text = @($dialog.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | ForEach-Object { $_.Current.Name }
      Check 'the dialog lists the app with both versions' (@($text | Where-Object { $_ -like ($strings.'Apps.UpdateLine' -replace '\{0\}', 'Notepad++' -replace '\{1\}', $OldVersion -replace '\{2\}', '*') }).Count -gt 0) (($text | Where-Object { $_ -like '*Notepad++*' }) -join ' | ')
      # Cancel first: nothing may change, then ask again and confirm.
      (Find $dialog $strings.'Common.Cancel' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep -Seconds 20
      Check 'cancelling the confirmation updates nothing' ((Installed-Version) -eq $OldVersion -and -not (Find $root ($strings.'Apps.UpdateResultDone.one' -replace '\{0\}', '1'))) (Installed-Version)
      $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      $dialog = Wait-For { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window)))) | Where-Object { $_.Current.ClassName -ne 'Popup' } | Select-Object -First 1 } 15
      $ok = Find $dialog $strings.'Apps.Update' $CT::Button
      $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    $banner = Wait-For { Find $root ($strings.'Apps.UpdateResultDone.one' -replace '\{0\}', '1') } 240
    Shot '2-after-update'
    Check 'the result banner says one app was updated' ($null -ne $banner)
  }

  $version = Installed-Version
  Check 'winget reports a newer version than before' ($version -and [version]$version -gt [version]$OldVersion) $version
  $file = (Get-Item 'C:\Program Files\Notepad++\notepad++.exe' -ErrorAction SilentlyContinue).VersionInfo.FileVersion
  Check 'the installed file has that version too' ($file -and $version -and $file.StartsWith($version.Substring(0, [Math]::Min(5, $version.Length)))) $file
  $gone = Wait-For { -not (@($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)))) | Where-Object { $_.Current.Name -like ($strings.'Apps.UpdateAutomationTo' -replace '\{0\}', 'Notepad++' -replace '\{1\}', '*') }) } 120
  Check 'the update button is gone afterwards' ([bool]$gone)

  $run = Get-ChildItem $data -Recurse -Filter run.json -ErrorAction SilentlyContinue | Select-Object -First 1
  $json = if ($run) { Get-Content -Raw $run.FullName } else { '' }
  Check 'the run is in the history as apps-update' ($json -match '"operation"\s*:\s*"apps-update"')
  Check 'the history says the app was updated' ($json -match 'Succeeded' -and $json -match 'Notepad')
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process $proc -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 1
  if (Installed-Version) { $code = Remove-App; Check 'Notepad++ removed again' (-not (Installed-Version)) "exit $code" }
  Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
Write-Host ''; Write-Host ("{0} checks, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
