# Real check of the install dialog: Mesura (never elevated) is started, one app is ticked, Install is pressed, the choice card named by -Choice is selected, the dialog's
# Install is pressed, and the result is read back independently (winget, the registry's uninstall entries, the files), together with what the result banner said.
# It installs ONE real app; remove it afterwards (tools\e2e-apps-uninstall.ps1, or `mesura apps uninstall --id <id> --apply` from an elevated terminal).
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-apps-install-choice.ps1 -Exe <Mesura.exe> -App 'Notepad++' -Choice Standard [-Report <file>]
#
# -Choice is the card's title as the interface shows it (English: Mesura, Custom, Standard). The app must not be installed yet. Nothing else is changed.
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [string]$App = 'Notepad++',
  [string]$AppId = 'Notepad++.Notepad++',
  [string]$Choice = 'Standard',
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot '..\src\Mesura.App\Localization\en.json') | ConvertFrom-Json
$results = New-Object System.Collections.Generic.List[string]
function Say([string]$l) { $results.Add($l); Write-Host $l; if ($Report) { $l | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }
function Find-All($root, [string]$name, $type) { @($root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)))) | Where-Object { $_.Current.ControlType -eq $type } }
function Listed { (winget list --id $AppId --exact --accept-source-agreements 2>&1 | Out-String) -match [regex]::Escape($AppId) }

if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
if (Listed) { throw "$App is already installed here: this check needs it absent." }
$env:MESURA_PREVIEW_LANGUAGE = 'en'
$p = Start-Process $Exe -ArgumentList '--no-elevate' -PassThru
Remove-Item Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
try {
  $d = (Get-Date).AddSeconds(40); while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  Start-Sleep 5
  $root = $A::FromHandle([IntPtr]$p.MainWindowHandle.ToInt64())
  (Find-All $root $strings.'Nav.Apps' $CT::RadioButton | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep 8
  $box = Find-All $root $App $CT::CheckBox | Select-Object -First 1
  Check "$App has a tick box that can be ticked" ($box -and $box.Current.IsEnabled)
  $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 600
  $dock = Find-All $root $strings.'Apps.Install' $CT::Button | Select-Object -First 1
  $dock.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 3
  # The dialog is a window owned by Mesura's, so UI Automation lists it under the main window.
  $card = Find-All $root $Choice $CT::RadioButton | Select-Object -First 1
  Check "the install dialog offers the card '$Choice'" ([bool]$card)
  if ($card) { $card.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400 }
  $cards = @('Mesura', 'Custom', 'Standard') | ForEach-Object { $c = Find-All $root $_ $CT::RadioButton | Select-Object -First 1; if ($c) { '{0}={1}/{2}' -f $_, $c.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected, $(if ($c.Current.IsEnabled) { 'on' } else { 'off' }) } }
  Say ('      cards: ' + ($cards -join '  '))
  $ok = Find-All $root $strings.'Apps.Install' $CT::Button | Where-Object { $_.Current.IsEnabled } | Select-Object -Last 1
  $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  # The install runs; wait for it to end (the dock shows progress, then the result banner).
  $end = (Get-Date).AddMinutes(8); $seen = $false
  while ((Get-Date) -lt $end) {
    Start-Sleep 5
    if (Listed) { $seen = $true; break }
  }
  Check "$App is installed (winget lists it)" $seen
  Start-Sleep 12   # the page re-reads the states and shows the result
  $all = @($root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
  $banner = $all | Where-Object { $_ -match '^Installed \d+ app|installed, \d+ not installed|Nothing new to install|Cancelled' } | Select-Object -First 1
  Check 'the result banner says it was installed' ([bool]$banner) $banner
  $pill = Find-All $root $App $CT::CheckBox | Select-Object -First 1
  Check "the card's check box is disabled now (installed apps can't be ticked)" ($pill -and -not $pill.Current.IsEnabled)
}
finally { Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force }
$failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
Say ''; Say ("{0} lines, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0) { exit 1 }
