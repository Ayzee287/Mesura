# Read-only GPU telemetry for a fixed time, from nvidia-smi: performance state, clocks, power draw and limit, temperature, load, fan, PCIe link, and why the clocks are held back. It changes nothing.
# Use it twice: with the PC idle (-Label idle, 60 s: what the profile costs in idle power, heat and noise) and during a game or a benchmark (-Label game -Seconds 300: what the card does under load).
# Writes <label>-telemetry.csv and <label>-telemetry.txt (a summary) into -Out (the Desktop by default).
#
#   powershell -ExecutionPolicy Bypass -File Collect-GpuTelemetry.ps1 -Label idle -Seconds 60
#   powershell -ExecutionPolicy Bypass -File Collect-GpuTelemetry.ps1 -Summarize <file.csv>        (only the summary of a file already made)
param(
  [string]$Label = 'idle',
  [int]$Seconds = 60,
  [int]$IntervalMs = 1000,
  [string]$Out = '',
  [string]$Summarize = ''
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
$baseFields = 'timestamp,pstate,clocks.gr,clocks.mem,clocks.sm,power.draw,power.limit,temperature.gpu,utilization.gpu,utilization.memory,fan.speed,pcie.link.gen.current'

function Num($v) { $d = 0.0; if ([double]::TryParse(("$v").Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $d } else { $null } }
function Stat($values) {
  $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
  if ($v.Count -eq 0) { return 'n/a' }
  $median = if ($v.Count % 2) { $v[[int](($v.Count - 1) / 2)] } else { ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2 }
  ('min {0:0.#}, median {1:0.#}, mean {2:0.#}, max {3:0.#}' -f $v[0], $median, (($v | Measure-Object -Average).Average), $v[-1])
}

function Summary([string]$csv) {
  $rows = @(Get-Content $csv | Where-Object { $_.Trim() } | ForEach-Object { $c = $_ -split ','; if ($c.Count -ge 12) { [pscustomobject]@{ Time = $c[0].Trim(); PState = $c[1].Trim(); Core = Num $c[2]; Mem = Num $c[3]; Sm = Num $c[4]; Power = Num $c[5]; Limit = Num $c[6]; Temp = Num $c[7]; Gpu = Num $c[8]; MemUse = Num $c[9]; Fan = Num $c[10]; Pcie = Num $c[11]; Throttle = $(if ($c.Count -ge 13) { $c[12].Trim() } else { 'n/a' }) } } })
  $out = New-Object System.Collections.Generic.List[string]
  if ($rows.Count -eq 0) { $out.Add("No samples in $csv"); return $out }
  $out.Add("Samples: $($rows.Count) from $($rows[0].Time) to $($rows[-1].Time)")
  $out.Add('Performance states: ' + ((($rows | Group-Object PState | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Count) ($([math]::Round(100 * $_.Count / $rows.Count))%)" })) -join ', '))
  $out.Add('Core clock (MHz): ' + (Stat ($rows | ForEach-Object { $_.Core })))
  $out.Add('Memory clock (MHz): ' + (Stat ($rows | ForEach-Object { $_.Mem })))
  $out.Add('Power draw (W): ' + (Stat ($rows | ForEach-Object { $_.Power })) + "; limit $((($rows | ForEach-Object { $_.Limit } | Select-Object -Unique) -join '/')) W")
  $out.Add('Temperature (C): ' + (Stat ($rows | ForEach-Object { $_.Temp })))
  $out.Add('GPU load (%): ' + (Stat ($rows | ForEach-Object { $_.Gpu })))
  $out.Add('Fan (%): ' + (Stat ($rows | ForEach-Object { $_.Fan })))
  $out.Add('PCIe generation: ' + ((($rows | Group-Object Pcie | ForEach-Object { "gen $($_.Name) x$($_.Count)" })) -join ', '))
  $out.Add('Clock throttle reason bit masks seen (0x0 = none; 0x1 idle, 0x4 power cap, 0x8 slowdown, 0x20 thermal): ' + ((($rows | Group-Object Throttle | ForEach-Object { "$($_.Name) x$($_.Count)" })) -join ', '))
  $out
}

if ($Summarize) { Summary $Summarize | ForEach-Object { Write-Host $_ }; exit 0 }

$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if (-not $smi) { Write-Host 'nvidia-smi was not found (no NVIDIA driver on this PC, or it is not on the PATH). Nothing was collected.'; exit 3 }
New-Item -ItemType Directory -Force $Out | Out-Null
# Newer drivers renamed the throttle-reason field (clocks_event_reasons); an old name or a new one that this driver does not know is dropped rather than failing the whole run.
$fields = $baseFields
foreach ($name in 'clocks_event_reasons.active', 'clocks_throttle_reasons.active') {
  $probe = (& nvidia-smi "--query-gpu=$name" '--format=csv,noheader' 2>&1) -join ' '
  if ($LASTEXITCODE -eq 0 -and $probe -notmatch 'not a valid|Field|ERROR|Unknown') { $fields = "$baseFields,$name"; break }
}
$csv = Join-Path $Out "$Label-telemetry.csv"
$err = Join-Path $env:TEMP 'mesura-telemetry-stderr.txt'
function GpuProcesses { @((& nvidia-smi -q 2>$null) | ForEach-Object { if ($_ -match '^\s+Name\s+:\s+(.+\.exe)\s*$') { Split-Path $Matches[1] -Leaf } } | Sort-Object -Unique) -join ', ' }   # what holds a GPU context (2026-11-04: an idle reading is only comparable if it says what was running)
$procsStart = GpuProcesses
Write-Host "Sampling the GPU every $IntervalMs ms for $Seconds s ($Label). Leave the PC as it is for this test."
$p = Start-Process -FilePath $smi.Source -ArgumentList @("--query-gpu=$fields", '--format=csv,noheader,nounits', "-lms", "$IntervalMs") -RedirectStandardOutput $csv -RedirectStandardError $err -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit($Seconds * 1000)) { try { $p.Kill() } catch { } }
Start-Sleep -Milliseconds 300
$procsEnd = GpuProcesses
$summary = Summary $csv
$txt = Join-Path $Out "$Label-telemetry.txt"
$header = @("GPU telemetry '$Label', $Seconds s, $(Get-Date -Format s) (read only)", "Driver: $((& nvidia-smi --query-gpu=driver_version,name --format=csv,noheader) -join ' / ')", "Programs with a GPU context at the start: $procsStart", "Programs with a GPU context at the end: $procsEnd", '') + $summary
[IO.File]::WriteAllLines($txt, $header, (New-Object System.Text.UTF8Encoding($true)))
$header | ForEach-Object { Write-Host $_ }
Write-Host "Wrote $csv and $txt"
