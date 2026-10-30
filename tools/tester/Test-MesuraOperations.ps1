# Mesura external operations test (0.2.0). For a TEST machine or virtual machine only: it applies and reverts real changes
# (app removal, a boot-entry element, hibernation, the memory agent, a network adapter keyword, a restore point) and checks
# each one with Windows' own tools, independently of Mesura, then writes a Markdown report.
#
#   Without -Run it changes NOTHING: it lists what each scenario would do on this PC and which ones can't run here and why.
#   With -Run -ConfirmTestMachine, from an elevated PowerShell, it runs the scenarios. Take a VM snapshot first.
#
#   powershell -ExecutionPolicy Bypass -File Test-MesuraOperations.ps1                       # dry run (read-only)
#   powershell -ExecutionPolicy Bypass -File Test-MesuraOperations.ps1 -Run -ConfirmTestMachine
#   ... -Scenario Appx,Bcd        run only some:  Info, Policy, RestorePoint, Appx, Bcd, Hibernate, MemoryAgent, Adapter
#
# A scenario that can't run here (no removable app installed, no adapter that declares the keyword, BitLocker on...) is
# reported SKIPPED, never PASSED. The report ends with what was and wasn't covered.
param(
  [string]$Cli = '',
  [string[]]$Scenario = @('All'),   # All, Info, Policy, RestorePoint, Appx, Bcd, Hibernate, MemoryAgent, Adapter (comma-separated)
  [string]$AppOperation = '',
  [switch]$Run,
  [switch]$ConfirmTestMachine,
  [switch]$AllowBitLocker,
  [string]$Report = ''
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Cli) {
  foreach ($c in @((Join-Path $here 'cli\mesura.exe'), (Join-Path $here '..\cli\mesura.exe'), (Join-Path $here '..\..\artifacts\current\Mesura\cli\mesura.exe'))) {
    if (Test-Path $c) { $Cli = (Resolve-Path $c).Path; break }
  }
}
if (-not $Cli -or -not (Test-Path $Cli)) { throw "mesura.exe (the command line) not found. Pass -Cli <path to cli\mesura.exe>." }
$Cli = (Resolve-Path $Cli).Path
if (-not $Report) { $Report = Join-Path ([Environment]::GetFolderPath('Desktop')) ("Mesura-operations-test-{0:yyyyMMdd-HHmmss}.md" -f (Get-Date)) }

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$valid = 'All', 'Info', 'Policy', 'RestorePoint', 'Appx', 'Bcd', 'Hibernate', 'MemoryAgent', 'Adapter'
foreach ($x in $Scenario) { if ($valid -notcontains $x) { throw "Unknown scenario '$x'. Use: $($valid -join ', ')" } }
$all = $Scenario -contains 'All'
function Wants($name) { return ($all -or ($Scenario -contains $name)) }

$results = New-Object System.Collections.Generic.List[object]
$script:current = ''
function Add-Result([string]$step, [string]$status, [string]$detail = '') {
  $results.Add([pscustomobject]@{ Scenario = $script:current; Step = $step; Status = $status; Detail = $detail })
  $color = switch ($status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'SKIPPED' { 'Yellow' } default { 'Gray' } }
  Write-Host ("  [{0,-7}] {1} {2}" -f $status, $step, $detail) -ForegroundColor $color
}
function Section([string]$name) { $script:current = $name; Write-Host "`n== $name" -ForegroundColor Cyan }

