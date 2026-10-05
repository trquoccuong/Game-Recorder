# Builds a self-contained Windows x64 package: odx-app, the odx command-line tool (input, record, build, validate, agent,
# simulate-game), odx-orchestrator (multiplayer, runs on the host's machine), the .NET runtime, a minimal ffmpeg and the game profiles (games/). Unzip it on any Windows 10 2004+ x64 machine and run
# "Odx Capture.exe" (or bin\odx.exe); nothing else needs installing. Needs MSYS2 (ffmpeg) and the MSVC build tools
# (NativeAOT launcher).
#
#   powershell -ExecutionPolicy Bypass -File publish.ps1 [-Version 0.1.0]
#
# Output: dist/odx-capture-<version>-win-x64/ and dist/odx-capture-<version>-win-x64.zip
param([string]$Version = "0.1.0")
$ErrorActionPreference = 'Stop'

# ffmpeg is built from pinned sources by ffmpeg/build-ffmpeg.sh (minimal: only what odx record uses) in MSYS2 and
# cached under .cache/, keyed by the script's hash so a changed script rebuilds it.
$root = $PSScriptRoot
$name = "odx-capture-$Version-win-x64"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
$cache = Join-Path $root '.cache'

$bin = Join-Path $out 'bin'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out, $bin, $cache | Out-Null
# NativeAOT finds the MSVC linker through vswhere.exe, which is not on PATH by default.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

# Layout: "Odx Capture.exe" (launcher) at the root; odx-app.exe, odx.exe, odx-orchestrator.exe, ffmpeg/ and games/ in bin/.
# The launcher is NativeAOT (~2 MB): Windows Defender scans a new executable before it runs, which takes 12-27 s for
# the 240 MB odx-app.exe on its first launch; the launcher is scanned at once and shows the splash meanwhile.
# Single-file: each executable embeds its own .NET runtime and libraries (WPF's native libraries are extracted at first
# run), so the two never share or overwrite files; no .pdb files. Not compressed: decompressing on every start took
# odx-app ~10 s to show its window, and the zip is compressed anyway. ReadyToRun avoids most JIT work at startup
# (odx record took ~3 s cold without it).
$publishes = @(
    @{ Project = 'Odx.App'; Out = $bin; Framework = $null },
    @{ Project = 'Odx.Cli'; Out = $bin; Framework = 'net8.0-windows10.0.22621.0' },
    @{ Project = 'Odx.Orchestrator'; Out = $bin; Framework = $null }
)
foreach ($p in $publishes) {
    $frameworkArgs = if ($p.Framework) { @('-f', $p.Framework) } else { @() }
    dotnet publish (Join-Path $root "src/$($p.Project)") -c Release -r win-x64 --self-contained @frameworkArgs `
        -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:Version=$Version -o $p.Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($p.Project) failed" }
}
$launcherOut = Join-Path $cache 'launcher'
dotnet publish (Join-Path $root 'src/Odx.Launcher') -c Release -r win-x64 -p:DebugType=None -p:Version=$Version -o $launcherOut
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish Odx.Launcher failed' }
Copy-Item (Join-Path $launcherOut 'odx-launcher.exe') (Join-Path $out 'Odx Capture.exe')

$buildScript = Join-Path $root 'ffmpeg/build-ffmpeg.sh'
$scriptHash = (Get-FileHash $buildScript -Algorithm SHA256).Hash.Substring(0, 12)
$ffmpegBuild = Join-Path $cache "ffmpeg-$scriptHash"
if (-not (Test-Path (Join-Path $ffmpegBuild 'ffmpeg.exe'))) {
    $msys = if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { 'C:\msys64' }
    $bash = Join-Path $msys 'usr\bin\bash.exe'
    if (-not (Test-Path $bash)) {
        throw "ffmpeg is not built yet and MSYS2 was not found at $msys (set MSYS2_ROOT). Install MSYS2, then in it: " +
              "pacman -S make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf"
    }
    Write-Host "building ffmpeg (once, ~10-20 min) into $ffmpegBuild"
    $toUnix = Join-Path $msys 'usr\bin\cygpath.exe'
    $env:MSYSTEM = 'UCRT64'
    $env:CHERE_INVOKING = '1'
    & $bash -lc "bash '$(& $toUnix -u $buildScript)' '$(& $toUnix -u $ffmpegBuild)'"
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg build failed' }
}
$ffmpegDir = Join-Path $bin 'ffmpeg'
New-Item -ItemType Directory -Force $ffmpegDir | Out-Null
Copy-Item (Join-Path $ffmpegBuild '*') $ffmpegDir

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageZip = Join-Path $dist "$name.zip"
if (Test-Path $packageZip) { Remove-Item -Force $packageZip }
[IO.Compression.ZipFile]::CreateFromDirectory($out, $packageZip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "package: $packageZip"
