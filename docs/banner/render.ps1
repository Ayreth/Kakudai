# Renders banner.html to docs/banner.png (1600 x 720) with headless Edge. Nothing is downloaded.
# Usage: powershell -ExecutionPolicy Bypass -File docs\banner\render.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path (Split-Path $here) 'banner.png'
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('kakudai-banner-' + [Guid]::NewGuid().ToString('N'))
$url = 'file:///' + (Join-Path $here 'banner.html').Replace([char]92, [char]47)
$edgeArgs = '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
  "--user-data-dir=`"$tmp`"", '--window-size=1600,720', '--virtual-time-budget=5000', "--screenshot=`"$out`"", "`"$url`""
Start-Process $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Wrote $out"
