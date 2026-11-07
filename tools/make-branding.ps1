# Regenerates Mesura's logo files from the one geometry below: the SVG masters, the PNGs, the Windows icon and a preview sheet.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-branding.ps1 [-Preview <png>]
#
# The mark (0.4.1; monochrome since 0.7, see $Palettes below) is one letter M with nothing behind it: a gradient letter with a soft glow of the same colours following its
# outline, and a thin bright edge so it stays a letter at small sizes. There is no tile, plate or frame, so on the window's dark glass it reads as
# light, and on a light surface the gradient letter is still the darkest thing on it. Sizes up to 32 px use a heavier M with a tighter glow (the
# master's soft halo would blur into the letter). Everything is drawn from the numbers here; nothing is traced or taken from another source.
# Output (committed): assets\branding\*.svg and *.png, src\Mesura.App\Assets\mesura.ico and mesura-mark.png, docs\images\mesura-logo.{svg,png}.
param([string]$Preview = '', [ValidateSet('violet', 'silver', 'ice', 'chrome')][string]$Style = 'chrome', [switch]$NoWrite)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# The mark's palettes. 'violet' is the 0.4.1 to 0.6.0 colour mark (kept so the sheet can show the change). The default, 'chrome', is the 0.7 monochrome mark: one cool silver-blue family,
# no hue of its own: a brushed-metal letter (white at the top, steel in the middle, light again at the foot) whose glow is the same silver-blue. Letter stops are (offset, colour);
# the glow uses its own, bluer stops; Contour is the thin dark line under the letter that keeps it a letter on a light surface (a taskbar, the installer) and is invisible on dark.
$Palettes = @{
  violet = @{ Letter = @(@(0, '#8A6BFF'), @(0.55, '#2AB6FF'), @(1, '#34F0B0')); Glow = @(@(0, '#8A6BFF'), @(0.55, '#2AB6FF'), @(1, '#34F0B0')); Edge = '#FFFFFF'; EdgeOpacity = 0.55; Contour = $null }
  silver = @{ Letter = @(@(0, '#F6F8FC'), @(1, '#AEBBD6')); Glow = @(@(0, '#C4D2F2'), @(1, '#8FA6D8')); Edge = '#FFFFFF'; EdgeOpacity = 0.7; Contour = '#0E1522' }
  ice = @{ Letter = @(@(0, '#FFFFFF'), @(0.5, '#DCE6FA'), @(1, '#9FB6E6')); Glow = @(@(0, '#B7CBF5'), @(1, '#6F93DA')); Edge = '#FFFFFF'; EdgeOpacity = 0.8; Contour = '#0E1522' }
  chrome = @{ Letter = @(@(0, '#FFFFFF'), @(0.38, '#C9D4EA'), @(0.52, '#8497BD'), @(0.7, '#DCE5F6'), @(1, '#F4F7FC')); Glow = @(@(0, '#C4D4F6'), @(0.5, '#8FA9E0'), @(1, '#C4D4F6')); Edge = '#FFFFFF'; EdgeOpacity = 0.75; Contour = '#0E1522' }
}
$Pal = $Palettes[$Style]
# The letter, in a 256 x 256 box. The master leaves room around it for the glow; the small one fills the box.
$masterPath = 'M 56 186 L 56 70 L 92 70 L 128 124 L 164 70 L 200 70 L 200 186 L 168 186 L 168 118 L 140 160 L 116 160 L 88 118 L 88 186 Z'
$smallPath = 'M 34 206 L 34 52 L 88 52 L 128 120 L 168 52 L 222 52 L 222 206 L 178 206 L 178 124 L 146 176 L 110 176 L 78 124 L 78 206 Z'

function Grad([double]$opacity, [bool]$glow = $false) {
  $g = New-Object System.Windows.Media.LinearGradientBrush
  $g.StartPoint = New-Object System.Windows.Point(0, 0); $g.EndPoint = New-Object System.Windows.Point(1, 1)
  foreach ($stop in $(if ($glow) { $Pal.Glow } else { $Pal.Letter })) {
    [void]$g.GradientStops.Add((New-Object System.Windows.Media.GradientStop(([System.Windows.Media.ColorConverter]::ConvertFromString($stop[1])), [double]$stop[0])))
  }
  $g.Opacity = $opacity; $g.Freeze(); $g
}

function SolidBrush([string]$hex, [double]$opacity) {
  $b = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Opacity = $opacity; $b.Freeze(); $b
}

# Draws the mark into a $px x $px bitmap with a transparent background.
function Draw([int]$px, [bool]$small) {
  $geo = [System.Windows.Media.Geometry]::Parse($(if ($small) { $smallPath } else { $masterPath }))
  $dv = New-Object System.Windows.Media.DrawingVisual
  $dc = $dv.RenderOpen()
  $k = $px / 256.0
  $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($k, $k)))
  # The glow: the outline stroked many times, wide to narrow, each layer faint, so the light is brightest at the letter and fades outwards.
  $layers = if ($small) { if ($px -le 32) { 4 } else { 8 } } else { 14 }
  $step = if ($small) { 3.0 } else { 3.4 }
  $strength = if ($small) { 0.080 } else { 0.034 }
  for ($i = $layers; $i -ge 1; $i--) {
    $pen = New-Object System.Windows.Media.Pen((Grad ($strength * (1.0 + (($layers - $i) / $layers) * 1.6)) $true), (6 + $i * $step))
    $pen.LineJoin = 'Round'; $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $dc.DrawGeometry($null, $pen, $geo)
  }
  # The letter: the gradient, a soft rounded edge, then a thin bright inner edge.
  if ($Pal.Contour) {
    $contour = New-Object System.Windows.Media.Pen((SolidBrush $Pal.Contour 0.6), 8.5); $contour.LineJoin = 'Round'
    $dc.DrawGeometry($null, $contour, $geo)
  }
  $edge = New-Object System.Windows.Media.Pen((Grad 1.0), 5); $edge.LineJoin = 'Round'
  $dc.DrawGeometry((Grad 1.0), $edge, $geo)
  $hi = New-Object System.Windows.Media.Pen((SolidBrush $Pal.Edge $Pal.EdgeOpacity), $(if ($small) { 2.5 } else { 1.6 })); $hi.LineJoin = 'Round'
  $dc.DrawGeometry($null, $hi, $geo)
  $dc.Pop(); $dc.Close()
  $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($px, $px, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($dv); $rtb.Freeze(); $rtb
}

