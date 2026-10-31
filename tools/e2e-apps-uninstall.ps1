# End-to-end check of uninstalling an app from the Apps page, with the real winget and the real window. It needs an app the PC does not
# have, so nothing of the owner's is touched: it installs Notepad++ (MSI, machine-wide), starts Mesura with its own data folder and checks
#
#   run from a normal (not elevated) terminal  -> Mesura is not administrator:
#     - Notepad++ has an uninstall button, disabled, whose name says it needs administrator rights,
#     - no app that Mesura has not verified (Brave, Chrome, Discord, Spotify, Steam, ...) has one,
#     - Notepad++ is still installed afterwards.
#   run from an elevated terminal              -> Mesura is administrator:
#     - the button is enabled; pressing it asks first, and Cancel leaves the app installed,
#     - confirming uninstalls it: the banner says so, winget and the folder on disk agree, the card says Not installed,
#     - the run is in the history as apps-uninstall.
#
# Either way it removes Notepad++ again at the end. It refuses to run if Notepad++ is already installed (that would be the owner's) or Mesura is open.
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-apps-uninstall.ps1 -Exe <Mesura.exe> [-Lang en|ru] [-Out <folder>]
#
# Installing the MSI needs administrator rights; winget asks for them itself (a UAC prompt unless the PC's policy allows silent elevation).
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [ValidateSet('en', 'ru')][string]$Lang = 'en',
  [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class MW3 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$id = 'Notepad++.Notepad++'
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$results = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') { $results.Add(('{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' }))); Write-Host $results[-1] }
function Installed-Version { $o = winget list --id $id --exact --disable-interactivity 2>$null | Out-String; if ($o -match [regex]::Escape($id) + '\s+(\S+)') { $Matches[1] } else { $null } }
function Remove-App { if ($elevated) { winget uninstall --id $id --exact --silent --disable-interactivity | Out-Null; $LASTEXITCODE } else { (Start-Process winget -ArgumentList 'uninstall', '--id', $id, '--exact', '--silent', '--disable-interactivity' -Verb RunAs -Wait -PassThru -WindowStyle Hidden).ExitCode } }

if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
if (Installed-Version) { throw 'Notepad++ is already installed on this PC; this check only works on a PC without it.' }
if ($Out) { New-Item -ItemType Directory -Force -Path $Out | Out-Null }
Write-Host "Mesura will run $(if ($elevated) { 'as administrator' } else { 'without administrator rights' })."

$data = Join-Path $env:TEMP ('mesura-appsun-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $data | Out-Null
$proc = $null
try {
  winget install --id $id --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
  Check 'Notepad++ installed for the test' ($null -ne (Installed-Version)) (Installed-Version)

  $env:MESURA_DATA_DIR = $data; $env:MESURA_PREVIEW_LANGUAGE = $Lang
  $proc = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
  Remove-Item Env:MESURA_DATA_DIR, Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
  $d = (Get-Date).AddSeconds(40); while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $proc.Refresh() }
  $h = $proc.MainWindowHandle
  Start-Sleep -Seconds 5
  $root = $A::FromHandle($h)
  function Shot([string]$name) {
    if (-not $Out) { return }
    $r = New-Object MW3+RECT; [MW3]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [MW3]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  }
  function Buttons { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)))) }
  function Find([System.Windows.Automation.AutomationElement]$scope, [string]$name, $type = $null) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    @($scope.FindAll($TS::Descendants, $c)) | Where-Object { -not $type -or $_.Current.ControlType -eq $type } | Select-Object -First 1
  }
  function Wait-For([scriptblock]$what, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $what; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; $null }
  function Dialog { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Window)))) | Where-Object { $_.Current.ClassName -ne 'Popup' } | Select-Object -First 1 }

  (Find $root $strings.'Nav.Apps' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  # Wait for the page to know Notepad++ is installed: its uninstall button shows only then.
  $nameEnabled = ($strings.'Apps.UninstallAutomation' -replace '\{0\}', 'Notepad++')
  $nameNeedsAdmin = ($strings.'Apps.UninstallAutomationNeedsAdmin' -replace '\{0\}', 'Notepad++')
  $button = Wait-For { Buttons | Where-Object { $_.Current.Name -eq $nameEnabled -or $_.Current.Name -eq $nameNeedsAdmin } | Select-Object -First 1 } 120
  Check 'Notepad++ has an uninstall button' ($null -ne $button) $(if ($button) { $button.Current.Name })
  $uninstallButtons = @(Buttons | Where-Object { $_.Current.Name -like ($strings.'Apps.UninstallAutomation' -replace '\{0\}', '*') })
  Check 'no other app has one (they are not verified, or not installed)' ($uninstallButtons.Count -eq 1) (($uninstallButtons | ForEach-Object { $_.Current.Name }) -join ' | ')
  Shot '1-uninstall-offered'

  if (-not $elevated) {
    Check 'the button is disabled and says it needs administrator rights' ($button -and $button.Current.Name -eq $nameNeedsAdmin -and -not $button.Current.IsEnabled)
    Start-Sleep -Seconds 2
    Check 'Notepad++ is still installed' ($null -ne (Installed-Version))
  } else {
    Check 'the button is enabled' ($button -and $button.Current.Name -eq $nameEnabled -and $button.Current.IsEnabled)
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $dialog = Wait-For { Dialog } 15
    Check 'a confirmation dialog opens' ($null -ne $dialog)
    if ($dialog) {
      (Find $dialog $strings.'Common.Cancel' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep -Seconds 15
      Check 'cancelling the confirmation uninstalls nothing' ($null -ne (Installed-Version)) (Installed-Version)
      $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      $dialog = Wait-For { Dialog } 15
      (Find $dialog $strings.'Apps.Uninstall' $CT::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    $banner = Wait-For { Find $root ($strings.'Apps.UninstallResultDone' -replace '\{0\}', 'Notepad++') } 240
    Shot '2-after-uninstall'
    Check 'the result banner says Notepad++ was uninstalled' ($null -ne $banner)
    Check 'winget no longer lists it' ($null -eq (Installed-Version))
    Check 'its program folder is gone' (-not (Test-Path 'C:\Program Files\Notepad++\notepad++.exe'))
    $gone = Wait-For { -not (Buttons | Where-Object { $_.Current.Name -eq $nameEnabled }) } 120
    Check 'the card has no uninstall button afterwards' ([bool]$gone)
    $run = Get-ChildItem $data -Recurse -Filter run.json -ErrorAction SilentlyContinue | Select-Object -First 1
    $json = if ($run) { Get-Content -Raw $run.FullName } else { '' }
    Check 'the run is in the history as apps-uninstall' ($json -match '"operation"\s*:\s*"apps-uninstall"' -and $json -match 'Succeeded')
  }
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
