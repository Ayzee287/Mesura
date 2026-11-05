# Read-only: what Windows says about each connected display (model, current resolution and refresh rate, how it is connected). For the V-Sync / variable refresh decision (G1):
# it works on any PC, whatever the graphics card. It changes nothing, uses no network, and leaves out serial numbers (the EDID serial is never read).
#
#   powershell -ExecutionPolicy Bypass -File .\collect-display-facts.ps1 [-Out "$env:USERPROFILE\Desktop\display-facts.txt"]
param(
  [string]$Out = ''
)
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'display-facts.txt' }
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MesuraDisplay {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DISPLAY_DEVICE {
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
  }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra; public int dmFields;
    public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput;
    public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight; public int dmDisplayFlags; public int dmDisplayFrequency;
    public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, int index, ref DISPLAY_DEVICE dd, int flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
}
'@

$lines = New-Object System.Collections.Generic.List[string]
function Add([string]$s) { $lines.Add($s) }
Add "Display facts (read only). $(Get-Date -Format s)"
$os = Get-CimInstance Win32_OperatingSystem
Add "Windows: $($os.Caption) build $($os.BuildNumber)"
Add ''

# Each active display output: the current mode and the monitor Windows attached to it (model name and hardware id, no serial number).
$n = 0
for ($i = 0; ; $i++) {
  $adapter = New-Object MesuraDisplay+DISPLAY_DEVICE; $adapter.cb = [Runtime.InteropServices.Marshal]::SizeOf($adapter)
  if (-not [MesuraDisplay]::EnumDisplayDevices([NullString]::Value, $i, [ref]$adapter, 0)) { break }
  if (($adapter.StateFlags -band 1) -eq 0) { continue }   # not attached to the desktop
  $n++
  $mode = New-Object MesuraDisplay+DEVMODE; $mode.dmSize = [int16][Runtime.InteropServices.Marshal]::SizeOf($mode)
  $ok = [MesuraDisplay]::EnumDisplaySettings($adapter.DeviceName, -1, [ref]$mode)
  Add "Output $n ($($adapter.DeviceName)) on adapter '$($adapter.DeviceString)'"
  if ($ok) { Add "  current mode: $($mode.dmPelsWidth) x $($mode.dmPelsHeight) at $($mode.dmDisplayFrequency) Hz, $($mode.dmBitsPerPel) bits" } else { Add '  current mode: not readable' }
  $monitor = New-Object MesuraDisplay+DISPLAY_DEVICE; $monitor.cb = [Runtime.InteropServices.Marshal]::SizeOf($monitor)
  if ([MesuraDisplay]::EnumDisplayDevices($adapter.DeviceName, 0, [ref]$monitor, 0)) {
    $hw = ($monitor.DeviceID -split '\\' | Select-Object -First 2) -join '\'
    Add "  monitor: $($monitor.DeviceString)  ($hw)"
  } else { Add '  monitor: none reported' }
}
if ($n -eq 0) { Add 'No active display output was reported.' }
Add ''

# The refresh rates each output offers at its current resolution (the top of this list is what the monitor and the connection allow).
Add 'Refresh rates offered at the current resolution:'
for ($i = 0; ; $i++) {
  $adapter = New-Object MesuraDisplay+DISPLAY_DEVICE; $adapter.cb = [Runtime.InteropServices.Marshal]::SizeOf($adapter)
  if (-not [MesuraDisplay]::EnumDisplayDevices([NullString]::Value, $i, [ref]$adapter, 0)) { break }
  if (($adapter.StateFlags -band 1) -eq 0) { continue }
  $current = New-Object MesuraDisplay+DEVMODE; $current.dmSize = [int16][Runtime.InteropServices.Marshal]::SizeOf($current)
  if (-not [MesuraDisplay]::EnumDisplaySettings($adapter.DeviceName, -1, [ref]$current)) { continue }
  $rates = @{}
  for ($m = 0; ; $m++) {
    $dm = New-Object MesuraDisplay+DEVMODE; $dm.dmSize = [int16][Runtime.InteropServices.Marshal]::SizeOf($dm)
    if (-not [MesuraDisplay]::EnumDisplaySettings($adapter.DeviceName, $m, [ref]$dm)) { break }
    if ($dm.dmPelsWidth -eq $current.dmPelsWidth -and $dm.dmPelsHeight -eq $current.dmPelsHeight) { $rates[$dm.dmDisplayFrequency] = $true }
  }
  Add "  $($adapter.DeviceName): $((@($rates.Keys | Sort-Object { [int]$_ }) -join ', ')) Hz"
}
Add ''

# How each monitor is connected, from Windows' WMI (no serial number is read).
Add 'Connection (WMI):'
$techNames = @{ '-2' = 'uninitialized'; '-1' = 'other'; '0' = 'VGA'; '1' = 'S-Video'; '2' = 'composite'; '3' = 'component'; '4' = 'DVI'; '5' = 'HDMI'; '6' = 'LVDS'; '8' = 'D-Jpn'; '9' = 'SDI'; '10' = 'DisplayPort (external)'; '11' = 'DisplayPort (embedded)'; '12' = 'UDI (external)'; '13' = 'UDI (embedded)'; '14' = 'SDTV dongle'; '15' = 'miracast'; '16' = 'indirect (wired)'; '2147483648' = 'internal' }
try {
  foreach ($c in @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction Stop)) {
    $id = ($c.InstanceName -split '\\' | Select-Object -First 2) -join '\'
    $t = [string][int64]$c.VideoOutputTechnology
    Add "  $id : $(if ($techNames.ContainsKey($t)) { $techNames[$t] } else { "code $t" })"
  }
} catch { Add "  not readable: $($_.Exception.Message)" }
Add ''

# Names the monitors report about themselves (no serial number).
Add 'Monitor identity (WMI):'
try {
  foreach ($m in @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction Stop)) {
    $maker = -join ($m.ManufacturerName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ })
    $model = -join ($m.UserFriendlyName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ })
    $id = ($m.InstanceName -split '\\' | Select-Object -First 2) -join '\'
    Add "  $id : $maker  $model  (made week $($m.WeekOfManufacture) of $($m.YearOfManufacture))"
  }
} catch { Add "  not readable: $($_.Exception.Message)" }
Add ''
Add 'Not read: serial numbers, EDID, anything about Adaptive-Sync / FreeSync / G-SYNC (Windows does not say; the graphics driver does: see the probes).'

[IO.File]::WriteAllLines($Out, $lines, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Wrote $Out ($($lines.Count) lines). Nothing was changed."
