# Start-up compatibility matrix: starts a published Mesura.exe under a set of hostile environments and records what happened,
# so a start-up problem can be reproduced and a fix re-checked (docs/compat-matrix.md has the last results).
#
#   powershell -ExecutionPolicy Bypass -File tools\compat-matrix.ps1 [-Exe <Mesura.exe>] [-Wait 12] [-Out <file.md>]
#
# Each scenario launches `Mesura.exe --no-elevate` (no UAC prompt), waits, and reports: did a window appear, did the start log reach
# "opening the window", what the log said, and any Windows event about the crash. Folders it creates (read-only ones included) are
# in %TEMP% and removed afterwards. It changes nothing else, and never runs while another Mesura is open.
param(
  [string]$Exe = '',
  [int]$Wait = 12,
  [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $repo 'artifacts\current\Mesura\Mesura.exe' }
if (-not (Test-Path $Exe)) { throw "No Mesura.exe at $Exe." }
$Exe = (Resolve-Path $Exe).Path
$folder = Split-Path -Parent $Exe
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is already running; close it first.' }

Add-Type -AssemblyName System.Security
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$work = Join-Path $env:TEMP ("mesura-compat-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$cleanup = New-Object System.Collections.Generic.List[string]

function Deny-Write([string]$dir) {
  $acl = Get-Acl $dir
  $rule = New-Object Security.AccessControl.FileSystemAccessRule($user, 'Write,CreateFiles,CreateDirectories,AppendData,WriteData', 'ContainerInherit,ObjectInherit', 'None', 'Deny')
  $acl.AddAccessRule($rule); Set-Acl $dir $acl
  $cleanup.Add($dir)
}
function Restore-Dir([string]$dir) {
  try {
    $acl = Get-Acl $dir
    foreach ($r in @($acl.Access | Where-Object { $_.AccessControlType -eq 'Deny' -and $_.IdentityReference.Value -eq $user })) { [void]$acl.RemoveAccessRule($r) }
    Set-Acl $dir $acl
  } catch {}
}

# a log written by this launch: the startup log is under LOCALAPPDATA (or TEMP when that can't be written)
function Read-Log([string]$localAppData, [string]$temp, [datetime]$since) {
  # only the lines this launch wrote (the log is shared by every launch): each starts with 'yyyy-MM-dd HH:mm:ss.fff'
  $mine = @()
  foreach ($p in @((Join-Path $localAppData 'Mesura\logs\mesura-startup.log'), (Join-Path $temp 'Mesura\logs\mesura-startup.log'), (Join-Path $temp 'mesura-startup.log'))) {
    if (-not (Test-Path $p)) { continue }
    foreach ($line in (Get-Content $p -Tail 40 -ErrorAction SilentlyContinue)) {
      $t = [datetime]::MinValue
      if ($line.Length -gt 23 -and [datetime]::TryParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null, 'None', [ref]$t) -and $t -ge $since.AddSeconds(-1)) { $mine += $line }
    }
  }
  return $mine
}

$results = New-Object System.Collections.Generic.List[object]
function Launch([string]$name, [string]$exe, [hashtable]$env = @{}, [bool]$shell = $false, [string]$expect = 'starts') {
  $psi = New-Object System.Diagnostics.ProcessStartInfo($exe, '--no-elevate')
  $psi.UseShellExecute = $shell
  if (-not $shell) {
    $psi.RedirectStandardError = $true; $psi.RedirectStandardOutput = $true
    foreach ($k in $env.Keys) { $psi.EnvironmentVariables[$k] = $env[$k] }
  }
  $localAppData = if ($env.ContainsKey('LOCALAPPDATA')) { $env['LOCALAPPDATA'] } else { $env:LOCALAPPDATA }
  $temp = if ($env.ContainsKey('TEMP')) { $env['TEMP'] } else { $env:TEMP }
  $marker = Get-Date
  $p = $null
  try { $p = [System.Diagnostics.Process]::Start($psi) } catch { $results.Add([pscustomobject]@{ Scenario = $name; Result = 'could not start'; Detail = $_.Exception.Message; Expected = $expect }); return }
  $exited = $p.WaitForExit($Wait * 1000)
  $window = $false; $title = ''
  if (-not $exited) { $p.Refresh(); $window = $p.MainWindowHandle -ne 0; $title = $p.MainWindowTitle }
  $code = if ($exited) { $p.ExitCode } else { $null }
  if (-not $exited) { try { $p.Kill() } catch {} ; Start-Sleep -Milliseconds 400 }
  # Windows' own record of a crash before managed code ran
  $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $marker } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'Mesura' } | Select-Object -First 1 | ForEach-Object { "event $($_.Id)" })
  $log = Read-Log $localAppData $temp $marker
  $logText = ($log -join ' | ')
  $reached = $logText -match 'stage: opening the window'
  $missing = $logText -match 'files missing beside'
  $result = if ($reached -and $window) { 'starts' } elseif ($window -and -not $log.Count -and $expect -eq 'window, no log') { 'window, no log' } elseif ($missing -and $window) { 'explains: native files missing' } elseif ($window) { 'a window appeared, start log did not reach the main window' } elseif ($exited) { "exited with code $code" } else { 'running, no window' }
  $detail = (@($(if ($ev) { $ev -join ',' }), $(if ($log.Count) { 'log: ' + ($log[-1] -replace '^\S+ \S+ ', '') } else { 'no log' })) | Where-Object { $_ }) -join '; '
  $results.Add([pscustomobject]@{ Scenario = $name; Result = $result; Detail = $detail; Expected = $expect })
}