function PngBytes($bitmap) {
  $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); $ms.ToArray()
}

function Stops($set) { (($set | ForEach-Object { '<stop offset="{0}" stop-color="{1}"/>' -f $_[0], $_[1] }) -join '') }

function ToSvg([bool]$small) {
  $d = if ($small) { $smallPath } else { $masterPath }
  $blur = if ($small) { 5 } else { 12 }
  $letterStops = Stops $Pal.Letter; $glowStops = Stops $Pal.Glow
  $contour = if ($Pal.Contour) { "  <path d=`"$d`" fill=`"none`" stroke=`"$($Pal.Contour)`" stroke-opacity=`"0.6`" stroke-width=`"8.5`" stroke-linejoin=`"round`"/>`n" } else { '' }
  @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" role="img" aria-label="Mesura">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">$letterStops</linearGradient>
    <linearGradient id="glowg" x1="0" y1="0" x2="1" y2="1">$glowStops</linearGradient>
    <filter id="glow" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="$blur"/></filter>
  </defs>
  <path d="$d" fill="url(#glowg)" stroke="url(#glowg)" stroke-width="22" stroke-linejoin="round" opacity="0.75" filter="url(#glow)"/>
$contour  <path d="$d" fill="url(#g)" stroke="url(#g)" stroke-width="5" stroke-linejoin="round"/>
  <path d="$d" fill="none" stroke="$($Pal.Edge)" stroke-opacity="$($Pal.EdgeOpacity)" stroke-width="1.6" stroke-linejoin="round"/>
</svg>
"@
}

function ShapesFor([int]$px) { $px -le 64 }

if (-not $NoWrite) {
$brand = Join-Path $repo 'assets\branding'
$docsImages = Join-Path $repo 'docs\images'
$assets = Join-Path $repo 'src\Mesura.App\Assets'
foreach ($d in $brand, $docsImages) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$utf8 = New-Object System.Text.UTF8Encoding($false)

[System.IO.File]::WriteAllText((Join-Path $brand 'mesura-logo.svg'), (ToSvg $false), $utf8)
[System.IO.File]::WriteAllText((Join-Path $brand 'mesura-logo-small.svg'), (ToSvg $true), $utf8)
Copy-Item (Join-Path $brand 'mesura-logo.svg') (Join-Path $docsImages 'mesura-logo.svg') -Force

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256, 512
$pngs = @{}
foreach ($px in $sizes) {
  $bytes = PngBytes (Draw $px (ShapesFor $px))
  $pngs[$px] = $bytes
  [System.IO.File]::WriteAllBytes((Join-Path $brand ("mesura-{0}.png" -f $px)), $bytes)
}
Copy-Item (Join-Path $brand 'mesura-512.png') (Join-Path $docsImages 'mesura-logo.png') -Force
# The window's own mark is shown at about 40 px: the heavier letter with a softer glow stays crisp there.
[System.IO.File]::WriteAllBytes((Join-Path $assets 'mesura-mark.png'), $pngs[128])

# The Windows icon: PNG-compressed frames, 16 to 256.
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
foreach ($px in $icoSizes) {
  $d = if ($px -eq 256) { 0 } else { $px }
  $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$px].Length); $w.Write([uint32]$offset); $offset += $pngs[$px].Length
}
foreach ($px in $icoSizes) { $w.Write([byte[]]$pngs[$px], 0, $pngs[$px].Length) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'mesura.ico'), $ms.ToArray())
}

if ($Preview) {
  # Contact sheet: the mark at its real sizes on the window's dark glass, on a mid grey and on a light surface, and large.
  $SheetW = 900; $SheetH = 330
  $dv = New-Object System.Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
  $dc.DrawRectangle((SolidBrush '#0B0C0F' 1), $null, (New-Object System.Windows.Rect(0, 0, 300, $SheetH)))
  $dc.DrawRectangle((SolidBrush '#3A3D44' 1), $null, (New-Object System.Windows.Rect(300, 0, 300, $SheetH)))
  $dc.DrawRectangle((SolidBrush '#F3F4F7' 1), $null, (New-Object System.Windows.Rect(600, 0, 300, $SheetH)))
  foreach ($half in 0, 1, 2) {
    $x = 14 + $half * 300
    foreach ($px in 16, 20, 24, 32, 48, 64) {
      $bmp = Draw $px (ShapesFor $px)
      $dc.DrawImage($bmp, (New-Object System.Windows.Rect($x, 14, $px, $px))); $x += $px + 10
    }
    $big = Draw 256 $false
    $dc.DrawImage($big, (New-Object System.Windows.Rect((14 + $half * 300), 90, 230, 230)))
  }
  $dc.Close()
  $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($SheetW, $SheetH, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($dv)
  [System.IO.File]::WriteAllBytes($Preview, (PngBytes $rtb))
}
if (-not $NoWrite) { Write-Output "Wrote $brand, $docsImages and the app's icon and mark ($Style)." }
