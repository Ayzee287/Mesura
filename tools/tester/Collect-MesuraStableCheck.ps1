# Mesura 0.6 pre-release check: collects what the developer cannot see from outside, in one zip on your Desktop. READ ONLY: it changes no setting, installs nothing, imports no profile, and
# sends nothing anywhere (you send the zip yourself). Run it from an ELEVATED PowerShell (right-click > Run as administrator): one of the checks is how Mesura behaves when it is elevated.
#
#   powershell -ExecutionPolicy Bypass -File Collect-MesuraStableCheck.ps1 [-Cli <path to mesura.exe>]
#
# What it records (report.txt in the zip):
#   1. Windows build, edition, whether UAC is on, whether this window is elevated.
#   2. Which Mesura copies are installed (per user / all users), their versions, and `mesura update copies`.
#   3. NVIDIA: the driver version (nvidia-smi, read only) and `mesura setup nvpi-check`: where NVIDIA Profile Inspector is, which build, whether Mesura may run it elevated, and ONE export of your customised
#      profiles (a read) saying which way it ran. This is the check no Sandbox can do: whether NVIDIA Profile Inspector can read the profiles with your normal (unelevated) rights.
#   4. Power plans as Windows lists them (powercfg /list, never edited here) and the restore points.
#   5. 0.6: what Mesura believes about this PC (`mesura hardware`) and how it plans both levels for it (`mesura windows plan`, Mesura Reference and Maximum performance). Read only: nothing is applied.
#   6. Mesura's own records of what it did on this PC: the last setup runs (what each step reported, including the Maximum Performance run), the logs, and the history list.
#      These files are copied into the zip (your Windows user name and the computer name are replaced by %USERNAME% and %COMPUTERNAME%); read them first if you like.
param([string]$Cli = '')
$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path ([IO.Path]::GetTempPath()) "mesura-stable-check-$stamp"
New-Item -ItemType Directory -Force $work | Out-Null
$report = Join-Path $work 'report.txt'
function Say([string]$line) { Write-Host $line; Add-Content -Path $report -Value $line -Encoding utf8 }
function Section([string]$title) { Say ''; Say "== $title" }
function Hide([string]$text) { if ($null -eq $text) { return '' }; $text.Replace($env:USERNAME, '%USERNAME%').Replace($env:COMPUTERNAME, '%COMPUTERNAME%') }
function Try-Run([scriptblock]$block) { try { (& $block 2>&1 | ForEach-Object { "$_" }) -join "`r`n" } catch { "failed: $($_.Exception.Message)" } }

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say "Mesura 0.6 check, collected $(Get-Date -Format s)"

Section '1. Windows'
$os = Get-CimInstance Win32_OperatingSystem
Say "Windows: $($os.Caption) build $($os.BuildNumber).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)"
$uac = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue
Say "UAC: EnableLUA=$($uac.EnableLUA) ConsentPromptBehaviorAdmin=$($uac.ConsentPromptBehaviorAdmin) PromptOnSecureDesktop=$($uac.PromptOnSecureDesktop)"
Say "This window is elevated: $elevated"
if (-not $elevated) { Say 'NOTE: not elevated. Please run it again from an elevated PowerShell if you can; the nvpi check below is most useful then.' }

Section '2. Mesura copies'
$copies = @()
foreach ($hive in 'HKCU', 'HKLM') {
  $k = Get-ItemProperty "${hive}:\Software\Mesura" -ErrorAction SilentlyContinue
  if ($k.InstallDir) { Say "$hive InstallDir: $(Hide $k.InstallDir)"; $copies += $k.InstallDir }
}
# The command line of the build this script came with (the 0.6 folder) comes first: an older installed copy does not have the checks below.
if (-not $Cli -and (Test-Path (Join-Path $PSScriptRoot 'cli\mesura.exe'))) { $Cli = Join-Path $PSScriptRoot 'cli\mesura.exe' }
if (-not $Cli) {
  foreach ($c in $copies) { $try = Join-Path $c 'cli\mesura.exe'; if (-not $Cli -and (Test-Path $try)) { $Cli = $try } }
}
if (-not $Cli -or -not (Test-Path $Cli)) { Say 'No mesura.exe (command line) found: pass -Cli <path to mesura.exe>. The checks that need it are skipped.'; $Cli = '' }
else {
  Say "Command line: $(Hide $Cli)"
  Say "File version: $((Get-Item $Cli).VersionInfo.ProductVersion)"
  Say (Hide (Try-Run { & $Cli update copies }))
}