function Invoke-Mesura([string[]]$arguments) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo($Cli)
  $psi.Arguments = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
  $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  $p = [System.Diagnostics.Process]::Start($psi)
  $out = $p.StandardOutput.ReadToEnd(); $err = $p.StandardError.ReadToEnd(); $p.WaitForExit()
  return [pscustomobject]@{ Code = $p.ExitCode; Text = ($out + "`n" + $err) }
}
function Run-Id([string]$text) {
  $m = [regex]::Match($text, 'history run (\d{8}T\d{6}Z-[a-z\-]+)')
  if ($m.Success) { return $m.Groups[1].Value } else { return '' }
}
function Reg-Value($path, $name) { try { (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name } catch { $null } }
function Describe($v) { if ($null -eq $v) { '(absent)' } else { "$v" } }
function Can-Run([string]$what) {
  if (-not $Run) { Add-Result 'dry run' 'SKIPPED' "would run: $what"; return $false }
  if (-not $elevated) { Add-Result 'needs administrator' 'SKIPPED' 'start PowerShell as administrator'; return $false }
  return $true
}

# ---- gates -------------------------------------------------------------------------------------------------------------
if ($Run -and -not $ConfirmTestMachine) { throw 'Refusing to run: add -ConfirmTestMachine to say this is a disposable test machine or a VM with a snapshot.' }
$bitlocker = ''
if ($elevated) { try { $bitlocker = (& manage-bde -status C: 2>$null | Out-String) } catch {} }
$bitlockerOn = $bitlocker -match 'Protection On|Защита включена'

Write-Host "Mesura operations test: $(if ($Run) { 'RUN (changes this PC)' } else { 'DRY RUN (changes nothing)' })" -ForegroundColor Cyan
Write-Host "CLI: $Cli"
$ver = (Get-Item $Cli).VersionInfo.ProductVersion

# ---- Info ---------------------------------------------------------------------------------------------------------------
if (Wants 'Info') {
  Section 'Info (read-only)'
  $list = Invoke-Mesura @('windows', 'list')
  if ($list.Code -eq 0 -and $list.Text -match 'operations') { Add-Result 'windows list' 'PASS' (($list.Text -split "`n" | Select-Object -First 1).Trim()) } else { Add-Result 'windows list' 'FAIL' "exit $($list.Code)" }

  $hv = Reg-Value 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled'
  $dg = $null; try { $dg = (Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Stop).SecurityServicesRunning } catch {}
  $show = Invoke-Mesura @('windows', 'show', '--id', 'security.memory-integrity-status')
  Add-Result 'memory integrity: Windows says' 'INFO' ("registry Enabled=" + (Describe $hv) + "; Win32_DeviceGuard.SecurityServicesRunning=" + (Describe ($dg -join ',')) + " (2 = memory integrity running)")
  Add-Result 'memory integrity: Mesura says' 'INFO' (($show.Text -split "`n" | Where-Object { $_ -match 'State here|HypervisorEnforcedCodeIntegrity.Enabled' } | Select-Object -First 2) -join ' | ')
  Add-Result 'memory integrity (compare by eye with Windows Security > Device security > Core isolation)' 'INFO' 'Mesura only reads and shows this setting; it never changes it'

  $hib = Reg-Value 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled'
  Add-Result 'hibernation as Windows reports it' 'INFO' ("HibernateEnabled=" + (Describe $hib) + "; hiberfil.sys " + $(if ([IO.File]::Exists('C:\hiberfil.sys')) { 'present' } else { 'not visible/absent' }))

  if ($elevated) {
    $bcd = (& bcdedit /enum '{current}' 2>&1 | Out-String)
    Add-Result 'bcdedit readable' $(if ($bcd -match 'identifier') { 'PASS' } else { 'FAIL' }) (($bcd -split "`n" | Where-Object { $_ -match '^\s*(useplatformclock|disabledynamictick|useplatformtick|tscsyncpolicy)' }) -join ' | ')
    try { $mm = Get-MMAgent; Add-Result 'Get-MMAgent readable' 'PASS' "MemoryCompression=$($mm.MemoryCompression) PageCombining=$($mm.PageCombining)" } catch { Add-Result 'Get-MMAgent readable' 'FAIL' $_.Exception.Message }
    $rp = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
    $cliRp = Invoke-Mesura @('windows', 'restore-points')
    Add-Result 'restore points: Windows vs Mesura' 'INFO' ("Windows lists $($rp.Count); Mesura lists " + @(($cliRp.Text -split "`n") | Where-Object { $_ -match '^\s+\d+\s+\d{4}-' }).Count)
  } else {
    Add-Result 'bcdedit / Get-MMAgent / restore points' 'SKIPPED' 'not elevated: these can only be read as administrator'
  }
}

# ---- Policy (a machine registry operation) ------------------------------------------------------------------------------
if (Wants 'Policy') {
  Section 'Policy: a machine registry setting, applied and reverted'
  $candidates = @(
    @{ Id = 'system.long-paths-on'; Path = 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem'; Name = 'LongPathsEnabled'; Want = 1 },
    @{ Id = 'telemetry.limit-dump-collection'; Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'; Name = 'LimitDumpCollection'; Want = 1 })
  $chosen = $candidates | Where-Object { (Reg-Value $_.Path $_.Name) -ne $_.Want } | Select-Object -First 1
  if (-not $chosen) { Add-Result 'candidate' 'SKIPPED' 'both candidate settings are already at the target value on this PC' }
  elseif (Can-Run "apply and revert $($chosen.Id)") {
    $before = Reg-Value $chosen.Path $chosen.Name
    $prev = Invoke-Mesura @('windows', 'apply', '--id', $chosen.Id)
    Add-Result 'preview changes nothing' $(if ((Reg-Value $chosen.Path $chosen.Name) -eq $before -and $prev.Text -match 'Preview') { 'PASS' } else { 'FAIL' }) "value still $(Describe (Reg-Value $chosen.Path $chosen.Name))"
    $ap = Invoke-Mesura @('windows', 'apply', '--id', $chosen.Id, '--apply', '--no-restore-point')
    $run = Run-Id $ap.Text
    Add-Result 'apply writes the value' $(if ((Reg-Value $chosen.Path $chosen.Name) -eq $chosen.Want -and $run) { 'PASS' } else { 'FAIL' }) "value now $(Describe (Reg-Value $chosen.Path $chosen.Name)), run '$run'"
    if ($run) {
      $rv = Invoke-Mesura @('windows', 'revert', '--run', $run, '--apply')
      $after = Reg-Value $chosen.Path $chosen.Name
      Add-Result 'revert restores the previous state' $(if ($after -eq $before) { 'PASS' } else { 'FAIL' }) "before $(Describe $before), after revert $(Describe $after)"
    }
  }
}

# ---- Restore point ------------------------------------------------------------------------------------------------------
if (Wants 'RestorePoint') {
  Section 'Restore point'
  if (Can-Run 'make a System Restore point with mesura windows restore-point --apply') {
    $beforeRp = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
    $maxBefore = if ($beforeRp.Count) { ($beforeRp | Measure-Object SequenceNumber -Maximum).Maximum } else { 0 }
    $r = Invoke-Mesura @('windows', 'restore-point', '--apply')
    $afterRp = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
    $maxAfter = if ($afterRp.Count) { ($afterRp | Measure-Object SequenceNumber -Maximum).Maximum } else { 0 }
    $text = ($r.Text -replace '\s+', ' ').Trim()
    if ($maxAfter -gt $maxBefore) { Add-Result 'a new restore point exists in Windows' 'PASS' "sequence $maxBefore -> $maxAfter; Mesura said: $text" }
    elseif ($r.Code -ne 0) { Add-Result 'no point made, and Mesura said so' 'PASS' "exit $($r.Code): $text (check why: System Protection off, or Windows' once-a-day limit)" }
    else { Add-Result 'Mesura reported success but no new restore point exists' 'FAIL' $text }
  }
}

# ---- Appx ---------------------------------------------------------------------------------------------------------------
if (Wants 'Appx') {
  Section 'Packaged app removal and restore'
  $map = [ordered]@{
    'apps.remove-solitaire' = 'Microsoft.MicrosoftSolitaireCollection'; 'apps.remove-3d-viewer' = 'Microsoft.Microsoft3DViewer'
    'apps.remove-feedback-hub' = 'Microsoft.WindowsFeedbackHub'; 'apps.remove-clipchamp' = 'Clipchamp.Clipchamp'
    'apps.remove-bing-news' = 'Microsoft.BingNews'; 'apps.remove-bing-weather' = 'Microsoft.BingWeather'; 'apps.remove-todo' = 'Microsoft.Todos' }
  $op = $AppOperation; $pkg = ''
  if ($op -and $map.Contains($op)) { $pkg = $map[$op] }
  elseif (-not $op) { foreach ($k in $map.Keys) { if (Get-AppxPackage -Name $map[$k] -ErrorAction SilentlyContinue) { $op = $k; $pkg = $map[$k]; break } } }
  if (-not $op -or -not (Get-AppxPackage -Name $pkg -ErrorAction SilentlyContinue)) {
    Add-Result 'candidate app' 'SKIPPED' 'none of the removable test apps is installed for this user (use a stock Windows image, or install one from the Store)'
  } elseif (Can-Run "remove $pkg with $op, then revert") {
    $before = @(Get-AppxPackage -Name $pkg | Select-Object -ExpandProperty PackageFullName)
    $prev = Invoke-Mesura @('windows', 'apply', '--id', $op)
    Add-Result 'preview lists the app as installed and changes nothing' $(if ($prev.Text -match 'change' -and @(Get-AppxPackage -Name $pkg).Count -eq $before.Count) { 'PASS' } else { 'FAIL' }) ''
    $ap = Invoke-Mesura @('windows', 'apply', '--id', $op, '--apply')
    $run = Run-Id $ap.Text
    $left = @(Get-AppxPackage -Name $pkg).Count
    Add-Result 'apply removes the app (checked with Get-AppxPackage)' $(if ($left -eq 0 -and $run) { 'PASS' } else { 'FAIL' }) "packages left: $left; run '$run'; Mesura: $((($ap.Text -split "`n") | Where-Object { $_ -match 'applied|failed' } | Select-Object -First 1))"
    if ($run) {
      $rv = Invoke-Mesura @('windows', 'revert', '--run', $run, '--apply')
      $back = @(Get-AppxPackage -Name $pkg).Count
      if ($back -gt 0) { Add-Result 'revert registers the app again' 'PASS' "packages back: $back" }
      elseif ($rv.Code -ne 0 -or $rv.Text -match 'failed|couldn''t') { Add-Result 'revert could not restore the app, and Mesura said so (reinstall from the Store)' 'PASS' (($rv.Text -split "`n" | Where-Object { $_ -match 'staged|Store|failed' } | Select-Object -First 1)) }
      else { Add-Result 'revert reported success but the app is not back' 'FAIL' $rv.Text }
    }
  }
}

# ---- Bcd ----------------------------------------------------------------------------------------------------------------
if (Wants 'Bcd') {
  Section 'Boot entry setting (bcdedit)'
  if ($bitlockerOn -and -not $AllowBitLocker) { Add-Result 'BitLocker is on for C:' 'SKIPPED' 'changing boot settings can trigger a recovery-key prompt; suspend BitLocker and pass -AllowBitLocker, or skip' }
  elseif (Can-Run 'set disabledynamictick yes, let Mesura remove it (cpu.dynamic-tick-default), revert, then restore the BCD') {
    $names = { (& bcdedit /enum '{current}' 2>&1 | Where-Object { $_ -match '^\S' -and $_ -notmatch '^---' } | ForEach-Object { ($_ -split '\s+')[0].ToLower() }) -join ',' }
    $backup = Join-Path $env:TEMP ("mesura-bcd-backup-{0:yyyyMMdd-HHmmss}.bcd" -f (Get-Date))
    & bcdedit /export $backup | Out-Null
    $start = & $names
    if ($start -match 'disabledynamictick') { Add-Result 'precondition' 'SKIPPED' 'disabledynamictick is already set on this PC; remove it first or test another PC' }
    else {
      & bcdedit /set '{current}' disabledynamictick yes | Out-Null
      Add-Result 'test element set with bcdedit' $(if ((& $names) -match 'disabledynamictick') { 'PASS' } else { 'FAIL' }) "backup: $backup"
      $ap = Invoke-Mesura @('windows', 'apply', '--id', 'cpu.dynamic-tick-default', '--apply', '--no-restore-point')
      $run = Run-Id $ap.Text
      Add-Result 'apply removes the element' $(if ((& $names) -notmatch 'disabledynamictick' -and $run) { 'PASS' } else { 'FAIL' }) "Mesura: $((($ap.Text -split "`n") | Where-Object { $_ -match 'applied|failed|couldn|can''t restore' } | Select-Object -First 1))"
      if ($run) {
        $rv = Invoke-Mesura @('windows', 'revert', '--run', $run, '--apply')
        Add-Result 'revert puts the element back' $(if ((& $names) -match 'disabledynamictick') { 'PASS' } else { 'FAIL' }) (($rv.Text -split "`n" | Where-Object { $_ -match 'reverted|failed' } | Select-Object -First 1))
      }
      & bcdedit /deletevalue '{current}' disabledynamictick 2>&1 | Out-Null
      $end = & $names
      Add-Result 'boot configuration is back to how it started' $(if ($end -eq $start) { 'PASS' } else { 'FAIL' }) $(if ($end -eq $start) { '' } else { "restore with: bcdedit /import `"$backup`"" })
    }
  }
}

# ---- Hibernate ----------------------------------------------------------------------------------------------------------
if (Wants 'Hibernate') {
  Section 'Hibernation'
  $pre = Reg-Value 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled'
  if ($pre -ne 1) { Add-Result 'precondition' 'SKIPPED' 'hibernation is already off on this PC (run powercfg /hibernate on first to test turning it off)' }
  elseif (Can-Run 'turn hibernation off with system.hibernation-off and revert') {
    $ap = Invoke-Mesura @('windows', 'apply', '--id', 'system.hibernation-off', '--apply', '--no-restore-point')
    $run = Run-Id $ap.Text
    Start-Sleep -Seconds 2
    $off = Reg-Value 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled'
    Add-Result 'apply turns hibernation off (registry and hiberfil.sys)' $(if ($off -eq 0 -and $run) { 'PASS' } else { 'FAIL' }) "HibernateEnabled=$(Describe $off); hiberfil.sys $(if ([IO.File]::Exists('C:\hiberfil.sys')) { 'still there' } else { 'gone' })"
    if ($run) {
      Invoke-Mesura @('windows', 'revert', '--run', $run, '--apply') | Out-Null
      Start-Sleep -Seconds 2
      $on = Reg-Value 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' 'HibernateEnabled'
      Add-Result 'revert turns it back on' $(if ($on -eq 1) { 'PASS' } else { 'FAIL' }) "HibernateEnabled=$(Describe $on)"
    }
  }
}

# ---- MemoryAgent --------------------------------------------------------------------------------------------------------
if (Wants 'MemoryAgent') {
  Section 'Memory agent (compression, page combining)'
  if (Can-Run 'turn memory compression off with memory.compression-off and revert (Get-MMAgent is the independent check)') {
    $pre = Get-MMAgent
    if (-not $pre.MemoryCompression) { Add-Result 'precondition' 'SKIPPED' 'memory compression is already off on this PC' }
    else {
      $ap = Invoke-Mesura @('windows', 'apply', '--id', 'memory.compression-off', '--apply', '--no-restore-point')
      $run = Run-Id $ap.Text
      $now = (Get-MMAgent).MemoryCompression
      Add-Result 'apply turns memory compression off (Get-MMAgent)' $(if (-not $now -and $run) { 'PASS' } else { 'FAIL' }) "MemoryCompression=$now; Mesura: $((($ap.Text -split "`n") | Where-Object { $_ -match 'applied|failed|keep' } | Select-Object -First 1))"
      if ($run) {
        Invoke-Mesura @('windows', 'revert', '--run', $run, '--apply') | Out-Null
        $back = (Get-MMAgent).MemoryCompression
        Add-Result 'revert turns it back on' $(if ($back) { 'PASS' } else { 'FAIL' }) "MemoryCompression=$back"
      }
    }
  }
}

# ---- Adapter ------------------------------------------------------------------------------------------------------------
if (Wants 'Adapter') {
  Section 'Per-adapter setting (Energy-Efficient Ethernet)'
  $props = @(Get-NetAdapterAdvancedProperty -RegistryKeyword '*EEE' -ErrorAction SilentlyContinue)
  if ($props.Count -eq 0) { Add-Result 'candidate adapter' 'SKIPPED' 'no network adapter on this PC declares the *EEE keyword (typical for virtual adapters): per-adapter settings cannot be tested here' }
  elseif ($props | Where-Object { "$($_.RegistryValue)" -eq '0' }) { Add-Result 'precondition' 'SKIPPED' 'an adapter already has EEE off' }
  elseif (Can-Run 'turn Energy-Efficient Ethernet off on every adapter that declares it (adapters restart: a brief network drop) and revert') {
    $beforeVals = $props | ForEach-Object { "$($_.Name)=$($_.RegistryValue)" }
    $ap = Invoke-Mesura @('windows', 'apply', '--id', 'network.ethernet-eee-off', '--apply', '--no-restore-point')
    $run = Run-Id $ap.Text
    Start-Sleep -Seconds 5
    $now = @(Get-NetAdapterAdvancedProperty -RegistryKeyword '*EEE' -ErrorAction SilentlyContinue) | ForEach-Object { "$($_.Name)=$($_.RegistryValue)" }
    Add-Result 'apply sets the keyword on the adapters that declare it' $(if (-not ($now -notmatch '=0$') -and $run) { 'PASS' } else { 'FAIL' }) ("before: " + ($beforeVals -join ', ') + "; now: " + ($now -join ', '))
    if ($run) {
      Invoke-Mesura @('windows', 'revert', '--run', $run, '--apply') | Out-Null
      Start-Sleep -Seconds 5
      $back = @(Get-NetAdapterAdvancedProperty -RegistryKeyword '*EEE' -ErrorAction SilentlyContinue) | ForEach-Object { "$($_.Name)=$($_.RegistryValue)" }
      Add-Result 'revert restores every adapter' $(if (($back -join ',') -eq ($beforeVals -join ',')) { 'PASS' } else { 'FAIL' }) ("after revert: " + ($back -join ', '))
    }
  }
}

# ---- report -------------------------------------------------------------------------------------------------------------
$pass = @($results | Where-Object Status -eq 'PASS').Count
$fail = @($results | Where-Object Status -eq 'FAIL').Count
$skip = @($results | Where-Object Status -eq 'SKIPPED').Count
$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Mesura operations test")
$md.Add("")
$md.Add("- Mode: $(if ($Run) { 'RUN' } else { 'dry run (nothing changed)' })   Elevated: $elevated")
$md.Add("- Windows: $($cv.ProductName) $($cv.DisplayVersion), build $($cv.CurrentBuild).$($cv.UBR), edition $($cv.EditionID)")
$md.Add("- Mesura CLI: $ver ($Cli)")
$md.Add("- Virtual machine: $((Get-CimInstance Win32_ComputerSystem).HypervisorPresent)   BitLocker on C: $bitlockerOn")
$md.Add("- Result: $pass passed, $fail FAILED, $skip skipped")
$md.Add("")
$md.Add("| Scenario | Step | Status | Detail |")
$md.Add("|---|---|---|---|")
foreach ($r in $results) { $md.Add("| $($r.Scenario) | $($r.Step) | $($r.Status) | $(($r.Detail -replace '\|', '/' -replace '\r?\n', ' '))" + " |") }
$md.Add("")
$md.Add("Skipped steps were NOT verified on this machine. PASS only means this machine did what Windows' own tools showed; it says nothing about other editions, builds or hardware.")
Set-Content -Path $Report -Value $md -Encoding UTF8
Write-Host "`nResult: $pass passed, $fail FAILED, $skip skipped. Report: $Report" -ForegroundColor $(if ($fail) { 'Red' } else { 'Cyan' })
exit $(if ($fail) { 1 } else { 0 })
