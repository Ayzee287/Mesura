# Read-only check of what a Mesura profile run really left on THIS PC. It changes nothing and sends nothing.
#
# What it does, in order:
#   1. finds the newest Mesura run that changed Windows (or the run you name) and reads EVERY value that run recorded straight from Windows' own tools (the registry, powercfg), not through Mesura,
#      and compares it with the value the run says it set;
#   2. asks Mesura for the plan of the level now: nothing should be left to apply (only "waiting for a restart" or "can't tell" may remain);
#   3. reads the active power plan and the values that matter for Maximum performance, and hardware-accelerated GPU scheduling;
#   4. reads the NVIDIA driver profile (through Mesura's NVAPI reader) and says which of Mesura's two NVIDIA profiles, if any, is in force;
#   5. compares a few facts with the baseline taken on the RTX 3080 PC on 2026-11-03 before the profile was applied (nvidia-3080-baseline.json), when that file is beside this script.
# Every line is PASS (a thing that should be true is), FAIL (it is not), or INFO (a fact, nothing to judge). A FAIL is a finding to look at, not proof of a Mesura defect: a value changed by something else
# afterwards, or an operation that waits for a restart, also shows up here.
#
#   powershell -ExecutionPolicy Bypass -File Verify-MesuraApplied.ps1 [-Level maximum-performance] [-Run <run id>] [-Cli <mesura.exe>] [-Out <folder>]
param(
  [string]$Level = 'maximum-performance',
  [string]$Run = '',
  [string]$Cli = '',
  [string]$Out = '',
  [string]$Baseline = ''
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
if (-not $Baseline) {
  # the baseline of the card this PC has (taken before any profile was applied): NVIDIA's if an NVIDIA adapter is present, else the Radeon's
  $hasNvidia = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).Count -gt 0
  $Baseline = Join-Path $PSScriptRoot $(if ($hasNvidia) { 'nvidia-3080-baseline.json' } else { 'amd-radeon-baseline.json' })
}
if (-not $Cli) {
  foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'), (Join-Path $env:ProgramFiles 'Mesura\cli\mesura.exe'))) {
    if (Test-Path $c) { $Cli = $c; break }
  }
}
$lines = New-Object System.Collections.Generic.List[string]
$counts = @{ PASS = 0; FAIL = 0; INFO = 0 }
function Say([string]$kind, [string]$text, [string]$detail = '') {
  $counts[$kind]++
  $line = ('{0,-5} {1}{2}' -f $kind, $text, $(if ($detail) { "  ($detail)" } else { '' }))
  $lines.Add($line); Write-Host $line -ForegroundColor $(switch ($kind) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Gray' } })
}
function Detail([string]$text) { $lines.Add("      $text"); Write-Host "      $text" }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say $(if ($ok) { 'PASS' } else { 'FAIL' }) $what $detail }
function Info([string]$what, [string]$detail = '') { Say 'INFO' $what $detail }

function Mesura([string[]]$arguments) {
  if (-not $Cli) { return $null }
  $err = Join-Path $env:TEMP 'mesura-verify-stderr.txt'
  $text = (& $Cli @arguments 2>$err | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Exit = $LASTEXITCODE }
}

