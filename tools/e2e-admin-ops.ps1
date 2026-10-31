# Real, elevated checks of the machine-wide operations with the heaviest consequences, one after the other: for each, the state is read with Windows' own tools,
# Mesura applies the operation, the new state is read again INDEPENDENTLY (sc.exe, the registry, powercfg, dism: never Mesura's own reading), the run is reverted,
# and the state is read a third time and must equal the first. Run from an ELEVATED terminal on a PC where it is acceptable to change these settings for a minute:
#
#   services.print-on / services.sens-on     the service's start type (set to Manual first so that there is something to change; restored at the end)
#   system.hibernation-off                   hibernation and hiberfil.sys (the file is deleted and recreated: size of about 40 % of the memory)
#   network.netbios-over-tcpip-off           NetBIOS over TCP/IP on every network interface (registry, one value per interface)
#   features.legacy-media-off                the Windows Media Player Legacy optional feature (DISM; may need a restart to be final)
#   power.usb-keep-awake                     a power-plan setting (powercfg, on AC) of the active plan\n#   cpu.dynamic-tick-default                 the boot configuration store (bcdedit; exported first, the element is set by hand so there is something to remove)\n#   memory.compression-off                   the memory manager's compression (Get-MMAgent)\n#   manual.restore-tcp-autotuning            a global TCP setting (netsh; set to 'restricted' first)\n#   telemetry.ceip-policy-off                a machine policy value (HKLM; absent before, so revert must delete it)\n#   system.ntfs-last-access-off              an NTFS setting (registry and fsutil)\n#   refusal                                  the same kind of change run WITHOUT administrator rights is refused and changes nothing (-Unelevated is used by the caller)\n#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-admin-ops.ps1 -Cli <mesura.exe> [-Only services,hibernation,netbios,feature,power,bcd,memory,tcp,policy,ntfs] [-Report <file>]
#
# Everything it changes it puts back; if a step fails it still tries to restore the original state and says so. Nothing is installed.
param(
  [Parameter(Mandatory = $true)][string]$Cli,
  [string[]]$Only = @('services', 'hibernation', 'netbios', 'feature', 'power', 'bcd', 'memory', 'tcp', 'policy', 'ntfs'),
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' })
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated terminal.' }
$results = New-Object System.Collections.Generic.List[string]
function Say([string]$line) { $results.Add($line); Write-Host $line; if ($Report) { $line | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$name, [bool]$ok, [string]$detail = '') { Say ('{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

# Runs one Mesura command (preview or apply) and returns its text and the history run id it recorded.
function Mesura([string[]]$arguments) {
  $ErrorActionPreference = 'Continue'   # the command line writes its progress to stderr; that is not an error here
  $text = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Run = $(if ($text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }); Exit = $LASTEXITCODE }
}
function State([string]$id) { $t = (Mesura @('windows', 'show', '--id', $id)).Text; if ($t -match 'State here: ([^.]*)\.') { $Matches[1].Trim() } else { '?' } }

function Test-Service([string]$name, [string]$opId) {
  $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
  $orig = Get-ItemProperty $key
  $origStart = $orig.Start; $origDelayed = $orig.DelayedAutostart
  Say "== $opId ($name): the start type is $origStart now; setting it to Manual first"
  try {
    sc.exe config $name start= demand | Out-Null
    Check "[$opId] Windows says the service is Manual before" ((Get-Service $name).StartType -eq 'Manual') "$((Get-Service $name).StartType)"
    Check "[$opId] Mesura reads it as not applied" ((State $opId) -like '-*' -or (State $opId) -like 'not applied*') (State $opId)
    $apply = Mesura @('windows', 'apply', '--id', $opId, '--apply')
    Check "[$opId] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
    $now = (Get-ItemProperty $key).Start
    Check "[$opId] the registry (read independently) says Automatic after apply" ($now -eq 2) "Start=$now"
    Check "[$opId] sc.exe says AUTO_START after apply" ((sc.exe qc $name | Out-String) -match 'AUTO_START') ''
    Check "[$opId] Mesura reads it as applied" ((State $opId) -like 'applied*') (State $opId)
    $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    $back = (Get-ItemProperty $key).Start
    Check "[$opId] revert put the start type back to Manual (3)" ($back -eq 3) "Start=$back; revert exit $($revert.Exit)"
    $again = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    Check "[$opId] a second revert of the same run changes nothing" ((Get-ItemProperty $key).Start -eq 3) "exit $($again.Exit)"
  } finally {
    # Back to what it was, exactly.
    $startName = switch ($origStart) { 2 { 'auto' } 3 { 'demand' } 4 { 'disabled' } default { 'auto' } }
    sc.exe config $name start= $startName | Out-Null
    if ($origDelayed -eq 1) { sc.exe config $name start= delayed-auto | Out-Null }
    Check "[$opId] the original start type is restored" ((Get-ItemProperty $key).Start -eq $origStart) "Start=$((Get-ItemProperty $key).Start)"
  }
}

function Hiberfile { [bool](cmd /c 'dir /a C:\hiberfil.sys 2>nul' | Select-String 'hiberfil.sys') }
function HibernateOnRegistry { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power').HibernateEnabled }
function Test-Hibernation {
  $op = 'system.hibernation-off'
  Say "== ${op}: hibernation is $(if (HibernateOnRegistry -eq 1) { 'on' } else { 'off' }) now"
  if ((HibernateOnRegistry) -ne 1) { Say 'SKIP  hibernation is already off on this PC: nothing to apply'; return }
  try {
    Check "[$op] hiberfil.sys exists before" (Hiberfile)
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    $apply = Mesura @('windows', 'apply', '--id', $op, '--apply')
    Check "[$op] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
    Check "[$op] the registry says hibernation is off" ((HibernateOnRegistry) -eq 0) "HibernateEnabled=$(HibernateOnRegistry)"
    Check "[$op] powercfg /a no longer lists Hibernate as available" (-not ((powercfg /a | Out-String) -split 'not available')[0].Contains('Hibernate')) ''
    Check "[$op] hiberfil.sys is gone" (-not (Hiberfile))
    $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    Check "[$op] revert turned hibernation on again" ((HibernateOnRegistry) -eq 1) "HibernateEnabled=$(HibernateOnRegistry); exit $($revert.Exit)"
    Check "[$op] hiberfil.sys is back" (Hiberfile)
    Check "[$op] powercfg /a lists Hibernate as available again" (((powercfg /a | Out-String) -split 'not available')[0].Contains('Hibernate')) ''
  } finally {
    if ((HibernateOnRegistry) -ne 1) { powercfg /h on | Out-Null; Check "[$op] original state restored by hand" ((HibernateOnRegistry) -eq 1) }
  }
}

function Test-NetBios {
  $op = 'network.netbios-over-tcpip-off'
  $root = 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces'
  function Read-All { Get-ChildItem $root | ForEach-Object { '{0}={1}' -f $_.PSChildName, (Get-ItemProperty $_.PSPath).NetbiosOptions } | Sort-Object }
  $before = @(Read-All)
  Say "== ${op}: $($before.Count) interfaces, values before: $(($before | ForEach-Object { $_.Split('=')[1] } | Sort-Object -Unique) -join ',')"
  try {
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    $apply = Mesura @('windows', 'apply', '--id', $op, '--apply')
    Check "[$op] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
    $after = @(Read-All)
    Check "[$op] every interface reads NetbiosOptions=2 afterwards (registry, independent)" (@($after | Where-Object { $_ -notlike '*=2' }).Count -eq 0) (($after | ForEach-Object { $_.Split('=')[1] }) -join ',')
    Check "[$op] Mesura reads it as applied" ((State $op) -like 'applied*') (State $op)
    $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    $restored = @(Read-All)
    Check "[$op] revert restored every interface to its own original value" ((($restored -join '|') -eq ($before -join '|'))) "exit $($revert.Exit)"
  } finally {
    # Hand-restore anything still different (the values are read back from the first listing).
    foreach ($line in $before) { $name, $value = $line.Split('='); $k = Join-Path $root $name; if ((Get-ItemProperty $k).NetbiosOptions -ne [int]$value) { Set-ItemProperty $k NetbiosOptions ([int]$value) } }
  }
}

function Feature-State { ((dism /online /get-featureinfo /featurename:WindowsMediaPlayer | Out-String) -split "`n" | Where-Object { $_ -match '^State' } | ForEach-Object { ($_ -split ':', 2)[1].Trim() }) -join '' }
function Test-Feature {
  $op = 'features.legacy-media-off'
  Say "== ${op}: the Windows Media Player Legacy feature is '$(Feature-State)' now"
  if ((Feature-State) -ne 'Enabled') { Say 'SKIP  the feature is not enabled on this PC'; return }
  try {
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $apply = Mesura @('windows', 'apply', '--id', $op, '--apply')
    Check "[$op] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit), $([int]$sw.Elapsed.TotalSeconds) s"
    $after = Feature-State
    Check "[$op] DISM (read independently) says the feature is no longer enabled" ($after -in 'Disabled', 'Disable Pending') "State: $after"
    Check "[$op] Mesura reads it as applied or pending" ((State $op) -match 'applied|pending') (State $op)
    $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    $restored = Feature-State
    Check "[$op] revert enabled the feature again (DISM, independent)" ($restored -in 'Enabled', 'Enable Pending') "State: $restored; revert exit $($revert.Exit)"
  } finally {
    if ((Feature-State) -notin 'Enabled', 'Enable Pending') { dism /online /enable-feature /featurename:WindowsMediaPlayer /norestart | Out-Null; Check "[$op] original state restored by hand" ((Feature-State) -in 'Enabled', 'Enable Pending') (Feature-State) }
  }
}

function ApplyRevert([string]$op, [scriptblock]$readAfter, [string]$afterLabel, [scriptblock]$readBack, [string]$backLabel) {
  $apply = Mesura @('windows', 'apply', '--id', $op, '--apply')
  Check "[$op] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
  $a = & $readAfter
  Check "[$op] $afterLabel (read independently)" ([bool]$a[0]) "$($a[1])"
  Check "[$op] Mesura reads it as applied" ((State $op) -like 'applied*') (State $op)
  $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
  $b = & $readBack
  Check "[$op] $backLabel after revert" ([bool]$b[0]) "$($b[1]); revert exit $($revert.Exit)"
}

function ActiveScheme { ((powercfg /getactivescheme) -replace '.*: ([0-9a-f-]{36}).*', '$1') }
function UsbAc { $o = powercfg /query (ActiveScheme) 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 | Out-String; if ($o -match 'Current AC Power Setting Index: 0x([0-9a-f]+)') { [Convert]::ToInt32($Matches[1], 16) } else { -1 } }
function Test-Power {
  $op = 'power.usb-keep-awake'; $orig = UsbAc
  Say "== ${op}: USB selective suspend (AC) of the active plan is $orig now"
  try {
    if ($orig -ne 1) { powercfg /setacvalueindex (ActiveScheme) 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 1; powercfg /setactive (ActiveScheme) }
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    ApplyRevert $op { @(((UsbAc) -eq 0), "AC=$(UsbAc)") } 'powercfg says the AC value is 0' { @(((UsbAc) -eq 1), "AC=$(UsbAc)") } 'the AC value is 1 again'
  } finally {
    if ($orig -ge 0) { powercfg /setacvalueindex (ActiveScheme) 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 $orig; powercfg /setactive (ActiveScheme) }
    Check "[$op] the original value is restored" ((UsbAc) -eq $orig) "AC=$(UsbAc)"
  }
}

function Bcd([string]$name) { $o = (bcdedit /enum '{current}' | Out-String); if ($o -match "(?m)^$name\s+(\S+)") { $Matches[1] } else { 'absent' } }
function Test-Bcd {
  $op = 'cpu.dynamic-tick-default'; $orig = Bcd 'disabledynamictick'
  $backup = Join-Path $env:TEMP 'mesura-e2e-bcd.bak'; Remove-Item $backup -ErrorAction SilentlyContinue; bcdedit /export $backup | Out-Null
  Say "== ${op}: disabledynamictick is '$orig' now; BCD exported to $backup"
  try {
    bcdedit /set disabledynamictick yes | Out-Null
    Check "[$op] bcdedit says the element is 'yes' before" ((Bcd 'disabledynamictick') -eq 'Yes') (Bcd 'disabledynamictick')
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    ApplyRevert $op { @(((Bcd 'disabledynamictick') -eq 'absent'), (Bcd 'disabledynamictick')) } 'bcdedit says the element is gone' { @(((Bcd 'disabledynamictick') -eq 'Yes'), (Bcd 'disabledynamictick')) } 'the previous value (yes) is back'
  } finally {
    if ($orig -eq 'absent') { bcdedit /deletevalue disabledynamictick 2>$null | Out-Null } else { bcdedit /set disabledynamictick $orig | Out-Null }
    Check "[$op] the original BCD state is restored" ((Bcd 'disabledynamictick') -eq $orig) (Bcd 'disabledynamictick')
  }
}

function Test-Memory {
  $op = 'memory.compression-off'; $orig = (Get-MMAgent).MemoryCompression
  Say "== ${op}: memory compression is $orig now"
  try {
    if (-not $orig) { Enable-MMAgent -MemoryCompression }
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    ApplyRevert $op { @((-not (Get-MMAgent).MemoryCompression), "MemoryCompression=$((Get-MMAgent).MemoryCompression)") } 'Get-MMAgent says compression is off' { @(((Get-MMAgent).MemoryCompression), "MemoryCompression=$((Get-MMAgent).MemoryCompression)") } 'compression is on again'
  } finally {
    if ($orig) { Enable-MMAgent -MemoryCompression } else { Disable-MMAgent -MemoryCompression }
    Check "[$op] the original state is restored" (((Get-MMAgent).MemoryCompression) -eq $orig) "$((Get-MMAgent).MemoryCompression)"
  }
}

function Autotuning { ((netsh int tcp show global | Out-String) -replace "(?s).*Receive Window Auto-Tuning Level\s*:\s*(\S+).*", '$1') }
function Test-Tcp {
  $op = 'manual.restore-tcp-autotuning'; $orig = Autotuning
  Say "== ${op}: receive window auto-tuning is '$orig' now; setting 'restricted' first"
  try {
    netsh int tcp set global autotuninglevel=restricted | Out-Null
    Check "[$op] netsh says restricted before" ((Autotuning) -eq 'restricted') (Autotuning)
    ApplyRevert $op { @(((Autotuning) -eq 'normal'), (Autotuning)) } 'netsh says normal' { @(((Autotuning) -eq 'restricted'), (Autotuning)) } "the previous value (restricted) is back"
  } finally {
    netsh int tcp set global autotuninglevel=$orig | Out-Null
    Check "[$op] the original level is restored" ((Autotuning) -eq $orig) (Autotuning)
  }
}

function Test-Policy {
  $op = 'telemetry.ceip-policy-off'; $key = 'HKLM:\SOFTWARE\Policies\Microsoft\SQMClient\Windows'
  $had = (Test-Path $key) -and ($null -ne (Get-ItemProperty $key -Name CEIPEnable -ErrorAction SilentlyContinue)); $origValue = if ($had) { (Get-ItemProperty $key).CEIPEnable } else { $null }
  Say "== ${op}: policy value CEIPEnable is $(if ($had) { $origValue } else { 'absent' }) now"
  try {
    if ($had) { Remove-ItemProperty $key -Name CEIPEnable }
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    ApplyRevert $op { @(((Get-ItemProperty $key -ErrorAction SilentlyContinue).CEIPEnable -eq 0), "CEIPEnable=$((Get-ItemProperty $key -ErrorAction SilentlyContinue).CEIPEnable)") } 'the registry says CEIPEnable=0' { @(($null -eq (Get-ItemProperty $key -ErrorAction SilentlyContinue).CEIPEnable), 'value absent again') } 'the value is absent again (it did not exist before)'
  } finally {
    if ($had) { New-Item -Path $key -Force | Out-Null; Set-ItemProperty $key -Name CEIPEnable -Value $origValue -Type DWord } else { Remove-ItemProperty $key -Name CEIPEnable -ErrorAction SilentlyContinue }
    Check "[$op] the original state is restored" (((Get-ItemProperty $key -ErrorAction SilentlyContinue).CEIPEnable) -eq $origValue) ''
  }
}

function LastAccess { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem').NtfsDisableLastAccessUpdate }
function Test-Ntfs {
  $op = 'system.ntfs-last-access-off'; $orig = LastAccess
  Say "== ${op}: NtfsDisableLastAccessUpdate is $orig now"
  try {
    Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' NtfsDisableLastAccessUpdate 2 -Type DWord   # bit 1 only: a value with a bit other than 0 set, so the check below shows that Mesura keeps the other bits
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    ApplyRevert $op { @(((LastAccess) -eq 3), "value=$(LastAccess)") } 'the registry says 3 (bit 0 set, the system-managed bit kept: the setting is a bit field)' { @(((LastAccess) -eq 2), "value=$(LastAccess)") } 'the previous value (2) is back'
  } finally {
    Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' NtfsDisableLastAccessUpdate $orig -Type DWord
    Check "[$op] the original value is restored" ((LastAccess) -eq $orig) "value=$(LastAccess)"
  }
}

if ($Only -contains 'power') { Test-Power }
if ($Only -contains 'bcd') { Test-Bcd }
if ($Only -contains 'memory') { Test-Memory }
if ($Only -contains 'tcp') { Test-Tcp }
if ($Only -contains 'policy') { Test-Policy }
if ($Only -contains 'ntfs') { Test-Ntfs }

if ($Only -contains 'services') { Test-Service 'Spooler' 'services.print-on'; Test-Service 'SENS' 'services.sens-on' }
if ($Only -contains 'hibernation') { Test-Hibernation }
if ($Only -contains 'netbios') { Test-NetBios }
if ($Only -contains 'feature') { Test-Feature }
$failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
Say ''; Say ("{0} lines, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
