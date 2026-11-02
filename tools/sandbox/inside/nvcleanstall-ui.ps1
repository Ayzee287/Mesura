# Inside Windows Sandbox: installs NVCleanstall (the file whose SHA-256 matches winget's manifest for 1.19.0), starts it, and records its window with UI Automation (every element's
# type, name, automation id, class, patterns, enabled state and position) and a screenshot, page by page, so the automation can be written against what is really there. It only
# presses buttons from a short allow-list that move between pages; it never presses anything that starts an installation. The sandbox has no GPU: what NVCleanstall offers without
# one is part of what this records.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
Say "== nvcleanstall-ui $(Get-Date -Format s) on $env:COMPUTERNAME"
$exe = 'C:\Host\packages\nvcleanstall\NVCleanstall_1.19.0.exe'
$expected = '9DD36EF956AF927CF41FA441F91B329A7973E13965E4E7D70E6FA9C1DF1CADE6'
Check 'the package is the one winget names' ((Get-FileHash $exe -Algorithm SHA256).Hash -eq $expected)
$p = Start-Process $exe -ArgumentList '/install', '/VERYSILENT' -PassThru
Check 'NVCleanstall installs silently' ($p.WaitForExit(240000) -and $p.ExitCode -eq 0) "exit $($p.ExitCode)"
$installed = @(Get-ChildItem 'C:\Program Files', 'C:\Program Files (x86)', "$env:LOCALAPPDATA\Programs" -Filter 'NVCleanstall*' -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'NVCleanstall.exe' } | Where-Object { Test-Path $_ })
Info 'installed at' ($installed -join '; ')
if ($installed.Count -eq 0) { Say '== stopped: no NVCleanstall.exe found'; return }
$info = Get-Item $installed[0]
Info 'file version' "$($info.VersionInfo.FileVersion), signature $((Get-AuthenticodeSignature $installed[0]).Status)"

function Dump-Element($e, [int]$depth, [System.Collections.Generic.List[string]]$out) {
  $c = $e.Current
  $patterns = @($e.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers.Pattern', '' }) -join ','
  $r = $c.BoundingRectangle
  $out.Add(('{0}{1} name="{2}" id="{3}" class="{4}" enabled={5} offscreen={6} patterns=[{7}] rect={8:F0},{9:F0},{10:F0},{11:F0}' -f ('  ' * $depth), ($c.ControlType.ProgrammaticName -replace 'ControlType.', ''), $c.Name, $c.AutomationId, $c.ClassName, $c.IsEnabled, $c.IsOffscreen, $patterns, $r.X, $r.Y, $r.Width, $r.Height))
  if ($depth -lt 12) {
    foreach ($child in $e.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) { Dump-Element $child ($depth + 1) $out }
  }
}

function Snapshot([string]$label) {
  $proc = Get-Process NVCleanstall -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) { Info "no NVCleanstall process at '$label'"; return $null }
  $deadline = (Get-Date).AddSeconds(60); $win = $null
  while (-not $win -and (Get-Date) -lt $deadline) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($w.Current.ProcessId -eq $proc.Id -and $w.Current.BoundingRectangle.Width -gt 200) { $win = $w; break } }
    if (-not $win) { Start-Sleep 2 }
  }
  if (-not $win) { Info "no window at '$label'"; return $null }
  $lines = New-Object System.Collections.Generic.List[string]
  for ($try = 0; $try -lt 15; $try++) { Start-Sleep 2; $lines.Clear(); Dump-Element $win 0 $lines; if ($lines.Count -ge 40) { break } }
  Set-Content "C:\Host\out\nvcleanstall-ui-$label.txt" $lines -Encoding UTF8
  $r = $win.Current.BoundingRectangle
  try { $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height); [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $bmp.Save("C:\Host\out\nvcleanstall-ui-$label.png") } catch { Info "no screenshot for '$label'" $_.Exception.Message }
  $buttons = @($lines | Where-Object { $_ -match '^\s*Button name="([^"]*)"' } | ForEach-Object { [regex]::Match($_, 'name="([^"]*)"').Groups[1].Value })
  Info "page '$label'" "$($lines.Count) elements; buttons: $($buttons -join ' | ')"
  $win
}

function Find-ById($root, [string]$id) {
  $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
}
function Title($win) {
  $t = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
  if ($t) { $t.Current.Name } else { '' }
}

$app = Start-Process $installed[0] -PassThru
Start-Sleep 5
$win = Snapshot 'p01-initial'
$package = Get-ChildItem 'C:\Host\packages\nvidia' -Filter '*.exe' | Select-Object -First 1
if ($win -and $package) {
  # "Use driver files on disk": the package NVIDIA's own service names, already downloaded (no GPU is needed to build from files).
  $radio = Find-ById $win 'RadioHaveDisk'
  try { $radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { Info 'select failed' $_.Exception.Message }
  Start-Sleep 2
  $box = Find-ById $win 'TextHaveDisk'
  try { $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($package.FullName) } catch { Info 'set path failed' $_.Exception.Message }
  Start-Sleep 2
  $win = Snapshot 'p02-disk-chosen'
  $typed = Find-ById $win 'TextHaveDisk'
  if (-not $typed -or -not $typed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value) { Say '== stopped: the driver path was not accepted by NVCleanstall'; Get-Process NVCleanstall -ErrorAction SilentlyContinue | Stop-Process -Force; return }
}

function Main-Window {
  $proc = Get-Process NVCleanstall -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) { return $null }
  foreach ($w in $A::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($w.Current.ProcessId -eq $proc.Id -and $w.Current.BoundingRectangle.Width -gt 200) { return $w } }
  $null
}

# Page by page: press Next (by its automation id, only while it is called "Next"), wait up to 15 minutes for the page to change (the package is unpacked and prepared in between), record it.
$title = Title $win
for ($i = 3; $i -le 16 -and $win; $i++) {
  $next = Find-ById $win 'ButtonNext'
  if (-not $next) { Info "no ButtonNext on '$title'"; break }
  $label = $next.Current.Name
  if ($label -ne 'Next' -or -not $next.Current.IsEnabled) { Info 'stopping before a button that is not an enabled Next' "'$label', enabled=$($next.Current.IsEnabled), on '$title'"; break }
  Info 'pressing Next' "from '$title'"
  try { $next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Info 'invoke failed' $_.Exception.Message; break }
  $deadline = (Get-Date).AddMinutes(15); $newTitle = $title
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 5
    $w = Main-Window
    if (-not $w) { Info 'NVCleanstall closed'; $win = $null; break }
    $win = $w; $newTitle = Title $w
    if ($newTitle -ne $title) { break }
  }
  if (-not $win) { break }
  if ($newTitle -eq $title) { Info "the page '$title' did not change in 15 minutes; stopping"; break }
  $title = $newTitle
  Start-Sleep 3
  $win = Snapshot ('p{0:D2}' -f $i)
  Info 'page title' $title
}
Get-Process NVCleanstall -ErrorAction SilentlyContinue | Stop-Process -Force
Say "== done $(Get-Date -Format s)"

