# Builds the players' package on the VM (dotnet/publish.ps1) for this server's orchestrator, in parallel with
# setup.ps1: a first build without the access key (MSYS2, ffmpeg, the app), then, once setup.ps1 has written the key,
# a second one with it (ffmpeg and NuGet cached, a few minutes), uploaded to the setup bucket. Runs as SYSTEM.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = 'C:\odx'
$tools = "$root\tools"
# Its own source copy: setup.ps1 deletes and re-extracts src\capture when it runs.
$pkgsrc = "$root\pkgsrc"
Start-Transcript -Append "$root\logs\build-package.log" | Out-Null
function Step($msg) { Write-Host "odx-package: $msg" }

$bash = 'C:\msys64\usr\bin\bash.exe'
if (-not (Test-Path $bash)) {
    Step 'install MSYS2'
    Invoke-WebRequest 'https://github.com/msys2/msys2-installer/releases/download/nightly-x86_64/msys2-base-x86_64-latest.sfx.exe' `
        -OutFile "$tools\msys2.sfx.exe" -UseBasicParsing
    & "$tools\msys2.sfx.exe" -y -oC:\ | Out-Host
    & $bash -lc 'true' | Out-Host
    & $bash -lc 'pacman -Syuu --noconfirm' | Out-Host
    & $bash -lc 'pacman -Syuu --noconfirm' | Out-Host
}
Step 'pacman: ffmpeg build tools'
& $bash -lc 'pacman -S --needed --noconfirm make diffutils curl tar xz bzip2 mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf' | Out-Host
if ($LASTEXITCODE) { throw 'pacman failed' }

New-Item -ItemType Directory -Force $pkgsrc | Out-Null
if (-not (Test-Path "$pkgsrc\capture")) {
    & gcloud storage cp gs://odx-game-server-setup/capture.tar "$pkgsrc\capture.tar" --quiet
    tar -xf "$pkgsrc\capture.tar" -C $pkgsrc
    if ($LASTEXITCODE) { throw 'untar failed' }
}

$env:PATH = "$tools\git\cmd;$tools\dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = "$root\dotnet-home"
$env:NUGET_PACKAGES = "$root\nuget-pkg"
$env:MSYS2_ROOT = 'C:\msys64'

$ip = Invoke-RestMethod -Headers @{ 'Metadata-Flavor' = 'Google' } `
    'http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/access-configs/0/external-ip'
$publish = "$pkgsrc\capture\dotnet\publish.ps1"
function Publish($extra) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $publish -Version 0.1.0 -Orchestrator "http://${ip}:5180" @extra | Out-Host
    if ($LASTEXITCODE) { throw "publish.ps1 failed: $LASTEXITCODE" }
}

Step "publish.ps1 for http://${ip}:5180 without the key (ffmpeg build ~10-20 min)"
Publish @()

$keyFile = "$root\secrets\access-key.txt"
Step 'waiting for setup.ps1 to write the access key'
while (-not (Test-Path $keyFile)) { Start-Sleep 30 }
Start-Sleep 5
Step 'publish.ps1 with the access key'
Publish @('-AccessKey', (Get-Content $keyFile -Raw).Trim())

& gcloud storage cp "$pkgsrc\capture\dotnet\dist\odx-capture-0.1.0-win-x64.zip" gs://odx-game-server-setup/packages/ --quiet
if ($LASTEXITCODE) { throw 'upload failed' }
[IO.File]::WriteAllText("$root\package-done.txt", (Get-Date -Format o))
Step 'DONE gs://odx-game-server-setup/packages/odx-capture-0.1.0-win-x64.zip'
Stop-Transcript | Out-Null
