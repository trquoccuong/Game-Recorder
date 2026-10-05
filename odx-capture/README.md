# odx-capture

Tooling for the client's canonical game-data format. See [`../document/software-plan.md`](../document/software-plan.md) for the full plan; this repo currently implements testing layers 1–2 (§7.10).

## What's here

| Module | Purpose | Spec |
|---|---|---|
| `Odx.Core.Geometry` | Engine → RFU axis conversion (RFU/RAGE, Unity, Unreal, Quake/Source), C2W assembly (RUB), roll-free reconstruction, intrinsics from vertical/horizontal FOV or projection | v2 §1.1.2.3–1.1.2.5, appendix §2 |
| `Odx.Core.Frames` | `frames.csv` column list, per-frame input aggregation (held + tapped keys, summed mouse deltas), CSV writer | v2 §1.1.2.6, §1.1.3 |
| `Odx.Session` / `odx build` | Builds a spec v2 session from a capture folder: `frames.csv` (grid-time frame clock, input aggregation, actions from the game profile's bindings; unbound keys left out) and `session.json` (UTC from `clock_samples.csv`, `capture_id` as `session_id`) | v2 §1.1.2, §1.1.3, §1.5 |
| `games/*.json` | Game profiles: `game_title`, `platform`, `localization`, `input_mouse_convention`, `bindings` (key/button → action) | v2 §1.1.2.1, §1.1.2.6 |
| `Odx.Session` / `odx validate` | Every vendor check in v2 §1.5 (session.json, frames structure, C2W, intrinsics, input, video read with `Odx.Core.Mp4Reader` — no ffprobe) | v2 §1.5 |
| `schemas/session_schema.json` | JSON Schema for `session.json`, extracted from the spec; embedded in `Odx.Session` | v2 §1.1.2.1 |
| `odx-orchestrator` / `Odx.Session.Multiplayer` | Multiplayer capture: time service, agent hub, game events → per-player start/stop, assembly of the v3 session tree and its sync gate | v3 §2–§4 |
| `tests/Odx.Tests/SessionFixtures.cs` | Synthetic valid session + one mutator per validation rule | plan §7.10.2 |

Everything except the in-game extractor (C++, `extractor/`) is C# (.NET 8) in `capture-cs/`. The Python pose audit lives outside the product in [`../research/analyze-pose/`](../research/analyze-pose/).

## Capture tools (C#, `capture-cs/`)

One command-line tool, `odx` (`src/Odx.Cli`): `odx input`, `odx record`, `odx build`, `odx validate`, plus `odx agent` and `odx simulate-game` for multiplayer (below). Its Windows build has all of them; its `net8.0` build (Linux servers) has `build`, `validate` and `simulate-game` only.

`odx input` records keyboard/mouse with Raw Input in its own process (never inside the game) and writes `input_events.csv` + `clock_samples.csv` (QPC ↔ UTC). `InputLogReader` reads the log back; the format is pinned by `tests/fixtures/input_events.csv` (writer output must match it byte for byte).

```bash
cd capture-cs
dotnet test                                   # .NET 8 SDK; video tests are skipped without ffmpeg/ffprobe on PATH (fixtures; ffprobe checks Mp4Reader)
dotnet run --project src/Odx.Cli -f net8.0-windows10.0.22621.0 -- input --out C:/odx_capture/session1 [--pid <game_pid>] [--ntp time.windows.com]
```

Language split: plan §6.5.

**Clock (spec v3 §4).** `clock_samples.csv` maps QPC to the Windows clock, which is only as good as its own sync (a test machine here was 44 s behind). `odx input --ntp <server>` also measures QPC against an NTP server (SNTP, RFC 4330) at start and every 15 s into `ntp_samples.csv` (`t_qpc_ms,utc_unix_ms,rtt_ms,system_offset_ms,server`, pinned by `tests/fixtures/ntp_samples.csv`); it does not change the Windows clock. `odx build` then takes UTC from the lower-RTT half of those samples (a line fit over ≥ 60 s to absorb QPC rate error, else the median offset), falls back to the Windows clock without them, and records the choice in `session.json` → `vendor_clock_sync` (`source`, `server`, `samples`, `rtt_ms`, `system_clock_offset_ms`). The game server can serve NTP itself so every player measures against the same clock.

**Time server (`odx-capture.json`).** The NTP server is preset per deployment in `odx-capture.json` next to the executables (`bin/` in the package; source: `odx-capture/odx-capture.json`), not chosen by users: `{"time_server": "time.windows.com"}` (`host` or `host:port`). odx-app passes it to `odx input --ntp`; `odx input` uses it when `--ntp` is not given; odx-orchestrator measures its own clock against it every minute and records the latest measurement in each `session.json` (`server_clock`). Without the file or the field, only the Windows clock is used. In multiplayer, players measure against the orchestrator's time service instead.

## In-game camera extractor (C++, `extractor/`, Path B)

`odx_extractor.dll` runs inside the game: a Lua profile finds the camera by signature and reads it on every Present. Its toggle key (default F9, read with `GetAsyncKeyState`) starts/stops writing `camera_samples.csv` (QPC time, position, forward/right/up, FOV — in engine axes and units, format pinned by `tests/fixtures/camera_samples.csv`) and `extractor.json` (the profile's `convention`, `units_to_world`, `fov_axis`, `camera_model`). It does nothing else: `odx build --camera <that folder>` converts the samples to the canonical C2W + intrinsics and puts them on the video frames (newest sample at or before each frame's capture time, none if older than 100 ms). Input and video come from `odx input` + `odx record` as for any title.

`odx record` captures a game window (or the primary monitor) with Windows.Graphics.Capture. Every captured frame carries its QPC time, the same clock as `odx input`. Frames are resampled onto a constant-rate grid and piped to ffmpeg (x264 by default, NVENC optional; no B-frames). Outputs:
- `video.mp4`
- `video_frames.csv` (`frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate`; read with `VideoFrameLogReader`, pinned by `tests/fixtures/video_frames.csv`)
- `recording.json` (size, fps, start UTC/QPC, duplicated/dropped frame counts)

```bash
dotnet run --project src/Odx.Cli -f net8.0-windows10.0.22621.0 -- record --out C:/odx_capture/session1 --window Game.exe [--fps 60] [--encoder x264|nvenc] [--seconds 120]
```

Run `odx input` and `odx record` into the same `--out` folder. Grid times (`t_grid_qpc_ms`) are the per-frame clock for `Frames.AggregateInputs`. Requires Windows 10 2004+ and ffmpeg (the packaged `ffmpeg/ffmpeg.exe`, else PATH, or `--ffmpeg`). x264 is the default; `--encoder nvenc` needs an NVIDIA GPU with driver 471.41+ (the packaged ffmpeg is built with NVENC 11.1 headers; ffmpeg builds with the newest headers need 610+). Both commands also accept `--stop-event <name>`: they stop when that named Win32 event is set.

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
dotnet run --project src/Odx.Cli -f net8.0 -- build path/to/<capture-dir> --profile ../games/example-fps.json
dotnet run --project src/Odx.Cli -f net8.0 -- validate path/to/<capture-dir> [more dirs...] [--json] [--no-video]
```

Exit code 1 when any error is found. Warnings (`c2w.roll`, `c2w.upright`) do not fail a session.

## Multiplayer (spec v3)

`odx-orchestrator` (`src/Odx.Orchestrator`, ASP.NET Core, cross-platform) runs next to the game server:

```bash
cd capture-cs
dotnet run --project src/Odx.Orchestrator -- --out C:/odx_sessions --game-profile ../games/example-fps.json [--http-port 5180] [--time-port 12300] [--fps 30] [--min-valid K] [--max-rtt-ms 100]
```

On each player's machine, and in place of a game server plugin while there is none:

```bash
odx agent --orchestrator http://<host>:5180 --player p1 --out C:/odx_capture [--name Alice] [--window cs2]
odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--rounds 2] [--round-s 20] [--match sim]
```

In odx-app, filling in **Orchestrator** (and **Player ID**, default: the machine name) turns the main button into Connect/Disconnect. The orchestrator then starts and stops recordings, and the game profile and FPS come from the orchestrator. A player who ticks **Host** starts and ends rounds with the hotkey, in place of a game server plugin: `round_start` carries `countdown_ms` 3000; every player starts capturing at once and sees the 3-2-1 (excluded from screen capture, so never in the video), and the next press ends the round for everyone (`end_reason` `player_stop:<id>`). Other players' hotkeys do nothing in agent mode.

`simulate-game` plays scripted rounds: each has a kill and a permanent death halfway through. It waits for uploads between rounds, prints each session's gate result and exits 1 if any session fails. Run on one machine (orchestrator + two agents recording the monitor + simulator), three 10 s rounds all pass; first frames arrive 0.75–0.93 s after `round_start`.

- **Time service:** an SNTP responder on UDP `--time-port`. Every player's capture measures QPC against it (`ntp_samples.csv`), so all players share one clock.
- **Agents:** each player's machine runs `AgentClient` (`Odx.Session/Multiplayer`). It connects to `ws://<host>:5180/agents`, measures its offset on `welcome`, starts its capture on `start` and stops on `stop`. Then it uploads the built capture folder (`PUT /sessions/{sid}/players/{pid}/files/{name}`) and reports `done`.
- **Game events:** the game server plugin posts JSON events to `POST /game/events` (`GameEvents`: `match_start`, `round_start`, `round_end`, `kill`, `player_death` with `permanent`, `player_connect`, `player_disconnect`, `match_end`). Events without `utc_ms` are stamped on arrival.
  - `round_start` arms and starts every idle agent at once; play starts after `countdown_ms` if given, so the video holds the start of play (`round_start_frame` in `player.json`). Players' starts differ by their capture start-up; every frame is put on server time afterwards.
  - A permanent `player_death` stops that player.
  - `round_end` stops the rest.