# ---- independent readers (the same ones tools\e2e-profiles.ps1 uses in the Sandbox) -----------------------------------------------------------
function RegRead([string]$hive, [string]$path, [string]$name) {
  $root = if ($hive -eq 'HKLM') { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
  $key = $root.OpenSubKey($path)
  if ($null -eq $key) { return $null }
  try {
    $v = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($v -is [int]) { $v = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$v), 0) }   # a DWORD above 0x7FFFFFFF comes back negative from .NET
    if ($null -eq $v) { $null } else { "$v" }
  } finally { $key.Dispose() }
}
function ActiveScheme { $t = (powercfg /getactivescheme) -join ' '; if ($t -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s*\(([^)]*)\)') { @{ Guid = $Matches[1]; Name = $Matches[2] } } else { @{ Guid = ''; Name = '' } } }
function Schemes { @(powercfg /list | ForEach-Object { if ($_ -match 'GUID: ([0-9a-f-]{36})') { $Matches[1] } }) }
function PowerValue([string]$scheme, [string]$sub, [string]$setting, [string]$side) {
  $text = (powercfg /query $scheme $sub $setting) -join "`n"
  if ($text -match "Current $side Power Setting Index: 0x([0-9a-fA-F]+)") { [string][Convert]::ToUInt32($Matches[1], 16) } else { $null }
}

function Show($v) { if ($null -eq $v) { '(absent)' } else { "'$v'" } }
function HasChanges($op) { $null -ne $op.changes -and @($op.changes | Where-Object { $null -ne $_ }).Count -gt 0 }   # a missing list is NOT a list of one: @($null).Count is 1

Say 'INFO' "Mesura profile verification, $(Get-Date -Format s) (read only)"
$os = Get-CimInstance Win32_OperatingSystem
Info 'Windows' "$($os.Caption) build $($os.BuildNumber)"
foreach ($g in @(Get-CimInstance Win32_VideoController)) { Info 'Display adapter' "$($g.Name), driver $($g.DriverVersion)" }
if ($Cli) { Info 'Mesura command line' "$Cli (file version $((Get-Item $Cli).VersionInfo.FileVersion))" } else { Info 'Mesura command line' 'not found: the plan and NVIDIA parts are skipped; pass -Cli <mesura.exe>' }

# ---- 1. the recorded run, read back from Windows ---------------------------------------------------------------------------------------------
$history = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\history'
$runs = @()
if (Test-Path $history) {
  $runs = @(Get-ChildItem $history -Directory | Sort-Object Name -Descending | ForEach-Object {
    $f = Join-Path $_.FullName 'run.json'
    if (Test-Path $f) { try { [pscustomobject]@{ Id = $_.Name; Run = (Get-Content -Raw -Encoding UTF8 $f | ConvertFrom-Json) } } catch { } }
  })
}
Info "Recorded runs: $($runs.Count)" $(if ($runs.Count) { ($runs | Select-Object -First 6 | ForEach-Object { $_.Id }) -join ', ' } else { "none under $history" })
$chosen = $null
if ($Run) { $chosen = $runs | Where-Object { $_.Id -eq $Run } | Select-Object -First 1 }
else { $chosen = $runs | Where-Object { @($_.Run.windows | Where-Object { HasChanges $_ }).Count -gt 0 } | Select-Object -First 1 }
if (-not $chosen) {
  Info 'No run with recorded Windows changes was found' 'nothing to read back; the plan below still tells what is and is not in place'
} else {
  $runData = $chosen.Run
  $ops = @($runData.windows | Where-Object { HasChanges $_ })
  Info "Run $($chosen.Id)" "$($runData.operation), started $($runData.startedUtc); $($ops.Count) operation(s) changed something"
  $checked = 0; $skipped = 0; $bad = @()
  foreach ($op in $ops) {
    foreach ($c in $op.changes) {
      $want = $c.after
      switch ($c.kind) {
        'Registry' {
          if ($c.registryKind -eq 'Binary') { $skipped++; continue }
          $now = RegRead $c.hive $c.path $c.name
          # an empty string is a value and a missing value is not (2026-11-04: "" and not set compared equal, so the undo of an empty default value could not be seen)
          $ok = if ($null -eq $want) { $null -eq $now } elseif ($null -eq $now) { $false } else { "$now" -eq "$want" -or ($now -match '^\d+$' -and "$want" -match '^\d+$' -and [uint64]$now -eq [uint64]$want) }
          $checked++; if (-not $ok) { $bad += "$($op.operationId): $($c.target) is $(Show $now), the run set $(Show $want)" }
        }
        'PowerSetting' {
          $side = if ($c.dc) { 'DC' } else { 'AC' }
          if (-not ((Schemes) -contains $c.scheme)) { $skipped++; $bad += "$($op.operationId): the power plan $($c.scheme) no longer exists"; continue }
          $now = PowerValue $c.scheme $c.path $c.name $side
          $checked++; if ("$now" -ne "$want") { $bad += "$($op.operationId): $($c.target) ($side) is '$now', the run set '$want'" }
        }
        'PowerScheme' {
          $checked++; $now = (ActiveScheme).Guid; if ($now -ne "$want") { $bad += "$($op.operationId): the active power plan is '$now', the run activated '$want'" }
        }
        default { $skipped++ }
      }
    }
  }
  Check "$checked value(s) the run recorded read back from Windows still equal what it set" ($bad.Count -eq 0) "$skipped not read independently (binary values, system tools)"
  foreach ($b in ($bad | Select-Object -First 12)) { Detail $b }
  $failedOps = @($runData.windows | Where-Object { $_.ok -eq $false })
  if ($failedOps.Count) { Info "$($failedOps.Count) operation(s) of the run did not succeed" (($failedOps | ForEach-Object { $_.operationId }) -join ', ') }
}

# ---- 2. the plan now ------------------------------------------------------------------------------------------------------------------------
if ($Cli) {
  $plan = Mesura @('windows', 'plan', '--profile', $Level, '--json')
  $json = $null
  if ($plan) { try { $json = ($plan.Text -split "`n" | Where-Object { $_ -notmatch '^Windows catalog:' }) -join "`n" | ConvertFrom-Json } catch { } }
  if (-not $json) { Check "mesura windows plan --profile $Level answered with JSON" $false }
  else {
    $entries = @($json.groups | Where-Object { $_.enabled } | ForEach-Object { $_.entries })
    $by = $entries | Group-Object state | ForEach-Object { "$($_.Count) $($_.Name)" }
    Info "Plan of '$Level' now" ($by -join ', ')
    # Optional entries are shown by the plan as ToApply but are not part of the level unless the user ticks them: they are not "left to apply".
    $left = @($entries | Where-Object { $_.state -eq 'ToApply' -and $_.role -ne 'Optional' })
    $optionalOff = @($entries | Where-Object { $_.state -eq 'ToApply' -and $_.role -eq 'Optional' })
    if ($optionalOff.Count) { Info "$($optionalOff.Count) optional entr(y/ies) not ticked, so not applied by this level" (($optionalOff | ForEach-Object { $_.operation }) -join ', ') }
    Check 'nothing is left to apply for this level' ($left.Count -eq 0) "$($left.Count) still to apply"
    foreach ($e in ($left | Select-Object -First 10)) { Detail "still to apply: $($e.operation)" }
    foreach ($e in @($entries | Where-Object { $_.state -in 'Waiting', 'CantTell' } | Select-Object -First 10)) { Detail "$($e.state): $($e.operation)" }
  }
}

# ---- 3. power plan and GPU scheduling -------------------------------------------------------------------------------------------------------
$active = ActiveScheme
Info 'Active power plan' "$($active.Name) $($active.Guid)"
$table = @(
  @('Processor minimum state (%)', 'SUB_PROCESSOR', 'PROCTHROTTLEMIN'), @('Processor maximum state (%)', 'SUB_PROCESSOR', 'PROCTHROTTLEMAX'),
  @('Energy performance preference', 'SUB_PROCESSOR', 'PERFEPP'), @('Boost mode', 'SUB_PROCESSOR', 'PERFBOOSTMODE'),
  @('PCI Express link state power management', 'SUB_PCIEXPRESS', 'ASPM'), @('Turn off hard disk after (s)', 'SUB_DISK', 'DISKIDLE'))
foreach ($t in $table) { $v = PowerValue 'SCHEME_CURRENT' $t[1] $t[2] 'AC'; Info "  $($t[0]) (on mains)" $(if ($null -ne $v) { $v } else { 'not registered on this PC' }) }
if ($active.Name -match 'Maximum Performance Mesura') {
  Check 'Maximum Performance Mesura is active and its processor minimum state is 100 %' ((PowerValue 'SCHEME_CURRENT' 'SUB_PROCESSOR' 'PROCTHROTTLEMIN' 'AC') -eq '100')
}
$hags = RegRead 'HKLM' 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode'
Info 'Hardware-accelerated GPU scheduling (HwSchMode)' $(if ($null -eq $hags) { 'not set (driver default); Mesura treats it as applied only when Windows reports it in effect' } else { "$hags (2 = on, 1 = off)" })
if ($Cli) { $h = Mesura @('windows', 'show', '--id', 'gpu.hardware-scheduling-on'); if ($h -and $h.Text -match 'State here: ([^\.\n]*)') { Info 'Mesura reads GPU scheduling as' $Matches[1].Trim() } }

# ---- 4. the NVIDIA driver profile ------------------------------------------------------------------------------------------------------------
$nv = $null
if ($Cli) { $r = Mesura @('gpu', 'nvidia', '--json'); if ($r -and $r.Exit -eq 0) { try { $nv = $r.Text | ConvertFrom-Json } catch { } } }
if ($nv) {
  Info 'NVIDIA driver' "$($nv.DriverVersion) ($($nv.Branch)), GPU: $(($nv.Gpus | ForEach-Object { $_.Name }) -join ', ')"
  $base = $nv.Profiles | Where-Object { $_.Kind -eq 'base' } | Select-Object -First 1
  $held = @($base.Settings | Where-Object { $_.Set -eq $true })
  Info "Base Profile holds $($held.Count) of the key settings read" $(($held | ForEach-Object { "$($_.Label)=0x$('{0:X}' -f [uint32]$_.Value)" }) -join '; ')
  # Mesura's two NVIDIA profiles (src\Mesura.Core\Nvidia): responsive = power management 1, VRR requested 2, G-SYNC globally 2; gaming adds V-Sync force off, pre-rendered frames 1, shader cache maximum.
  $responsive = [ordered]@{ '0x1057EB71' = 1; '0x1094F1F7' = 2; '0x1194F158' = 2 }
  $gaming = [ordered]@{ '0x00A879CF' = 0x08416747; '0x007BA09E' = 1; '0x00AC8497' = 4294967295 }
  function ProfileIn([System.Collections.Specialized.OrderedDictionary]$want) {
    foreach ($k in $want.Keys) { $s = $base.Settings | Where-Object { $_.Id -eq $k } | Select-Object -First 1; if (-not $s -or $s.Set -ne $true -or [uint32]$s.Value -ne [uint32]$want[$k]) { return $false } }
    $true
  }
  if (ProfileIn $responsive) { Info 'The mesura-responsive NVIDIA profile is in force (power management, G-SYNC settings as written)' } elseif ($held.Count -eq 0) { Info 'No NVIDIA profile is in force: the Base Profile is empty, every key setting is at the driver default' } else { Info 'The Base Profile is not exactly mesura-responsive' 'see the held settings above' }
  if (ProfileIn $gaming) { Info 'The extra settings of mesura-gaming are in force too' }
  foreach ($d in @($nv.Gpus | ForEach-Object { $_.Displays })) { Info "Display $($d.Index) ($($d.GdiName))" "variable refresh possible=$($d.VrrPossible) enabled=$($d.VrrEnabled)" }
}
# ---- 4b. an AMD driver: Mesura's ADLX reader (read only) ---------------------------------------------------------------------------------------
$amd = $null
if ($Cli) { $r = Mesura @('gpu', 'amd', '--json'); if ($r -and $r.Exit -eq 0) { try { $amd = $r.Text | ConvertFrom-Json } catch { } } }
if ($amd -and $amd.Gpus) {
  function OnOff($f) { if ($null -eq $f.Supported) { 'unknown' } elseif (-not $f.Supported) { 'not supported' } elseif ($null -eq $f.Enabled) { 'supported, state unknown' } elseif ($f.Enabled) { 'on' } else { 'off' } }
  foreach ($g in $amd.Gpus) {
    Info "AMD GPU $($g.Index): $($g.Name)" "ADLX $($amd.DllVersion); VRAM $($g.VramMb) MB"
    Info '  Anti-Lag' "$(OnOff $g.AntiLag), level $($g.AntiLagLevel) (0 = the original; Mesura never enables Anti-Lag Next)"
    Info '  Chill / Boost / Enhanced Sync / frame rate target / Image Sharpening' "$(OnOff $g.Chill) / $(OnOff $g.Boost) / $(OnOff $g.EnhancedSync) / $(OnOff $g.FrameRateTarget) / $(OnOff $g.ImageSharpening)"
    Info '  SmartAccess Memory (the driver switch)' (OnOff $g.SmartAccessMemory)
    Info '  V-Sync mode' "$($g.VerticalRefreshMode)"
    if ($g.Tuning) { Info '  Tuning at factory settings' "$($g.Tuning.AtFactory) (Mesura changes no tuning value; false means Adrenalin or another tool did)" }
    if ($g.Metrics) { Info '  Now' "core $($g.Metrics.ClockMhz) MHz, memory $($g.Metrics.MemoryClockMhz) MHz, board power $($g.Metrics.BoardPowerW) W, $($g.Metrics.TemperatureC) C, fan $($g.Metrics.FanRpm) rpm" }
  }
  foreach ($d in @($amd.Displays | Where-Object { $_.Index -ge 0 })) { Info "AMD display $($d.Index): $($d.Name)" "$($d.NativeWidth)x$($d.NativeHeight) at $($d.RefreshHz) Hz, FreeSync $(OnOff $d.FreeSync)" }
  # Changes Mesura itself made to AMD switches (Anti-Lag, SmartAccess Memory, FreeSync) are recorded under Setup\gpu: list them so a switch that differs from the baseline can be explained.
  $gpuChanges = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\gpu'
  $recorded = @(Get-ChildItem $gpuChanges -Filter '*.json' -ErrorAction SilentlyContinue)
  Info "GPU switch changes recorded by Mesura: $($recorded.Count)" $(if ($recorded.Count) { ($recorded | Sort-Object Name -Descending | Select-Object -First 5 | ForEach-Object { $_.BaseName }) -join ', ' } else { 'none: no AMD switch was changed by Mesura' })
}

$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($smi) {
  $q = (& nvidia-smi --query-gpu=driver_version,pstate,clocks.gr,clocks.mem,power.draw,power.limit,temperature.gpu,pcie.link.gen.current,fan.speed --format=csv,noheader,nounits) -split ','
  if ($q.Count -ge 9) { Info 'nvidia-smi now' "driver $($q[0].Trim()), $($q[1].Trim()), core $($q[2].Trim()) MHz, memory $($q[3].Trim()) MHz, $($q[4].Trim()) W of $($q[5].Trim()) W, $($q[6].Trim()) C, PCIe gen $($q[7].Trim()), fan $($q[8].Trim()) %" }
}

# ---- 5. against the baseline taken before the profile was applied ----------------------------------------------------------------------------
if (Test-Path $Baseline) {
  $b = Get-Content -Raw $Baseline | ConvertFrom-Json
  Info "Baseline ($($b.taken), $($b.pc))" 'what this PC looked like before any profile or GPU change'
  if ($b.summary) { Info '  then' $b.summary }
  else { Info '  then' "HwSchMode $(if ($null -eq $b.hwSchMode) { 'not set' } else { $b.hwSchMode }); Game Mode $($b.gameMode); power plan $($b.powerPlan); Base Profile settings held: $($b.baseProfileSettingsHeld); driver $($b.driver); idle: $($b.idle.pstate), $($b.idle.coreMhz)/$($b.idle.memoryMhz) MHz, $($b.idle.powerW) W" }
  Info '  now' "HwSchMode $(if ($null -eq $hags) { 'not set' } else { $hags }); power plan $($active.Name)$(if ($nv) { '; Base Profile settings held: ' + @($base.Settings | Where-Object { $_.Set -eq $true }).Count })"
  if ($amd -and $amd.Gpus -and $b.features) {
    $g = $amd.Gpus[0]
    $pairs = @(@('Anti-Lag', $b.features.antiLag, $g.AntiLag.Enabled), @('Chill', $b.features.chill, $g.Chill.Enabled), @('Boost', $b.features.boost, $g.Boost.Enabled), @('Enhanced Sync', $b.features.enhancedSync, $g.EnhancedSync.Enabled),
      @('Frame rate target', $b.features.frameRateTarget, $g.FrameRateTarget.Enabled), @('Image Sharpening', $b.features.imageSharpening, $g.ImageSharpening.Enabled), @('SmartAccess Memory', $b.features.smartAccessMemory, $g.SmartAccessMemory.Enabled))
    $changed = @($pairs | Where-Object { $null -ne $_[2] -and $_[1] -ne $_[2] } | ForEach-Object { "$($_[0]): $($_[1]) -> $($_[2])" })
    Info 'AMD switches that differ from the baseline' $(if ($changed.Count) { $changed -join '; ' } else { 'none' })
  }
}

$summary = "PASS $($counts.PASS), FAIL $($counts.FAIL), INFO $($counts.INFO)"
Say 'INFO' "Summary: $summary"
$file = Join-Path $Out ("mesura-verify-{0}.txt" -f (Get-Date -Format yyyyMMdd-HHmmss))
[IO.File]::WriteAllLines($file, $lines, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Wrote $file"
if ($counts.FAIL -gt 0) { exit 1 } else { exit 0 }
