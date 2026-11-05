# Runs a read-only GPU probe one section at a time, each in its own process with a time limit, and writes ONE json file with what every section said,
# including the ones that crashed, hung, were blocked or could not be started. Nothing is changed on the PC: the probe only reads (it never calls a setter), and this script writes only its output file.
#
#   cd <folder with the probe and this script>
#   powershell -ExecutionPolicy Bypass -File .\run-amd-probe.ps1 [-Out "$env:USERPROFILE\Desktop\amd-probe.json"]                  (Radeon PC: adlx_probe.exe, native\adlx-probe)
#   powershell -ExecutionPolicy Bypass -File .\run-amd-probe.ps1 -Kind nvidia [-Out "$env:USERPROFILE\Desktop\nvapi-probe.json"]  (NVIDIA PC: nvapi_probe.exe, native\nvapi-probe)
#
# Why: the first probe wrote an empty file on a Radeon PC and nobody could tell whether it crashed, hung or was blocked. Here the file always says which.
param(
  [ValidateSet('amd', 'nvidia')][string]$Kind = 'amd',
  [string]$Exe = '',
  [string]$Out = '',
  [int]$SectionSeconds = 60
)
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot $(if ($Kind -eq 'nvidia') { 'nvapi_probe.exe' } else { 'adlx_probe.exe' }) }
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) $(if ($Kind -eq 'nvidia') { 'nvapi-probe.json' } else { 'amd-probe.json' }) }
$sectionNames = if ($Kind -eq 'nvidia') { @('nvapi', 'displays', 'settings') } else { @('init', 'gpus', 'settings3d', 'tuning', 'metrics') }
$ErrorActionPreference = 'Continue'
function J([string]$s) { if ($null -eq $s) { 'null' } else { '"' + ($s -replace '\\', '\\' -replace '"', '\"' -replace "`r", '' -replace "`n", '\n' -replace "`t", ' ') + '"' } }

$sections = [ordered]@{}
$start = Get-Date
$info = [ordered]@{ script = 'run-amd-probe.ps1'; started = $start.ToString('s'); exe = $Exe }
$os = Get-CimInstance Win32_OperatingSystem
$info.windows = "$($os.Caption) build $($os.BuildNumber)"
$info.exeFound = Test-Path $Exe
if ($info.exeFound) {
  $info.exeSha256 = (Get-FileHash $Exe -Algorithm SHA256).Hash
  $zone = Get-Content -Path $Exe -Stream Zone.Identifier -ErrorAction SilentlyContinue
  $info.markOfTheWeb = [bool]$zone
  $sig = Get-AuthenticodeSignature $Exe -ErrorAction SilentlyContinue
  $info.signature = "$($sig.Status)"
}
$dll = Join-Path $env:WINDIR ('System32\' + $(if ($Kind -eq 'nvidia') { 'nvapi64.dll' } else { 'amdadlx64.dll' }))
$info.kind = $Kind
$info.systemDll = if (Test-Path $dll) { (Get-Item $dll).VersionInfo.FileVersion } else { 'not present' }
$info.defender = try { $d = Get-MpComputerStatus -ErrorAction Stop; "RealTimeProtection=$($d.RealTimeProtectionEnabled) AMServiceEnabled=$($d.AMServiceEnabled)" } catch { 'not readable' }

foreach ($name in $sectionNames) {
  $entry = [ordered]@{}
  if (-not $info.exeFound) { $entry.status = 'exe-not-found'; $sections[$name] = $entry; continue }
  $stdout = Join-Path $env:TEMP "adlx-probe-$name.out"; $stderr = Join-Path $env:TEMP "adlx-probe-$name.err"
  Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
  $t = Get-Date
  try {
    $p = Start-Process -FilePath $Exe -ArgumentList @('--section', $name, '--timeout', '40') -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -ErrorAction Stop
    $null = $p.Handle
    if (-not $p.WaitForExit($SectionSeconds * 1000)) { try { $p.Kill() } catch { }; $entry.status = 'killed-by-script'; $entry.exitCode = $null } else { $entry.exitCode = $p.ExitCode }
  } catch {
    $entry.status = 'could-not-start'; $entry.error = $_.Exception.Message
  }
  $entry.seconds = [math]::Round(((Get-Date) - $t).TotalSeconds, 1)
  $text = (Get-Content $stdout -Raw -ErrorAction SilentlyContinue)
  $err = @(Get-Content $stderr -ErrorAction SilentlyContinue | Select-Object -Last 12)
  if (-not $entry.status) {
    $entry.status = switch ($entry.exitCode) { 0 { 'ok' } 3 { 'not-available' } 4 { 'hang' } 5 { 'crash' } default { "exit-$($entry.exitCode)" } }
  }
  if ($text -and $text.Trim().StartsWith('{')) { $entry.json = $text.Trim() } else { $entry.stdoutEmpty = (-not $text) }
  $entry.lastStages = $err
  $sections[$name] = $entry
  Write-Host ("{0,-11} {1,-18} {2}s" -f $name, $entry.status, $entry.seconds)
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{"info":{')
[void]$sb.Append(($info.GetEnumerator() | ForEach-Object { '"' + $_.Key + '":' + $(if ($_.Value -is [bool]) { "$($_.Value)".ToLower() } else { J "$($_.Value)" }) }) -join ',')
[void]$sb.Append('},"sections":{')
$first = $true
foreach ($k in $sections.Keys) {
  $e = $sections[$k]
  if (-not $first) { [void]$sb.Append(',') }; $first = $false
  [void]$sb.Append('"' + $k + '":{"status":' + (J $e.status) + ',"exitCode":' + $(if ($null -ne $e.exitCode) { $e.exitCode } else { 'null' }) + ',"seconds":' + $(if ($null -ne $e.seconds) { $e.seconds.ToString([Globalization.CultureInfo]::InvariantCulture) } else { 'null' }))
  if ($e.error) { [void]$sb.Append(',"error":' + (J $e.error)) }
  if ($e.json) { [void]$sb.Append(',"json":' + $e.json) } elseif ($e.Contains('stdoutEmpty')) { [void]$sb.Append(',"stdoutEmpty":' + "$($e.stdoutEmpty)".ToLower()) }
  [void]$sb.Append(',"lastStages":[' + (($e.lastStages | ForEach-Object { J "$_" }) -join ',') + ']}')
}
[void]$sb.Append('}}')
[IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $Out ($((Get-Item $Out).Length) bytes). Send it back."
