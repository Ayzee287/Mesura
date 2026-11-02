# Collects, READ ONLY, what a PC's graphics driver actually has in the registry and in its tools, so GPU tweaks can be written from evidence instead of from tweak guides
# (docs/gpu-0.5.md: "each entry needs the exact value name, the driver generation and an observed effect").
#
# It changes nothing: no registry write, no driver or service call, no network access. It reads
#   * every display adapter (Win32_VideoController) and the PnP device behind it,
#   * the display-class registry key Windows really uses for each adapter (found through the device's Driver value, never by index), with every value and one level of subkeys,
#   * the graphics settings keys (GraphicsDrivers, DirectX user GPU preferences),
#   * the vendor services (NVIDIA, AMD): name, start type, state,
#   * nvidia-smi's full query when it is installed, and whether AMD's ADLX runtime (amdadlx64.dll) is present, with its version,
#   * the active power plan.
# The computer name and the user name are replaced by placeholders. Run it as yourself; no administrator rights are needed (a few HKLM keys read the same without them).
#
#   powershell -ExecutionPolicy Bypass -File tools\collect-gpu-evidence.ps1 -Out <file.txt>
param([Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Continue'
$lines = New-Object System.Collections.Generic.List[string]
function W([string]$text = '') { $lines.Add($text) }
function Scrub([string]$text) { $text -replace [regex]::Escape($env:COMPUTERNAME), '<computer>' -replace [regex]::Escape($env:USERNAME), '<user>' }

W "Mesura GPU evidence, collected $(Get-Date -Format s) (read only)"
$os = Get-CimInstance Win32_OperatingSystem
W "Windows: $($os.Caption) build $($os.BuildNumber).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)"
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
W "CPU: $($cpu.Name.Trim())"
W ''

W '== Display adapters (Win32_VideoController)'
foreach ($v in Get-CimInstance Win32_VideoController) {
  W ("  {0} | vendor {1} | driver {2} ({3}) | PNP {4} | VRAM {5} MB" -f $v.Name, $v.AdapterCompatibility, $v.DriverVersion, $v.DriverDate, $v.PNPDeviceID, [math]::Round($v.AdapterRAM / 1MB))
}
W ''

function Dump-Key([string]$path, [string]$indent, [int]$depth) {
  $key = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
  if (-not $key) { W "$indent(not present: $path)"; return }
  foreach ($name in $key.GetValueNames() | Sort-Object) {
    $kind = $key.GetValueKind($name)
    $value = $key.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
    $shown = switch ($kind) {
      'Binary' { $b = [byte[]]$value; if ($b.Length -gt 64) { "binary, $($b.Length) bytes" } else { 'binary ' + (($b | ForEach-Object { $_.ToString('x2') }) -join '') } }
      'MultiString' { ($value -join ' | ') }
      default { [string]$value }
    }
    $label = if ($name -eq '') { '(default)' } else { $name }
    W ("{0}{1} [{2}] = {3}" -f $indent, $label, $kind, (Scrub $shown))
  }
  if ($depth -gt 0) {
    foreach ($sub in $key.GetSubKeyNames() | Sort-Object) {
      W "$indent[$sub]"
      Dump-Key (Join-Path $path $sub) ($indent + '  ') ($depth - 1)
    }
  }
}

W '== Display-class registry key of each adapter (found through the device''s Driver value)'
$classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class'
foreach ($dev in Get-PnpDevice -Class Display -ErrorAction SilentlyContinue) {
  $driver = (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_Driver' -ErrorAction SilentlyContinue).Data
  W "-- $($dev.FriendlyName) | $($dev.InstanceId) | status $($dev.Status) | Driver value: $driver"
  if ($driver) {
    W "   $classRoot\$driver"
    Dump-Key "$classRoot\$driver" '   ' 1
  }
  W ''
}

W '== Graphics settings'
foreach ($p in 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers', 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences', 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm') {
  W "-- $p"
  Dump-Key $p '   ' 0
}
W ''

W '== Vendor services'
foreach ($s in Get-CimInstance Win32_Service | Where-Object { $_.Name -match 'nv|amd|radeon|ati' -or $_.DisplayName -match 'NVIDIA|AMD|Radeon' } | Sort-Object Name) {
  W ("  {0} | {1} | start {2} | {3}" -f $s.Name, $s.DisplayName, $s.StartMode, $s.State)
}
W ''

W '== NVIDIA'
$smi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if ($smi) { W "nvidia-smi: $($smi.Source)"; (& $smi.Source -q 2>&1) | ForEach-Object { W (Scrub $_) } } else { W 'nvidia-smi is not installed (no NVIDIA driver)' }
W ''

W '== AMD'
$adlx = Join-Path $env:WINDIR 'System32\amdadlx64.dll'
if (Test-Path $adlx) { W "amdadlx64.dll: present, file version $((Get-Item $adlx).VersionInfo.FileVersion), product version $((Get-Item $adlx).VersionInfo.ProductVersion)" } else { W 'amdadlx64.dll: not present (no ADLX-capable AMD driver)' }
W 'HKLM\SOFTWARE\AMD:'
Dump-Key 'HKLM:\SOFTWARE\AMD' '   ' 1
W ''

W '== Power'
(powercfg /getactivescheme) | ForEach-Object { W (Scrub $_) }

$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($Out), $lines, (New-Object Text.UTF8Encoding($true)))
Write-Output "Wrote $($lines.Count) lines to $Out. Nothing was changed on this PC."
