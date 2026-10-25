# Regenerates Mesura's logo files from the one geometry below: the SVG masters, the PNGs, the Windows icon and a preview sheet.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-branding.ps1 [-Preview <png>]
#
# The mark is an M measured with a dimension line: the M is the product, the mint line under it with its two end caps is
# the measurement. The tile is near-black, so the mark reads the same on a light or a dark surface. Sizes up to 32 px use an
# optically heavier variant (thicker strokes, no end caps) because the master's fine parts would blur; everything is drawn
# from the numbers here, nothing is traced or taken from another source. Output (committed): assets\branding\*.svg and
# *.png, src\Mesura.App\Assets\mesura.ico and mesura-mark.png, docs\images\mesura-logo.{svg,png}.
param([string]$Preview = '')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$Tile = '#0E1013'; $Ink = '#EDF0F7'; $Accent = '#62D6B8'; $Edge = '#FFFFFF'
# A shape is: @(kind, data, fill, opacity). kind 'path' or 'rect' (x y w h rx).
$master = @(
  @('rect', '0 0 256 256 56', $Tile, 1),
  @('path', 'M 62 164 L 62 56 L 90 56 L 128 108 L 166 56 L 194 56 L 194 164 L 166 164 L 166 100 L 134 144 L 122 144 L 90 100 L 90 164 Z', $Ink, 1),
  @('rect', '62 190 132 8 0', $Accent, 1),
  @('rect', '62 182 8 24 0', $Accent, 1),
  @('rect', '186 182 8 24 0', $Accent, 1)
)
$small = @(
  @('rect', '0 0 256 256 56', $Tile, 1),
  @('path', 'M 54 160 L 54 52 L 92 52 L 128 100 L 164 52 L 202 52 L 202 160 L 166 160 L 166 106 L 138 146 L 118 146 L 90 106 L 90 160 Z', $Ink, 1),
  @('rect', '54 182 148 20 0', $Accent, 1)
)

function ToSvg($shapes, [bool]$edge) {
  $out = New-Object System.Text.StringBuilder
  [void]$out.AppendLine('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" role="img" aria-label="Mesura">')
  foreach ($s in $shapes) {
    if ($s[0] -eq 'rect') { $v = $s[1] -split ' '; [void]$out.AppendLine(('  <rect x="{0}" y="{1}" width="{2}" height="{3}" rx="{4}" fill="{5}"/>' -f $v[0], $v[1], $v[2], $v[3], $v[4], $s[2])) }
    else { [void]$out.AppendLine(('  <path d="{0}" fill="{1}"/>' -f $s[1], $s[2])) }
    if ($edge -and $s -eq $shapes[0]) { [void]$out.AppendLine(('  <rect x="1" y="1" width="254" height="254" rx="55" fill="none" stroke="{0}" stroke-opacity="0.14" stroke-width="2"/>' -f $Edge)) }
  }
  [void]$out.AppendLine('</svg>')
  $out.ToString()
}

function Brush([string]$hex, [double]$opacity) {
  $c = [System.Windows.Media.ColorConverter]::ConvertFromString($hex)
  $b = New-Object System.Windows.Media.SolidColorBrush($c); $b.Opacity = $opacity; $b.Freeze(); $b
}

# Draws shapes into a bitmap of $px x $px; $bg is an optional backdrop colour (preview sheets only).
function Draw($shapes, [int]$px, [bool]$edge) {
  $dv = New-Object System.Windows.Media.DrawingVisual
  $dc = $dv.RenderOpen()
  $k = $px / 256.0
  $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($k, $k)))
  foreach ($s in $shapes) {
    $brush = Brush $s[2] $s[3]
    if ($s[0] -eq 'rect') { $v = $s[1] -split ' '; $r = [double]$v[4]
      $dc.DrawRoundedRectangle($brush, $null, (New-Object System.Windows.Rect([double]$v[0], [double]$v[1], [double]$v[2], [double]$v[3])), $r, $r) }
    else { $dc.DrawGeometry($brush, $null, [System.Windows.Media.Geometry]::Parse($s[1])) }
    if ($edge -and $s -eq $shapes[0]) {
      $pen = New-Object System.Windows.Media.Pen((Brush $Edge 0.14), 2)
      $dc.DrawRoundedRectangle($null, $pen, (New-Object System.Windows.Rect(1, 1, 254, 254)), 55, 55)
    }
  }
  $dc.Pop(); $dc.Close()
  $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($px, $px, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($dv); $rtb.Freeze(); $rtb
}

function PngBytes($bitmap) {
  $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); $ms.ToArray()
}

function ShapesFor([int]$px) { if ($px -le 32) { $small } else { $master } }

$brand = Join-Path $repo 'assets\branding'
$docsImages = Join-Path $repo 'docs\images'
$assets = Join-Path $repo 'src\Mesura.App\Assets'
foreach ($d in $brand, $docsImages) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$utf8 = New-Object System.Text.UTF8Encoding($false)

[System.IO.File]::WriteAllText((Join-Path $brand 'mesura-logo.svg'), (ToSvg $master $true), $utf8)
[System.IO.File]::WriteAllText((Join-Path $brand 'mesura-logo-small.svg'), (ToSvg $small $true), $utf8)
Copy-Item (Join-Path $brand 'mesura-logo.svg') (Join-Path $docsImages 'mesura-logo.svg') -Force

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256, 512
$pngs = @{}
foreach ($px in $sizes) {
  $bytes = PngBytes (Draw (ShapesFor $px) $px $true)
  $pngs[$px] = $bytes
  [System.IO.File]::WriteAllBytes((Join-Path $brand ("mesura-{0}.png" -f $px)), $bytes)
}
Copy-Item (Join-Path $brand 'mesura-512.png') (Join-Path $docsImages 'mesura-logo.png') -Force
[System.IO.File]::WriteAllBytes((Join-Path $assets 'mesura-mark.png'), $pngs[256])

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

if ($Preview) {
  # Contact sheet: the icon at its real sizes on a dark and on a light surface, and at 4x for the large ones.
  $SheetW = 760; $SheetH = 330
  $dv = New-Object System.Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
  $dc.DrawRectangle((Brush '#1B1D22' 1), $null, (New-Object System.Windows.Rect(0, 0, ($SheetW / 2), $SheetH)))
  $dc.DrawRectangle((Brush '#F3F4F7' 1), $null, (New-Object System.Windows.Rect(($SheetW / 2), 0, ($SheetW / 2), $SheetH)))
  foreach ($half in 0, 1) {
    $x = 20 + $half * ($SheetW / 2)
    foreach ($px in 16, 20, 24, 32, 48, 64) {
      $bmp = Draw (ShapesFor $px) $px $true
      $dc.DrawImage($bmp, (New-Object System.Windows.Rect($x, 20, $px, $px))); $x += $px + 14
    }
    $big = Draw $master 256 $true
    $dc.DrawImage($big, (New-Object System.Windows.Rect((20 + $half * ($SheetW / 2)), 80, 230, 230)))
    $mid = Draw $master 96 $true
    $dc.DrawImage($mid, (New-Object System.Windows.Rect((270 + $half * ($SheetW / 2)), 80, 96, 96)))
    $tiny = Draw $small 40 $true
    $dc.DrawImage($tiny, (New-Object System.Windows.Rect((270 + $half * ($SheetW / 2)), 190, 40, 40)))
  }
  $dc.Close()
  $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($SheetW, $SheetH, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($dv)
  [System.IO.File]::WriteAllBytes($Preview, (PngBytes $rtb))
}
Write-Output "Wrote $brand, $docsImages and the app's icon and mark."
