# Publie Kakudai dans .\app et crée le raccourci « Kakudai » sur le Bureau.
# Utilisation : powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'app'

dotnet publish (Join-Path $root 'src\Kakudai.App') -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué (code $LASTEXITCODE)." }

$exe = Join-Path $out 'Kakudai.exe'
$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $desktop 'Kakudai.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $out
$link.IconLocation = "$exe,0"
$link.Description = 'Kakudai: osu! map amplifier and trainer'
$link.Save()

Write-Host "Publié dans $out, raccourci créé sur le Bureau."
