# Builds a self-contained Windows x64 package: odx-app, the odx command-line tool (input, record, build, validate), the
# .NET runtime, ffmpeg/ffprobe and the game profiles (games/). Unzip it on any Windows 10 2004+ x64 machine and run
# odx-app.exe (or odx.exe); nothing else needs installing.
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

# Layout: odx-app.exe and odx.exe side by side, ffmpeg in ffmpeg/, game profiles in games/.
# Single-file: each executable embeds its own .NET runtime and libraries (WPF's native libraries are extracted at first
# run), so the two never share or overwrite files; no .pdb files. Not compressed: decompressing on every start took
# odx-app ~10 s to show its window, and the zip is compressed anyway. ReadyToRun avoids most JIT work at startup
# (odx record took ~3 s cold without it).
$publishes = @(
    @{ Project = 'Odx.App'; Out = $out; Framework = $null },
    @{ Project = 'Odx.Cli'; Out = $out; Framework = 'net8.0-windows10.0.22621.0' }
)
foreach ($p in $publishes) {
    $frameworkArgs = if ($p.Framework) { @('-f', $p.Framework) } else { @() }
    dotnet publish (Join-Path $root "src/$($p.Project)") -c Release -r win-x64 --self-contained @frameworkArgs `
        -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:Version=$Version -o $p.Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($p.Project) failed" }
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
