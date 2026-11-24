Add-Type -AssemblyName System.Drawing

$size = 64
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(11, 15, 20))

function New-Rounded($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$accent = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, 212, 167), 3)
$path = New-Rounded 7 11 50 42 10
$g.DrawPath($accent, $path)

$bar = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, 212, 167), 3.4)
$bar.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$bar.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($bar, 17, 27, 30, 27)
$g.DrawLine($bar, 17, 37, 38, 37)

$dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(45, 212, 167))
$g.FillEllipse($dot, 42, 23, 9, 9)
$g.Dispose()

$hicon = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hicon)
$stream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$icon.Save($stream)
$stream.Close()
$icon.Dispose()
$bmp.Dispose()

Write-Output 'app.ico created'
