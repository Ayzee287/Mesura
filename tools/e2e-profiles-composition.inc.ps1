# Dot-sourced by e2e-profiles.ps1 inside the per-level loop: the owner's composition decisions of 2026-11-04 (docs/profile-composition-0.6.0.md) checked against what THIS PC says, so a Sandbox without a
# Radeon and with a virtual network adapter must plan none of the hardware-bound entries, and a PC that has the hardware must plan them.
# Uses: $level, $plan0, $ops0 (the level's planned operation ids) and the Check/Info helpers of the caller.
$entries0 = @($plan0.groups | ForEach-Object { $_.entries })
$presetMakers = @{
  'network.preset-realtek-ethernet' = @('Realtek', 'Ethernet'); 'network.preset-intel-ethernet' = @('Intel', 'Ethernet'); 'network.preset-killer-ethernet' = @('Killer', 'Ethernet')
  'network.preset-intel-wifi' = @('Intel|Killer|Rivet', 'Wi-Fi|Wireless|802\.11'); 'network.preset-realtek-wifi' = @('Realtek', 'Wi-Fi|Wireless|802\.11')
}
Check "[$level] the five network adapter presets are entries of the level" (@($entries0 | Where-Object { $_.operation -like 'network.preset-*' }).Count -eq 5)
$physicalAdapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)
Info "[$level] physical network adapters Windows lists" (($physicalAdapters | ForEach-Object { $_.InterfaceDescription }) -join '; ')
foreach ($id in $presetMakers.Keys) {
  $maker = $presetMakers[$id][0]; $kind = $presetMakers[$id][1]
  $fits = @($physicalAdapters | Where-Object { $_.InterfaceDescription -match $maker -and ($_.InterfaceDescription -match $presetMakers[$id][1] -or $kind -eq 'Ethernet') }).Count -gt 0
  $planned = $ops0 -contains $id
  Check "[$level] $id is planned only where a physical adapter of its maker exists" ((-not $planned) -or $fits) "planned=$planned, adapter of the maker present=$fits"
  $entry = $entries0 | Where-Object { $_.operation -eq $id } | Select-Object -First 1
  if (-not $planned) { Check "[$level] $id not planned: it says why" ($entry.state -in 'Unsupported', 'CantTell', 'Applied' -and -not [string]::IsNullOrWhiteSpace($entry.reason)) "$($entry.state) [$($entry.reason)]" }
}
$amd = @(Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -match 'VEN_1002' }).Count -gt 0
$antiLag = $entries0 | Where-Object { $_.operation -eq 'gpu.amd-anti-lag-on' } | Select-Object -First 1
if ($level -eq 'maximum-performance') {
  Check "[$level] Anti-Lag is an entry of Maximum performance" ($null -ne $antiLag)
  if (-not $amd) { Check "[$level] no AMD adapter: Anti-Lag is not planned and says why" (($ops0 -notcontains 'gpu.amd-anti-lag-on') -and $antiLag.state -in 'Unsupported', 'CantTell' -and $antiLag.reason) "$($antiLag.state) [$($antiLag.reason)]" }
  else { Info "[$level] an AMD adapter is present" "Anti-Lag state $($antiLag.state) [$($antiLag.reason)]" }
  $hags = $entries0 | Where-Object { $_.operation -eq 'gpu.hardware-scheduling-on' } | Select-Object -First 1
  Check "[$level] hardware-accelerated GPU scheduling is offered, not applied" (($null -ne $hags) -and $hags.role -eq 'Optional' -and $ops0 -notcontains 'gpu.hardware-scheduling-on') "role $($hags.role)"
}
else {
  Check "[$level] Anti-Lag and GPU scheduling are not entries of Mesura Reference" ($null -eq $antiLag -and $null -eq ($entries0 | Where-Object { $_.operation -eq 'gpu.hardware-scheduling-on' }))
}
