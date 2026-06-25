<#
    Generates src/CoreShift/Assets/CoreShift.ico — a multi-resolution icon
    (dark rounded tile, green ring, white power glyph).

    Layout: 16-128 px frames are stored as uncompressed 32-bpp BMP/DIB (max compatibility
    with GDI+, WIC and the shell); the 256 px frame is stored as PNG. This is the standard
    Windows .ico layout.

    Run with Windows PowerShell (it relies on System.Drawing / GDI+):
        powershell.exe -ExecutionPolicy Bypass -File build\generate-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root    = Split-Path -Parent $PSScriptRoot
$outDir  = Join-Path $root 'src\CoreShift\Assets'
$outPath = Join-Path $outDir 'CoreShift.ico'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Bitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile background (vertical gradient)
    $inset = [single]($S * 0.06)
    $rad   = [single]($S * 0.22)
    $rect  = New-Object System.Drawing.RectangleF($inset, $inset, ($S - 2 * $inset), ($S - 2 * $inset))
    $path  = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d     = [single]($rad * 2)
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 30, 30, 30)
    $c2 = [System.Drawing.Color]::FromArgb(255, 18, 18, 18)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 90)
    $g.FillPath($bg, $path)

    # Green accent ring
    $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 27, 94, 32), [single]($S * 0.05))
    $g.DrawPath($ringPen, $path)

    # Power glyph: arc (gap at top) + vertical bar
    $cx = [single]($S / 2.0)
    $cy = [single]($S * 0.54)
    $r  = [single]($S * 0.20)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 245, 245, 245), [single]([Math]::Max(1.4, $S * 0.085)))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pen, ($cx - $r), ($cy - $r), (2 * $r), (2 * $r), -65, 290)
    $g.DrawLine($pen, $cx, ($cy - $r * 1.35), $cx, ($cy - $r * 0.1))

    $g.Dispose(); $ringPen.Dispose(); $pen.Dispose(); $bg.Dispose(); $path.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

function Get-DibBytes($bmp) {
    $S = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER — height is doubled to cover the XOR image + AND mask
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$S)
    $bw.Write([Int32]($S * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)      # BI_RGB
    $bw.Write([UInt32]0)
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    # XOR pixel data, 32-bpp BGRA, bottom-up
    for ($y = $S - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $S; $x++) {
            $px = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$px.B); $bw.Write([byte]$px.G); $bw.Write([byte]$px.R); $bw.Write([byte]$px.A)
        }
    }

    # AND mask: all opaque (alpha channel carries transparency); rows padded to 4 bytes
    $rowBytes = [int]([Math]::Floor(($S + 31) / 32) * 4)
    $zeroRow  = New-Object byte[] $rowBytes
    for ($y = 0; $y -lt $S; $y++) { $bw.Write($zeroRow) }

    $bw.Flush()
    return , $ms.ToArray()
}

# Build frame payloads
$payloads = @()
foreach ($s in $sizes) {
    $bmp = New-Bitmap $s
    if ($s -ge 256) { $payloads += , (Get-PngBytes $bmp) }
    else            { $payloads += , (Get-DibBytes $bmp) }
    $bmp.Dispose()
}

# Write the .ico container
$fs = [System.IO.File]::Open($outPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s   = $sizes[$i]
    $len = $payloads[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len)
    $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $payloads) { $bw.Write($p) }
$bw.Flush(); $fs.Close()

Write-Host "Created $outPath ($([Math]::Round((Get-Item $outPath).Length / 1KB, 1)) KB)"
