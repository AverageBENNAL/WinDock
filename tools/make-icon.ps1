# Draws the WinDock app icon and writes icon.ico (plus a preview PNG) next to the project.
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256, $size / 256)

    # Background: blue-to-violet rounded square.
    $back = New-RoundedRect 12 12 232 232 54
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(12, 12)), (New-Object System.Drawing.PointF(244, 244)),
        [System.Drawing.Color]::FromArgb(255, 46, 148, 255), [System.Drawing.Color]::FromArgb(255, 124, 72, 255))
    $g.FillPath($gradient, $back)

    # The dock: a frosted bar with three icons, the middle one magnified.
    $bar = New-RoundedRect 38 150 180 56 22
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 255, 255, 255))), $bar)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $soft = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 255, 255, 255))
    $g.FillPath($soft, (New-RoundedRect 54 158 38 38 11))
    $g.FillPath($white, (New-RoundedRect 98 122 60 74 17))
    $g.FillPath($soft, (New-RoundedRect 164 158 38 38 11))

    $g.Dispose()
    $bmp
}

$root = Split-Path $PSScriptRoot -Parent
$sizes = 256, 64, 48, 32, 24, 16
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $root 'icon.png')) }
    , $stream.ToArray()
}

# ICO container: header, one directory entry per size, then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
[System.IO.File]::WriteAllBytes((Join-Path $root 'icon.ico'), $out.ToArray())
"Wrote icon.ico ($($out.Length) bytes)"
