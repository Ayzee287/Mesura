# Checks that a result line is announced to assistive technology: when a page shows a new result, the window raises a UI Automation LiveRegionChanged event for it, with the
# result's text as the element's name (that is what Narrator reads). The result is made by a harmless action: Quick select on the Tweaks page ticks settings in the list and
# changes nothing on the PC. The same result chosen twice must be announced twice.
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-announce.ps1 -Exe <Mesura.exe> [-Lang en|ru]
#
# It starts Mesura (not elevated, its own data folder), listens for the event with the managed UI Automation client, and closes the instance it started. It will not run while
# another Mesura is open.
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [ValidateSet('en', 'ru')][string]$Lang = 'en'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase -TypeDefinition @"
using System; using System.Collections.Generic; using System.Windows.Automation;
public static class LiveWatch {
  public static readonly List<string> Events = new List<string>();
  public static void Start(AutomationElement root) {
    Automation.AddAutomationEventHandler(AutomationEvent.LookupById(20024) /* UIA_LiveRegionChangedEventId */, root, TreeScope.Subtree, delegate (object s, AutomationEventArgs e) {
      try { var el = (AutomationElement)s; lock (Events) { Events.Add(el.Current.Name); } } catch (Exception) { }
    });
  }
  public static string[] Snapshot() { lock (Events) { return Events.ToArray(); } }
}
"@
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$results = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') { $results.Add(('{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' }))); Write-Host $results[-1] }
function Wait-For([scriptblock]$what, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $what; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }; $null }
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
$data = Join-Path $env:TEMP ('mesura-announce-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $data | Out-Null
$proc = $null
try {
  $env:MESURA_DATA_DIR = $data; $env:MESURA_PREVIEW_LANGUAGE = $Lang
  $proc = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
  Remove-Item Env:MESURA_DATA_DIR, Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
  $d = (Get-Date).AddSeconds(40); while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $proc.Refresh() }
  Start-Sleep -Seconds 5
  $root = $A::FromHandle($proc.MainWindowHandle)
  function Find([string]$name, $type = $null, $scope = $root) { $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name); @($scope.FindAll($TS::Descendants, $c)) | Where-Object { -not $type -or $_.Current.ControlType -eq $type } | Select-Object -First 1 }
  [LiveWatch]::Start($root)
  (Find $strings.'Nav.Tweaks' $CT::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Seconds 4
  function Choose-FirstCollection {
    $toggle = Wait-For { Find $strings.'AllSettings.QuickSelect' } 20
    if (-not $toggle) { return $false }
    $tp = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne 'On') { $tp.Toggle() }
    Start-Sleep -Milliseconds 800
    # The choices live in a popup: a window of class Popup that UI Automation lists under the desktop, not under the window.
    $popup = Wait-For { @($A::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'Popup')))) | Where-Object { $_.Current.ProcessId -eq $proc.Id } | Select-Object -First 1 } 10
    $choice = if ($popup) { @($popup.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)))) | Where-Object { $_.Current.IsEnabled } | Select-Object -First 1 }
    if (-not $choice) { return $false }
    $choice.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $true
  }
  $before = @([LiveWatch]::Snapshot()).Count
  Check 'Quick select offers a collection and it was chosen' (Choose-FirstCollection)
  $selected = ($strings.'AllSettings.CollectionSelected.other' -replace '\{0\}', '*')
  $first = Wait-For { @([LiveWatch]::Snapshot() | Select-Object -Skip $before) | Where-Object { $_ -like $selected -or $_ -eq $strings.'AllSettings.CollectionDone' -or $_ -like ($strings.'AllSettings.CollectionSelected.one') } | Select-Object -First 1 } 10
  Check 'the result is announced as a live region change, with its text as the name' ([bool]$first) $first
  $count1 = @([LiveWatch]::Snapshot()).Count
  Check 'the same choice again is announced again' ((Choose-FirstCollection) -and [bool](Wait-For { @([LiveWatch]::Snapshot()).Count -gt $count1 } 10))
  Check 'the result text is also on screen' ($null -ne (Wait-For { Find $first } 5)) $first
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process $proc -Force -ErrorAction SilentlyContinue }
  Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
Write-Host ''; Write-Host ("{0} checks, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
