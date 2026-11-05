# Network latency measurement and A/B comparison for adapter settings (the network counterpart of Measure-FrameTimes.ps1). It changes no setting and sends only ICMP echo requests to the host you name
# (by default your own router, the default gateway, so the measurement stays on your LAN and isolates the adapter, its driver and the link from the internet). Modes:
#
#   -Capture      sends -Count echo requests, one every -IntervalMs, times each with a high-resolution stopwatch and writes <Label>-<Run>.netlat.json (round-trip times, loss, the adapter's state)
#   -Compare <folder>   reads the *.netlat.json files of that folder, groups them by label and says whether label B differs from label A by more than the run-to-run noise
#
# The rule that makes a result believable (the same as docs\gpu-measurement-g7.md): run A B A B A B (for example preset off, on, off, on, off, on), nothing else changed, the same time of day, the PC idle
# otherwise, and the first run thrown away as a warm-up. An effect counts ONLY when the difference between the medians of the two labels is larger than the spread (max minus min) of either label.
# Otherwise the answer is "no measurable difference on this PC", which is a valid result.
#
# What this can and cannot see: a ping measures the round trip of one small packet through your adapter, the cable or radio, and the router. Interrupt moderation, EEE and power saving act on the order of
# microseconds to a millisecond, which is at the edge of what a software stopwatch resolves (the timer is precise to about 0.1 ms; each probe also includes the Windows ping call). That is why a run needs
# many probes (default 1000) and why the verdict depends on the spread over repeated runs, not on one number. It does not measure throughput, game frame times or bufferbloat under load.
#
#   powershell -ExecutionPolicy Bypass -File Measure-NetworkLatency.ps1 -Capture -Label preset-off -Run 1 -Out C:\Captures
#   powershell -ExecutionPolicy Bypass -File Measure-NetworkLatency.ps1 -Capture -Label preset-on  -Run 1 -Out C:\Captures
#   powershell -ExecutionPolicy Bypass -File Measure-NetworkLatency.ps1 -Compare C:\Captures -A preset-off -B preset-on
param(
  [switch]$Capture,
  [string]$Compare = '',
  [string]$Target = '',
  [int]$Count = 1000,
  [int]$IntervalMs = 20,
  [int]$TimeoutMs = 1000,
  [string]$Label = '',
  [int]$Run = 1,
  [string]$Out = '',
  [string]$A = '',
  [string]$B = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }

function Percentile([double[]]$sorted, [double]$p) {
  if ($sorted.Count -eq 0) { return $null }
  $rank = $p / 100.0 * ($sorted.Count - 1); $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
  if ($lo -eq $hi) { return $sorted[[int]$lo] }
  $sorted[[int]$lo] + ($sorted[[int]$hi] - $sorted[[int]$lo]) * ($rank - $lo)
}

function Summarize([double[]]$rtts, [int]$lost) {
  $sorted = @($rtts | Sort-Object)
  $diffs = for ($i = 1; $i -lt $rtts.Count; $i++) { [math]::Abs($rtts[$i] - $rtts[$i - 1]) }
  [ordered]@{
    answered = $rtts.Count; lost = $lost; lossPercent = [math]::Round(100.0 * $lost / [math]::Max(1, $rtts.Count + $lost), 3)
    minMs = [math]::Round($sorted[0], 3); medianMs = [math]::Round((Percentile $sorted 50), 3); p95Ms = [math]::Round((Percentile $sorted 95), 3)
    p99Ms = [math]::Round((Percentile $sorted 99), 3); maxMs = [math]::Round($sorted[-1], 3)
    jitterMs = if ($diffs) { [math]::Round(($diffs | Measure-Object -Average).Average, 3) } else { 0 }
  }
}

if ($Capture) {
  if (-not $Label) { throw 'Give -Label (for example preset-off).' }
  if (-not $Target) {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1
    if (-not $route) { throw 'No default gateway was found; pass -Target.' }
    $Target = $route.NextHop
  }
  # The adapter the route to the target leaves through, and the settings of it that matter here, so a result says what state it was taken in.
  $routeToTarget = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1
  $adapter = if ($routeToTarget) { Get-NetAdapter -InterfaceIndex $routeToTarget.InterfaceIndex -ErrorAction SilentlyContinue } else { $null }
  $state = [ordered]@{}
  if ($adapter) {
    $state.name = $adapter.Name; $state.description = $adapter.InterfaceDescription; $state.driver = $adapter.DriverVersionString; $state.linkSpeed = $adapter.LinkSpeed; $state.guid = $adapter.InterfaceGuid
    $props = [ordered]@{}
    foreach ($p in Get-NetAdapterAdvancedProperty -Name $adapter.Name -ErrorAction SilentlyContinue) {
      if ($p.RegistryKeyword -in '*InterruptModeration', '*EEE', 'EnableGreenEthernet', 'ULPMode', 'ReduceSpeedOnPowerDown', 'PowerSavingMode', '*ReceiveBuffers', 'MIMOPowerSaveMode', 'RoamAggressiveness', 'RoamingPreferredBandType', 'bLeisurePs') { $props[$p.RegistryKeyword] = $p.DisplayValue }
    }
    $state.properties = $props
  }
  Write-Host "Capture $Label run $Run : $Count probes to $Target every $IntervalMs ms (about $([math]::Round($Count * $IntervalMs / 1000.0)) s). Leave the PC idle." -ForegroundColor Cyan
  $ping = New-Object System.Net.NetworkInformation.Ping
  $buffer = [byte[]](1..32)
  $options = New-Object System.Net.NetworkInformation.PingOptions(64, $true)
  [void]$ping.Send($Target, $TimeoutMs, $buffer, $options)   # one throw-away probe warms the path (ARP, neighbour cache)
  $rtts = New-Object System.Collections.Generic.List[double]; $lost = 0
  $watch = New-Object System.Diagnostics.Stopwatch
  for ($i = 0; $i -lt $Count; $i++) {
    $watch.Restart()
    $reply = $ping.Send($Target, $TimeoutMs, $buffer, $options)
    $watch.Stop()
    if ($reply.Status -eq 'Success') { $rtts.Add($watch.Elapsed.TotalMilliseconds) } else { $lost++ }
    $sleep = $IntervalMs - [int]$watch.Elapsed.TotalMilliseconds
    if ($sleep -gt 0) { Start-Sleep -Milliseconds $sleep }
  }
  if ($rtts.Count -eq 0) { throw "No reply from $Target at all; nothing to summarise." }
  $summary = Summarize $rtts.ToArray() $lost
  $record = [ordered]@{ label = $Label; run = $Run; target = $Target; time = (Get-Date -Format s); count = $Count; intervalMs = $IntervalMs; adapter = $state; summary = $summary; rttMs = @($rtts | ForEach-Object { [math]::Round($_, 3) }) }
  $file = Join-Path $Out "$Label-$Run.netlat.json"
  $record | ConvertTo-Json -Depth 6 | Set-Content -Path $file -Encoding UTF8
  Write-Host ("median {0} ms, p95 {1}, p99 {2}, max {3}, jitter {4}, lost {5} of {6}" -f $summary.medianMs, $summary.p95Ms, $summary.p99Ms, $summary.maxMs, $summary.jitterMs, $lost, $Count)
  Write-Host "Wrote $file"
  exit 0
}

