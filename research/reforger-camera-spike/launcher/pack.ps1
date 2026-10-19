param([string]$Out = 'D:\odx-dev\tbrain-reforger-launcher.zip')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Files at the zip root: extracted into the game folder, the launcher finds the addon next to itself.
$stage = Join-Path (Split-Path $Out) ('tbrain-pack-' + [guid]::NewGuid())
New-Item -ItemType Directory $stage | Out-Null
try {
    Copy-Item (Join-Path $PSScriptRoot 'Launch-Tbrain.cmd'), (Join-Path $PSScriptRoot 'Launch-Tbrain.ps1') $stage
    Copy-Item (Join-Path $PSScriptRoot '..\addon\TbrainCameraSpike') $stage -Recurse
    if (Test-Path $Out) { Remove-Item $Out }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $Out)
} finally {
    Remove-Item $stage -Recurse -Force
}
"Packed $Out"
