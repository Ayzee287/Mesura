# Read-only telemetry of a Radeon for a fixed time, through Mesura's own ADLX reader (`mesura gpu amd --json`, one reading per sample): GPU load, core and memory clock, temperature, hot spot,
# board power, fan speed and voltage. It changes nothing. Each sample starts the reader once (about half a second), so the default interval is 2 seconds.
# Use it twice, as for NVIDIA: idle (-Label idle, 60 s: what the PC costs in idle power, heat and noise) and during a game (-Label game -Seconds 300).
# Writes <label>-amd-telemetry.csv and <label>-amd-telemetry.txt (a summary) into -Out (the Desktop by default).
#
#   powershell -ExecutionPolicy Bypass -File Collect-AmdTelemetry.ps1 -Label idle -Seconds 60
#   powershell -ExecutionPolicy Bypass -File Collect-AmdTelemetry.ps1 -Summarize <file.csv>        (only the summary of a file already made)
param(
  [string]$Label = 'idle',
  [int]$Seconds = 60,
  [int]$IntervalMs = 2000,
  [string]$Cli = '',
  [string]$Out = '',
  [string]$Summarize = ''
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
$header = 'time,usagePercent,coreMhz,memoryMhz,temperatureC,hotspotC,boardPowerW,fanRpm,voltageMv'

function Num($v) { $d = 0.0; if ([double]::TryParse(("$v").Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $d } else { $null } }
function Stat($values) {
  $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
  if ($v.Count -eq 0) { return 'n/a' }
  $median = if ($v.Count % 2) { $v[[int](($v.Count - 1) / 2)] } else { ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2 }
  ('min {0:0.#}, median {1:0.#}, mean {2:0.#}, max {3:0.#}' -f $v[0], $median, (($v | Measure-Object -Average).Average), $v[-1])
}
function Summary([string]$csv) {
  $rows = @(Import-Csv $csv)
  $out = New-Object System.Collections.Generic.List[string]
  if ($rows.Count -eq 0) { $out.Add("No samples in $csv"); return $out }
  $out.Add("Samples: $($rows.Count) from $($rows[0].time) to $($rows[-1].time)")
  $out.Add('GPU load (%): ' + (Stat ($rows | ForEach-Object { Num $_.usagePercent })))
  $out.Add('Core clock (MHz): ' + (Stat ($rows | ForEach-Object { Num $_.coreMhz })))
  $out.Add('Memory clock (MHz): ' + (Stat ($rows | ForEach-Object { Num $_.memoryMhz })))
  $out.Add('Temperature (C): ' + (Stat ($rows | ForEach-Object { Num $_.temperatureC })))
  $out.Add('Hot spot (C): ' + (Stat ($rows | ForEach-Object { Num $_.hotspotC })))
  $out.Add('Board power (W): ' + (Stat ($rows | ForEach-Object { Num $_.boardPowerW })))
  $out.Add('Fan (rpm; 0 = the fan is stopped, the zero-RPM mode): ' + (Stat ($rows | ForEach-Object { Num $_.fanRpm })))
  $out.Add('Voltage (mV): ' + (Stat ($rows | ForEach-Object { Num $_.voltageMv })))
  $out
}

if ($Summarize) { Summary $Summarize | ForEach-Object { Write-Host $_ }; exit 0 }
if (-not $Cli) { Write-Host 'mesura.exe was not found (put it beside this script or pass -Cli). Nothing was collected.'; exit 3 }

New-Item -ItemType Directory -Force $Out | Out-Null
$csv = Join-Path $Out "$Label-amd-telemetry.csv"
Set-Content -Path $csv -Value $header -Encoding ASCII
Write-Host "Sampling the Radeon every $IntervalMs ms for $Seconds s ($Label). Leave the PC as it is for this test."
$deadline = (Get-Date).AddSeconds($Seconds)
$taken = 0; $failed = 0
while ((Get-Date) -lt $deadline) {
  $started = Get-Date
  $text = (& $Cli gpu amd --json 2>$null | ForEach-Object { "$_" }) -join "`n"
  $m = $null
  try { $m = ($text | ConvertFrom-Json).Gpus[0].Metrics } catch { }
  if ($m) {
    $f = { param($v) if ($null -eq $v) { '' } else { ([double]$v).ToString([Globalization.CultureInfo]::InvariantCulture) } }
    Add-Content -Path $csv -Value ((@((Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), (& $f $m.UsagePercent), (& $f $m.ClockMhz), (& $f $m.MemoryClockMhz), (& $f $m.TemperatureC), (& $f $m.HotspotC), (& $f $m.BoardPowerW), (& $f $m.FanRpm), (& $f $m.VoltageMv)) -join ',')) -Encoding ASCII
    $taken++
  } else { $failed++; if ($failed -ge 5 -and $taken -eq 0) { Write-Host 'The ADLX reader gave no metrics five times in a row (no AMD driver, or it would not start). Stopping.'; break } }
  $wait = $IntervalMs - [int]((Get-Date) - $started).TotalMilliseconds
  if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
}
$summary = if ($taken -gt 0) { Summary $csv } else { @('No samples were taken.') }
$txt = Join-Path $Out "$Label-amd-telemetry.txt"
$lines = @("Radeon telemetry '$Label', $Seconds s, $taken samples ($failed without metrics), $(Get-Date -Format s) (read only)", '') + $summary
[IO.File]::WriteAllLines($txt, $lines, (New-Object System.Text.UTF8Encoding($true)))
$lines | ForEach-Object { Write-Host $_ }
Write-Host "Wrote $csv and $txt"
if ($taken -eq 0) { exit 3 }
