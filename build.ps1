# Builds bin\NotchAgent.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'bin'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x introuvable ($csc)." }
New-Item -ItemType Directory -Force $out | Out-Null

# --- App icon: a mini notch tile with the working grid, as a PNG-compressed multi-size .ico ---------------
Add-Type -AssemblyName System.Drawing
function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = [single]($size * 0.22); $d = 2 * $r; $s = [single]($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0.5, 0.5, $d, $d, 180, 90); $path.AddArc($s - $d, 0.5, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90); $path.AddArc(0.5, $s - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 12, 12))), $path)
    $g.SmoothingMode = 'None'
    $cell = [math]::Max(3, [math]::Floor($size * 0.72 / 3)); $o = [math]::Floor(($size - 3 * $cell) / 2)
    $alpha = @(0.10, 0.30, 0.65, 0.10, 1.0, 0.10, 0.10, 0.10, 0.10)   # a snake trail mid-run
    for ($i = 0; $i -lt 9; $i++) {
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([int](255 * $alpha[$i]), 255, 204, 155))
        $g.FillRectangle($b, $o + ($i % 3) * $cell, $o + [math]::Floor($i / 3) * $cell, $cell, $cell)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return , $ms.ToArray()
}
$sizes = 16, 20, 24, 32, 48, 64, 256
$pngs = $sizes | ForEach-Object { , (New-IconPng $_) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
$icoPath = Join-Path $out 'NotchAgent.ico'
[System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())

# --- Compile -------------------------------------------------------------------------------------------
$wpf = Join-Path $fw 'WPF'
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Web.Extensions.dll",
    "$fw\System.Management.dll", "$fw\System.Core.dll", "$fw\System.dll"
) | ForEach-Object { "/reference:$_" }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $out 'NotchAgent.exe'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 `
    "/out:$exe" "/win32icon:$icoPath" "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    $refs $sources
if ($LASTEXITCODE -ne 0) { throw "Compilation échouée." }
Write-Host "OK  $exe"
