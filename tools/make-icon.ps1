# Generates src/Lumina/Assets/lumina.ico (PNG-compressed, multi-size). Run with Windows PowerShell.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Lumina\Assets\lumina.ico'))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]($s * 0.24); $d = $r * 2; $e = [single]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($e - $d, 0, $d, $d, 270, 90)
    $path.AddArc($e - $d, $e - $d, $d, $d, 0, 90)
    $path.AddArc(0, $e - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $p0 = New-Object System.Drawing.PointF 0, 0
    $p1 = New-Object System.Drawing.PointF $s, $s
    $c0 = [System.Drawing.Color]::FromArgb(255, 124, 92, 255)
    $c1 = [System.Drawing.Color]::FromArgb(255, 46, 168, 255)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $p0, $p1, $c0, $c1
    $g.FillPath($brush, $path)

    $w = [single][Math]::Max(1.6, $s * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $m = [single]($s * 0.25); $l = [single]($s * 0.17); $f = [single]($s - $m)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $m, ($m + $l)), (New-Object System.Drawing.PointF $m, $m), (New-Object System.Drawing.PointF ($m + $l), $m)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($f - $l), $m), (New-Object System.Drawing.PointF $f, $m), (New-Object System.Drawing.PointF $f, ($m + $l))))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $f, ($f - $l)), (New-Object System.Drawing.PointF $f, $f), (New-Object System.Drawing.PointF ($f - $l), $f)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($m + $l), $f), (New-Object System.Drawing.PointF $m, $f), (New-Object System.Drawing.PointF $m, ($f - $l))))
    $dot = [single]($s * 0.09)
    $g.FillEllipse([System.Drawing.Brushes]::White, ($s / 2 - $dot), ($s / 2 - $dot), ($dot * 2), ($dot * 2))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = @(); foreach ($s in $sizes) { $frames += ,(New-Frame $s) }
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f) }
$bw.Close()
Write-Output "Wrote $Out"
