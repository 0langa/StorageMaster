Add-Type -AssemblyName System.Drawing

function New-Bitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $s = $size
    $r = [int]($s * 0.14)

    # Background gradient: deep blue
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 10, 60, 148),
        [System.Drawing.Color]::FromArgb(255, 21, 101, 192))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r*2, $r*2, 180, 90)
    $path.AddArc($s-$r*2, 0, $r*2, $r*2, 270, 90)
    $path.AddArc($s-$r*2, $s-$r*2, $r*2, $r*2, 0, 90)
    $path.AddArc(0, $s-$r*2, $r*2, $r*2, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bgBrush, $path)

    # HDD body (white rounded rect, lower portion)
    $hx = [int]($s*0.12); $hy = [int]($s*0.38)
    $hw = $s - 2*$hx;     $hh = [int]($s*0.50)
    $hr = [int]($s*0.08)
    $hddBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230,255,255,255))
    $hddPath  = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hddPath.AddArc($hx,        $hy,        $hr*2, $hr*2, 180, 90)
    $hddPath.AddArc($hx+$hw-$hr*2, $hy,    $hr*2, $hr*2, 270, 90)
    $hddPath.AddArc($hx+$hw-$hr*2, $hy+$hh-$hr*2, $hr*2, $hr*2, 0, 90)
    $hddPath.AddArc($hx,        $hy+$hh-$hr*2, $hr*2, $hr*2, 90, 90)
    $hddPath.CloseFigure()
    $g.FillPath($hddBrush, $hddPath)

    # Disk platter circle inside HDD
    $cx = [int]($s*0.63); $cy = [int]($hy + $hh/2); $pr = [int]($hh*0.32)
    $platBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 10, 60, 148))
    $g.FillEllipse($platBrush, $cx-$pr, $cy-$pr, $pr*2, $pr*2)
    $dotR = [int]($pr*0.32)
    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 41, 121, 212))
    $g.FillEllipse($dotBrush, $cx-$dotR, $cy-$dotR, $dotR*2, $dotR*2)

    # Read arm
    if ($s -ge 32) {
        $armW = [float]([Math]::Max(1.5, $s*0.025))
        $armPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 10, 60, 148), $armW)
        $armPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $armPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($armPen, $cx-$pr, $cy, [int]($hx+$hw*0.12), [int]($hy+$hh*0.25))
    }

    # Screws
    $screwBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 10, 60, 148))
    $sr = [int]([Math]::Max(2, $s*0.038))
    $sx = $hx + [int]($hw*0.09)
    $g.FillEllipse($screwBrush, $sx-$sr, $cy-[int]($hh*0.28)-$sr, $sr*2, $sr*2)
    $g.FillEllipse($screwBrush, $sx-$sr, $cy+[int]($hh*0.28)-$sr, $sr*2, $sr*2)

    # 4-point sparkle star in gold (top-right area)
    $starCX = [int]($s*0.73); $starCY = [int]($s*0.20)
    $outerR = [int]($s*0.145); $innerR = [int]($s*0.055)
    $starBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 210, 0))
    $starPoints = New-Object System.Drawing.PointF[] 8
    for ($i = 0; $i -lt 8; $i++) {
        $angle  = [Math]::PI * $i / 4.0 - [Math]::PI/2.0
        $radius = if ($i % 2 -eq 0) { $outerR } else { $innerR }
        $starPoints[$i] = New-Object System.Drawing.PointF(
            [float]($starCX + $radius * [Math]::Cos($angle)),
            [float]($starCY + $radius * [Math]::Sin($angle)))
    }
    $g.FillPolygon($starBrush, $starPoints)

    # Speed lines (top-left area) — white, semi-transparent
    if ($s -ge 32) {
        $lineW = [float]([Math]::Max(1.0, $s*0.02))
        $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(140,255,255,255), $lineW)
        $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $linePen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $lx = [int]($s*0.15); $ly = [int]($s*0.18)
        $g.DrawLine($linePen, $lx, $ly,                 $lx+[int]($s*0.20), $ly)
        $g.DrawLine($linePen, $lx, $ly+[int]($s*0.07), $lx+[int]($s*0.14), $ly+[int]($s*0.07))
        $g.DrawLine($linePen, $lx, $ly+[int]($s*0.14), $lx+[int]($s*0.17), $ly+[int]($s*0.14))
    }

    $g.Dispose()
    return $bmp
}

$sizes   = @(256, 48, 32, 16)
$bitmaps = $sizes | ForEach-Object { New-Bitmap $_ }

$pngStreams = $bitmaps | ForEach-Object {
    $ps = New-Object System.IO.MemoryStream
    $_.Save($ps, [System.Drawing.Imaging.ImageFormat]::Png)
    $ps.ToArray()
}

$ms     = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ms)

$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

$dataOffset = 6 + 16 * $sizes.Count

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz  = $sizes[$i]
    $dim = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
    $writer.Write($dim)
    $writer.Write($dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$pngStreams[$i].Length)
    $off = $dataOffset
    for ($j = 0; $j -lt $i; $j++) { $off += $pngStreams[$j].Length }
    $writer.Write([uint32]$off)
}

foreach ($stream in $pngStreams) { $writer.Write($stream) }
$writer.Flush()

$outPath = "C:\Users\juliu\source\repos\StorageMaster\src\StorageMaster.UI\Assets\storagemaster.ico"
[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
Write-Host "ICO written: $($ms.Length) bytes to $outPath"
