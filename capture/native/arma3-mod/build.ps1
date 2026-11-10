# Builds TbrainArma3.exe into -OutDir: tbrain_capture_x64.dll first, then the launcher that embeds it with the addon.
#   powershell -ExecutionPolicy Bypass -File build.ps1 -OutDir <folder>
param([Parameter(Mandatory)] [string]$OutDir)
$ErrorActionPreference = 'Stop'

$extractor = Join-Path $PSScriptRoot '..\extractor'
$build = Join-Path $extractor 'build'
if (-not (Test-Path (Join-Path $build 'CMakeCache.txt'))) { cmake -S $extractor -B $build; if ($LASTEXITCODE) { exit 1 } }
cmake --build $build --config Release --target tbrain_capture
if ($LASTEXITCODE) { exit 1 }

dotnet build (Join-Path $PSScriptRoot 'launcher\TbrainArma3') -c Release -o $OutDir
if ($LASTEXITCODE) { exit 1 }
Write-Host "built $(Join-Path $OutDir 'TbrainArma3.exe')"
