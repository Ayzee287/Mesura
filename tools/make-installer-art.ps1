# Draws the two pictures the installer wizard uses (installer\art\banner.bmp and dialog.bmp) from the logo's colours, so the Windows Installer
# wizard looks like Mesura: a graphite panel with the violet-azure-mint light on the welcome and finish pages, a white banner with the mark on the others.
# The wizard's own text stays Windows' (it cannot be dark: it is the system's dialog), which is why the banner is white and the art carries no words.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-installer-art.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $repo 'installer\art'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$mark = [System.Drawing.Image]::FromFile((Join-Path $repo 'assets\branding\mesura-256.png'))

function Save-Bmp($bitmap, $name) {
  $bitmap.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Bmp)
  $bitmap.Dispose()
}

function New-Canvas([int]$w, [int]$h) {
  $b = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  return @($b, $g)
}

# ---- dialog.bmp: 493 x 312, the welcome and finish pages (the art is the left 164 px; the rest is the dialog's own white)
($b, $g) = New-Canvas 493 312
$g.Clear([System.Drawing.Color]::White)
$panel = New-Object System.Drawing.Rectangle(0, 0, 164, 312)
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#0E1013'))), $panel)
# two soft lights: azure from the top, violet from the bottom left, mint low on the right, as in the window's ambient field
function Light($g, [int]$cx, [int]$cy, [int]$r, [string]$hex, [int]$alpha) {
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $path.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
  $br = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
  $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
  $br.CenterColor = [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
  $br.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $c.R, $c.G, $c.B))
  $g.FillPath($br, $path)
}
$g.SetClip($panel)
Light $g 40 30 150 '#2AB6FF' 150
Light $g 20 300 170 '#7A5CFF' 170
Light $g 150 210 110 '#34F0B0' 90
$g.ResetClip()
$g.DrawImage($mark, (New-Object System.Drawing.Rectangle(34, 40, 96, 96)))
# a thin measuring line under the mark, the logo's own idea
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 255, 255, 255), 2)
$g.DrawLine($pen, 34, 160, 130, 160); $g.DrawLine($pen, 34, 154, 34, 166); $g.DrawLine($pen, 130, 154, 130, 166)
$g.Dispose(); Save-Bmp $b 'dialog.bmp'

# ---- banner.bmp: 493 x 58, the other pages: white, the mark at the right, a thin gradient edge
($b, $g) = New-Canvas 493 58
$g.Clear([System.Drawing.Color]::White)
$g.DrawImage($mark, (New-Object System.Drawing.Rectangle(433, 8, 42, 42)))
$edge = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0, 54, 493, 4)), [System.Drawing.ColorTranslator]::FromHtml('#7A5CFF'), [System.Drawing.ColorTranslator]::FromHtml('#34F0B0'), 0.0)
$g.FillRectangle($edge, 0, 54, 493, 4)
$g.Dispose(); Save-Bmp $b 'banner.bmp'
$mark.Dispose()
Write-Output "Wrote $out"
