# Generates the app icon and README images from a single minimal gamepad design.
# Outputs:
#   build\CouchMode.ico  (multi-size, embedded into the exe)
#   assets\logo.png         (256px transparent)
#   assets\banner.png       (README hero image)
#
# The same gamepad geometry is mirrored in CouchMode.cs (MakeIcon) so the
# tray icon matches this exe icon.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = $PSScriptRoot

$Green  = [System.Drawing.Color]::FromArgb(255, 16, 124, 16)
$Gray   = [System.Drawing.Color]::FromArgb(255, 110, 110, 110)
$White  = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the gamepad into a transparent bitmap of the given size.
function New-GamepadBitmap([int]$S, [System.Drawing.Color]$fill) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $S / 32.0

    $bFill   = New-Object System.Drawing.SolidBrush($fill)
    $bDetail = New-Object System.Drawing.SolidBrush($White)

    # Silhouette: two rounded grips + a body, all in the fill colour.
    $g.FillEllipse($bFill, 2*$k, 12*$k, 13*$k, 13*$k)   # left grip
    $g.FillEllipse($bFill, 17*$k, 12*$k, 13*$k, 13*$k)  # right grip
    $body = New-RoundRect (4*$k) (7*$k) (24*$k) (13*$k) (6*$k)
    $g.FillPath($bFill, $body)

    # Details in white: D-pad (left) and two buttons (right).
    $g.FillRectangle($bDetail, 8.6*$k, 12*$k, 1.8*$k, 7*$k)   # dpad vertical
    $g.FillRectangle($bDetail, 6*$k, 14.6*$k, 7*$k, 1.8*$k)   # dpad horizontal
    $g.FillEllipse($bDetail, 20*$k, 12*$k, 3*$k, 3*$k)        # button A
    $g.FillEllipse($bDetail, 23.3*$k, 15.3*$k, 3*$k, 3*$k)    # button B

    $body.Dispose(); $bFill.Dispose(); $bDetail.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# Build a PNG-compressed .ico (valid on Windows Vista+; fine for Windows 11).
function Write-Ico([string]$path, [int[]]$sizes) {
    $pngs = @()
    foreach ($s in $sizes) {
        $b = New-GamepadBitmap $s $Green
        $pngs += ,(Get-PngBytes $b)
        $b.Dispose()
    }
    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)  # ICONDIR
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $bw.Write([byte]($s -band 0xFF))   # width (0 = 256)
        $bw.Write([byte]($s -band 0xFF))   # height
        $bw.Write([byte]0)                 # colours
        $bw.Write([byte]0)                 # reserved
        $bw.Write([UInt16]1)               # planes
        $bw.Write([UInt16]32)              # bitcount
        $bw.Write([UInt32]$len)            # bytes in resource
        $bw.Write([UInt32]$offset)         # offset
        $offset += $len
    }
    foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

# --- Outputs ---
New-Item -ItemType Directory -Force -Path (Join-Path $root "build") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root "assets") | Out-Null

Write-Ico (Join-Path $root "build\CouchMode.ico") @(256, 128, 64, 48, 32, 16)
Write-Host "Wrote build\CouchMode.ico" -ForegroundColor Green

$logo = New-GamepadBitmap 256 $Green
$logo.Save((Join-Path $root "assets\logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Host "Wrote assets\logo.png" -ForegroundColor Green

# Banner: dark background, gamepad on the left, title + tagline on the right.
$bw_ = 760; $bh = 240
$banner = New-Object System.Drawing.Bitmap($bw_, $bh)
$bg = [System.Drawing.Graphics]::FromImage($banner)
$bg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$bg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$bg.Clear([System.Drawing.Color]::FromArgb(255, 14, 14, 14))

$icon = New-GamepadBitmap 160 $Green
$bg.DrawImage($icon, 48, 40, 160, 160)
$icon.Dispose()

$titleFont = New-Object System.Drawing.Font("Segoe UI", 34, [System.Drawing.FontStyle]::Bold)
$tagFont   = New-Object System.Drawing.Font("Segoe UI", 15, [System.Drawing.FontStyle]::Regular)
$wBrush = New-Object System.Drawing.SolidBrush($White)
$gBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 170, 170, 170))
$bg.DrawString("CouchMode", $titleFont, $wBrush, 240, 78)
$bg.DrawString([char]0x2192 + " controller on = Xbox mode  ·  off = desktop", $tagFont, $gBrush, 244, 134)

$banner.Save((Join-Path $root "assets\banner.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$titleFont.Dispose(); $tagFont.Dispose(); $wBrush.Dispose(); $gBrush.Dispose(); $bg.Dispose(); $banner.Dispose()
Write-Host "Wrote assets\banner.png" -ForegroundColor Green
