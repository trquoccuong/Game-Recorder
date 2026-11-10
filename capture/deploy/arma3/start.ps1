# Starts the Arma 3 test server from -ServerDir (SteamCMD app 233780) with server.cfg and the tbrain_test mission.
# Players join with TbrainArma3.exe, Server = <this machine>:2302, password "tbrain".
#   powershell -ExecutionPolicy Bypass -File start.ps1 -ServerDir <folder>
param([Parameter(Mandatory)] [string]$ServerDir)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $ServerDir 'arma3server_x64.exe'
if (-not (Test-Path $exe)) { throw "arma3server_x64.exe not found in $ServerDir" }

Copy-Item (Join-Path $PSScriptRoot 'server.cfg') $ServerDir -Force
Copy-Item (Join-Path $PSScriptRoot 'mpmissions\*') (Join-Path $ServerDir 'mpmissions') -Recurse -Force
$profiles = Join-Path $ServerDir 'profiles'
New-Item -ItemType Directory -Force $profiles | Out-Null
Start-Process $exe -WorkingDirectory $ServerDir -ArgumentList '-port=2302', '-config=server.cfg', "-profiles=`"$profiles`"",
    '-name=server', '-world=empty'
Write-Host "server starting; log: $profiles\arma3server_x64_*.rpt"
