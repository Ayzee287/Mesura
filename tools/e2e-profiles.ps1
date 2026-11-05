# Real, elevated check of Mesura's optimization levels (0.6) on a TEST PC or Windows Sandbox: the hardware profile is compared with what Windows says, each level is planned for the PC,
# applied through the same operations the app applies, every value it recorded is read back INDEPENDENTLY (the registry, powercfg: never Mesura's own reading), planned again (nothing
# it applied may still be waiting to be applied), applied a second time (nothing may change), reverted, and every value is read a third time and must be what it was before.
#
# It changes dozens of Windows settings, the active power plan and creates and deletes power schemes. It puts everything back by reverting the run, also when a step fails (it tries
# once more and says so). Run it ONLY in Windows Sandbox (tools\sandbox\Start-MesuraSandbox.ps1 -Scenario profiles) or on a PC you can afford to repair by hand.
#
#   Run from an ELEVATED terminal:
#   powershell -ExecutionPolicy Bypass -File tools\e2e-profiles.ps1 -Cli <mesura.exe> [-Levels recommended-gaming,maximum-performance] [-Report <file>]
param(
  [Parameter(Mandatory = $true)][string]$Cli,
  [string[]]$Levels = @('recommended-gaming', 'maximum-performance'),
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$Levels = @($Levels | ForEach-Object { $_ -split ',' })
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated terminal.' }
$Cli = (Resolve-Path $Cli).Path
function Say([string]$line) { Write-Host $line; if ($Report) { $line | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$name, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' })) }
function Info([string]$name, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f 'INFO', $name, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

function Mesura([string[]]$arguments) {
  $ErrorActionPreference = 'Continue'
  $text = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Run = $(if ($text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }); Exit = $LASTEXITCODE }
}
function Plan([string]$level, [string[]]$more = @()) {
  $r = Mesura (@('windows', 'plan', '--profile', $level, '--json') + $more)
  $json = ($r.Text -split "`n" | Where-Object { $_ -notmatch '^Windows catalog:' }) -join "`n"
  $json | ConvertFrom-Json
}
function RunRecord([string]$id) { Get-Content -Raw -Encoding UTF8 (Join-Path $env:LOCALAPPDATA "Mesura\Setup\history\$id\run.json") | ConvertFrom-Json }

# --- independent readers ------------------------------------------------------------------------------------------------------------------
function RegRead([string]$hive, [string]$path, [string]$name) {
  $root = if ($hive -eq 'HKLM') { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
  $key = $root.OpenSubKey($path)
  if ($null -eq $key) { return $null }
  try {
    $v = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    # a DWORD above 0x7FFFFFFF comes back from .NET as a negative Int32 (NtfsDisableLastAccessUpdate = 0x80000003): read it as the unsigned number the registry holds
    if ($v -is [int]) { $v = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$v), 0) }
    if ($null -eq $v) { $null } else { "$v" }
  } finally { $key.Dispose() }
}
function Active { if ((powercfg /getactivescheme) -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') { $Matches[1] } else { '' } }
function Schemes { @(powercfg /list | ForEach-Object { if ($_ -match 'GUID: ([0-9a-f-]{36})') { $Matches[1] } }) }
function PowerValue([string]$scheme, [string]$sub, [string]$setting, [string]$side) {
  $text = (powercfg /query $scheme $sub $setting) -join "`n"
  if ($text -match "Current $side Power Setting Index: 0x([0-9a-fA-F]+)") { [string][Convert]::ToUInt32($Matches[1], 16) } else { $null }
}

# What Windows itself says about every value a run recorded: 'now' is the value after (apply) or before (revert) the run.
function Verify-Changes($run, [string]$which, [string]$label) {
  $checked = 0; $bad = @(); $skipped = 0
  foreach ($op in $run.windows) {
    foreach ($c in $op.changes) {
      $want = if ($which -eq 'after') { $c.after } else { $c.before }
      switch ($c.kind) {
        'Registry' {
          if ($c.registryKind -eq 'Binary') { $skipped++; continue }
          $now = RegRead $c.hive $c.path $c.name
          $ok = if ($null -eq $want) { $null -eq $now } else { "$now" -eq "$want" -or ($now -match '^\d+$' -and $want -match '^\d+$' -and [uint64]$now -eq [uint64]$want) }
          $checked++; if (-not $ok) { $bad += "$($op.operationId): $($c.target) is '$now', expected '$want'" }
        }
        'PowerSetting' {
          $side = if ($c.dc) { 'DC' } else { 'AC' }
          $now = PowerValue $c.scheme $c.path $c.name $side
          if ($which -eq 'before' -and -not ((Schemes) -contains $c.scheme)) { $skipped++; continue }   # the copy is gone after the revert: nothing left to read
          $checked++; if ("$now" -ne "$want") { $bad += "$($op.operationId): $($c.target) ($side) is '$now', expected '$want'" }
        }
        'PowerScheme' {
          $checked++; $now = Active; if ($now -ne "$want") { $bad += "$($op.operationId): active power scheme is '$now', expected '$want'" }
        }
        default { $skipped++ }
      }
    }
  }
  Check "[$label] $checked recorded values read back independently match ($which)" ($bad.Count -eq 0) "$skipped not read independently (binary, system tools, scheme copies)"
  foreach ($b in ($bad | Select-Object -First 8)) { Say "      $b" }
}

# --- 1. the hardware profile against what Windows says ------------------------------------------------------------------------------------------
Say "== mesura hardware vs Windows ($(Get-Date -Format s), build $([Environment]::OSVersion.Version.Build))"
$hwText = (Mesura @('hardware', '--json')).Text
$hw = ($hwText -split "`n" | Where-Object { $_ -notmatch '^Windows catalog:' }) -join "`n" | ConvertFrom-Json
Check 'hardware: Windows build equals the OS build' ($hw.Windows.Build -eq [Environment]::OSVersion.Version.Build) "$($hw.Windows.Build)"
$cpuWmi = @(Get-CimInstance Win32_Processor)
$logical = ($cpuWmi | Measure-Object NumberOfLogicalProcessors -Sum).Sum
$cores = ($cpuWmi | Measure-Object NumberOfCores -Sum).Sum
Check 'hardware: processor name is the one Windows registers' ($null -ne $hw.Cpu -and $hw.Cpu.Name -eq $cpuWmi[0].Name.Trim()) "$($hw.Cpu.Name)"
Check 'hardware: thread count equals the logical processors' ($hw.Cpu.Threads -eq $logical) "mesura $($hw.Cpu.Threads), wmi $logical"
Info 'hardware: cores' "mesura $($hw.Cpu.Cores), wmi $cores (SMBIOS can differ in a virtual machine)"
$ramGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
Check 'hardware: memory is within 1 GB of what Windows reports' ([math]::Abs($hw.MemoryGb - $ramGb) -le 1) "mesura $($hw.MemoryGb) GB, wmi $ramGb GB"
$bat = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue).Count -gt 0
Check 'hardware: battery agrees with Win32_Battery' ($hw.Power.HasBattery -eq $bat) "mesura $($hw.Power.HasBattery), wmi $bat"
Check 'hardware: every adapter is a hardware adapter Windows lists' (@($hw.Gpus | Where-Object { $_.Name -notin @(Get-CimInstance Win32_VideoController | ForEach-Object Name) }).Count -eq 0) (($hw.Gpus | ForEach-Object Name) -join '; ')
if (@($hw.Disks).Count -gt 0) {
  $phys = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)
  Check 'hardware: the disks are the ones Windows lists' (@($hw.Disks).Count -eq $phys.Count) "mesura $(@($hw.Disks).Count), Get-PhysicalDisk $($phys.Count)"
  Check 'hardware: one disk holds Windows' (@($hw.Disks | Where-Object { $_.HoldsWindows }).Count -eq 1)
} else { Info 'hardware: disks' "none read (this PC's storage is not listed by Windows' storage management: $(@(Get-PhysicalDisk -ErrorAction SilentlyContinue).Count) by Get-PhysicalDisk); the system disk is therefore not checked" }
Info 'hardware: form' "$($hw.Form), cooling $($hw.Cooling), edition $($hw.Windows.Edition), policies honoured $($hw.Windows.PolicyEdition)"

# --- 2. each level ------------------------------------------------------------------------------------------------------------------------------
foreach ($level in $Levels) {
  Say ''
  Say "== level $level"
  $origActive = Active
  $origSchemes = Schemes
  $plan0 = Plan $level
  $ops0 = @($plan0.operations)
  Check "[$level] the plan lists operations" ($ops0.Count -gt 0) "$($ops0.Count) operations"
  Check "[$level] the plan puts the power plan first" ($ops0.Count -eq 0 -or $ops0[0] -like 'power.mesura-*') "$($ops0[0])"
  $notHere = @($plan0.groups | ForEach-Object { $_.entries } | Where-Object { $_.state -in 'Unsupported', 'CantTell' })
  Info "[$level] not for this PC" (($notHere | ForEach-Object { "$($_.operation) [$($_.reason)]" }) -join '; ')

  . (Join-Path $PSScriptRoot 'e2e-profiles-composition.inc.ps1')

  # group and entry choices reach the plan
  $planOff = Plan $level @('--group-off', 'telemetry,cpu', '--entry-off', 'input.pointer-precision-off')
  $offIds = @($plan0.groups | Where-Object { $_.id -in 'telemetry', 'cpu' } | ForEach-Object { $_.entries } | ForEach-Object { $_.operation })
  Check "[$level] a group switched off removes all its entries from the plan" (@($planOff.operations | Where-Object { $_ -in $offIds }).Count -eq 0) "$($ops0.Count) -> $(@($planOff.operations).Count)"
  Check "[$level] an entry switched off is gone from the plan" ($planOff.operations -notcontains 'input.pointer-precision-off')

  $apply = $null
  try {
    $apply = Mesura @('windows', 'apply', '--profile', $level, '--apply', '--no-restore-point')
    Check "[$level] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
    $run = RunRecord $apply.Run
    $bad = @($run.windows | Where-Object { $_.status -in 'Failed', 'NeedsElevation', 'Blocked', 'Unknown' })
    Check "[$level] no operation failed, was blocked or needed more rights" ($bad.Count -eq 0) (($bad | ForEach-Object { "$($_.operationId): $($_.status) $($_.detail)" }) -join '; ')
    $applied = @($run.windows | Where-Object { $_.status -eq 'Applied' } | ForEach-Object { $_.operationId })
    $notApplied = @($run.windows | Where-Object { $_.status -ne 'Applied' })
    if ($notApplied.Count -gt 0) { Info "[$level] planned but not reported Applied" (($notApplied | ForEach-Object { "$($_.operationId): $($_.status) - $($_.detail)" }) -join '; ') }
    Check "[$level] no hardware-bound entry that was not planned is reported Applied" (@($applied | Where-Object { ($_ -like 'network.preset-*' -or $_ -eq 'gpu.amd-anti-lag-on' -or $_ -eq 'gpu.hardware-scheduling-on') -and $ops0 -notcontains $_ }).Count -eq 0) (($applied | Where-Object { $_ -like 'network.preset-*' -or $_ -like 'gpu.*' }) -join ', ')
    Check "[$level] only operations of the plan were applied" (@($applied | Where-Object { $ops0 -notcontains $_ }).Count -eq 0) "$($applied.Count) applied of $($ops0.Count) planned"
    Check "[$level] nothing was written to the battery side of a power plan" (@($run.windows | ForEach-Object { $_.changes } | Where-Object { $_.kind -eq 'PowerSetting' -and $_.dc }).Count -eq 0)
    Verify-Changes $run 'after' $level
    if ($ops0 -contains 'power.mesura-maximum-plan' -and $applied -contains 'power.mesura-maximum-plan') { Check "[$level] the Maximum Performance Mesura plan is the active plan (powercfg)" ((Active) -eq '76d36892-93d0-4c33-9e09-c2e1fb87cf49') (Active) }
    if ($ops0 -contains 'power.mesura-balanced-plan' -and $applied -contains 'power.mesura-balanced-plan') { Check "[$level] the Balanced Mesura plan is the active plan (powercfg)" ((Active) -eq 'b71ddbcf-9d21-46ea-9def-6f2a2378e65a') (Active) }

    # read back through Mesura's own reading: the entries it applied are in place now and nothing is left to apply
    $plan1 = Plan $level
    $stillToApply = @($plan1.groups | ForEach-Object { $_.entries } | Where-Object { $_.state -eq 'ToApply' -and $_.role -eq 'Default' -and $applied -contains $_.operation })
    Check "[$level] planned again, nothing it applied is still waiting to be applied" ($stillToApply.Count -eq 0) (($stillToApply | ForEach-Object { $_.operation }) -join ', ')
    $inPlace = @($plan1.groups | ForEach-Object { $_.entries } | Where-Object { $_.state -in 'Applied', 'Waiting', 'Covered' })
    Info "[$level] after the apply: $($inPlace.Count) entries in place, $(@($plan1.operations).Count) still planned" ((@($plan1.operations)) -join ', ')

    # a second apply changes nothing
    $again = Mesura @('windows', 'apply', '--profile', $level, '--apply', '--no-restore-point')
    $againRun = if ($again.Run) { RunRecord $again.Run } else { $null }
    $changed = if ($againRun) { @($againRun.windows | Where-Object { $_.changes.Count -gt 0 }) } else { @() }
    Check "[$level] a second apply changes nothing" ($changed.Count -eq 0) "$($again.Run) $(($changed | ForEach-Object { $_.operationId }) -join ', ')"
  } finally {
    if ($apply -and $apply.Run) {
      $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
      Check "[$level] revert ran" ($revert.Exit -eq 0) "exit $($revert.Exit)"
      if ($revert.Exit -ne 0) { Say $revert.Text; $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply'); Info "[$level] revert tried again" "exit $($revert.Exit)" }
      $run = RunRecord $apply.Run
      Verify-Changes $run 'before' $level
    }
    Check "[$level] the original power plan is active again (powercfg)" ((Active) -eq $origActive) "was $origActive, is $(Active)"
    $leftover = @(Schemes | Where-Object { $origSchemes -notcontains $_ })
    Check "[$level] no power scheme of the run is left" ($leftover.Count -eq 0) ($leftover -join ', ')
  }
  $plan2 = Plan $level
  Check "[$level] after the revert the plan is the same as before the run" (($plan2.operations -join ',') -eq ($ops0 -join ',')) "before $($ops0.Count), after $(@($plan2.operations).Count)"
}

# --- 3. a part-way failure: one key refuses the write ---------------------------------------------------------------------------------------------
# The Edge policy key is made to refuse writes (an explicit deny for Administrators and SYSTEM), so the two Edge operations of Maximum performance fail while the rest of the level goes
# through. Each operation is all-or-nothing and the others must be unaffected; the run must still be recorded and revert cleanly; the key is cleaned up afterwards.
if ($Levels -contains 'maximum-performance') {
  Say ''
  Say '== maximum-performance with the Edge policy key refusing writes'
  $edgePath = 'SOFTWARE\Policies\Microsoft\Edge'
  $createdKey = $null -eq [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($edgePath)
  $origActive = Active
  $origSchemes = Schemes
  $apply = $null
  try {
    $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($edgePath)
    $acl = $key.GetAccessControl()
    foreach ($who in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
      $rule = New-Object System.Security.AccessControl.RegistryAccessRule($who, 'SetValue, CreateSubKey, Delete', 'ContainerInherit', 'None', 'Deny')
      $acl.AddAccessRule($rule)
    }
    $key.SetAccessControl($acl); $key.Dispose()
    $plan = Plan 'maximum-performance'
    Check '[poisoned] the Edge operations are in the plan' ($plan.operations -contains 'edge.no-startup-boost' -and $plan.operations -contains 'edge.no-background-mode')
    $apply = Mesura @('windows', 'apply', '--profile', 'maximum-performance', '--apply', '--no-restore-point')
    Check '[poisoned] the run was recorded although part of it failed' ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
    $run = RunRecord $apply.Run
    $failed = @($run.windows | Where-Object { $_.status -eq 'Failed' } | ForEach-Object { $_.operationId })
    Check '[poisoned] exactly the two Edge operations failed' (($failed | Sort-Object) -join ',' -eq 'edge.no-background-mode,edge.no-startup-boost') ($failed -join ', ')
    Check '[poisoned] a failed operation recorded no change and left no value behind' (@($run.windows | Where-Object { $_.status -eq 'Failed' -and $_.changes.Count -gt 0 }).Count -eq 0 -and $null -eq (RegRead 'HKLM' $edgePath 'StartupBoostEnabled') -and $null -eq (RegRead 'HKLM' $edgePath 'BackgroundModeEnabled'))
    $others = @($run.windows | Where-Object { $_.status -eq 'Applied' })
    Check '[poisoned] the other operations were applied and are in place' ($others.Count -ge 20) "$($others.Count) applied"
    Verify-Changes $run 'after' 'poisoned'
    Check '[poisoned] the Maximum plan is active (the failure did not stop the rest)' ((Active) -eq '76d36892-93d0-4c33-9e09-c2e1fb87cf49') (Active)
  } finally {
    if ($apply -and $apply.Run) {
      $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
      Check '[poisoned] revert ran' ($revert.Exit -eq 0) "exit $($revert.Exit)"
      Verify-Changes (RunRecord $apply.Run) 'before' 'poisoned'
    }
    # clean up the key whatever happened: the deny rules first, then the key itself if the test created it
    try {
      $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($edgePath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::ChangePermissions -bor [System.Security.AccessControl.RegistryRights]::ReadKey)
      if ($key) { $acl = $key.GetAccessControl(); foreach ($r in @($acl.GetAccessRules($true, $false, [System.Security.Principal.NTAccount]) | Where-Object { $_.AccessControlType -eq 'Deny' })) { [void]$acl.RemoveAccessRule($r) }; $key.SetAccessControl($acl); $key.Dispose() }
      if ($createdKey) { [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree($edgePath, $false) }
    } catch { Say "FAIL  [poisoned] cleanup of the Edge key failed: $($_.Exception.Message)" }
    Check '[poisoned] the original power plan is active again and no scheme is left' ((Active) -eq $origActive -and @(Schemes | Where-Object { $origSchemes -notcontains $_ }).Count -eq 0)
    if ($createdKey) { Check '[poisoned] the test key is gone' ($null -eq [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($edgePath)) }
  }
}
Say ''
Say "== done $(Get-Date -Format s)"
