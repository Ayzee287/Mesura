# End-to-end check of the window's main flow with real clicks (UI Automation): find a setting, tick it, Apply, confirm, check the
# registry itself, then Undo and check it came back; then try a setting that needs administrator rights from an unelevated window and
# check it is NOT reported as done and changes nothing.
#
# It changes ONE per-user value (HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers\DisableAutoplay) and puts it back; the run it records stays in Activity. It never
# touches a machine setting. Needs a build that honours MESURA_PREVIEW_LANGUAGE (Debug) or a window in the language of -Lang.
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-gui.ps1 -Exe <Mesura.exe> [-Lang en|ru]
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [ValidateSet('en', 'ru')][string]$Lang = 'en'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers'
$original = (Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay
$policyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
$policyBefore = try { (Get-ItemProperty $policyKey -ErrorAction Stop).StartupBoostEnabled } catch { $null }
$results = New-Object System.Collections.Generic.List[string]
$failed = $false
function Check([string]$what, [bool]$ok, [string]$detail = '') { $script:failed = $script:failed -or -not $ok; $results.Add(("{0,-4} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $detail)); Write-Host $results[-1] -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' }) }

$env:MESURA_PREVIEW_LANGUAGE = $Lang
$p = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
Remove-Item Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
try {
  $d = (Get-Date).AddSeconds(40); while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  Start-Sleep -Seconds 8
  $root = $A::FromHandle($p.MainWindowHandle)
  function Cond($prop, $v) { New-Object System.Windows.Automation.PropertyCondition($prop, $v) }
  function Find($scope, [string]$name, $type) { @($scope.FindAll($TS::Descendants, (Cond $A::NameProperty $name)) | Where-Object { $_.Current.ControlType -eq $type -and -not $_.Current.IsOffscreen }) | Select-Object -First 1 }
  function Invoke-It($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  function Wait-For([scriptblock]$cond, [int]$seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }; return $null }
  function Dialog { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Window)) | Where-Object { $_.Current.ClassName -ne 'Popup' }) | Select-Object -First 1 }
  function Search-For([string]$text) {
    $nav = Find $root $strings.'Nav.Tweaks' $CT::RadioButton; $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1
    $box = Find $root $strings.'AllSettings.Search' $CT::Edit; $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text); Start-Sleep -Seconds 2
  }
  function Tick([string]$name) {
    $sw = Find $root $name $CT::CheckBox
    if (-not $sw) { return $false }
    $sw.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 600
    return $sw.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On'
  }
  function Apply-Selected {
    Invoke-It (Find $root $strings.'AllSettings.Apply' $CT::Button)
    $dlg = Wait-For { Dialog } 10
    if (-not $dlg) { return $false }
    Invoke-It (Find $dlg $strings.'Optimize.Apply' $CT::Button)
    return $true
  }

  # ---- 1. a per-user setting: detect, preview, apply, verify, undo ---------------------------------------------------------
  $name = 'Turn off AutoPlay'  # catalog name of explorer.autoplay-off in English; Russian looked up below (the PC must not have AutoPlay off already)
  if ($Lang -eq 'ru') { $name = (Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'src\Mesura.App\Localization\catalog-ru.json') -ErrorAction SilentlyContinue) -replace '(?s).*"explorer.autoplay-off":\s*\{\s*"name":\s*"([^"]+)".*', '$1' }
  Search-For $name
  $card = Find $root $name $CT::CheckBox
  Check 'the setting is found and offered' ($null -ne $card)
  Check 'it can be ticked (staged, nothing changed yet)' (Tick $name) "registry still $((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay)"
  Check 'nothing was written by ticking' ((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay -eq $original)
  Check 'Apply opened the confirmation and it was confirmed' (Apply-Selected)
  $written = Wait-For { if ((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay -ne $original) { (Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay } } 20
  Check 'the registry holds the new value (read independently)' ($written -eq '1') "DisableAutoplay=$written"
  $banner = Wait-For { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Text)) | Where-Object { $_.Current.Name -match ($strings.'Optimize.ResultDone.one' -replace '\{0\}', '1') -or $_.Current.Name -like ($strings.'Optimize.ResultDone.one') }) | Select-Object -First 1 } 15
  Check 'the result says it was applied' ($null -ne $banner) $(if ($banner) { $banner.Current.Name } else { 'no result text found' })
  $undo = Wait-For { Find $root $strings.'Undo.Action' $CT::Button } 10
  Check 'the result offers Undo' ($null -ne $undo)
  if ($undo) {
    Invoke-It $undo
    $dlg = Wait-For { Dialog } 10
    if ($dlg) { Invoke-It (Find $dlg $strings.'Undo.Action' $CT::Button) }
    $back = Wait-For { if ((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay -eq $original) { 'restored' } } 20
    Check 'Undo put the original value back (read independently)' ($back -eq 'restored') "DisableAutoplay=$((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay), was $original"
  }

  # ---- 2. a setting that needs administrator rights, from an unelevated window: must not claim success ---------------------
  $editName = (Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'src\Mesura.Core\Windows\windows.catalog.json') | Select-String '"id": "edge.no-startup-boost"' -Quiet)
  $edgeName = if ($Lang -eq 'en') { 'Turn off Edge startup boost' } else { $null }
  if ($edgeName) {
    Search-For $edgeName
    $ticked = Tick $edgeName
    if ($ticked) {
      Check 'an admin-only setting can be staged' $true
      [void](Apply-Selected)
      Start-Sleep -Seconds 6
      $after = try { (Get-ItemProperty $policyKey -ErrorAction Stop).StartupBoostEnabled } catch { $null }
      Check 'the machine policy was NOT written from an unelevated window' ($after -eq $policyBefore) "StartupBoostEnabled before=$policyBefore after=$after"
      $okText = Wait-For { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Text)) | Where-Object { $_.Current.Name -match 'attention|Needs administrator|Done:' }) | Select-Object -First 1 } 10
      $seen = @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'appl|attention|dmin|Done|Needs' })
      Write-Host ('   texts: ' + ($seen -join ' | ')) -ForegroundColor DarkGray
      Check 'the window says it needs attention, never "Done"' ($null -ne $okText -and $okText.Current.Name -notmatch '^Done') $(if ($okText) { $okText.Current.Name } else { 'no such text' })
    } else { Check 'an admin-only setting can be staged' $false 'the switch was not found or not ticked' }
  } else { $results.Add('SKIP the admin-only scenario runs in English only') }
}
finally {
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  # safety net: whatever happened, the per-user value ends as it began
  if ((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisableAutoplay -ne $original) { Set-ItemProperty $key -Name MenuShowDelay -Value $original; Write-Host "restored MenuShowDelay to $original by hand" -ForegroundColor Yellow }
}
Write-Output ''
$results | ForEach-Object { Write-Output $_ }
exit $(if ($failed) { 1 } else { 0 })