Section '3. NVIDIA'
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($smi) { Say (Hide (Try-Run { & $smi.Source --query-gpu=name,driver_version --format=csv,noheader })) } else { Say 'nvidia-smi not found (no NVIDIA driver, or not on PATH).' }
if ($Cli) {
  Say '-- mesura setup nvpi-check (read only; it may take up to a few minutes if NVIDIA Profile Inspector shows a message and waits)'
  $job = Start-Process $Cli -ArgumentList 'setup', 'nvpi-check' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $work 'nvpi-check.txt') -RedirectStandardError (Join-Path $work 'nvpi-check-err.txt')
  $null = $job.Handle   # keeps the exit code readable after the process ends
  if (-not $job.WaitForExit(420000)) { try { $job.Kill() } catch { }; Say 'nvpi-check did not finish in 7 minutes and was stopped (NVIDIA Profile Inspector may still be open with a message: read it, then close it).' }
  Say (Hide ((Get-Content (Join-Path $work 'nvpi-check.txt') -Raw -ErrorAction SilentlyContinue) + (Get-Content (Join-Path $work 'nvpi-check-err.txt') -Raw -ErrorAction SilentlyContinue)))
  Say "exit code: $($job.ExitCode)"
}

Section '4. Power plans and restore points (read only)'
Say (Hide (Try-Run { powercfg /list }))
Say (Hide (Try-Run { powercfg /getactivescheme }))
if ($Cli) { Say (Hide (Try-Run { & $Cli windows restore-points })) }

Section '5. Hardware and the profiles planned for it (read only, nothing is applied)'
if ($Cli) {
  Say '-- mesura hardware'
  Say (Hide (Try-Run { & $Cli hardware }))
  $hw = Try-Run { & $Cli hardware --json }
  Set-Content -Path (Join-Path $work 'hardware.json') -Value (Hide $hw) -Encoding utf8
  foreach ($profile in 'recommended-gaming', 'maximum-performance') {
    Say ''
    Say "-- mesura windows plan --profile $profile"
    $plan = Hide (Try-Run { & $Cli windows plan --profile $profile })
    Say $plan
    Set-Content -Path (Join-Path $work "plan-$profile.txt") -Value $plan -Encoding utf8
  }
  if (-not $elevated) { Say 'NOTE: not elevated, so a few states (for example memory compression) read as "can''t tell"; run it elevated for the full picture.' }
} else { Say 'No command line: skipped.' }

Section '6. Mesura records on this PC'
$data = Join-Path $env:LOCALAPPDATA 'Mesura'
if (-not (Test-Path $data)) { Say "No $(Hide $data) folder." }
else {
  $dest = Join-Path $work 'mesura-data'
  New-Item -ItemType Directory -Force $dest | Out-Null
  # The last setup runs: run.json says, step by step, what each one reported.
  $history = Join-Path $data 'Setup\history'
  if (Test-Path $history) {
    foreach ($run in (Get-ChildItem $history -Directory | Sort-Object Name -Descending | Select-Object -First 6)) {
      $to = Join-Path $dest "history\$($run.Name)"
      New-Item -ItemType Directory -Force $to | Out-Null
      Get-ChildItem $run.FullName -File | ForEach-Object { Copy-Item $_.FullName $to }
      $files = Join-Path $run.FullName 'files'
      if (Test-Path $files) { Get-ChildItem $files -File | Where-Object { $_.Length -lt 2MB } | ForEach-Object { New-Item -ItemType Directory -Force (Join-Path $to 'files') | Out-Null; Copy-Item $_.FullName (Join-Path $to 'files') } }
      Say "history run: $($run.Name)"
    }
  }
  foreach ($file in 'Setup\pending-setup.json', 'app-settings.json') { $p = Join-Path $data $file; if (Test-Path $p) { Copy-Item $p (Join-Path $dest ([IO.Path]::GetFileName($file))); Say "copied $file" } }
  $logs = Join-Path $data 'logs'
  if (Test-Path $logs) { Get-ChildItem $logs -File | Sort-Object LastWriteTime -Descending | Select-Object -First 8 | ForEach-Object { New-Item -ItemType Directory -Force (Join-Path $dest 'logs') | Out-Null; Copy-Item $_.FullName (Join-Path $dest 'logs'); Say "copied log $($_.Name)" } }
  # Your user name and the computer name never leave this PC in the text files.
  Get-ChildItem $dest -Recurse -File | Where-Object { $_.Extension -in '.json', '.log', '.txt', '.nip', '.xml' } | ForEach-Object {
    $raw = [IO.File]::ReadAllText($_.FullName)
    $clean = $raw.Replace($env:USERNAME, '%USERNAME%').Replace($env:COMPUTERNAME, '%COMPUTERNAME%')
    if ($clean -ne $raw) { [IO.File]::WriteAllText($_.FullName, $clean) }
  }
}
if ($Cli) { Say (Hide (Try-Run { & $Cli history list })) }

$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "mesura-stable-check-$stamp.zip"
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
Say ''
Say "Done. Send this file: $zip"
Write-Host ''
Write-Host "Done. Send this file: $zip" -ForegroundColor Green
