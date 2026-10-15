# Draws icon.ico: a dark rounded tile with an orange RSS symbol and a blue unread dot.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Png([int]$s) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $s * 0.06); $w = $s - 2 * $m; $r = $s * 0.2
    $body = RoundRect $m $m $w $w $r
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 32, 40))), $body)
    $orange = [System.Drawing.Color]::FromArgb(255, 255, 145, 50)
    # RSS symbol, bottom-left: a dot and two quarter rings
    $ox = $m + $w * 0.24; $oy = $m + $w * 0.76; $pw = [Math]::Max(1.6, $w * 0.11)
    $pen = New-Object System.Drawing.Pen $orange, $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $dot = $w * 0.16
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $orange), [float]($ox - $dot / 2), [float]($oy - $dot / 2), [float]$dot, [float]$dot)
    foreach ($k in 0.27, 0.48) {
        $rr = $w * $k
        $g.DrawArc($pen, [float]($ox - $rr), [float]($oy - $rr), [float]($rr * 2), [float]($rr * 2), 270, 90)
    }
    # an unread dot, top-right
    $nd = $w * 0.22
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 96, 150, 250))), [float]($m + $w * 0.72 - $nd / 2), [float]($m + $w * 0.28 - $nd / 2), [float]$nd, [float]$nd)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$imgs = $sizes | ForEach-Object { , (Png $_) }
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $imgs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $imgs) { $bw.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $out.ToArray())
Write-Output "Wrote icon.ico"
