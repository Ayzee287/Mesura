# Frame-time measurement and A/B comparison for GPU settings (level 0 of docs/gpu-measurement-g7.md). It changes no setting. Three modes:
#
#   -Capture   runs Intel's open-source PresentMon (you supply the exe) against a running game for N seconds and summarises the file it writes
#   -Summarize reads a PresentMon CSV you already have and writes <file>.summary.json beside it
#   -Compare <folder>  reads the *.summary.json files of that folder, groups them by label, and says whether label B differs from label A by more than the run-to-run noise
#
# The rule that makes a result believable: run A B A B A B (for example HAGS off, on, off, on, off, on), the same scene each time, a warm-up run thrown away, and an effect counts ONLY when the difference
# between the medians of the two labels is larger than the spread (max minus min) of either label. Otherwise the answer is "no measurable difference on this PC", which is a valid result.
#
#   powershell -ExecutionPolicy Bypass -File Measure-FrameTimes.ps1 -Capture -PresentMon C:\Tools\PresentMon.exe -Process game.exe -Seconds 120 -Label hags-off -Run 1 -Out C:\Captures
#   powershell -ExecutionPolicy Bypass -File Measure-FrameTimes.ps1 -Summarize C:\Captures\hags-off-1.csv
#   powershell -ExecutionPolicy Bypass -File Measure-FrameTimes.ps1 -Compare C:\Captures -A hags-off -B hags-on
param(
  [switch]$Capture,
  [string]$Summarize = '',
  [string]$Compare = '',
  [string]$PresentMon = '',
  [string]$Process = '',
  [int]$Seconds = 120,
  [string]$Label = '',
  [int]$Run = 1,
  [string]$Out = '',
  [string]$A = '',
  [string]$B = '',
  [switch]$LegacyArguments   # PresentMon 1.x uses single-dash options (-process_name, -timed, -output_file); 2.x uses double dashes
)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }

function Percentile([double[]]$sorted, [double]$p) {
  if ($sorted.Count -eq 0) { return $null }
  $rank = $p / 100.0 * ($sorted.Count - 1); $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
  if ($lo -eq $hi) { return $sorted[[int]$lo] }
  $sorted[[int]$lo] + ($sorted[[int]$hi] - $sorted[[int]$lo]) * ($rank - $lo)
}

function Summarize-Csv([string]$csv, [string]$process, [string]$label) {
  $table = Import-Csv $csv
  if (-not $table) { throw "No rows in $csv" }
  $names = $table[0].PSObject.Properties.Name
  # PresentMon 2.x names the column FrameTime; 1.x MsBetweenPresents. Both are milliseconds between presented frames.
  $col = $names | Where-Object { $_ -ieq 'FrameTime' } | Select-Object -First 1
  if (-not $col) { $col = $names | Where-Object { $_ -ieq 'MsBetweenPresents' } | Select-Object -First 1 }
  if (-not $col) { throw "$csv has no FrameTime or MsBetweenPresents column (columns: $($names -join ', '))" }
  $appCol = $names | Where-Object { $_ -ieq 'Application' } | Select-Object -First 1
  $rows = $table
  if ($process -and $appCol) { $rows = @($table | Where-Object { $_.$appCol -ieq $process }) }
  elseif ($appCol) {   # no process named: the application with the most frames
    $top = $table | Group-Object $appCol | Sort-Object Count -Descending | Select-Object -First 1
    $rows = @($top.Group); $process = $top.Name
  }
  $ft = @($rows | ForEach-Object { $v = 0.0; if ([double]::TryParse($_.$col, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v) -and $v -gt 0) { $v } })
  if ($ft.Count -lt 30) { throw "Only $($ft.Count) usable frames in $csv for '$process': too few to say anything" }
  $sorted = @($ft | Sort-Object)
  $mean = ($ft | Measure-Object -Average).Average
  $median = Percentile $sorted 50
  $p99 = Percentile $sorted 99; $p999 = Percentile $sorted 99.9
  [pscustomobject][ordered]@{
    label = $label; file = (Split-Path $csv -Leaf); application = $process; frames = $ft.Count
    avgFps = [math]::Round(1000.0 / $mean, 2)
    onePercentLowFps = [math]::Round(1000.0 / $p99, 2)          # the frame rate of the 99th-percentile (slowest 1 %) frame time
    pointOnePercentLowFps = [math]::Round(1000.0 / $p999, 2)
    medianFrameMs = [math]::Round($median, 3); p95FrameMs = [math]::Round((Percentile $sorted 95), 3); p99FrameMs = [math]::Round($p99, 3); p999FrameMs = [math]::Round($p999, 3)
    framesOver2xMedian = @($ft | Where-Object { $_ -gt 2 * $median }).Count
    seconds = [math]::Round((($ft | Measure-Object -Sum).Sum) / 1000.0, 1)
  }
}

