<#
.SYNOPSIS
  Draws the PodBattery app icon (two earbuds on a green rounded square) and writes src\PodBattery\PodBattery.ico.
  Only needed if you want to change the icon:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x2E, 0x7D, 0x32))
    $square = New-RoundedRect 0 0 $s $s ($s * 0.22)
    $g.FillPath($bg, $square)

    # Two earbuds facing each other: a rounded head with a stem hanging down on the inner side.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    foreach ($side in -1, 1) {
        $headX = $s * (0.5 + $side * 0.17)
        $stemX = $s * (0.5 + $side * 0.105)
        $g.FillEllipse($white, $headX - $s * 0.145, $s * 0.17, $s * 0.29, $s * 0.29)
        $stem = New-RoundedRect ($stemX - $s * 0.05) ($s * 0.33) ($s * 0.10) ($s * 0.50) ($s * 0.05)
        $g.FillPath($white, $stem)
        $stem.Dispose()
    }

    $g.Dispose(); $bg.Dispose(); $white.Dispose(); $square.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO container with PNG-compressed entries (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()

$target = Join-Path $PSScriptRoot '..\src\PodBattery\PodBattery.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Host "Wrote $((Resolve-Path $target).Path) ($($out.Length) bytes)"
