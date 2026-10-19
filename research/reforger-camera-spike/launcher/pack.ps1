param([string]$Out = 'D:\odx-dev\odx-reforger-launcher.zip')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Files at the zip root: extracted into the game folder, the launcher finds the addon next to itself.
$stage = Join-Path (Split-Path $Out) ('odx-pack-' + [guid]::NewGuid())
New-Item -ItemType Directory $stage | Out-Null
try {
    Copy-Item (Join-Path $PSScriptRoot 'Launch-Odx.cmd'), (Join-Path $PSScriptRoot 'Launch-Odx.ps1') $stage
    Copy-Item (Join-Path $PSScriptRoot '..\addon\OdxCameraSpike') $stage -Recurse
    if (Test-Path $Out) { Remove-Item $Out }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $Out)
} finally {
    Remove-Item $stage -Recurse -Force
}
"Packed $Out"
