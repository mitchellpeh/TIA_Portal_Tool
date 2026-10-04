# Rebuilds TiaPortalTool\app.ico (window and exe icon) from the logo, so the two always match. Scales the whole
# image to 16-256 px PNG entries; the logo should be square (it is never cropped).
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 [-Png <logo.png>] [-Ico <app.ico>]
param(
  [string]$Png = "$PSScriptRoot\..\TiaPortalTool\images\logopng.png",
  [string]$Ico = "$PSScriptRoot\..\TiaPortalTool\app.ico")
$Png = (Resolve-Path $Png).Path
$Ico = [System.IO.Path]::GetFullPath($Ico)

Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($Png)
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.DrawImage($src, 0, 0, $s, $s)   # whole image, scaled; no cropping
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $images += , @($s, $ms.ToArray())
  $bmp.Dispose()
}
$src.Dispose()

# ICO container: header, one directory entry per size, then the PNG data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
  $s = $img[0]; $data = $img[1]
  $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256 in an ICO directory entry
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
[System.IO.File]::WriteAllBytes($Ico, $out.ToArray())
"wrote $Ico ($($out.Length) bytes, sizes $($sizes -join ','))"
