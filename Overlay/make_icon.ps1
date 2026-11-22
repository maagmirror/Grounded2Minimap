param([string]$Out, [string]$PreviewDir)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-Frame([int]$s) {
    # Draw at 4x then downscale for clean edges.
    $k = 4; $BS = $s * $k
    $big = New-Object System.Drawing.Bitmap $BS, $BS, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $small = $s -le 24
    $pad = [float]($BS * 0.03)
    $d = [float]($BS - 2 * $pad)
    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse($pad, $pad, $d, $d)

    if ($s -ge 48) {
        $sh = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
        $g.FillEllipse($sh, $pad, $pad + $BS * 0.02, $d, $d)
    }
    # Terrain
    $base = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($BS, $BS)), ([System.Drawing.Color]::FromArgb(255, 62, 104, 48)), ([System.Drawing.Color]::FromArgb(255, 28, 52, 30))
    $g.FillPath($base, $disc)
    $g.SetClip($disc)
    if (-not $small) {
        $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 96, 140, 62))
        $g.FillEllipse($light, $BS * 0.05, $BS * 0.08, $BS * 0.42, $BS * 0.34)
        $g.FillEllipse($light, $BS * 0.58, $BS * 0.60, $BS * 0.40, $BS * 0.36)
        $water = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 58, 118, 140))
        $g.FillEllipse($water, $BS * 0.66, $BS * 0.10, $BS * 0.26, $BS * 0.20)
        $path = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 205, 176, 112)), ([float]($BS * 0.05))
        $path.StartCap = 'Round'; $path.EndCap = 'Round'
        $g.DrawBezier($path, [float]($BS * 0.0), [float]($BS * 0.78), [float]($BS * 0.35), [float]($BS * 0.55), [float]($BS * 0.55), [float]($BS * 0.95), [float]($BS * 1.0), [float]($BS * 0.40))
    }
    $g.ResetClip()

    # Ring
    $rw = [float]([Math]::Max($BS * 0.075, $k * 1.6))
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 222, 196, 120)), $rw
    $g.DrawEllipse($ring, $pad + $rw / 2, $pad + $rw / 2, $d - $rw, $d - $rw)

    # Player arrow (same shape as the overlay), pointing north-east
    $st = $g.Save()
    $g.TranslateTransform($BS / 2, $BS / 2)
    $g.RotateTransform(-45)
    $div = 34; if ($small) { $div = 26 }
    $u = [float]($BS / $div)
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(11 * $u, 0), [System.Drawing.PointF]::new(-7 * $u, -7 * $u),
        [System.Drawing.PointF]::new(-3 * $u, 0), [System.Drawing.PointF]::new(-7 * $u, 7 * $u))
    $g.TranslateTransform(-2 * $u, 0)
    $outline = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]([Math]::Max($BS * 0.045, $k * 1.2)))
    $outline.LineJoin = 'Round'
    if ($s -ge 32) {
        $sh2 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 0, 0, 0))
        $g.TranslateTransform($BS * 0.015, $BS * 0.025); $g.FillPolygon($sh2, $pts); $g.TranslateTransform(-$BS * 0.015, -$BS * 0.025)
    }
    $g.DrawPolygon($outline, $pts)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 236, 84, 40))), $pts)
    $g.Restore($st)
    $g.Dispose()

    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'; $g2.CompositingQuality = 'HighQuality'
    $g2.DrawImage($big, 0, 0, $s, $s)
    $g2.Dispose(); $big.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$b) {
    $s = $b.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) {
        $c = $b.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush(); return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$blobs = @()
foreach ($s in $sizes) {
    $b = New-Frame $s
    if ($PreviewDir) { $b.Save((Join-Path $PreviewDir "icon_$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($s -ge 256) { $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $blobs += , $ms.ToArray() }
    else { $blobs += , (Get-DibBytes $b) }
    $b.Dispose()
}
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$blobs[$i].Length); $w.Write([int]$offset)
    $offset += $blobs[$i].Length
}
foreach ($bl in $blobs) { $w.Write($bl) }
$w.Close()
"ok $Out"
