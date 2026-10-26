# Draws survey.txt (green) and spawn_points.txt (red, numbered) on a 12.8 km square, north up, to check the spread.
param([string]$Survey, [string]$Points, [string]$Out = 'spawn-points-map.png', [int]$Size = 700)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap $Size, $Size
$g = [Drawing.Graphics]::FromImage($bmp)
$g.Clear([Drawing.Color]::FromArgb(30, 60, 110))
$scale = $Size / 12800.0
foreach ($l in Get-Content $Survey) {
    $p = $l.Split(' ')
    $g.FillRectangle([Drawing.Brushes]::DarkSeaGreen, [float]([double]$p[0] * $scale - 2), [float]($Size - [double]$p[1] * $scale - 2), 5, 5)
}
$font = New-Object Drawing.Font 'Arial', 9, ([Drawing.FontStyle]::Bold)
foreach ($l in Get-Content $Points | Where-Object { $_ -and $_ -notmatch '^#' }) {
    $p = $l.Split(' ')
    $x = [double]$p[0] * $scale; $y = $Size - [double]$p[1] * $scale
    $g.FillEllipse([Drawing.Brushes]::Red, [float]($x - 6), [float]($y - 6), 12, 12)
    $g.DrawString($p[-1], $font, [Drawing.Brushes]::White, [float]($x + 7), [float]($y - 7))
}
$bmp.Save((Join-Path (Get-Location) $Out))
$g.Dispose(); $bmp.Dispose()