if ($Capture) {
  if (-not $PresentMon -or -not (Test-Path $PresentMon)) { throw 'Give -PresentMon <path to PresentMon.exe> (Intel PresentMon, MIT licence, from its GitHub releases). Mesura does not bundle it.' }
  if (-not $Process) { throw 'Give -Process <game.exe> (the name shown in Task Manager).' }
  if (-not $Label) { throw 'Give -Label (for example hags-off); use the same label for all runs of one condition.' }
  New-Item -ItemType Directory -Force $Out | Out-Null
  $csv = Join-Path $Out "$Label-$Run.csv"
  $pmArgs = if ($LegacyArguments) { @('-process_name', $Process, '-timed', "$Seconds", '-output_file', $csv, '-terminate_after_timed') } else { @('--process_name', $Process, '--timed', "$Seconds", '--output_file', $csv, '--terminate_after_timed', '--no_console_stats') }
  Write-Host "Capturing $Seconds s of $Process as '$Label' run $Run. Start the scene now; PresentMon needs administrator rights."
  $p = Start-Process -FilePath $PresentMon -ArgumentList $pmArgs -PassThru -Wait
  if ($p.ExitCode -ne 0 -or -not (Test-Path $csv)) { throw "PresentMon ended with exit code $($p.ExitCode) and no file $csv" }
  $Summarize = $csv
}

if ($Summarize) {
  $summary = Summarize-Csv $Summarize $Process $(if ($Label) { $Label } else { [IO.Path]::GetFileNameWithoutExtension($Summarize) -replace '-\d+$', '' })
  $json = [IO.Path]::ChangeExtension($Summarize, 'summary.json')
  $summary | ConvertTo-Json | Set-Content -Path $json -Encoding UTF8
  $summary | Format-List | Out-String | Write-Host
  Write-Host "Wrote $json"
  exit 0
}

if ($Compare) {
  $folder = $Compare
  $all = @(Get-ChildItem $folder -Filter '*.summary.json' | ForEach-Object { Get-Content -Raw $_.FullName | ConvertFrom-Json })
  if (-not $A -or -not $B) { Write-Host "Labels found: $(($all | Select-Object -ExpandProperty label -Unique) -join ', ')"; throw 'Give -A <label> and -B <label>.' }
  $ga = @($all | Where-Object { $_.label -eq $A }); $gb = @($all | Where-Object { $_.label -eq $B })
  if ($ga.Count -lt 3 -or $gb.Count -lt 3) { Write-Host "WARNING: $A has $($ga.Count) run(s) and $B has $($gb.Count); fewer than 3 each cannot show a noise band, so no verdict is given." }
  function Med($v) { $s = @($v | Sort-Object); if ($s.Count % 2) { $s[[int](($s.Count - 1) / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }
  $metrics = @(@('avgFps', 'average FPS', $true), @('onePercentLowFps', '1 % low FPS', $true), @('pointOnePercentLowFps', '0.1 % low FPS', $true), @('p99FrameMs', '99th percentile frame time (ms)', $false), @('framesOver2xMedian', 'frames over 2x the median', $false))
  '{0,-34} {1,-22} {2,-22} {3}' -f 'Metric', "$A (n=$($ga.Count))", "$B (n=$($gb.Count))", 'Verdict' | Write-Host
  foreach ($m in $metrics) {
    $va = @($ga | ForEach-Object { [double]$_.($m[0]) }); $vb = @($gb | ForEach-Object { [double]$_.($m[0]) })
    if (-not $va -or -not $vb) { continue }
    $ma = Med $va; $mb = Med $vb
    $spread = [math]::Max(($va | Measure-Object -Maximum).Maximum - ($va | Measure-Object -Minimum).Minimum, ($vb | Measure-Object -Maximum).Maximum - ($vb | Measure-Object -Minimum).Minimum)
    $diff = $mb - $ma
    $verdict = if ($ga.Count -lt 3 -or $gb.Count -lt 3) { 'not enough runs' } elseif ([math]::Abs($diff) -gt $spread) { $(if (($diff -gt 0) -eq $m[2]) { "B better by $([math]::Abs($diff).ToString('0.##')) (noise band $($spread.ToString('0.##')))" } else { "B worse by $([math]::Abs($diff).ToString('0.##')) (noise band $($spread.ToString('0.##')))" }) } else { "no measurable difference (diff $($diff.ToString('0.##')) is inside the noise band $($spread.ToString('0.##')))" }
    '{0,-34} {1,-22} {2,-22} {3}' -f $m[1], ('{0:0.##} [{1:0.##}-{2:0.##}]' -f $ma, ($va | Measure-Object -Minimum).Minimum, ($va | Measure-Object -Maximum).Maximum), ('{0:0.##} [{1:0.##}-{2:0.##}]' -f $mb, ($vb | Measure-Object -Minimum).Minimum, ($vb | Measure-Object -Maximum).Maximum), $verdict | Write-Host
  }
  Write-Host 'The values in brackets are the lowest and highest run of that label; medians outside them are impossible, so the bracket width is the noise band.'
  exit 0
}

Write-Host 'Choose a mode: -Capture, -Summarize <csv> or -Compare. Read the header of this script.'
exit 2