- **Assembly:** once every player is done (or 15 min later), `MultiplayerSession.Assemble` writes:

  ```
  <out>/<vendor>/<mm-dd-yyyy>/<game>/<match>-rNN/
    session.json  validation.json  server/events.jsonl
    players/<pid>/ player.json frames.csv video.mp4 events.jsonl capture/
  ```

  - `player.json` holds the ping (median NTP RTT), clock offset, first/last frame UTC, `round_start_frame` / `round_start_video_time_ms`, stop reason and validity. `session.json` carries `spec_version` 3.1, `round_index`, `countdown_ms` and `end_reason`.
  - `events.jsonl` holds the server events inside the player's recording, each with the `frame_index` on screen at that moment.
  - `capture/` holds the v2 `session.json` and the raw logs.
- **Gate** (`validation.json`):
  - each player passes v2 §1.5;
  - `sync.clock`: median RTT ≤ 100 ms;
  - `sync.start`: first frame ≤ 3 s after `round_start` + `countdown_ms`;
  - `sync.end`: last frame ≥ stop − 1 s;
  - the session needs K of N valid players (K = N up to 3 players, else N − 1).

## Decisions pending with the client

- **Null encoding in CSV:** nulls are written as the literal `null` (`Frames.Null`), as spec v2 §1.5.5 asks ("null, not empty string") and as the client's multiplayer sample delivery does; confirm.
- **Column-name case:** the spec uses `c2w_mRC` (§1.1.2.4, §1.5.3) but `C2W_M00` in the summary table (§1.1.3). Uppercase `C2W_M00` is used here, as in the summary table and the client's sample delivery (which also writes `camera_model` as `pinhole`; we keep the spec's `PINHOLE`).
- **Extra `session.json` fields:** `vendor_tool_version` and `vendor_clock_sync` are not in the spec (the schema allows additional properties); confirm the client accepts them.
- **Validation tolerances** not fixed by the spec (all in `ValidationTolerances`, `Odx.Session/Validation/Issue.cs`): rotation `1e-3`, principal point `4 px`, frame spacing `max(1 ms, 25%)`, level-roll warning `0.05`, fx = fy within `1e-3`, duration checks `1 s` / `1000 ms` / `2 frames`.
- **Multiplayer (provisional):**
  - One round = one session (`<match>-rNN`).
  - Ping is the median NTP RTT to the orchestrator.
  - The sync tolerances above (`SyncTolerances`) and the K-of-N rule are proposals (v3 §4 leaves them open).
  - Agents still uploading the previous round are left out of the next one.
  - Host control: any player who ticks Host can start and end rounds (no exclusive host). There is no per-player stop without a game server, so everyone records the whole round.
  - `frames.bin`, network packets, `server_state.bin` and `schema.bin.gz` are not produced yet.
