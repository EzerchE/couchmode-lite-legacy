# Generates the app/tray icon and README images by recoloring the couch source
# art (assets/couch-src.png). Free build = green, Pro build = blue (set $Brand).
# Outputs:
#   build\CouchMode.ico  (multi-size, embedded into the exe; recolored couch)
#   assets\logo.png      (rounded-square promo tile)
#   assets\banner.png    (README hero image)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = $PSScriptRoot

# Brand colour for THIS repo. Free repo: green. Pro repo: blue (#0177EB).
$Brand  = [System.Drawing.Color]::FromArgb(255, 16, 124, 16)    # green
$White  = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

$CouchSrc = [System.Drawing.Image]::FromFile((Join-Path $root "assets\couch-src.png"))

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

# Resizes the couch source to S and repaints every opaque pixel in $color
# (alpha preserved, so anti-aliased edges stay smooth). Optional inset margin.
function New-Couch([int]$S, [System.Drawing.Color]$color, [single]$inset) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [int]($S * $inset)
    $g.DrawImage($CouchSrc, $m, $m, ($S - 2*$m), ($S - 2*$m))
    $g.Dispose()
    for ($y = 0; $y -lt $S; $y++) {
        for ($x = 0; $x -lt $S; $x++) {
            $p = $bmp.GetPixel($x, $y)
            if ($p.A -gt 0) {
                $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($p.A, $color.R, $color.G, $color.B))
            }
        }
    }
    return $bmp
}

# App/tray icon: the couch alone (no square) in the brand colour.
function New-GamepadBitmap([int]$S, [System.Drawing.Color]$fill) {
    return New-Couch $S $fill 0.04
}

# Promo tile (rounded square + white couch) for README/Store.
function New-CouchTile([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $S / 32.0
    $lighter = [System.Drawing.Color]::FromArgb(255,
        [Math]::Min(255, $Brand.R + 60), [Math]::Min(255, $Brand.G + 60), [Math]::Min(255, $Brand.B + 60))
    $square = New-RoundRect (1*$k) (1*$k) (30*$k) (30*$k) (7*$k)
    $rect = New-Object System.Drawing.RectangleF (0,0,$S,$S)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $lighter, $Brand, 45.0)
    $g.FillPath($grad, $square)
    $grad.Dispose(); $square.Dispose()
    # White couch inset within the square.
    $white = New-Couch $S $White 0.2
    $g.DrawImage($white, 0, 0, $S, $S)
    $white.Dispose(); $g.Dispose()
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
        $b = New-GamepadBitmap $s $Brand
        $pngs += ,(Get-PngBytes $b)
        $b.Dispose()
    }
    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $bw.Write([byte]($s -band 0xFF)); $bw.Write([byte]($s -band 0xFF))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
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

$logo = New-GamepadBitmap 256 $Brand
$logo.Save((Join-Path $root "assets\logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Host "Wrote assets\logo.png" -ForegroundColor Green

# Banner: dark background, tile on the left, title + tagline on the right.
$bw_ = 760; $bh = 240
$banner = New-Object System.Drawing.Bitmap($bw_, $bh)
$bg = [System.Drawing.Graphics]::FromImage($banner)
$bg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$bg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$bg.Clear([System.Drawing.Color]::FromArgb(255, 14, 14, 14))
$icon = New-GamepadBitmap 160 $Brand
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

$CouchSrc.Dispose()
