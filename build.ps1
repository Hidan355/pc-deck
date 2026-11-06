# Gera dist\PCDeck-Setup.exe usando o compilador C# que ja vem no Windows (.NET Framework 4.x).
# Uso: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$Dist = Join-Path $Root 'dist'
New-Item -ItemType Directory -Force $Dist | Out-Null

# ---------- icone (16-256 px, quadrados brancos sobre roxo) ----------
Add-Type -AssemblyName System.Drawing

function Add-RoundRect($g, $brush, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $g.FillPath($brush, $p)
}

function New-DeckBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        [System.Drawing.Color]::FromArgb(255, 141, 113, 255), [System.Drawing.Color]::FromArgb(255, 92, 62, 230))
    Add-RoundRect $g $bg (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $w = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $w2 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(140, 255, 255, 255))
    Add-RoundRect $g $w (56 * $s) (56 * $s) (64 * $s) (64 * $s) (16 * $s)
    Add-RoundRect $g $w (136 * $s) (56 * $s) (64 * $s) (64 * $s) (16 * $s)
    Add-RoundRect $g $w (56 * $s) (136 * $s) (64 * $s) (64 * $s) (16 * $s)
    Add-RoundRect $g $w2 (136 * $s) (136 * $s) (64 * $s) (64 * $s) (16 * $s)
    $g.Dispose()
    $bmp
}

# tamanhos pequenos em DIB 32 bits (compativel com System.Drawing.Icon), 256 em PNG
$entries = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $bmp = New-DeckBitmap $size
    if ($size -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
        $bd = $bmp.LockBits($rect, 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
        $pixels = New-Object byte[] ($size * $size * 4)
        [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $pixels, 0, $pixels.Length)
        $bmp.UnlockBits($bd)
        $maskRow = [int]([math]::Ceiling($size / 32.0) * 4)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]($pixels.Length + $maskRow * $size)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $size * 4, $size * 4) }
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
        $data = $ms.ToArray()
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $data }
}

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$e.Data.Length); $bw.Write([uint32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write($e.Data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $Root 'deck.ico'), $ico.ToArray())
New-Item -ItemType Directory -Force (Join-Path $Root 'docs') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $Root 'docs\logo.png'), ($entries | Where-Object Size -eq 256).Data)

# ---------- compilacao ----------
$out = Join-Path $Dist 'PCDeck-Setup.exe'
$resources = @(
    "/resource:$Root\web\index.html,index.html",
    "/resource:$Root\web\editor.html,editor.html",
    "/resource:$Root\web\qr.html,qr.html",
    "/resource:$Root\web\manifest.json,manifest.json",
    "/resource:$Root\config.json,default-config.json",
    "/resource:$Root\deck.ico,deck.ico"
)
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
        'Microsoft.CSharp.dll', "$Fw\System.Runtime.WindowsRuntime.dll" | ForEach-Object { "/r:$_" }

& "$Fw\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$out" "/win32icon:$Root\deck.ico" @refs @resources (Get-ChildItem "$Root\src\*.cs").FullName
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilacao' }

Write-Host "OK: $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)" -ForegroundColor Green
