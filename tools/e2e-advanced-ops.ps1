# Real, elevated checks of the advanced settings of 0.7.0 (the two collections "Security protections off" and "Less in the background", D052), meant for Windows Sandbox or a disposable PC.
# For each setting: the state is read with Windows' own tools (the registry, sc.exe, bcdedit: never Mesura's own reading), Mesura applies the setting, the state is read again
# independently and must be the wanted one, the run is reverted, and the state is read a third time and must equal the first. A second revert of the same run changes nothing.
# Also checks that neither profile plan names any of the settings (they are explicit choices only).
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-advanced-ops.ps1 -Cli <mesura.exe> [-Report <file>]
#
# It changes security settings of the PC it runs on (and puts them back): NEVER run it on a PC you use. Nothing is installed.
param(
  [Parameter(Mandatory = $true)][string]$Cli,
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated terminal.' }
function Say([string]$line) { Write-Host $line; if ($Report) { $line | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$name, [bool]$ok, [string]$detail = '') { Say ('{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

function Mesura([string[]]$arguments) {
  $ErrorActionPreference = 'Continue'
  $text = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Run = $(if ($text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }); Exit = $LASTEXITCODE }
}
function InPlace([string]$s) { $s -like 'applied*' -or $s -like 'set*' -or $s -like 'restart*' -or $s -like 'sign*' -or $s -like 'explorer*' }
function State([string]$id) { $t = (Mesura @('windows', 'show', '--id', $id)).Text; if ($t -match 'State here: ([^.]*)\.') { $Matches[1].Trim() } else { '?' } }

# One registry value as text: "absent" or "<value>".
function RegValue([string]$path, [string]$name) {
  $item = Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction SilentlyContinue
  if ($null -eq $item) { 'absent' } else { "$($item.$name)" }
}
function RegSnapshot($targets) { ($targets | ForEach-Object { "$($_[1])=$(RegValue $_[0] $_[1])" }) -join '; ' }
function RegWanted($targets) { ($targets | ForEach-Object { "$($_[1])=$($_[2])" }) -join '; ' }

function Test-Registry([string]$id, $targets) {
  Say "== $id"
  $before = RegSnapshot $targets
  $state0 = State $id
  if ($state0 -like 'n/a*' -or $state0 -like 'not for this pc*' -or $state0 -like 'not supported*') { Say "SKIP  [$id] $state0"; return }
  $apply = Mesura @('windows', 'apply', '--id', $id, '--apply')
  Check "[$id] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
  $after = RegSnapshot $targets
  Check "[$id] the registry, read independently, holds the wanted values" ($after -eq (RegWanted $targets)) "before: $before; after: $after"
  Check "[$id] Mesura reads it as in place" (InPlace (State $id)) (State $id)
  $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
  $back = RegSnapshot $targets
  Check "[$id] revert put every value back exactly" ($back -eq $before) "after revert: $back; exit $($revert.Exit)"
  $again = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
  Check "[$id] a second revert of the same run changes nothing" ((RegSnapshot $targets) -eq $before) "exit $($again.Exit)"
}

function Start-Of([string]$name) { $v = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Start -ErrorAction SilentlyContinue; if ($v) { "$($v.Start)" } else { 'absent' } }
function Test-Services([string]$id, [string[]]$names) {
  Say "== $id"
  $before = ($names | ForEach-Object { "$_=$(Start-Of $_)" }) -join '; '
  $apply = Mesura @('windows', 'apply', '--id', $id, '--apply')
  Check "[$id] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
  $after = ($names | ForEach-Object { "$_=$(Start-Of $_)" }) -join '; '
  Check "[$id] every service is Disabled (4) in the registry" (-not ($names | Where-Object { (Start-Of $_) -ne '4' })) $after
  Check "[$id] sc.exe says DISABLED" (-not ($names | Where-Object { (sc.exe qc $_ | Out-String) -notmatch 'DISABLED' })) ''
  $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
  $back = ($names | ForEach-Object { "$_=$(Start-Of $_)" }) -join '; '
  Check "[$id] revert put every start type back" ($back -eq $before) "before: $before; after revert: $back; exit $($revert.Exit)"
}

function Bcd([string]$element) { $t = (bcdedit.exe /enum '{current}' 2>&1 | Out-String); if ($LASTEXITCODE -ne 0) { return $null }; if ($t -match "(?im)^\s*$element\s+(\S+)") { $Matches[1].ToLowerInvariant() } else { 'absent' } }
function Test-Hypervisor {
  $id = 'security.hypervisor-off'
  Say "== $id"
  $before = Bcd 'hypervisorlaunchtype'
  if ($null -eq $before) { Say "SKIP  [$id] this Windows has no boot store bcdedit can read (the Sandbox): Mesura must then say unknown and write nothing"; $s = State $id; Check "[$id] Mesura does not guess: state is unknown" ($s -like 'unknown*' -or $s -like 'n/a*') $s; return }
  $apply = Mesura @('windows', 'apply', '--id', $id, '--apply')
  Check "[$id] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
  Check "[$id] bcdedit, read independently, says Off" ((Bcd 'hypervisorlaunchtype') -eq 'off') (Bcd 'hypervisorlaunchtype')
  $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
  Check "[$id] revert put the element back to what it was ($before)" ((Bcd 'hypervisorlaunchtype') -eq $before) "now: $(Bcd 'hypervisorlaunchtype'); exit $($revert.Exit)"
}

Say "== advanced ops $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME"
$mm = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
$dg = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard'
$hv = "$dg\Scenarios\HypervisorEnforcedCodeIntegrity"
$pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
Test-Registry 'security.memory-integrity-off' @(, @($hv, 'Enabled', '0'))
Test-Registry 'security.vbs-off' @(@($dg, 'EnableVirtualizationBasedSecurity', '0'), @($hv, 'Enabled', '0'))
Test-Registry 'security.cpu-mitigations-off' @(@($mm, 'FeatureSettingsOverride', '3'), @($mm, 'FeatureSettingsOverrideMask', '3'))
Test-Registry 'security.smartscreen-off' @(, @("$pol\System", 'EnableSmartScreen', '0'))
Test-Registry 'security.mark-of-the-web-off' @(, @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments', 'SaveZoneInformation', '1'))
Test-Registry 'privacy.no-background-apps' @(, @("$pol\AppPrivacy", 'LetAppsRunInBackground', '2'))
Test-Registry 'privacy.no-app-diagnostics' @(, @("$pol\AppPrivacy", 'LetAppsGetDiagnosticInfo', '2'))
Test-Registry 'privacy.location-policy-off' @(, @("$pol\LocationAndSensors", 'DisableLocation', '1'))
Test-Registry 'updates.store-auto-download-off' @(, @('HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore', 'AutoDownload', '2'))
Test-Registry 'updates.driver-search-off' @(@("$pol\DriverSearching", 'SearchOrderConfig', '0'), @("$pol\Device Metadata", 'PreventDeviceMetadataFromNetwork', '1'))
Test-Registry 'system.onedrive-sync-off' @(, @("$pol\OneDrive", 'DisableFileSyncNGSC', '1'))
Test-Registry 'appearance.menu-show-delay-off' @(, @('HKCU:\Control Panel\Desktop', 'MenuShowDelay', '0'))
Test-Services 'services.diagnostic-tracking-off' @('DiagTrack')
Test-Services 'services.print-spooler-off' @('Spooler', 'PrintNotify')
Test-Hypervisor

Say '== the profiles never name an advanced setting'
$advanced = @('security.memory-integrity-off', 'security.vbs-off', 'security.cpu-mitigations-off', 'security.hypervisor-off', 'security.smartscreen-off', 'security.mark-of-the-web-off', 'privacy.no-background-apps', 'privacy.no-app-diagnostics', 'privacy.location-policy-off', 'updates.store-auto-download-off', 'updates.driver-search-off', 'system.onedrive-sync-off', 'services.diagnostic-tracking-off', 'services.print-spooler-off', 'appearance.menu-show-delay-off')
foreach ($level in 'recommended-gaming', 'maximum-performance') {
  $plan = (Mesura @('windows', 'plan', '--profile', $level, '--json')).Text
  $hits = @($advanced | Where-Object { $plan -match [regex]::Escape("`"$_`"") })
  Check "[$level] the plan names none of the 15 advanced settings (and no generic network.adapter-*)" ($hits.Count -eq 0 -and $plan -notmatch '"network\.adapter-') ($hits -join ', ')
}
Say "== advanced ops done $(Get-Date -Format s)"
