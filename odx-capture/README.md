# odx-capture

Tooling for the client's canonical game-data format. See [`../document/software-plan.md`](../document/software-plan.md) for the full plan; this repo currently implements testing layers 1–2 (§7.10).

## What's here

| Module | Purpose | Spec |
|---|---|---|
| `Odx.Core.Geometry` | Engine → RFU axis conversion (RFU/RAGE, Unity, Unreal, Quake/Source), C2W assembly (RUB), roll-free reconstruction, intrinsics from vertical/horizontal FOV or projection | v2 §1.1.2.3–1.1.2.5, appendix §2 |
| `Odx.Core.Frames` | `frames.csv` column list, per-frame input aggregation (held + tapped keys, summed mouse deltas), CSV writer | v2 §1.1.2.6, §1.1.3 |
| `Odx.Session` / `odx build` | Builds a spec v2 session from a capture folder: `frames.csv` (grid-time frame clock, input aggregation, actions from the game profile's bindings; unbound keys left out) and `session.json` (UTC from `clock_samples.csv`, `capture_id` as `session_id`) | v2 §1.1.2, §1.1.3, §1.5 |
| `games/*.json` | Game profiles: `game_title`, `platform`, `localization`, `input_mouse_convention`, `bindings` (key/button → action), optional `window_process` (the game's process: odx-app switches to that window, agents record it when the player picked the monitor) | v2 §1.1.2.1, §1.1.2.6 |
| `Odx.Session` / `odx validate` | Every vendor check in v2 §1.5 (session.json, frames structure, C2W, intrinsics, input, video read with `Odx.Core.Mp4Reader` — no ffprobe) | v2 §1.5 |
| `schemas/session_schema.json` | JSON Schema for `session.json`, extracted from the spec; embedded in `Odx.Session` | v2 §1.1.2.1 |
| `odx-orchestrator` / `Odx.Session.Multiplayer` | Multiplayer capture: time service, agent hub, game events → per-player start/stop, assembly of the v3 session tree and its sync gate | v3 §2–§4 |
| `tests/Odx.Tests/SessionFixtures.cs` | Synthetic valid session + one mutator per validation rule | plan §7.10.2 |

Everything except the in-game extractor (C++, `extractor/`) is C# (.NET 10) in `capture-cs/`. The Python pose audit lives outside the product in [`../research/analyze-pose/`](../research/analyze-pose/).

## Capture tools (C#, `capture-cs/`)

One command-line tool, `odx` (`src/Odx.Cli`): `odx input`, `odx record`, `odx build`, `odx validate`, plus `odx agent` and `odx simulate-game` for multiplayer (below). Its Windows build has all of them; its `net10.0` build (Linux servers) has `build`, `validate` and `simulate-game` only.

`odx input` records keyboard/mouse with Raw Input in its own process (never inside the game) and writes `input_events.csv` + `clock_samples.csv` (QPC ↔ UTC). `InputLogReader` reads the log back; the format is pinned by `tests/fixtures/input_events.csv` (writer output must match it byte for byte).

```bash
cd capture-cs
dotnet test                                   # .NET 10 SDK; video tests are skipped without ffmpeg/ffprobe on PATH (fixtures; ffprobe checks Mp4Reader)
dotnet run --project src/Odx.Cli -f net10.0-windows10.0.22621.0 -- input --out C:/odx_capture/session1 [--pid <game_pid>] [--ntp time.windows.com]
```

Language split: plan §6.5.

**Clock (spec v3 §4).** `clock_samples.csv` maps QPC to the Windows clock, which is only as good as its own sync (a test machine here was 44 s behind). `odx input --ntp <server>` also measures QPC against an NTP server (SNTP, RFC 4330) at start and every 15 s into `ntp_samples.csv` (`t_qpc_ms,utc_unix_ms,rtt_ms,system_offset_ms,server`, pinned by `tests/fixtures/ntp_samples.csv`); it does not change the Windows clock. `odx build` then takes UTC from the lower-RTT half of those samples (a line fit over ≥ 60 s to absorb QPC rate error, else the median offset), falls back to the Windows clock without them, and records the choice in `session.json` → `vendor_clock_sync` (`source`, `server`, `samples`, `rtt_ms`, `system_clock_offset_ms`). The game server can serve NTP itself so every player measures against the same clock.

**Time server (`odx-capture.json`).** The NTP server is preset per deployment in `odx-capture.json` next to the executables (`bin/` in the package; source: `odx-capture/odx-capture.json`), not chosen by users: `{"time_server": "time.windows.com"}` (`host` or `host:port`). odx-app passes it to `odx input --ntp`; `odx input` uses it when `--ntp` is not given; odx-orchestrator measures its own clock against it every minute and records the latest measurement in each `session.json` (`server_clock`). Without the file or the field, only the Windows clock is used. In multiplayer, players measure against the orchestrator's time service instead.

## In-game camera extractor (C++, `extractor/`, Path B)

`odx_extractor.dll` runs inside the game: a Lua profile finds the camera by signature and reads it on every Present. Its toggle key (default F9, read with `GetAsyncKeyState`) starts/stops writing `camera_samples.csv` (QPC time, position, forward/right/up, FOV — in engine axes and units, format pinned by `tests/fixtures/camera_samples.csv`) and `extractor.json` (the profile's `convention`, `units_to_world`, `fov_axis`, `camera_model`). It does nothing else: `odx build --camera <that folder>` converts the samples to the canonical C2W + intrinsics and puts them on the video frames (newest sample at or before each frame's capture time, none if older than 100 ms). Input and video come from `odx input` + `odx record` as for any title.

`odx record` captures a game window (or the primary monitor) with Windows.Graphics.Capture. Every captured frame carries its QPC time, the same clock as `odx input`. Frames are resampled onto a constant-rate grid and piped to ffmpeg (NVENC when available, else x264; no B-frames). Outputs:
- `video.mp4`
- `video_frames.csv` (`frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate`; read with `VideoFrameLogReader`, pinned by `tests/fixtures/video_frames.csv`)
- `recording.json` (size, fps, start UTC/QPC, duplicated/dropped frame counts)

```bash
dotnet run --project src/Odx.Cli -f net10.0-windows10.0.22621.0 -- record --out C:/odx_capture/session1 --window Game.exe [--fps 60] [--encoder auto|x264|nvenc] [--seconds 120]
```

Run `odx input` and `odx record` into the same `--out` folder. Grid times (`t_grid_qpc_ms`) are the per-frame clock for `Frames.AggregateInputs`. Requires Windows 10 2004+ and ffmpeg (the packaged `ffmpeg/ffmpeg.exe`, else PATH, or `--ffmpeg`). `--encoder auto` (default) uses NVENC when it works on the machine, which keeps the CPU free for the game, else x264; the probe (~2 s cold) is cached per ffmpeg build in `%LOCALAPPDATA%\Odx\encoder.json`. NVENC needs an NVIDIA GPU with driver 471.41+ (the packaged ffmpeg is built with NVENC 11.1 headers; ffmpeg builds with the newest headers need 610+). Both commands also accept `--stop-event <name>`: they stop when that named Win32 event is set.

`odx-app` (WPF, `src/Odx.App`) is the desktop front end. It runs `odx input` and `odx record` as child processes into `<output folder>/<yyyyMMdd-HHmmss>/` and stops both through `--stop-event`.
- English / Vietnamese UI (all texts in `src/Odx.App/Strings.cs`; defaults to the Windows display language, switchable at any time). Validator issue messages stay in English.
- Source (primary monitor or a window), FPS and output folder; the time server comes from `odx-capture.json` (the result shows the measured RTT and Windows clock offset and warns above 1 s); settings saved in `%APPDATA%\Odx\odx-app.json`.
- Global start/stop hotkey, default `F10`; click the hotkey box and press a new combination to change it. While the app runs, other programs (and the game) do not receive that key. It is still recorded in `input_events.csv`; `odx build` leaves it out of `input_keys` as long as the game profile does not bind it.
- Start shows a 3-second countdown overlay (click-through, never takes focus); capture begins after it closes, so it is not in the video. Start/hotkey again during the countdown cancels. Stopping shows an "END" badge once both capture tools have exited (also never in the video), then a Windows notification with the result; clicking it opens the session folder.
- One instance only: starting it again brings the running window back from the tray.
- Minimizing hides it to the tray; the hotkey keeps working.

```bash
dotnet run --project src/Odx.App
```

## Package for other machines

```powershell
cd capture-cs
powershell -ExecutionPolicy Bypass -File publish.ps1 -Version 0.1.0
```

Produces `capture-cs/dist/odx-capture-<version>-win-x64.zip` (~140 MB, 8 files). Building it needs MSYS2 (ffmpeg, below) and the MSVC build tools (NativeAOT launcher). Unzip it on any Windows 10 2004+ x64 machine and run `Odx Capture.exe`; no .NET or ffmpeg install is needed.

```
odx-capture-<version>-win-x64/
  Odx Capture.exe          launcher (NativeAOT, 2 MB): shows the splash at once, starts bin\odx-app.exe
  bin/
    odx-app.exe               desktop front end
    odx.exe                   command-line tool (input, record, build, validate)
    ffmpeg/                   ffmpeg.exe (6 MB, built by ffmpeg/build-ffmpeg.sh), LICENSE.txt, SOURCES.txt
    games/                    game profiles
```

**Start-up.** Windows Defender scans a new executable before it runs: for the 240 MB `odx-app.exe` that took 12–27 s on its first launch after unzipping, with nothing on screen. The launcher is scanned at once and shows the splash (the same image odx-app's own WPF splash shows next, so the hand-over is invisible); it closes when odx-app shows a window. Measured: splash after 0.1–0.2 s and main window after ~1.5 s on later launches; on the very first launch of a freshly unzipped package Defender still held even the launcher 6–9 s. Code-signing the executables is the remaining fix for that.

Both odx executables are self-contained single files: each embeds its own .NET runtime (WPF's native libraries are extracted to a temp folder on first run), so they never share or overwrite each other's files. They are not compressed: decompressing on every start made each launch 0.7–1.3 s slower, and the zip is compressed anyway.

odx record uses `ffmpeg/ffmpeg.exe` from the package when present, otherwise `ffmpeg` on PATH; nothing needs ffprobe.

**ffmpeg build.** `capture-cs/ffmpeg/build-ffmpeg.sh` builds a static ffmpeg with only what `odx record` uses — rawvideo from stdin, crop/scale/format, libx264 and h264_nvenc, MP4 to a file — from pinned, SHA-256-checked sources (FFmpeg 9.0.2, x264 stable, nv-codec-headers 11.1.5.4): 6 MB instead of ~100 MB for a general build. `publish.ps1` runs it once in MSYS2 (`C:\msys64` or `MSYS2_ROOT`; `pacman -S make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf`), ~10–15 min, and caches it in `.cache/ffmpeg-<script hash>/` so changing the script rebuilds it. The result is GPLv2 (FFmpeg `--enable-gpl` with x264); `LICENSE.txt` and `SOURCES.txt` (exact sources and configure line, for the GPL source offer) ship next to it. Our tools only run it as a separate process (not linked).

## Versions and CI

Every tool stamps `<version>+<git commit>` into its output: `tool_version` in `recording.json`, `vendor_tool_version` in `session.json` (an extra field; the schema allows additional properties). The .NET SDK is pinned by `capture-cs/global.json`. CI (`.github/workflows/ci.yml`, Windows) builds and tests capture-cs, then the extractor with ctest.

## Build and validate a session

`odx-app` does both automatically after each recording when a game profile is selected. By hand:

```bash
cd capture-cs
dotnet run --project src/Odx.Cli -f net10.0 -- build path/to/<capture-dir> --profile ../games/example-fps.json
dotnet run --project src/Odx.Cli -f net10.0 -- validate path/to/<capture-dir> [more dirs...] [--json] [--no-video]
```

Exit code 1 when any error is found. Warnings (`c2w.roll`, `c2w.upright`) do not fail a session.

## Multiplayer (spec v3)

`odx-orchestrator` (`src/Odx.Orchestrator`, ASP.NET Core, cross-platform) runs on one machine everybody can reach (next to the game server):

```bash
cd capture-cs
dotnet run --project src/Odx.Orchestrator -- --out C:/odx_sessions --game-profile ../games/example-fps.json [--http-port 5180] [--time-port 12300] [--fps 30] [--min-valid K] [--max-rtt-ms 100]
```

**Rooms.** Players are recorded only with the other members of their room. Set the orchestrator's address once in `odx-capture.json` (`"orchestrator": "http://<host>:5180"`; empty: odx-app records on its own only, the room rows stay hidden). In odx-app:

- **Create room** opens a room with you as its host and shows its 6-character invite code (no 0/O or 1/I) to share. **Close room** ends it for everyone; a room nobody is in also closes after 30 min.
- **LAN (ZeroTier)**, ticked when creating a room, gives the room its own virtual LAN for games that only play over LAN (e.g. Warcraft III Classic/1.29): everyone in the room is on one layer-2 network, so LAN game lists and broadcasts work. The orchestrator creates a ZeroTier network per room on its controller (`--zerotier-api`, token from `--zerotier-token-file` or `ODX_ZT_TOKEN`), authorizes each member's ZeroTier node while they are in the room and deletes the network when the room closes; odx-app joins and leaves it through the local ZeroTier service (the per-user token copy that ZeroTier's tray app keeps, so no admin rights) and shows its LAN IP. Without ZeroTier, odx-app offers to install it (winget, one UAC prompt).
- **Join room** asks for the invite code in a dialog; **Leave room** to go. Leaving during a countdown cancels the round; leaving during a round stops your recording.
- In a room, the main button and the hotkey are the room action:
  - the host starts and ends rounds (3 s countdown);
  - the other players say they are ready (again to take it back); the host can start only once all of them are (the button waits, the orchestrator checks too). 

  The status line lists the members (✓ = ready). The game profile and FPS come from the orchestrator.

Rounds start and end only on request: the host's, or a game server plugin's `round_start` / `round_end` in a room without a host (`POST /rooms`). Ready is only a signal for the host; it never starts a round by itself.

Every member starts capturing when the countdown starts and sees it on screen (excluded from screen capture, so never in the video). A `round_end`, an unready, or a member leaving during the countdown cancels the round (`round_cancelled`): everyone stops, nothing is uploaded or assembled, and the round number is used again. After a lost connection an agent joins its room again.

Headless capture machines and the simulator (in place of a game server plugin while there is none):

```bash
odx agent --orchestrator http://<host>:5180 --room <code> --player p1 --out C:/odx_capture [--name Alice] [--window cs2] [--auto-ready]
odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--room <code>] [--rounds 2] [--round-s 20] [--match sim]
```

`simulate-game` opens a room without a host (or uses `--room`), prints its invite code, waits until every `--players` member has joined, then plays scripted rounds: each has a kill and a permanent death halfway through. It waits for uploads between rounds, prints each session's gate result and exits 1 if any session fails. Run on one machine (orchestrator + two agents recording the monitor + simulator), three 10 s rounds all pass; first frames arrive 0.75–0.93 s after `round_start`.

- **Time service:** an SNTP responder on UDP `--time-port`. Every player's capture measures QPC against it (`ntp_samples.csv`), so all players share one clock.
- **Agents:** each player's machine runs `AgentClient` (`Odx.Session/Multiplayer`). It connects to `ws://<host>:5180/agents`, measures its offset on `welcome`, creates or joins a room (`create_room`, `join_room`; the orchestrator answers with `room` updates), starts its capture on `start` and stops on `stop`. Then it uploads the built capture folder (`PUT /sessions/{sid}/players/{pid}/files/{name}`) and reports `done`.
- **Game events:** a game server plugin opens a room without a host (`POST /rooms` → `{"code"}`), lets the players join it, and posts JSON events to `POST /rooms/{code}/events`; `GET /rooms/{code}` and `GET /status` show the rooms (`GameEvents`: `match_start`, `round_start`, `round_end`, `kill`, `player_death` with `permanent`, `player_connect`, `player_disconnect`, `match_end`). Events without `utc_ms` are stamped on arrival.
  - `round_start` arms and starts every idle agent at once. With `countdown_ms` it is recorded as `round_countdown` and the orchestrator adds `round_start` when play starts, so the video holds the start of play (`round_start_frame` in `player.json`). Players' starts differ by their capture start-up (~0.8 s warm; ~3 s on the first start after an install, while Windows Defender scans the new files); every frame is put on server time afterwards. Events between rounds (`match_start`, `player_ready`, `round_cancelled`) open the next round's events.
  - A permanent `player_death` stops that player.
  - `round_end` stops the rest.
- **Assembly:** once every player is done (or 15 min later), `MultiplayerSession.Assemble` writes:

  ```
  <out>/<vendor>/<mm-dd-yyyy>/<game>/<match>-rNN/
    session.json  validation.json  schema.bin.gz  server/events.jsonl
    players/<pid>/ player.json frames.csv frames.bin video.mp4 events.jsonl capture/
  ```

  - `session.json`: `spec_version` 3.1, `round_index`, `countdown_ms`, `end_reason`, the players (id, name, machine id, tool version, stop reason, validity), the orchestrator's own NTP measurement (`server_clock`), and how to decode the `.bin` files.
  - `player.json`: ping, `clock_sync` (offset = server − player clock, RTT, jitter, drift, every sample), first/last frame UTC, `round_start_frame` / `round_start_video_time_ms`, recording details (fps, sizes, encoder, capture source, duplicated/dropped frames), frame stats, key bindings, stop reason, and `qa` (every check with its result).
  - `frames.bin` (spec v3 §2.2): `frames.csv` at full precision plus each frame's `server_time_us`, as length-delimited protobuf `odx.capture.v1.FrameRecord` after a `FileHeader`; `schema.bin.gz` is the schema (`capture.proto`) as a gzip-compressed `FileDescriptorSet`.
  - Every server event carries `event_id`, `utc`, `server_time_us`, `session_id` and `round_index`. A player's `events.jsonl` holds every event of the round with its `video_time_ms` in that player's video (negative before the first frame) and the `frame_index` on screen at that moment (null outside the video).
  - `packets.bin` / `packets.txt` (spec v3 §2.3), for games whose profile has a `network` section: each player's game traffic of the round, captured by pktmon (built into Windows; it needs administrator rights, so odx-app starts `odx packet-capture` once per room with a UAC prompt and drives it over a named pipe only that user can open; `odx agent` uses pktmon directly when run as administrator). The capture covers the profile's ports on the network adapters (Ethernet and Wi-Fi/802.11 frames) and goes into `capture/packets.pcapng`. Records are `odx.capture.v1.PacketRecord` (server and local time, direction from this machine's addresses, endpoints, message, bytes, decoded fields); `packets.txt` lists them tab-separated.
    - `w3gs` (Warcraft III 1.26–1.29, `games/warcraft3.json`): TCP streams are reassembled (retransmissions skipped, gaps listed as `TCP_GAP`) and split into W3GS messages, every one decoded: LAN discovery (`GAMEINFO` with map and host from the encoded stat string), joining and slots, chat, lag, map download, and the lockstep command blocks action by action (orders with order id and coordinates, selections, groups, items, resources, pings…); unknown messages or actions keep their bytes as hex.
    - `csgo` (CS:GO legacy 1.38.8.1, `games/csgo.json`): every UDP datagram of the game server port goes through the netchannel layers: connectionless packets (A2S_INFO, challenge, connect; a server's `S2A_INFO` also tells its build, which switches the key to it), split datagrams reassembled, ICE-2 decryption with the key derived from `network.version` (the build), unpadding, LZSS/Snappy decompression, the netchannel header (sequence, ack, flags, choked), reliable subchannel transfers (fragments across datagrams, compression, files) and the netmessages, named `svc_*` from the server and `clc_*` from the client (`net_*` both ways) after netmessages.proto. Message payloads are decoded by protobuf field number (`fields`), not yet by name. ICE is ported from the author's public-domain reference and passes its certification triplets.
    - `raw` (CS2, `games/cs2.json`): one record per UDP/TCP payload. CS2 encrypts every connection with its own AES-GCM key (GameNetworkingSockets), so nobody can decode it passively; its decoded game messages have to come from our own server (planned: a server-side plugin).
  - `capture/` holds the v2 `session.json` and the raw logs.
- **Gate** (`validation.json`, per-player checks in `player.json` → `qa`; `GateTolerances`):
  - each player passes v2 §1.5;
  - floors: ≥ 30 fps, ≥ 1280x720 (spec v3 §2), ≥ 10 s (not for a player stopped by a permanent death), camera on ≥ 90 % of frames when there is camera data;
  - `sync.clock`: median RTT ≤ 100 ms;
  - `sync.start`: first frame ≤ 3 s after `round_start` + `countdown_ms`;
  - `sync.end`: last frame ≥ stop − 1 s;
  - `capture.window` (advisory): only the game window is captured;
  - the session needs K of N valid players (K = N up to 3 players, else N − 1).

## Deploy the orchestrator (Linux server)

`deploy/` runs odx-orchestrator in Docker behind Caddy, which gets the HTTPS certificate for your domain from Let's Encrypt and passes the agents' WebSocket and uploads through.

1. On the server: Docker with the compose plugin; the domain's DNS A record pointing at the server (directly, not behind a proxy such as Cloudflare's orange cloud: players also reach the time service at `<domain>:12300/udp`); ports 80/tcp, 443/tcp+udp, 12300/udp and 9993/udp (ZeroTier controller for LAN rooms) open. Never publish 9993/tcp: that is the controller's API, reached only by the orchestrator inside the compose network.
2. Copy `odx-capture/` there (`git clone`, or `rsync -a --exclude bin --exclude obj --exclude dist --exclude .cache odx-capture/ server:odx-capture/`).
3. `bash odx-capture/deploy/up.sh`. The first run writes `deploy/.env` with a random `ODX_ACCESS_KEY` (and later the ZeroTier controller's `ODX_ZT_TOKEN`); set `ODX_DOMAIN` (and `ODX_GAME_PROFILE`, a file of `games/`) in it and run it again. It builds the image, starts orchestrator + Caddy (restarting on reboot), and prints the address and the key. Run it again after an update.
4. Package odx-app for that server on Windows: `powershell -ExecutionPolicy Bypass -File publish.ps1 -Orchestrator https://<domain> -AccessKey <key>` writes both into the package's `bin/odx-capture.json`, so players only type invite codes.

Sessions land in `deploy/sessions/` (`<vendor>/<mm-dd-yyyy>/<game>/<session>/`; plan disk space for every player's video). Logs: `docker compose -f deploy/compose.yaml logs -f orchestrator`. Every request needs the access key (`Authorization: Bearer <key>`, 401 otherwise); the time service is open (it only tells the time). A game server plugin calls `POST /rooms` and `POST /rooms/{code}/events` with the same key.

## Decisions pending with the client

- **Null encoding in CSV:** nulls are written as the literal `null` (`Frames.Null`), as spec v2 §1.5.5 asks ("null, not empty string") and as the client's multiplayer sample delivery does; confirm.
- **Column-name case:** the spec uses `c2w_mRC` (§1.1.2.4, §1.5.3) but `C2W_M00` in the summary table (§1.1.3). Uppercase `C2W_M00` is used here, as in the summary table and the client's sample delivery (which also writes `camera_model` as `pinhole`; we keep the spec's `PINHOLE`).
- **Extra `session.json` fields:** `vendor_tool_version` and `vendor_clock_sync` are not in the spec (the schema allows additional properties); confirm the client accepts them.
- **Validation tolerances** not fixed by the spec (all in `ValidationTolerances`, `Odx.Session/Validation/Issue.cs`): rotation `1e-3`, principal point `4 px`, frame spacing `max(1 ms, 25%)`, level-roll warning `0.05`, fx = fy within `1e-3`, duration checks `1 s` / `1000 ms` / `2 frames`.
- **Multiplayer (provisional):**
  - One round = one session (`<match>-rNN`).
  - Ping is the median NTP RTT to the orchestrator.
  - The sync tolerances above (`GateTolerances`) and the K-of-N rule are proposals (v3 §4 leaves them open); the 10 s and 90 % camera floors come from the client's sample delivery, not the spec.
  - The 10 s floor does not apply to a player stopped by a permanent death: v3 §1 asks to cut that recording, so a player who dies early would otherwise always fail.
  - Agents still uploading the previous round are left out of the next one.
  - Rooms: the player who creates a room is its only host; only the host (or a game server plugin) starts and ends rounds, once every other member is ready. There is no per-player stop without a game server, so everyone records the whole round. Match ids start with the room's code; a cancelled round's number is used again.
  - Player packets: Warcraft III decoded (field layouts from BNETDocs/gowarcraft3) and CS:GO legacy decoded down to netmessages (layout from Synacktiv's protocol notes and open Source engine client emulators; netmessage contents by field number for now), neither yet checked against a real game here; CS2 kept raw (encrypted per connection). Server-side `server/packets.*` and `server_state.bin` are not produced yet (they need a game server plugin; Warcraft III has no server, its host's capture is the authoritative side).
