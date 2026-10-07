# CS2 dedicated server

The server records each round's server side: a SourceTV demo (world state every tick, game events) and the
`odx_netlog` plugin's log (every net message with each client, the server tick on every line). Its agent,
`odx game-server`, starts and stops the demo over RCON when the orchestrator says so and keeps both, per round, on this
machine.

Requirements: Windows 10/11 x64, about 60 GB free on one drive (or see [hard links](#install-from-a-cs2-client-with-hard-links)),
the Microsoft Visual C++ runtime (installed with Steam games).

## 1. Install the server

**With SteamCMD** (the usual way):

```powershell
# SteamCMD: https://developer.valvesoftware.com/wiki/SteamCMD (unzip steamcmd.zip to e.g. D:\steamcmd)
D:\steamcmd\steamcmd.exe +force_install_dir D:\cs2-server +login anonymous +app_update 730 validate +quit
```

About 74 GB to download. On a machine without the Steam client, the server also needs SteamCMD's Steam client libraries
next to `cs2.exe`, or it exits while loading the map ([troubleshooting.md](troubleshooting.md#cs2-server-exits-while-loading-the-map)):

```powershell
Copy-Item D:\steamcmd\steamclient64.dll, D:\steamcmd\tier0_s64.dll, D:\steamcmd\vstdlib_s64.dll D:\cs2-server\game\bin\win64\
```

### Install from a CS2 client with hard links

On a machine that already has CS2 installed on the **same drive**, the server can be a tree of hard links to the client's
files: no extra space, and the client's install stays untouched. Only `gameinfo.gi` is a real copy, because it is edited
in step 2. Never edit another file of this tree: a hard link shares its content with the client.

```powershell
$src = 'D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive'
$dst = 'D:\cs2-server'
Get-ChildItem $src -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length)
    $target = Join-Path $dst $rel
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    if ($rel -eq '\game\csgo\gameinfo.gi') { Copy-Item $_.FullName $target }
    else { New-Item -ItemType HardLink -Path $target -Target $_.FullName | Out-Null }
}
```

New files (configs, Metamod, the plugin) are fine: they are not links. When Steam updates the client, rebuild the tree.

## 2. Metamod:Source 2.0

1. Download the latest Windows build from <https://mms.alliedmods.net/mmsdrop/2.0/> (`mmsource-2.0.0-gitNNNN-windows.zip`).
2. Unzip it into `D:\cs2-server\game\csgo` (it adds `addons\metamod\` and `addons\metamod.vdf`).
3. In `D:\cs2-server\game\csgo\gameinfo.gi`, under `SearchPaths`, add one line **above** `Game	csgo`:

   ```
   			Game	csgo/addons/metamod
   			Game	csgo
   ```

4. That line also makes `addons\metamod` the folder the engine writes to, so SourceTV would put each round's demo in
   `addons\metamod\replays\`; the agent looks in `csgo\replays\`. Join the two (as administrator):

   ```powershell
   New-Item -ItemType Directory -Force D:\cs2-server\game\csgo\replays | Out-Null
   New-Item -ItemType Junction -Path D:\cs2-server\game\csgo\addons\metamod\replays -Target D:\cs2-server\game\csgo\replays
   ```

## 3. The odx_netlog plugin

Build it as `cs2-plugin/README.md` says (MSVC, hl2sdk branch `cs2`, Metamod's source tree), or take a `odx_netlog.dll`
built for the same CS2 version. Then:

1. Copy `odx_netlog.dll` to `D:\cs2-server\game\csgo\addons\odx\bin\win64\`.
2. Create `D:\cs2-server\game\csgo\addons\metamod\odx_netlog.vdf`:

   ```
   "Metamod Plugin"
   {
   	"alias"	"odx"
   	"file"	"addons/odx/bin/win64/odx_netlog"
   }
   ```

The plugin writes `game\csgo\odx-netlog\<UTC start>.jsonl` (about 7 MB per minute per player) and
`odx-netlog\senddata\<UTC start>.jsonl` (each player's join data, which carries the game event list). Delete old
files between test days; the game server agent copies what each round needs.

After a CS2 update, check that the plugin still loads and logs (see [troubleshooting.md](troubleshooting.md#after-a-cs2-update)).

## 4. SourceTV configs

The stock game mode configs set a SourceTV delay of 30 to 105 s, which would cut the end of every round's demo. Create
`D:\cs2-server\game\csgo\cfg\gamemode_competitive_server.cfg` (and the same as `gamemode_casual_server.cfg` if you play
casual); CS2 runs it after the game mode's own config:

```
// Odx capture server: odx game-server starts and stops each round's demo over RCON.
tv_enable 1
tv_delay 0
tv_autorecord 0
sv_hibernate_when_empty 0
```

With the hard-link install, `server.cfg` and the `gamemode_*.cfg` files are links to the client's: do not edit them; the
`*_server.cfg` files are new files.

## 5. Start

```powershell
cd D:\cs2-server\game\bin\win64
.\cs2.exe -dedicated -insecure -usercon -port 27015 +game_type 0 +game_mode 1 +map de_dust2 +rcon_password <RCON password>
```

- `-insecure`: no VAC on this server (needed for Metamod; players launch CS2 with `-insecure` too).
- `-usercon` and `+rcon_password`: RCON for the game server agent (step 6). Pick a long random password and put the same
  in the file the agent reads. RCON listens on tcp/27015 of the machine's network address (not 127.0.0.1): never open
  TCP 27015 to the internet.
- `+game_type 0 +game_mode 1`: competitive; `+game_mode 0` for casual.

Check in the console:

```
[odx] logging net messages to …\odx-netlog\…jsonl (server tick N)
[META] Loaded 1 plugin.
```

`meta list` (console or RCON) lists the plugin; `status` shows `sourcetv[0]` and the players.

### Bots and warmup

- End warmup before a test round: `mp_warmup_end` (in warmup everyone respawns at once).
- Add bots with `bot_add_t` / `bot_add_ct`; set how many with `bot_quota N`. Bot changes and match restarts can kick
  SourceTV, see [troubleshooting.md](troubleshooting.md#sourcetv-is-not-recording).

### A player hosting the server

The server can run on one player's PC (the player also runs CS2 and the app there). Two things to know:

- The dedicated server also is `cs2.exe` and opens a console window titled "Counter-Strike 2"; the app records the
  window of process `cs2`, and could pick the server's. Hide the server's window after starting it:

  ```powershell
  Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'
  $server = Get-Process cs2 | Where-Object { $_.Path -like 'D:\cs2-server\*' }
  [W.U]::ShowWindow($server.MainWindowHandle, 0) | Out-Null
  ```

- That player connects to the server at `127.0.0.1:27015`. Their own game traffic then goes over loopback, which pktmon
  cannot see: their `packets.*` stay empty (the server's `server/packets.*` still hold their messages).

## 6. The game server agent

`odx game-server` runs next to the server (`odx.exe` is in the package's `bin\`). It connects to the orchestrator, and
for every round the orchestrator starts it records the server's side, samples this machine's clock against the
orchestrator's time service, and keeps everything in `<work>\<session>\`: `server\` (demo, plugin log,
`ntp_samples.csv`) and, once every participant is done, the round's files from the orchestrator (`round.json`,
`server_events.jsonl`, `game_profile.json`). A cancelled round is deleted. Copy these folders for `odx merge`
([orchestrator.md](orchestrator.md#build-the-sessions)).

```powershell
# The RCON password from step 5, alone in a file only this user can read.
Set-Content -Path D:\odx\rcon.txt -Value '<RCON password>' -NoNewline

$bin = 'D:\odx\odx-capture-0.1.0-win-x64\bin'
& "$bin\odx.exe" game-server --orchestrator http://<orchestrator>:5180 --cs2-game-dir D:\cs2-server\game\csgo `
    --cs2-rcon-password-file D:\odx\rcon.txt --work D:\odx\game-server
```

| Option | |
|---|---|
| `--orchestrator` | The orchestrator, as this machine reaches it. |
| `--cs2-game-dir` | The server's `game\csgo` folder (where `replays\` and `odx-netlog\` are). |
| `--cs2-rcon-password-file` | Or the `ODX_CS2_RCON_PASSWORD` environment variable. |
| `--cs2-rcon` | Where RCON listens; default this machine's first IPv4 address, port 27015 (`netstat -ano \| findstr :27015` shows it). |
| `--access-key` | The orchestrator's; or `ODX_ACCESS_KEY`, else `access_key` of the `odx-capture.json` next to `odx.exe` (set when the package was built). |
| `--work` | Where the rounds are kept; default `odx-game-server` in the temp folder (`%TEMP%`): set one that stays. |
| `--id` | Its id at the orchestrator; default `game-server`. Give each one its own when several servers share an orchestrator, and open their rooms with `POST /rooms?server=<id>`. |
| `--name` | How the orchestrator names it; default "CS2 server". |

Check: the orchestrator logs `game server agent game-server (CS2 server, 0.1.0) connected`, and its `/status` lists it under
`"game_servers"`.
Start the agent after the server; it reconnects every 5 s if the orchestrator restarts.
