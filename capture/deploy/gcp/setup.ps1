# Sets up the CS2 capture server on a fresh Windows Server VM (guides/cs2-server.md + guides/orchestrator.md + guides/network.md).
# Runs as SYSTEM from the VM's startup script; idempotent, every step skips what is already done.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = 'C:\odx'
$tools = "$root\tools"
$src = "$root\src"
$cs2 = "$root\cs2-server"
$csgo = "$cs2\game\csgo"
$odx = "$root\odx"
$secrets = "$root\secrets"
$bucket = 'gs://odx-game-server-setup'
New-Item -ItemType Directory -Force $root, $tools, $src, "$root\logs", $secrets, "$odx\bin", "$odx\data\delivery" | Out-Null
Start-Transcript -Append "$root\logs\setup.log" | Out-Null
function Step($msg) { Write-Host "odx-setup: $msg" }

Add-MpPreference -ExclusionPath $root -ErrorAction SilentlyContinue
$max = (Get-PartitionSupportedSize -DriveLetter C).SizeMax
if ((Get-Partition -DriveLetter C).Size -lt $max - 1MB) { Step 'extend C:'; Resize-Partition -DriveLetter C -Size $max }

function Fetch($url, $file) { if (-not (Test-Path $file)) { Step "download $url"; Invoke-WebRequest $url -OutFile $file -UseBasicParsing } }

# --- tools: MinGit, CMake, VS Build Tools (C++), .NET 10 SDK, SteamCMD
if (-not (Test-Path "$tools\git\cmd\git.exe")) {
    Fetch 'https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/MinGit-2.47.1-64-bit.zip' "$tools\mingit.zip"
    Expand-Archive "$tools\mingit.zip" "$tools\git" -Force
}
if (-not (Test-Path "$tools\cmake\bin\cmake.exe")) {
    Fetch 'https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-x86_64.zip' "$tools\cmake.zip"
    Expand-Archive "$tools\cmake.zip" "$tools\cmake-x" -Force
    Move-Item (Get-ChildItem "$tools\cmake-x")[0].FullName "$tools\cmake"
}
$env:PATH = "$tools\git\cmd;$tools\cmake\bin;$tools\dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = "$root\dotnet-home"
$env:NUGET_PACKAGES = "$root\nuget"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$hasVc = (Test-Path $vswhere) -and (& $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if (-not $hasVc) {
    Fetch 'https://aka.ms/vs/17/release/vs_BuildTools.exe' "$tools\vs_BuildTools.exe"
    Step 'install VS Build Tools (C++), ~15 min'
    $p = Start-Process "$tools\vs_BuildTools.exe" -Wait -PassThru -ArgumentList `
        '--quiet --wait --norestart --nocache --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
    if ($p.ExitCode -notin 0, 3010) { throw "VS Build Tools failed: $($p.ExitCode)" }
}
if (-not (Test-Path "$tools\dotnet\dotnet.exe")) {
    Fetch 'https://dot.net/v1/dotnet-install.ps1' "$tools\dotnet-install.ps1"
    Step 'install .NET 10 SDK'
    & "$tools\dotnet-install.ps1" -Channel 10.0 -InstallDir "$tools\dotnet" -NoPath
}
if (-not (Test-Path "$tools\steamcmd\steamcmd.exe")) {
    Fetch 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' "$tools\steamcmd.zip"
    Expand-Archive "$tools\steamcmd.zip" "$tools\steamcmd" -Force
}

# --- CS2 dedicated server (guides/cs2-server.md §1)
if (-not (Test-Path "$cs2\game\bin\win64\cs2.exe")) {
    for ($i = 1; $i -le 6 -and -not (Test-Path "$cs2\game\bin\win64\cs2.exe"); $i++) {
        Step "steamcmd app_update 730, attempt $i (~74 GB)"
        & "$tools\steamcmd\steamcmd.exe" +force_install_dir $cs2 +login anonymous +app_update 730 validate +quit | Out-Host
    }
    if (-not (Test-Path "$cs2\game\bin\win64\cs2.exe")) { throw 'CS2 server install failed' }
}

# Without a Steam client on the machine, SteamGameServer_Init fails ("Could not determine Steam client install
# directory"): the server takes steamclient64.dll from next to cs2.exe instead.
Copy-Item "$tools\steamcmd\steamclient64.dll", "$tools\steamcmd\tier0_s64.dll", "$tools\steamcmd\vstdlib_s64.dll" `
    "$cs2\game\bin\win64\" -Force

# --- Metamod:Source 2.0 (§2)
if (-not (Test-Path "$csgo\addons\metamod.vdf")) {
    # Served as application/octet-stream: PowerShell 5.1 returns its Content as bytes, so read it from a file.
    Invoke-WebRequest 'https://mms.alliedmods.net/mmsdrop/2.0/mmsource-latest-windows' -OutFile "$tools\mms-latest.txt" -UseBasicParsing
    $latest = (Get-Content "$tools\mms-latest.txt" -Raw).Trim()
    Fetch "https://mms.alliedmods.net/mmsdrop/2.0/$latest" "$tools\$latest"
    Step "install $latest"
    Expand-Archive "$tools\$latest" $csgo -Force
}
$gi = "$csgo\gameinfo.gi"
$lines = [IO.File]::ReadAllLines($gi)
if (-not ($lines -match 'csgo/addons/metamod')) {
    $at = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -match '^\s*Game\s+csgo\s*$' })
    $lines = @($lines[0..($at - 1)]) + "`t`t`tGame`tcsgo/addons/metamod" + @($lines[$at..($lines.Length - 1)])
    [IO.File]::WriteAllLines($gi, $lines)
    Step 'gameinfo.gi: metamod search path added'
}

