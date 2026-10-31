# Real Windows display scaling check: sets the primary display's scale (Settings > System > Display > Scale) through the same call
# Settings uses, starts Mesura (never elevated), walks its pages with UI Automation (navigation only: nothing is applied, installed or
# changed), saves one PNG per page at the real pixel size, and ALWAYS puts the previous scale back, whatever happens.
#
#   powershell -ExecutionPolicy Bypass -File tools\shoot-scaled.ps1 -Exe <Mesura.exe> -Out <folder> -Scale 200 [-Lang en|ru] [-Width 0 -Height 0] [-Pages ...]
#
# -Width/-Height are device-independent pixels (what the window's Width/Height mean); 0 keeps the app's own start size.
# Never pipe this script into Select-Object -First or Out-Null early: a closed pipe ends it before the scale is restored (redirect to a file).
# The scale change is system-wide for a moment: every open window re-lays itself out, then again when it is restored.
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$Out,
  [ValidateSet(100, 125, 150, 175, 200, 225, 250, 300)][int]$Scale = 200,
  [ValidateSet('en', 'ru')][string]$Lang = 'en',
  [int]$Width = 0,
  [int]$Height = 0,
  [string[]]$Pages = @('Welcome', 'Setup', 'Tweaks', 'Debloat', 'Firmware', 'Apps', 'Profiles', 'SystemInfo', 'Activity', 'Settings', 'Configurations', 'Activation'),
  [int]$Settle = 6,
  [int]$Wait = 1800
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Dpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
  [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathInfo, ref uint modes, IntPtr modeInfo, IntPtr topology);
  [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
  [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(IntPtr packet);
  static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
  // The first active path's source: its adapter (LUID) and id.
  static void Source(out long luid, out uint id) {
    uint np, nm; GetDisplayConfigBufferSizes(2, out np, out nm);
    IntPtr p = Marshal.AllocHGlobal((int)np * 72), m = Marshal.AllocHGlobal((int)nm * 64);
    try { QueryDisplayConfig(2, ref np, p, ref nm, m, IntPtr.Zero); luid = Marshal.ReadInt64(p, 0); id = (uint)Marshal.ReadInt32(p, 8); }
    finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(m); }
  }
  // Returns { minRel, curRel, maxRel }; the recommended step is the one with relative index 0.
  public static int[] Get() {
    long luid; uint id; Source(out luid, out id);
    IntPtr b = Marshal.AllocHGlobal(32);
    try {
      Marshal.WriteInt32(b, 0, -3); Marshal.WriteInt32(b, 4, 32); Marshal.WriteInt64(b, 8, luid); Marshal.WriteInt32(b, 16, (int)id);
      if (DisplayConfigGetDeviceInfo(b) != 0) throw new Exception("DisplayConfigGetDeviceInfo failed");
      return new[] { Marshal.ReadInt32(b, 20), Marshal.ReadInt32(b, 24), Marshal.ReadInt32(b, 28) };
    } finally { Marshal.FreeHGlobal(b); }
  }
  public static int Percent(int[] g) { return Steps[g[1] - g[0]]; }   // index = rel - min
  public static void SetRel(int rel) {
    long luid; uint id; Source(out luid, out id);
    IntPtr b = Marshal.AllocHGlobal(24);
    try {
      Marshal.WriteInt32(b, 0, -4); Marshal.WriteInt32(b, 4, 24); Marshal.WriteInt64(b, 8, luid); Marshal.WriteInt32(b, 16, (int)id); Marshal.WriteInt32(b, 20, rel);
      if (DisplayConfigSetDeviceInfo(b) != 0) throw new Exception("DisplayConfigSetDeviceInfo failed");
    } finally { Marshal.FreeHGlobal(b); }
  }
  public static void SetPercent(int percent) {
    int[] g = Get();
    // The lowest scale the display offers is Steps[0] (100%), so a scale's relative value is min + its index in Steps.
    int rel = g[0] + Array.IndexOf(Steps, percent);
    if (rel < g[0] || rel > g[2]) throw new Exception("Scale " + percent + "% is not offered for this display");
    SetRel(rel);
  }
}
"@
# Per-monitor aware (v2): window rectangles and bitmaps are in real pixels at the display's current scale, not the scale this session started with.
[Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
$before = [Dpi]::Get()
$was = [Dpi]::Percent($before)
Write-Host "Display scale is $was%; setting $Scale%."
$p = $null
try {
  if ($was -ne $Scale) { [Dpi]::SetPercent($Scale); Start-Sleep -Seconds 4 }   # an unchanged scale is left alone: a display change also makes Windows reload its colours
  $env:MESURA_PREVIEW_LANGUAGE = $Lang
  $p = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
  Remove-Item Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
  $d = (Get-Date).AddSeconds(40); while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  $h = $p.MainWindowHandle
  $scaleNow = [Dpi]::GetDpiForWindow($h) / 96.0
  if ($Width -gt 0) {
    $r0 = New-Object Dpi+RECT; [Dpi]::GetWindowRect($h, [ref]$r0) | Out-Null
    [Dpi]::MoveWindow($h, $r0.Left, $r0.Top, [int]($Width * $scaleNow), [int]($Height * $scaleNow), $true) | Out-Null
  }
  Start-Sleep -Seconds $Settle
  $r = New-Object Dpi+RECT; [Dpi]::GetWindowRect($h, [ref]$r) | Out-Null
  Write-Host ("window: {0}x{1} px = {2:N0}x{3:N0} DIP at system scale {4}" -f ($r.Right - $r.Left), ($r.Bottom - $r.Top), (($r.Right - $r.Left) / $scaleNow), (($r.Bottom - $r.Top) / $scaleNow), $scaleNow)
  $root = $A::FromHandle($h)
  function Find-Named([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    @($root.FindAll($TS::Descendants, $c)) | Select-Object -First 1
  }
  function Shot([string]$file) {
    $rc = New-Object Dpi+RECT; [Dpi]::GetWindowRect($h, [ref]$rc) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($rc.Right - $rc.Left), ($rc.Bottom - $rc.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [Dpi]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  }
  $map = @{ Welcome = 'Nav.Home'; Setup = 'Nav.Optimize'; Tweaks = 'Nav.Tweaks'; Debloat = 'Nav.Debloat'; Apps = 'Nav.Apps'; Profiles = 'Nav.Profiles'; SystemInfo = 'Nav.SystemInfo'
            Activity = 'Nav.Activity'; Firmware = 'Nav.Firmware'; Settings = 'Nav.Settings'; Configurations = 'Nav.Configurations'; Activation = 'Nav.Activation' }
  $i = 0
  foreach ($page in ($Pages -join ',').Split(',')) {
    $i++
    $key = $map[$page]; $label = $strings.$key
    if (-not $label) { Write-Host "skip $page"; continue }
    if ($page -in 'Configurations', 'Profiles', 'Settings') { $mb = Find-Named $strings.'Nav.More'; if ($mb) { $mb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500 } }
    $item = $null; $deadline = (Get-Date).AddSeconds(5)
    $kind = if ($page -eq 'Welcome') { $CT::Button } else { $CT::RadioButton }   # the logo (home) is a button, the dock's pages are radio buttons
    while (-not $item -and (Get-Date) -lt $deadline) {
      $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $label)
      $item = @($root.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.ControlType -eq $kind } | Select-Object -First 1
      if (-not $item) { $item = @($A::RootElement.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.ControlType -eq $kind } | Select-Object -First 1 }
      if (-not $item) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $item) { Write-Host "skip $page (no nav item '$label')"; continue }
    if ($page -eq 'Welcome') { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    else { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    Start-Sleep -Milliseconds $Wait
    $file = Join-Path $Out ("{0:00}-{1}-{2}-{3}pct.png" -f $i, $page.ToLower(), $Lang, $Scale)
    Shot $file
    Write-Host "saved $file"
  }
}
finally {
  if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  if ([Dpi]::Percent([Dpi]::Get()) -ne $was) { [Dpi]::SetPercent($was); Start-Sleep -Seconds 2 }
  Write-Host ("Display scale is {0}%." -f [Dpi]::Percent([Dpi]::Get()))
}
