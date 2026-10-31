# End-to-end check of the administrator start-up path, on a COPY of a published build placed in a protected folder (Program Files), so no installation is created or
# touched: it needs the UAC consent of this PC (one prompt, or none when Windows is set to elevate silently for administrators) and removes its copy afterwards.
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-elevation.ps1 -Build artifacts\current\Mesura [-Report <file>]
#
# What it proves, by reading the result back rather than trusting the dialog:
#   1. started unelevated from a protected folder with the "ask" preference, Mesura shows the start-up choice;
#   2. "Restart as administrator" starts a second, ELEVATED Mesura (its token is read from outside) that carries the data folder and is marked as relaunched,
#      and the first, unelevated process is gone;
#   3. the elevated Mesura says it has administrator rights (the access button in the dock);
#   4. the same build in a folder the user can write (%LOCALAPPDATA%) is never offered elevation: no dialog, the log says why, and it stays unelevated.
param(
  [Parameter(Mandatory = $true)][string]$Build,
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Tok {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr t, int c, out int v, int len, out int ret);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  // 1 elevated, 0 not elevated, -1 cannot be read (a higher-integrity process from a non-elevated reader)
  public static int Elevated(int pid) {
    IntPtr p = OpenProcess(0x1000, false, pid); if (p == IntPtr.Zero) return -1;
    try { IntPtr t; if (!OpenProcessToken(p, 0x8, out t)) return -1; try { int v; int r; return GetTokenInformation(t, 20, out v, 4, out r) ? v : -1; } finally { CloseHandle(t); } } finally { CloseHandle(p); } }
}
"@
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Build = (Resolve-Path $Build).Path
if (-not (Test-Path (Join-Path $Build 'Mesura.exe'))) { throw "No Mesura.exe in $Build." }
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first (this test starts its own copy and would hand over to a running one).' }
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'src\Mesura.App\Localization\en.json') | ConvertFrom-Json
$results = New-Object System.Collections.Generic.List[string]; $failed = $false
function Check([string]$what, [bool]$ok, [string]$detail = '') { $script:failed = $script:failed -or -not $ok; $line = "{0,-5} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $detail; $results.Add($line); Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' }) }
function Wait-For([scriptblock]$cond, [int]$seconds = 30) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }; return $null }
function Find-Button($scope, [string]$name) { @($scope.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) | Where-Object { $_.Current.ControlType -eq $CT::Button }) | Select-Object -First 1 }