# The Metamod search path makes csgo\addons\metamod the engine's write path: tv_record would write demos to
# addons\metamod\replays, the orchestrator reads csgo\replays.
New-Item -ItemType Directory -Force "$csgo\replays" | Out-Null
if (-not (Test-Path "$csgo\addons\metamod\replays")) { New-Item -ItemType Junction -Path "$csgo\addons\metamod\replays" -Target "$csgo\replays" | Out-Null }

# --- SourceTV configs (§4)
$tvCfg = "tv_enable 1`ntv_delay 0`ntv_autorecord 0`nsv_hibernate_when_empty 0`n"
foreach ($mode in 'competitive', 'casual') { [IO.File]::WriteAllText("$csgo\cfg\gamemode_${mode}_server.cfg", $tvCfg) }

# --- source: capture/ at the uploaded commit
Step 'fetch capture source'
& gcloud storage cp "$bucket/capture.tar" "$src\capture.tar" --quiet
if (Test-Path "$src\capture") { Remove-Item -Recurse -Force "$src\capture" }
tar -xf "$src\capture.tar" -C $src
if ($LASTEXITCODE -ne 0) { throw 'untar failed' }
$tc = "$src\capture"

# --- odx_netlog plugin (§3, native/cs2-plugin/README.md)
$pluginStatus = 'not built'
try {
    if (-not (Test-Path "$src\hl2sdk-cs2")) { git clone --depth 1 --recursive -b cs2 https://github.com/alliedmodders/hl2sdk "$src\hl2sdk-cs2"; if ($LASTEXITCODE) { throw 'clone hl2sdk' } }
    if (-not (Test-Path "$src\metamod-source")) { git clone --depth 1 --recursive https://github.com/alliedmodders/metamod-source "$src\metamod-source"; if ($LASTEXITCODE) { throw 'clone metamod-source' } }
    Step 'build odx_netlog'
    cmake -S "$tc\native\cs2-plugin" -B "$root\build\cs2-plugin" -G 'Visual Studio 17 2022' -A x64 "-DHL2SDK_CS2=$src\hl2sdk-cs2" "-DMMSOURCE=$src\metamod-source" | Out-Host
    if ($LASTEXITCODE) { throw 'cmake configure' }
    cmake --build "$root\build\cs2-plugin" --config Release | Out-Host
    if ($LASTEXITCODE) { throw 'cmake build' }
    New-Item -ItemType Directory -Force "$csgo\addons\odx\bin\win64" | Out-Null
    Copy-Item "$root\build\cs2-plugin\Release\odx_netlog.dll" "$csgo\addons\odx\bin\win64\" -Force
    [IO.File]::WriteAllText("$csgo\addons\metamod\odx_netlog.vdf",
        "`"Metamod Plugin`"`n{`n`t`"alias`"`t`"odx`"`n`t`"file`"`t`"addons/odx/bin/win64/odx_netlog`"`n}`n")
    $pluginStatus = 'installed'
}
catch { $pluginStatus = "FAILED: $_"; Step "plugin $pluginStatus" }

# --- odx-orchestrator and odx (game-server), laid out like the package (bin\ next to data\, games\ in bin\)
Get-Process odx-orchestrator, odx -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($p in @(@('Odx.Orchestrator', @()), @('Odx.Cli', @('-f', 'net10.0')))) {
    Step "publish $($p[0])"
    dotnet publish "$tc\dotnet\src\Apps\$($p[0])" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None @($p[1]) -o "$odx\bin" | Out-Host
    if ($LASTEXITCODE) { throw "dotnet publish $($p[0]) failed" }
}
Copy-Item "$tc\profiles" "$odx\bin\games" -Recurse -Force

# --- secrets: RCON password and access key, readable by SYSTEM and Administrators only
function NewSecret($file) {
    if (-not (Test-Path $file)) {
        $chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
        $bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        [IO.File]::WriteAllText($file, -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] }))
    }
}
NewSecret "$secrets\rcon.txt"
NewSecret "$secrets\access-key.txt"
icacls $secrets /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

# --- Windows firewall (guides/network.md); TCP 27015 (RCON) stays closed
foreach ($r in @(@('CS2 server', 'UDP', 27015), @('Odx orchestrator', 'TCP', 5180), @('Odx time service', 'UDP', 12300))) {
    if (-not (Get-NetFirewallRule -DisplayName $r[0] -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $r[0] -Direction Inbound -Action Allow -Protocol $r[1] -LocalPort $r[2] | Out-Null
    }
}

# --- run all three at boot as SYSTEM (guides/cs2-server.md §5, §6, guides/orchestrator.md "Run")
[IO.File]::WriteAllText("$root\run-cs2.ps1", @'
$pw = Get-Content C:\odx\secrets\rcon.txt -Raw
Set-Location C:\odx\cs2-server\game\bin\win64
& .\cs2.exe -dedicated -insecure -usercon -port 27015 +game_type 0 +game_mode 1 +map de_dust2 +rcon_password $pw *>> "C:\odx\logs\cs2-$(Get-Date -Format yyyyMMdd-HHmmss).log"
'@)
[IO.File]::WriteAllText("$root\run-orchestrator.ps1", @'
$env:ODX_ACCESS_KEY = Get-Content C:\odx\secrets\access-key.txt -Raw
$bin = 'C:\odx\odx\bin'
& "$bin\odx-orchestrator.exe" --game-profile "$bin\games\cs2.json" `
    *>> "C:\odx\logs\orchestrator-$(Get-Date -Format yyyyMMdd-HHmmss).log"
'@)
[IO.File]::WriteAllText("$root\run-game-server.ps1", @'
# RCON: CS2 binds it to the NIC's address, which odx game-server uses by default.
$env:ODX_ACCESS_KEY = Get-Content C:\odx\secrets\access-key.txt -Raw
& C:\odx\odx\bin\odx.exe game-server --orchestrator http://127.0.0.1:5180 --cs2-game-dir C:\odx\cs2-server\game\csgo `
    --cs2-rcon-password-file C:\odx\secrets\rcon.txt --work C:\odx\game-server *>> "C:\odx\logs\game-server-$(Get-Date -Format yyyyMMdd-HHmmss).log"
'@)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId SYSTEM -RunLevel Highest
Unregister-ScheduledTask -TaskName odx-merge -Confirm:$false -ErrorAction SilentlyContinue
foreach ($t in 'cs2', 'orchestrator', 'game-server') {
    $action = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -ExecutionPolicy Bypass -File $root\run-$t.ps1"
    Register-ScheduledTask -TaskName "odx-$t" -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) `
        -Settings $settings -Principal $principal -Force | Out-Null
    Stop-ScheduledTask "odx-$t" -ErrorAction SilentlyContinue
}
Get-Process cs2, odx-orchestrator, odx -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($t in 'cs2', 'orchestrator', 'game-server') { Start-ScheduledTask "odx-$t" }

[IO.File]::WriteAllText("$root\setup-done.txt", (Get-Date -Format o))
Step "DONE (plugin: $pluginStatus)"
Stop-Transcript | Out-Null
