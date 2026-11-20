# Renders logo.svg with headless Edge and writes logo-256.png, logo-512.png and src/Kakudai.App/kakudai.ico
# (16, 20, 24, 32, 40, 48, 64 px as 32-bit bitmaps, 256 px as PNG). Nothing is downloaded.
# Usage: powershell -ExecutionPolicy Bypass -File docs\logo\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here '..\..')
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('kakudai-icon-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$sheet = Join-Path $tmp 'sheet.png'
$url = 'file:///' + (Join-Path $here 'sizes.html').Replace([char]92, [char]47)
$edgeArgs = '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
  '--default-background-color=00000000', "--user-data-dir=`"$tmp\profile`"", '--window-size=1100,600',
  "--screenshot=`"$sheet`"", "`"$url`""
Start-Process $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
for ($i = 0; $i -lt 50 -and -not (Test-Path $sheet); $i++) { Start-Sleep -Milliseconds 200 }

$sizes = 16, 20, 24, 32, 40, 48, 64, 256, 512
$all = [System.Drawing.Bitmap]::FromFile($sheet)
$png = @{}
$x = 0
foreach ($s in $sizes) {
  $one = $all.Clone((New-Object System.Drawing.Rectangle $x, 0, $s, $s), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $path = Join-Path $tmp "$s.png"
  $one.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $one.Dispose()
  $png[$s] = $path
  $x += $s + 4
}
$all.Dispose()
Copy-Item $png[256] (Join-Path $here 'logo-256.png') -Force
Copy-Item $png[512] (Join-Path $here 'logo-512.png') -Force

# .ico: header, one directory entry per image, then the images.
$images = foreach ($s in 16, 20, 24, 32, 40, 48, 64, 256) {
  if ($s -eq 256) { , @($s, [IO.File]::ReadAllBytes($png[$s])); continue }
  $bmp = [System.Drawing.Bitmap]::FromFile($png[$s])
  $bd = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $s, $s), 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $raw = New-Object byte[] ($bd.Stride * $s)
  [Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $raw, 0, $raw.Length)
  $bmp.UnlockBits($bd); $bmp.Dispose()
  $maskStride = [int]([math]::Ceiling($s / 32.0) * 4)
  $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
  # BITMAPINFOHEADER: height counts the colour rows and the AND mask rows.
  $w.Write([int32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
  $w.Write([int32]0); $w.Write([int32]($s * $s * 4 + $maskStride * $s)); $w.Write([int32]0); $w.Write([int32]0)
  $w.Write([int32]0); $w.Write([int32]0)
  for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($raw, $y * $bd.Stride, $s * 4) }   # bottom-up BGRA
  $w.Write((New-Object byte[] ($maskStride * $s)))                                  # AND mask: alpha does the work
  $w.Flush(); , @($s, $ms.ToArray())
}
$ico = Join-Path $repo 'src\Kakudai.App\kakudai.ico'
$fs = [IO.File]::Create($ico); $w = New-Object IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
  $b = if ($img[0] -ge 256) { 0 } else { $img[0] }
  $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([int16]1); $w.Write([int16]32); $w.Write([int32]$img[1].Length); $w.Write([int32]$offset)
  $offset += $img[1].Length
}
foreach ($img in $images) { $w.Write($img[1]) }
$w.Flush(); $fs.Dispose()
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Wrote logo-256.png, logo-512.png and $ico ($offset bytes, $($images.Count) sizes)."