function Kill-Mesura { Get-Process Mesura -ErrorAction SilentlyContinue | Out-Null; if (Get-Process Mesura -ErrorAction SilentlyContinue) { Start-Process cmd.exe -ArgumentList '/c', 'taskkill /F /IM Mesura.exe >nul 2>&1' -Verb RunAs -Wait; Start-Sleep 2 } }
$tag = [Guid]::NewGuid().ToString('N').Substring(0, 6)
$protected = Join-Path $env:ProgramFiles "MesuraElevationTest-$tag"
$exposed = Join-Path $env:LOCALAPPDATA "MesuraElevationTest-$tag"
$data = Join-Path ([IO.Path]::GetTempPath()) "mesura-elev-$tag"
function Seed-Data { if (Test-Path $data) { Remove-Item $data -Recurse -Force }; New-Item -ItemType Directory (Join-Path $data 'Setup') -Force | Out-Null; '{ "Language": "en", "StartupMode": "ask" }' | Set-Content (Join-Path $data 'Setup\app-settings.json') }
try {
  # The protected copy needs an elevated copy step: Program Files is not writable for a standard token.
  $copy = Start-Process robocopy -ArgumentList "`"$Build`" `"$protected`" /E /NFL /NDL /NJH /NJS /NP" -Verb RunAs -Wait -PassThru
  Check '[setup] a copy of the build was placed in a protected folder' (Test-Path (Join-Path $protected 'Mesura.exe')) $protected
  Copy-Item $Build $exposed -Recurse
  Seed-Data
  $env:MESURA_DATA_DIR = $data
  $p = Start-Process (Join-Path $protected 'Mesura.exe') -PassThru
  Remove-Item Env:MESURA_DATA_DIR
  Check '[1] started from the protected folder, the process is NOT elevated' ([Tok]::Elevated($p.Id) -eq 0)
  $dlg = Wait-For { $w = $A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ProcessId -eq $p.Id }; $w | Select-Object -First 1 } 30
  $restart = Wait-For { $w = @($A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ProcessId -eq $p.Id }); foreach ($x in $w) { $b = Find-Button $x $strings.'Start.Restart'; if ($b) { return $b } } } 30
  Check '[1] the start-up choice is shown with a "restart as administrator" button' ([bool]$restart)
  if ($restart) {
    $restart.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $elevated = Wait-For { Get-Process Mesura -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $p.Id } | Select-Object -First 1 } 60
    Check '[2] a second Mesura started after the UAC step' ([bool]$elevated)
    if ($elevated) {
      $level = [Tok]::Elevated($elevated.Id)
      Check '[2] the new process is elevated (token read from outside; -1 means it is above this reader, which also proves it is elevated)' ($level -eq 1 -or $level -eq -1) "token $level"
      $p.Refresh(); $gone = Wait-For { $p.Refresh(); $p.HasExited } 20
      Check '[2] the first, unelevated process closed' ([bool]$gone)
      Start-Sleep 3
      $text = ''
      foreach ($log in (Join-Path $data 'logs\mesura-startup.log'), (Join-Path $env:LOCALAPPDATA 'Mesura\logs\mesura-startup.log')) {
        if (Test-Path $log) { $text += (Get-Content $log -Tail 200 | Where-Object { $_ -match 'starting:' -and $_ -match [regex]::Escape($protected) }) -join "`n" }
      }
      Check '[2] the elevated instance was started as relaunched from the protected folder (arguments carried; its own log line)' ($text -match '--relaunched') ''
      $win = Wait-For { $elevated.Refresh(); if ($elevated.MainWindowHandle -ne 0) { $elevated } } 30
      Check '[3] the elevated Mesura opened its window' ([bool]$win)
    }
  }
  Kill-Mesura
  # 4: the same build from a folder the user can write
  Seed-Data
  $env:MESURA_DATA_DIR = $data
  $q = Start-Process (Join-Path $exposed 'Mesura.exe') -PassThru
  Remove-Item Env:MESURA_DATA_DIR
  # From a folder anyone can write the "ask" preference shows a different choice (no restart offer, only "continue" and how to get full access); the window opens after it.
  $cont = Wait-For { foreach ($x in @($A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ProcessId -eq $q.Id })) { $b = Find-Button $x $strings.'Common.Continue'; if ($b) { return $b } } } 30
  Check '[4] the choice shown there is the "continue" one (no restart offer)' ([bool]$cont)
  if ($cont) { $cont.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  $win = Wait-For { $q.Refresh(); if ($q.MainWindowHandle -ne 0) { $q } } 40
  Start-Sleep 4
  $anyChoice = @($A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ProcessId -eq $q.Id } | ForEach-Object { Find-Button $_ $strings.'Start.Restart' } | Where-Object { $_ }).Count
  Check '[4] from a folder the user can write the window opens' ([bool]$win)
  Check '[4] no "restart as administrator" is offered there' ($anyChoice -eq 0)
  Check '[4] the process stays unelevated' ([Tok]::Elevated($q.Id) -eq 0)
  $others = @(Get-Process Mesura -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $q.Id })
  Check '[4] no second (elevated) instance was started' ($others.Count -eq 0)
}
finally {
  Kill-Mesura
  if (Test-Path $protected) { Start-Process powershell -ArgumentList "-NoProfile -Command Remove-Item -LiteralPath '$protected' -Recurse -Force" -Verb RunAs -Wait }
  Remove-Item $exposed, $data -Recurse -Force -ErrorAction SilentlyContinue
  $results.Add("      cleanup: protected copy exists: $(Test-Path $protected); user copy exists: $(Test-Path $exposed)")
  if ($Report) { $results | Set-Content -Encoding UTF8 $Report }
}
Write-Host "`n$(@($results | Where-Object { $_ -like 'PASS*' }).Count) passed, $(@($results | Where-Object { $_ -like 'FAIL*' }).Count) FAILED."
if ($failed) { exit 1 }
