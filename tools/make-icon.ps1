# Draws the programs' icon (a teal tile with a small plate of wells, a few of them lit) at 16-256 px and writes
# src\app.ico (PNG images inside, as Windows 10/11 read them). Run from this folder: powershell -File make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$teal = [System.Drawing.Color]::FromArgb(255, 7, 118, 130)
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    # the tile: a rounded square
    $r = [Math]::Max(2.0, $s * 0.19); $m = [Math]::Max(0.5, $s * 0.03); $w = $s - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $teal), $path)
    # the wells: 3 x 3 on small icons, 4 x 4 from 32 px up; the lit ones run along a diagonal (a curve rising)
    $n = if ($s -lt 32) { 3 } else { 4 }
    $lit = if ($n -eq 3) { @('2,0', '1,1', '0,2', '2,1') } else { @('3,0', '2,1', '1,2', '3,1', '2,2', '0,3') }
    $pad = $s * 0.22; $step = ($s - 2 * $pad) / ($n - 1); $dot = [Math]::Max(1.6, $step * 0.62)
    for ($row = 0; $row -lt $n; $row++) {
        for ($col = 0; $col -lt $n; $col++) {
            $cx = $pad + $col * $step; $cy = $pad + $row * $step
            $on = $lit -contains "$row,$col"
            $alpha = if ($on) { 255 } else { 95 }
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255))
            $g.FillEllipse($brush, [float]($cx - $dot / 2), [float]($cy - $dot / 2), [float]$dot, [float]$dot)
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($s, $ms.ToArray())
    $bmp.Dispose()
}
# ICO: header, one 16-byte entry per image, then the PNG data
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Close()
"{0}  {1:N0} bytes" -f (Resolve-Path $out), (Get-Item $out).Length
