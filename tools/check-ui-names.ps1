# Checks what assistive technology (and UI Automation scripts) read from the running app: every button, checkbox, list item,
# radio button and text box on every page must have an accessible name, and none may be named after a C# type
# ("Mesura.App.SettingRow"). Exits 1 and lists what it found when something fails.
#
# Read-only: it starts Mesura with --no-elevate in the chosen language (Debug builds honour MESURA_PREVIEW_LANGUAGE; Release
# builds use Windows' language), selects each navigation item and opens All settings, never presses anything that changes
# the PC, and closes the instance it started. It will not run while another Mesura is open (the second launch would hand
# over to it). Usage:
#   powershell -ExecutionPolicy Bypass -File tools\check-ui-names.ps1 [-Exe <Mesura.exe>] [-Lang en|ru]
param(
  [string]$Exe = '',
  [ValidateSet('en', 'ru')][string]$Lang = 'en'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $repo 'artifacts\current\Mesura\Mesura.exe' }
if (-not (Test-Path $Exe)) { throw "No Mesura.exe at $Exe (run tools\publish.ps1, or pass -Exe)." }
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is already running; close it first, or this check would attach to it.' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$env:MESURA_PREVIEW_LANGUAGE = $Lang
$p = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
Remove-Item Env:MESURA_PREVIEW_LANGUAGE
try {
  $deadline = (Get-Date).AddSeconds(40)
  while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  if ($p.MainWindowHandle -eq 0) { throw 'Mesura showed no window.' }
  Start-Sleep -Seconds 20
  $root = $A::FromHandle($p.MainWindowHandle)
  function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
  function Pick($name, $type) { @($root.FindAll($TS::Descendants, (Cond $A::NameProperty $name)) | Where-Object { $_.Current.ControlType -eq $type -and -not $_.Current.IsOffscreen }) | Select-Object -First 1 }
  $kinds = 'Button', 'CheckBox', 'ListItem', 'RadioButton', 'Edit', 'ComboBox', 'Hyperlink', 'DataItem', 'TabItem', 'MenuItem'
  $problems = New-Object System.Collections.Generic.List[string]
  function Scan($label) {
    $isControl = Cond $A::IsControlElementProperty $true
    foreach ($e in $root.FindAll($TS::Descendants, $isControl)) {
      if ($e.Current.IsOffscreen) { continue }
      $t = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
      if ($t -notin $kinds) { continue }
      $n = $e.Current.Name
      if ([string]::IsNullOrWhiteSpace($n)) { $problems.Add("${label}: $t has no accessible name") }
      elseif ($n -match '^[A-Za-z_]+(\.[A-Za-z_]+)+$') { $problems.Add("${label}: $t is named after a type, '$n'") }
    }
  }
  foreach ($page in 'Nav.Optimize', 'Nav.Tweaks', 'Nav.Debloat', 'Nav.Apps', 'Nav.Configurations', 'Nav.Profiles', 'Nav.Activation', 'Nav.Activity', 'Nav.Settings') {
    $nav = Pick $strings.$page $CT::RadioButton
    if (-not $nav) {
      # Configurations and Activation sit under the dock's "More" menu: open it, then look again.
      $more = Pick $strings.'Nav.More' $CT::Button
      if ($more) {
        $o = $null
        if ($more.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$o)) { $o.Toggle(); Start-Sleep -Milliseconds 700 }
        $nav = Pick $strings.$page $CT::RadioButton
      }
    }
    if (-not $nav) { $problems.Add("$page : navigation item not found"); continue }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds $(if ($page -eq 'Nav.Apps') { 20 } else { 3 }) # Apps checks every app with winget first
    Scan $page
    if ($page -eq 'Nav.Debloat') {
      foreach ($tab in 'Debloat.Tab.ai', 'Debloat.Tab.startup') {
        $chip = Pick $strings.$tab $CT::RadioButton
        if ($chip) { $chip.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1500; Scan "Debloat ($tab)" }
      }
    }
  }
}
finally {
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}

$distinct = $problems | Group-Object | Sort-Object Name
if ($distinct) {
  $distinct | ForEach-Object { "$($_.Name)  (x$($_.Count))" }
  Write-Output "FAILED: $($distinct.Count) accessibility name problem(s) ($Lang)."
  exit 1
}
Write-Output "OK: every control on every page has an accessible name ($Lang)."
