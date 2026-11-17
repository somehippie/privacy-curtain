<#
.SYNOPSIS
  Draws the curtain's icon into src\curtain.ico, which build.ps1 puts into ClaudeCurtain.exe.

.DESCRIPTION
  A closed curtain in the curtain's own colours: warm-grey folds, light and shade in turn, hanging
  from a terracotta rod, on a near-black tile. Each size (16 to 256 pixels) is drawn on whole
  pixels so the small ones stay crisp, and the .ico holds them as PNG images. Only needed again
  when the design changes; the .ico is kept in the repository.

.PARAMETER Out
  Where to write the .ico. Default: src\curtain.ico.

.PARAMETER PreviewPng
  Also draw every size on a dark and a light ground into this PNG, for looking at.
#>
param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\curtain.ico'),
    [string]$PreviewPng
)
Add-Type -AssemblyName System.Drawing
$ground = [System.Drawing.Color]::FromArgb(255, 20, 20, 19)
$edge   = [System.Drawing.Color]::FromArgb(255, 54, 52, 49)
$accent = [System.Drawing.Color]::FromArgb(255, 217, 119, 87)
$light  = [System.Drawing.Color]::FromArgb(255, 206, 202, 194)
$shade  = [System.Drawing.Color]::FromArgb(255, 160, 156, 149)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, [Math]::Min(2 * $r, [Math]::Min($w, $h)))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'Half'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(2, [Math]::Round($S * 0.2))
    $tile = RoundRect 0 0 $S $S $r
    $g.FillPath((New-Object System.Drawing.SolidBrush $ground), $tile)
    if ($S -ge 24) { $g.DrawPath((New-Object System.Drawing.Pen $edge, ([Math]::Max(1, [Math]::Round($S / 48)))), (RoundRect 0.5 0.5 ($S - 1) ($S - 1) $r)) }

    # The rod: across the top, the accent.
    $left = [Math]::Round($S * 0.19); $right = $S - $left
    $rodY = [Math]::Round($S * 0.22); $rodH = [Math]::Max(1, [Math]::Round($S * 0.065))
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), (RoundRect $left $rodY ($right - $left) $rodH ([Math]::Max(0.5, $rodH / 2))))

    # The fabric: five folds side by side, light and shade in turn, each rounded at the hem.
    $n = 5
    $inL = [Math]::Round($S * 0.22); $inR = $S - $inL
    $w = [Math]::Max(1, [Math]::Floor(($inR - $inL) / $n))
    $span = $n * $w
    $x0 = [Math]::Round(($S - $span) / 2)
    $top = $rodY + $rodH
    $bottom = [Math]::Round($S * 0.80)
    for ($i = 0; $i -lt $n; $i++) {
        $x = $x0 + $i * $w
        $c = if ($i % 2 -eq 0) { $light } else { $shade }
        $hem = if ($S -ge 32) { $w / 2 } else { 0.5 }
        $brush = New-Object System.Drawing.SolidBrush $c
        if ($S -ge 32) {
            $g.FillRectangle($brush, $x, $top, $w, ($bottom - $top - $hem))
            $g.FillEllipse($brush, $x, ($bottom - 2 * $hem), $w, (2 * $hem))
        } else {
            $g.FillRectangle($brush, $x, $top, $w, ($bottom - $top))
        }
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $b = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $ms.ToArray()
}

# ICONDIR, one ICONDIRENTRY per size, then the PNG images.
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Out, $ico.ToArray())
"wrote $Out ($($ico.Length) bytes, sizes $($sizes -join ', '))"

if ($PreviewPng) {
    # A sheet of every size on a dark and a light ground, for looking at.
    $sheet = New-Object System.Drawing.Bitmap 760, 300
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 0, 760, 150)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))), 0, 150, 760, 150)
    $x = 10
    foreach ($s in 16, 24, 32, 48, 64) {
        $b = Draw $s
        $g.DrawImage($b, $x, 20, $s * 2, $s * 2)
        $g.DrawImage($b, $x, 170, $s * 2, $s * 2)
        $b.Dispose()
        $x += $s * 2 + 14
    }
    $b = Draw 256
    $g.DrawImage($b, 560, 10, 130, 130)
    $g.DrawImage($b, 560, 160, 130, 130)
    $b.Dispose()
    $g.Dispose()
    $sheet.Save($PreviewPng)
    $sheet.Dispose()
}