try {
  # 1. baseline
  Launch 'normal start' $Exe

  # 2-3. TEMP / TMP unusable
  $gone = Join-Path $work 'does-not-exist'
  Launch 'TEMP and TMP point at a folder that does not exist' $Exe @{ TEMP = $gone; TMP = $gone }
  $asFile = Join-Path $work 'a-file.txt'; Set-Content $asFile 'x'
  Launch 'TEMP and TMP point at a file' $Exe @{ TEMP = $asFile; TMP = $asFile }

  # 4. TEMP read-only (access denied)
  $roTemp = Join-Path $work 'ro-temp'; New-Item -ItemType Directory $roTemp | Out-Null; Deny-Write $roTemp
  Launch 'TEMP and TMP are read-only (write denied)' $Exe @{ TEMP = $roTemp; TMP = $roTemp }

  # 5. a very long TEMP path
  $long = $work; while ($long.Length -lt 230) { $long = Join-Path $long 'segment-of-a-long-path' }
  try { New-Item -ItemType Directory $long -Force | Out-Null; Launch 'TEMP is a 230+ character path' $Exe @{ TEMP = $long; TMP = $long } } catch { $results.Add([pscustomobject]@{ Scenario = 'TEMP is a 230+ character path'; Result = 'could not set up'; Detail = $_.Exception.Message; Expected = 'starts' }) }

  # 6. LOCALAPPDATA missing / read-only / both it and TEMP unusable
  # (Windows' known-folder API, not the environment variable, decides where Mesura keeps its files, so write access to the real
  # folders is what is taken away here, and given back afterwards)
  $mesuraData = Join-Path $env:LOCALAPPDATA 'Mesura'
  $logs = Join-Path $mesuraData 'logs'
  New-Item -ItemType Directory $logs -Force | Out-Null
  Deny-Write $logs
  Launch 'the log folder cannot be written (the log goes to TEMP)' $Exe
  Restore-Dir $logs
  Deny-Write $mesuraData
  Launch '%LOCALAPPDATA%\Mesura cannot be written at all (settings, history, log)' $Exe
  Launch '... and TEMP does not exist either (the log creates it)' $Exe @{ TEMP = $gone; TMP = $gone }
  Restore-Dir $mesuraData

  # 7. the folder itself: a path with spaces and non-Latin letters; a read-only folder
  $odd = Join-Path $work ("Папка с пробелами и Ёё " + [char]0x00E9)
  New-Item -ItemType Directory $odd | Out-Null
  Copy-Item -Path (Join-Path $folder '*') -Destination $odd -Recurse -Force
  Launch 'folder path has spaces and Cyrillic letters' (Join-Path $odd 'Mesura.exe')
  $ro = Join-Path $work 'ro-folder'; New-Item -ItemType Directory $ro | Out-Null
  Copy-Item -Path (Join-Path $folder '*') -Destination $ro -Recurse -Force; Deny-Write $ro
  Launch 'Mesura folder is read-only' (Join-Path $ro 'Mesura.exe')

  # 8. the exe copied out of its folder: native libraries missing
  $alone = Join-Path $work 'alone'; New-Item -ItemType Directory $alone | Out-Null; Copy-Item $Exe $alone
  Launch 'Mesura.exe copied alone (native libraries missing)' (Join-Path $alone 'Mesura.exe') @{} $false 'explains: native files missing'

  # 9. downloaded-from-the-internet mark: only a shell launch (what double-clicking does) is asked about it
  $mark = Join-Path $work 'marked'; New-Item -ItemType Directory $mark | Out-Null
  Copy-Item -Path (Join-Path $folder '*') -Destination $mark -Recurse -Force
  Set-Content -Path (Join-Path $mark 'Mesura.exe') -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
  Launch 'marked as downloaded (Zone.Identifier), started directly' (Join-Path $mark 'Mesura.exe')
}
finally {
  Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  foreach ($d in $cleanup) { Restore-Dir $d }
  Start-Sleep -Milliseconds 500
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$lines = @("# Start-up matrix", "", "- Build: $((Get-Item $Exe).VersionInfo.ProductVersion)", "- Windows: $(if ([int]$cv.CurrentBuild -ge 22000) { $cv.ProductName -replace 'Windows 10', 'Windows 11' } else { $cv.ProductName }) $($cv.DisplayVersion), build $($cv.CurrentBuild).$($cv.UBR), $($cv.EditionID)", "- Run: $(Get-Date -Format 'yyyy-MM-dd HH:mm'), each scenario waited $Wait s", "", "| Scenario | Result | Expected | Detail |", "|---|---|---|---|")
foreach ($r in $results) { $lines += "| $($r.Scenario) | $($r.Result) | $($r.Expected) | $(($r.Detail -replace '\|', '/')) |" }
$lines | ForEach-Object { Write-Output $_ }
if ($Out) { $lines | Set-Content -Path $Out -Encoding UTF8 }
$bad = @($results | Where-Object { $_.Result -ne $_.Expected })
Write-Output ""
Write-Output $(if ($bad.Count) { "$($bad.Count) scenario(s) did not match what is expected: " + (($bad | ForEach-Object Scenario) -join '; ') } else { 'Every scenario matched what is expected.' })
exit $(if ($bad.Count) { 1 } else { 0 })
