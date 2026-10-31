# Checks Mesura under the colours of Windows' four high contrast themes (Ease of Access Themes\*.theme): sets the system colours and the high contrast flag for this\n# session only (SetSysColors / SystemParametersInfo, nothing written to the registry), shoots the pages with tools\shoot-scaled.ps1, and puts the colours back.\n# One theme per run is the reliable way: a second theme set in the same session can keep the first one's colours.\n#   powershell -ExecutionPolicy Bypass -File tools\shoot-contrast.ps1 -Out <folder> -Themes hcwhite   (hcblack | hcwhite | hc1 | hc2)\nparam([string]$Out, [string]$Lang = 'en', [string[]]$Themes = @('hcblack', 'hcwhite', 'hc1', 'hc2'), [string[]]$Pages = @('Setup', 'Tweaks', 'Debloat', 'Firmware', 'Apps', 'Activation', 'SystemInfo', 'Settings'))
# Applies the colours of Windows' own high contrast theme files (Control Panel\Colors) with SetSysColors, switches the high contrast flag on, shoots
# Mesura's pages, and puts the session's previous colours and flag back. Nothing is written to the registry.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = "$repo\src\Mesura.App\bin\Debug\net10.0-windows\win-x64\Mesura.exe"
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class HC {
  [StructLayout(LayoutKind.Sequential)] public struct HIGHCONTRAST { public uint cbSize; public uint dwFlags; public IntPtr lpszDefaultScheme; }
  [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")] static extern bool SPI(uint a, uint b, ref HIGHCONTRAST c, uint d);
  [DllImport("user32.dll")] static extern int GetSysColor(int i);
  [DllImport("user32.dll")] static extern bool SetSysColors(int n, int[] elements, int[] rgb);
  public static bool On() { var h = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST)) }; SPI(0x42, h.cbSize, ref h, 0); return (h.dwFlags & 1) != 0; }
  public static void Flag(bool on) { var h = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST)), dwFlags = on ? 3u : 2u, lpszDefaultScheme = Marshal.StringToHGlobalUni("") }; SPI(0x43, h.cbSize, ref h, 3); }
  public static int[] Read() { var a = new int[31]; for (int i = 0; i < 31; i++) a[i] = GetSysColor(i); return a; }
  public static void Apply(int[] elements, int[] rgb) { SetSysColors(elements.Length, elements, rgb); }
}
"@
$index = @{ Scrollbar = 0; Background = 1; ActiveTitle = 2; InactiveTitle = 3; Menu = 4; Window = 5; WindowFrame = 6; MenuText = 7; WindowText = 8; TitleText = 9; ActiveBorder = 10; InactiveBorder = 11
            AppWorkspace = 12; Hilight = 13; HilightText = 14; ButtonFace = 15; ButtonShadow = 16; GrayText = 17; ButtonText = 18; InactiveTitleText = 19; ButtonHilight = 20; ButtonDkShadow = 21
            ButtonLight = 22; InfoText = 23; InfoWindow = 24; ButtonAlternateFace = 25; HotTrackingColor = 26; GradientActiveTitle = 27; GradientInactiveTitle = 28; MenuHilight = 29; MenuBar = 30 }
$original = [HC]::Read()
try {
  foreach ($t in $Themes) {
    $sec = $false; $el = @(); $rgb = @()
    foreach ($l in Get-Content "C:\Windows\Resources\Ease of Access Themes\$t.theme") {
      if ($l -match '^\[(.+)\]') { $sec = $Matches[1] -eq 'Control Panel\Colors'; continue }
      if ($sec -and $l -match '^(\w+)=(\d+) (\d+) (\d+)') {
        if ($index.ContainsKey($Matches[1])) { $el += $index[$Matches[1]]; $rgb += ([int]$Matches[2] -bor ([int]$Matches[3] -shl 8) -bor ([int]$Matches[4] -shl 16)) }
      }
    }
    [HC]::Flag($true); [HC]::Apply([int[]]$el, [int[]]$rgb); Start-Sleep -Seconds 5
    "$t : $($el.Count) colours, high contrast flag = $([HC]::On())"
    powershell -ExecutionPolicy Bypass -File "$repo\tools\shoot-scaled.ps1" -Exe $exe -Out (Join-Path $Out $t) -Scale 150 -Lang $Lang -Pages ($Pages -join ',') -Settle 8 2>&1 | Where-Object { $_ -match 'skip|restored|rror|xception' } | ForEach-Object { "  $_" }
  }
}
finally {
  [HC]::Flag($false); [HC]::Apply([int[]](0..30), $original); Start-Sleep -Seconds 4
  "restored: flag = $([HC]::On())"
}
