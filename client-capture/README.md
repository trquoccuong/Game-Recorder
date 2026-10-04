# client-capture

Tooling for the Client canonical game-data format. See [`../document/software-plan.md`](../document/software-plan.md) for the full plan; this repo currently implements testing layers 1–2 (§7.10).

## What's here

| Module | Purpose | Spec |
|---|---|---|
| `Odx.Core.Geometry` | Engine → RFU axis conversion (RFU/RAGE, Unity, Unreal, Quake/Source), C2W assembly (RUB), roll-free reconstruction, intrinsics from vertical/horizontal FOV or projection | v2 §1.1.2.3–1.1.2.5, appendix §2 |
| `Odx.Core.Frames` | `frames.csv` column list, per-frame input aggregation (held + tapped keys, summed mouse deltas), CSV writer | v2 §1.1.2.6, §1.1.3 |
| `Odx.Session` / `odx-build` | Builds a spec v2 session from a capture folder: `frames.csv` (grid-time frame clock, input aggregation, actions from the game profile's bindings; unbound keys left out) and `session.json` (UTC from `clock_samples.csv`, `capture_id` as `session_id`) | v2 §1.1.2, §1.1.3, §1.5 |
| `games/*.json` | Game profiles: `game_title`, `platform`, `localization`, `input_mouse_convention`, `bindings` (key/button → action) | v2 §1.1.2.1, §1.1.2.6 |
| `Odx.Validate` | `odx-validate` CLI: every vendor check in v2 §1.5 (session.json, frames structure, C2W, intrinsics, input, video via ffprobe) | v2 §1.5 |
| `schemas/session_schema.json` | JSON Schema for `session.json`, extracted from the spec; embedded in `odx-validate` | v2 §1.1.2.1 |
| `tests/Odx.Tests/SessionFixtures.cs` | Synthetic valid session + one mutator per validation rule | plan §7.10.2 |

Everything except the in-game extractor (C++, `extractor/`) is C# (.NET 8) in `capture-cs/`. The Python pose audit lives outside the product in [`../research/analyze-pose/`](../research/analyze-pose/).

## Capture tools (C#, `capture-cs/`)

`odx-input` records keyboard/mouse with Raw Input in its own process (never inside the game) and writes `input_events.csv` + `clock_samples.csv` (QPC ↔ UTC). `InputLogReader` reads the log back; the format is pinned by `tests/fixtures/input_events.csv` (writer output must match it byte for byte).

```bash
cd capture-cs
dotnet test                                   # .NET 8 SDK; needs ffmpeg/ffprobe on PATH for the video checks
dotnet run --project src/Odx.Input -- --out C:/odx_capture/session1 [--pid <game_pid>] [--forward]
```

`--forward` also sends events to the C++ extractor DLL (Path B) over a mailslot. Language split: plan §6.5.

`odx-record` captures a game window (or the primary monitor) with Windows.Graphics.Capture. Every captured frame carries its QPC time, the same clock as `odx-input`. Frames are resampled onto a constant-rate grid and piped to ffmpeg (x264 by default, NVENC optional; no B-frames). Outputs:
- `video.mp4`
- `video_frames.csv` (`frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate`; read with `VideoFrameLogReader`, pinned by `tests/fixtures/video_frames.csv`)
- `recording.json` (size, fps, start UTC/QPC, duplicated/dropped frame counts)

```bash
dotnet run --project src/Odx.Recorder -- --out C:/odx_capture/session1 --window Game.exe [--fps 60] [--encoder x264|nvenc] [--seconds 120]
```

Run `odx-input` and `odx-record` into the same `--out` folder. Grid times (`t_grid_qpc_ms`) are the per-frame clock for `Frames.AggregateInputs`. Requires Windows 10 2004+ and ffmpeg on PATH (or `--ffmpeg`). `--encoder nvenc` with ffmpeg 9 needs NVIDIA driver 610+; older drivers fail at startup, so x264 is the default. Both tools also accept `--stop-event <name>`: they stop when that named Win32 event is set.

`odx-app` (WPF, `src/Odx.App`) is the desktop front end. It runs `odx-input` and `odx-record` as child processes into `<output folder>/<yyyyMMdd-HHmmss>/` and stops both through `--stop-event`.
- Source (primary monitor or a window), FPS and output folder; settings saved in `%APPDATA%\Client\odx-app.json`.
- Global start/stop hotkey, default `Ctrl+Shift+R`; click the hotkey box and press a new combination to change it. The hotkey keys are recorded in `input_events.csv` like any other input.
- Start shows a 3-second countdown overlay (click-through, never takes focus); capture begins after it closes, so it is not in the video. Start/hotkey again during the countdown cancels.
- Minimizing hides it to the tray; the hotkey keeps working.

```bash
dotnet run --project src/Odx.App
```

## Package for other machines

```powershell
cd capture-cs
powershell -ExecutionPolicy Bypass -File publish.ps1 -Version 0.1.0
```

Produces `capture-cs/dist/odx-capture-<version>-win-x64.zip` (~270 MB). Unzip it on any Windows 10 2004+ x64 machine and run `odx-app.exe`; no .NET or ffmpeg install is needed.

```
odx-capture-<version>-win-x64/
  odx-app.exe                 desktop front end (self-contained .NET runtime)
  tools/odx-input/            each tool self-contained in its own folder: their runtimes differ (WPF vs console,
  tools/odx-record/           System.Text.Json 8 vs 9), so sharing one folder would break one of them
  tools/odx-build/
  tools/odx-validate/
  games/                      game profiles
  ffmpeg/                     ffmpeg.exe, ffprobe.exe, LICENSE (pinned gyan.dev 9.0.2 essentials build, SHA-256 checked)
```

The tools use `ffmpeg/` from the package when present, otherwise `ffmpeg`/`ffprobe` on PATH. The ffmpeg build is GPLv3: its LICENSE ships alongside, and our tools only run it as a separate process (not linked).

## Versions and CI

Every tool stamps `<version>+<git commit>` into its output: `tool_version` in `recording.json`, `vendor_tool_version` in `session.json` (an extra field; the schema allows additional properties). The .NET SDK is pinned by `capture-cs/global.json`. CI (`.github/workflows/ci.yml`, Windows) builds and tests capture-cs, then the extractor with ctest.

## Build and validate a session

`odx-app` does both automatically after each recording when a game profile is selected. By hand:

```bash
cd capture-cs
dotnet run --project src/Odx.Build -- path/to/<capture-dir> --profile ../games/example-fps.json
dotnet run --project src/Odx.Validate -- path/to/<capture-dir> [more dirs...] [--json] [--no-video]
```

Exit code 1 when any error is found. Warnings (`c2w.roll`, `c2w.upright`) do not fail a session.

## Decisions pending with Client

- **Null encoding in CSV:** nulls are written as empty fields (`Frames.Null`). Spec v2 §1.5.5 distinguishes "null" from "empty string", which plain CSV cannot express; confirm the expected token.
- **Column-name case:** the spec uses `c2w_mRC` (§1.1.2.4, §1.5.3) but `C2W_M00` in the summary table (§1.1.3). Lowercase is used here.
- **Validation tolerances** not fixed by the spec (`SessionValidator` constants): rotation `1e-3`, principal point `4 px`, frame spacing `max(1 ms, 25%)`, level-roll warning `0.05`.