if ($Compare) {
  if (-not $A -or -not $B) { throw 'Give -A and -B (the two labels to compare).' }
  $files = Get-ChildItem $Compare -Filter *.netlat.json
  $runs = $files | ForEach-Object { Get-Content -Raw -Encoding UTF8 $_.FullName | ConvertFrom-Json }
  $ga = @($runs | Where-Object { $_.label -eq $A }); $gb = @($runs | Where-Object { $_.label -eq $B })
  Write-Host "Label $A : $($ga.Count) run(s); label $B : $($gb.Count) run(s). The first run of each label is a warm-up and should be left out by deleting its file."
  if ($ga.Count -lt 3 -or $gb.Count -lt 3) { Write-Host 'WARNING: fewer than 3 runs of a label cannot show a noise band, so no verdict is given.' }
  # The runs must alternate (A B A B A B): a link drifts over minutes (Wi-Fi especially), and when all of one label precede all of the other, drift looks like an effect.
  $order = @($runs | Where-Object { $_.label -eq $A -or $_.label -eq $B } | Sort-Object { [datetime]$_.time } | ForEach-Object { $_.label })
  $switches = 0; for ($i = 1; $i -lt $order.Count; $i++) { if ($order[$i] -ne $order[$i - 1]) { $switches++ } }
  $needed = 2 * [math]::Min($ga.Count, $gb.Count) - 1
  $interleaved = $switches -ge $needed
  if (-not $interleaved) { Write-Host "WARNING: the runs were taken in the order $($order -join ' '), which switches label $switches time(s); at least $needed are needed (A B A B A B). Drift of the link would look like an effect, so no verdict is given." }
  # lower is better for every metric here
  $metrics = @(@('medianMs', 'median round trip (ms)'), @('p99Ms', '99th percentile (ms)'), @('jitterMs', 'jitter: mean change between probes (ms)'), @('lossPercent', 'loss (%)'))
  foreach ($m in $metrics) {
    $va = @($ga | ForEach-Object { [double]$_.summary.($m[0]) }); $vb = @($gb | ForEach-Object { [double]$_.summary.($m[0]) })
    if ($va.Count -eq 0 -or $vb.Count -eq 0) { continue }
    $ma = Percentile @($va | Sort-Object) 50; $mb = Percentile @($vb | Sort-Object) 50
    $spread = [math]::Max(($va | Measure-Object -Maximum).Maximum - ($va | Measure-Object -Minimum).Minimum, ($vb | Measure-Object -Maximum).Maximum - ($vb | Measure-Object -Minimum).Minimum)
    $diff = $mb - $ma
    $verdict = if ($ga.Count -lt 3 -or $gb.Count -lt 3) { 'not enough runs' }
      elseif (-not $interleaved) { 'no verdict: runs not interleaved' }
      elseif ([math]::Abs($diff) -gt $spread) { if ($diff -lt 0) { "B lower (better) by $([math]::Abs($diff).ToString('0.###')) (noise band $($spread.ToString('0.###')))" } else { "B higher (worse) by $($diff.ToString('0.###')) (noise band $($spread.ToString('0.###')))" } }
      else { "no measurable difference (diff $($diff.ToString('0.###')) is inside the noise band $($spread.ToString('0.###')))" }
    Write-Host ("{0,-44} A {1,8} [{2}..{3}]   B {4,8} [{5}..{6}]   {7}" -f $m[1], $ma.ToString('0.###'), ($va | Measure-Object -Minimum).Minimum.ToString('0.###'), ($va | Measure-Object -Maximum).Maximum.ToString('0.###'), $mb.ToString('0.###'), ($vb | Measure-Object -Minimum).Minimum.ToString('0.###'), ($vb | Measure-Object -Maximum).Maximum.ToString('0.###'), $verdict)
  }
  Write-Host 'The values in brackets are the lowest and highest run of that label; their width is the noise band. A result that stays inside it is "no measurable difference on this PC".'
  exit 0
}

Write-Host 'Choose a mode: -Capture or -Compare <folder>. Read the header of this script.'
