# Starts Mesura (never elevated, so no UAC prompt), walks its pages with UI Automation (navigation only: nothing is applied, installed or
# changed) and saves one PNG per page. Used for design review and for checking the window at other sizes.
#
#   powershell -ExecutionPolicy Bypass -File tools\shoot-app.ps1 -Exe <Mesura.exe> -Out <folder> [-Lang en|ru] [-Width 1280 -Height 860] [-Pages Setup,Tweaks,...]
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$Out,
  [ValidateSet('en', 'ru')][string]$Lang = 'en',
  [int]$Width = 0,
  [int]$Height = 0,
  [string[]]$Pages = @('Setup', 'Tweaks', 'Debloat', 'Apps', 'Profiles', 'SystemInfo', 'Activity', 'Settings', 'Configurations', 'Activation'),
  [int]$Settle = 6,
  [int]$Scroll = -1
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class MW {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$strings = Get-Content -Raw -Encoding UTF8 (Join-Path $repo "src\Mesura.App\Localization\$Lang.json") | ConvertFrom-Json
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $CT = [System.Windows.Automation.ControlType]
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
$env:MESURA_PREVIEW_LANGUAGE = $Lang
$p = Start-Process -FilePath $Exe -ArgumentList '--no-elevate' -PassThru
Remove-Item Env:MESURA_PREVIEW_LANGUAGE -ErrorAction SilentlyContinue
try {
  $d = (Get-Date).AddSeconds(40); while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $d) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  $h = $p.MainWindowHandle
  if ($Width -gt 0) { [MW]::MoveWindow($h, 40, 20, $Width, $Height, $true) | Out-Null }
  Start-Sleep -Seconds $Settle
  $root = $A::FromHandle($h)
  function Find-Named([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
    @($root.FindAll($TS::Descendants, $c)) | Select-Object -First 1
  }
  function Shot([string]$file) {
    $r = New-Object MW+RECT; [MW]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [MW]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  }
  $map = @{ Setup = 'Nav.Optimize'; Tweaks = 'Nav.Tweaks'; Debloat = 'Nav.Debloat'; Apps = 'Nav.Apps'; Profiles = 'Nav.Profiles'; SystemInfo = 'Nav.SystemInfo'
            Activity = 'Nav.Activity'; Settings = 'Nav.Settings'; Configurations = 'Nav.Configurations'; Activation = 'Nav.Activation' }
  $i = 0
  foreach ($page in ($Pages -join ',').Split(',')) {
    $i++
    $key = $map[$page]; $label = $strings.$key
    if (-not $label) { Write-Host "skip $page (no string $key)"; continue }
    $more = $page -in 'Configurations', 'Activation'
    if ($more) { $mb = Find-Named $strings.'Nav.More'; if ($mb) { $mb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 500 } }
    $item = $null
    $deadline = (Get-Date).AddSeconds(5)
    while (-not $item -and (Get-Date) -lt $deadline) {
      $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $label)
      $item = @($root.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.ControlType -eq $CT::RadioButton } | Select-Object -First 1
      if (-not $item) { $item = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.ControlType -eq $CT::RadioButton } | Select-Object -First 1 }
      if (-not $item) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $item) { Write-Host "skip $page (no nav item '$label')"; continue }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1800
    if ($Scroll -ge 0) {
      # Scrolls the page's own scroll viewer (the first visible one that scrolls vertically) to a percentage, to see below the fold.
      $c = New-Object System.Windows.Automation.PropertyCondition($A::IsScrollPatternAvailableProperty, $true)
      $viewer = @($root.FindAll($TS::Descendants, $c)) | Where-Object { -not $_.Current.IsOffscreen -and $_.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable -and $_.Current.BoundingRectangle.Width -gt 600 } | Select-Object -First 1
      if ($viewer) { $viewer.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, $Scroll); Start-Sleep -Milliseconds 700 }
    }
    $file = Join-Path $Out ("{0:00}-{1}-{2}.png" -f $i, $page.ToLower(), $Lang)
    Shot $file
    Write-Host "saved $file"
  }
}
finally {
  Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force
}
