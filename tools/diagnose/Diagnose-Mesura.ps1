# Collects what is needed to find out why Mesura does not start on a PC, into one text file on the Desktop.
# Reads only: it changes nothing, installs nothing and sends nothing; you send the file yourself.
# Run it by double-clicking "Diagnose Mesura.cmd" in the Mesura folder (or: powershell -ExecutionPolicy Bypass -File Diagnose-Mesura.ps1).
# Personal data kept out: the Windows user name in paths is replaced by %USERNAME%; no serial numbers, accounts or file contents.
param([string]$Folder = $PSScriptRoot, [string]$Out = '')

$ErrorActionPreference = 'Continue'
$user = $env:USERNAME
$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$text = '') { $lines.Add(($text -replace [regex]::Escape($user), '%USERNAME%')) }
function Section([string]$title) { Add-Line ''; Add-Line "== $title" }
function Try-Do([scriptblock]$block, [string]$what) { try { & $block } catch { Add-Line "   ($what failed: $($_.Exception.Message))" } }

if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) ("Mesura-diagnostics-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$exe = Join-Path $Folder 'Mesura.exe'
if (-not (Test-Path $exe)) { $c = Get-ChildItem -Path $Folder -Filter 'Mesura.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1; if ($c) { $exe = $c.FullName; $Folder = $c.DirectoryName } }

Add-Line "Mesura diagnostics, made $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

Section 'Windows'
Try-Do {
  $os = Get-CimInstance Win32_OperatingSystem
  $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
  $name = if ([int]$cv.CurrentBuild -ge 22000) { $cv.ProductName -replace 'Windows 10', 'Windows 11' } else { $cv.ProductName }
  Add-Line "   $name $($cv.DisplayVersion), build $($cv.CurrentBuild).$($cv.UBR), edition $($cv.EditionID), install type $($cv.InstallationType)"
  Add-Line "   OS architecture $($os.OSArchitecture); process is 64-bit: $([Environment]::Is64BitProcess); processor $($env:PROCESSOR_ARCHITECTURE) / $($env:PROCESSOR_ARCHITEW6432)"
  Add-Line "   UI language $((Get-UICulture).Name); system locale $((Get-WinSystemLocale).Name); RAM $([math]::Round($os.TotalVisibleMemorySize/1MB,1)) GB; free on system drive $([math]::Round((Get-PSDrive ($env:SystemDrive.TrimEnd(':'))).Free/1GB,1)) GB"
  Add-Line ("   this build of Mesura needs Windows 10 1607 (build 14393) or later, 64-bit: " + $(if ([int]$cv.CurrentBuild -ge 14393 -and $os.OSArchitecture -match '64') { 'this PC qualifies' } else { 'THIS PC DOES NOT' }))
} 'OS read'

Section 'Mesura files'
if (Test-Path $exe) {
  Add-Line "   folder: $Folder"
  Get-ChildItem $Folder -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { Add-Line ("   {0,12:N0}  {1}" -f $_.Length, $_.Name) }
  Try-Do { Add-Line "   Mesura.exe SHA-256: $((Get-FileHash $exe -Algorithm SHA256).Hash.ToLower())" } 'hash'
  Try-Do { Add-Line "   Mesura.exe version: $((Get-Item $exe).VersionInfo.ProductVersion)" } 'version'
  $zone = Get-Content "$exe`:Zone.Identifier" -ErrorAction SilentlyContinue
  Add-Line ("   downloaded-from-the-internet mark (Zone.Identifier): " + $(if ($zone) { ($zone -join '; ') } else { 'none' }))
  $productVersion = (Get-Item $exe).VersionInfo.ProductVersion
  if ($productVersion -match '^0\.1\.') {
    # 0.1.x unpacks the WPF native libraries into %TEMP% when it starts and needs nothing beside the exe; if TEMP can't be used it
    # ends before its own code runs and leaves no log. The TEMP check below is the one that matters for it.
    Add-Line "   version $productVersion unpacks its native libraries into TEMP at start: nothing is expected beside the exe; see the TEMP check below"
  } else {
    foreach ($n in 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll', 'PenImc_cor3.dll', 'vcruntime140_cor3.dll', 'D3DCompiler_47_cor3.dll') {
      Add-Line ("   {0}: {1}" -f $n, $(if (Test-Path (Join-Path $Folder $n)) { 'present' } else { 'MISSING (a single-file Mesura.exe needs these beside it)' }))
    }
  }
  Add-Line ("   environment: DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + $(if ($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) { $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR } else { '(not set)' }) + "; TEMP exists: " + (Test-Path $env:TEMP) + "; TMP exists: " + (Test-Path $env:TMP))
} else { Add-Line "   Mesura.exe not found in $Folder" }

Section 'Things that block or break an unsigned program'
Try-Do {
  $sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
  $sacText = switch ($sac) { 0 { 'off' } 1 { 'ON (blocks unsigned apps it does not trust)' } 2 { 'evaluation mode (may turn on and block unsigned apps)' } default { "not present ($sac)" } }
  Add-Line "   Smart App Control: $sacText"
} 'Smart App Control read'
Try-Do {
  $srp = Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers'; $apl = Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2'
  Add-Line "   Software Restriction Policies present: $srp; AppLocker policy present: $apl"
} 'policy read'
Try-Do { Add-Line "   Defender real-time protection: $((Get-MpComputerStatus -ErrorAction Stop).RealTimeProtectionEnabled)" } 'Defender status'
Try-Do {
  $d = Get-MpThreatDetection -ErrorAction Stop | Where-Object { ($_.Resources -join ' ') -match 'Mesura' } | Select-Object -First 5
  if ($d) { foreach ($x in $d) { Add-Line "   Defender detection: $($x.InitialDetectionTime) action $($x.ActionSuccess) $($x.Resources -join ',')" } } else { Add-Line '   Defender detections that mention Mesura: none' }
} 'Defender detections'
Try-Do {
  Add-Line "   TEMP = $env:TEMP (writable: $(try { $f = Join-Path $env:TEMP ('mesura-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType File $f -ErrorAction Stop | Out-Null; Remove-Item $f; $true } catch { $false }))"
  Add-Line "   LOCALAPPDATA = $env:LOCALAPPDATA (writable: $(try { $f = Join-Path $env:LOCALAPPDATA ('mesura-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType File $f -ErrorAction Stop | Out-Null; Remove-Item $f; $true } catch { $false }))"
  $nonAscii = ($env:USERPROFILE -match '[^\u0000-\u007F]')
  Add-Line "   user profile path has non-ASCII characters: $nonAscii; length of the Mesura path: $($Folder.Length)"
  Add-Line "   DOTNET_BUNDLE_EXTRACT_BASE_DIR = '$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR'; DOTNET_ROOT = '$env:DOTNET_ROOT'"
} 'folder checks'
Try-Do {
  $vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
  Add-Line ("   Visual C++ 2015-2022 x64 runtime: " + $(if ($vc) { "installed $($vc.Version)" } else { 'not installed (Mesura carries its own copy)' }))
} 'VC++ read'
Try-Do {
  $gpu = Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" }
  Add-Line "   graphics: $($gpu -join '; ')"
} 'graphics read'

Section 'Windows event log: crashes and runtime errors that mention Mesura (last 30 days)'
Try-Do {
  $since = (Get-Date).AddDays(-30)
  $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Application Hang', 'SideBySide', 'Windows Error Reporting' -and $_.Message -match 'Mesura|mesura' } |
    Select-Object -First 8
  if (-not $ev) { Add-Line '   none found' }
  foreach ($e in $ev) { Add-Line "   [$($e.TimeCreated)] $($e.ProviderName) $($e.Id)"; $m = ($e.Message -replace '\s+', ' '); Add-Line ('      ' + $m.Substring(0, [Math]::Min(700, $m.Length))) }
} 'event log read'

Section 'Mesura startup log (%LOCALAPPDATA%\Mesura\logs)'
$log = Join-Path $env:LOCALAPPDATA 'Mesura\logs\mesura-startup.log'
if (Test-Path $log) { Get-Content $log -Tail 60 | ForEach-Object { Add-Line ('   ' + $_) } } else { Add-Line '   no startup log: Mesura has never reached its first line of code on this PC (a block before it runs, or a version without the log)' }
$alt = Join-Path $env:TEMP 'Mesura\logs\mesura-startup.log'
if (Test-Path $alt) { Add-Line '   (a log in the temp folder:)'; Get-Content $alt -Tail 40 | ForEach-Object { Add-Line ('   ' + $_) } }

[System.IO.File]::WriteAllLines($Out, $lines, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Done. The report is here:`n  $Out`nSend that file to whoever asked for it. Nothing was changed or sent."
