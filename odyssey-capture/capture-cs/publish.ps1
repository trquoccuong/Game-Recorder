# Builds a self-contained Windows x64 package: odx-app, odx-input, odx-record, odx-validate, the .NET runtime and
# ffmpeg/ffprobe. Unzip it on any Windows 10 2004+ x64 machine and run odx-app.exe (or tools\odx-validate\odx-validate.exe);
# nothing else needs installing.
#
#   powershell -ExecutionPolicy Bypass -File publish.ps1 [-Version 0.1.0]
#
# Output: dist/odx-capture-<version>-win-x64/ and dist/odx-capture-<version>-win-x64.zip
param([string]$Version = "0.1.0")
$ErrorActionPreference = 'Stop'

# Pinned gyan.dev "essentials" build: has libx264 and h264_nvenc. It is GPLv3; its LICENSE ships next to the binaries.
# Our tools only run it as a separate process, so they are not linked against it.
$FfmpegVersion = '9.0.2'
$FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$FfmpegVersion/ffmpeg-$FfmpegVersion-essentials_build.zip"
$FfmpegSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'

$root = $PSScriptRoot
$name = "odx-capture-$Version-win-x64"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
$cache = Join-Path $root '.cache'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out, $cache | Out-Null

# Layout: odx-app at the root, each command-line tool self-contained in tools/<name>/, ffmpeg in ffmpeg/.
# Separate folders because the tools' runtimes differ (WPF vs console, System.Text.Json 8 vs 9 via Vortice);
# sharing one folder would overwrite one tool's assemblies with another's.
# ReadyToRun avoids most JIT work at startup (odx-record took ~3 s on a cold start without it).
$projects = [ordered]@{
    'Odx.App'      = $out
    'Odx.Input'    = Join-Path $out 'tools/odx-input'
    'Odx.Recorder' = Join-Path $out 'tools/odx-record'
    'Odx.Validate' = Join-Path $out 'tools/odx-validate'
}
foreach ($project in $projects.Keys) {
    dotnet publish (Join-Path $root "src/$project") -c Release -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:Version=$Version -o $projects[$project]
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

$ffmpegZip = Join-Path $cache "ffmpeg-$FfmpegVersion-essentials_build.zip"
if (-not (Test-Path $ffmpegZip) -or (Get-FileHash $ffmpegZip -Algorithm SHA256).Hash -ne $FfmpegSha256) {
    Write-Host "downloading $FfmpegUrl"
    Invoke-WebRequest $FfmpegUrl -OutFile $ffmpegZip -UseBasicParsing
    $hash = (Get-FileHash $ffmpegZip -Algorithm SHA256).Hash
    if ($hash -ne $FfmpegSha256) { throw "ffmpeg archive SHA-256 mismatch: $hash" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$ffmpegDir = Join-Path $out 'ffmpeg'
New-Item -ItemType Directory -Force $ffmpegDir | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($ffmpegZip)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -match '/(bin/ffmpeg\.exe|bin/ffprobe\.exe|LICENSE|README\.txt)$') {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $ffmpegDir $entry.Name), $true)
        }
    }
} finally {
    $archive.Dispose()
}

$packageZip = Join-Path $dist "$name.zip"
if (Test-Path $packageZip) { Remove-Item -Force $packageZip }
[IO.Compression.ZipFile]::CreateFromDirectory($out, $packageZip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "package: $packageZip"
