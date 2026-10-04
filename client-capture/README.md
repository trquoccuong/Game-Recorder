# client-capture

Tooling for the Client canonical game-data format. See [`../document/software-plan.md`](../document/software-plan.md) for the full plan; this repo currently implements testing layers 1–2 (§7.10).

## What's here

| Module | Purpose | Spec |
|---|---|---|
| `client_capture.geometry` | Engine → RFU axis conversion (RFU/RAGE, Unity, Unreal, Quake/Source), C2W assembly (RUB), roll-free reconstruction, intrinsics from vertical/horizontal FOV or projection | v2 §1.1.2.3–1.1.2.5, appendix §2 |
| `client_capture.frames` | `frames.csv` column list, per-frame input aggregation (held + tapped keys, summed mouse deltas), CSV writer | v2 §1.1.2.6, §1.1.3 |
| `client_capture.validate` | `client-validate` CLI: every vendor check in v2 §1.5 (session.json, frames structure, C2W, intrinsics, input, video via ffprobe) | v2 §1.5 |
| `schemas/session_schema.json` | JSON Schema for `session.json`, extracted from the spec | v2 §1.1.2.1 |
| `tests/sessions.py` | Synthetic valid session + one mutator per validation rule | plan §7.10.2 |
| `tools/analyze_pose.py` | Reference audit (not yet part of the validator): matrix basis, forward vs motion, units, proxy-mouse detection, pose↔video lag, fx estimated from video. Run `python tools/analyze_pose.py <root>` on `<game>/<session>/` folders | `document/reviews/sample-camera-pose-action-labeled.md` |

## Capture tools (C#, `capture-cs/`)

`odx-input` records keyboard/mouse with Raw Input in its own process (never inside the game) and writes `input_events.csv` + `clock_samples.csv` (QPC ↔ UTC). `client_capture.frames.read_input_events` reads the log; the format is pinned by `tests/fixtures/input_events.csv`, checked from both C# and Python.

```bash
cd capture-cs
dotnet test                                   # .NET 8 SDK; portable tests run on any OS
dotnet run --project src/Odx.Input -- --out C:/odx_capture/session1 [--pid <game_pid>] [--forward]
```

`--forward` also sends events to the C++ extractor DLL (Path B) over a mailslot. Language split: plan §6.5.

`odx-record` captures a game window (or the primary monitor) with Windows.Graphics.Capture. Every captured frame carries its QPC time, the same clock as `odx-input`. Frames are resampled onto a constant-rate grid and piped to ffmpeg (NVENC or x264, no B-frames). Outputs:
- `video.mp4`
- `video_frames.csv` (`frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate`; read with `client_capture.frames.read_video_frames`, pinned by `tests/fixtures/video_frames.csv`)
- `recording.json` (size, fps, start UTC/QPC, duplicated/dropped frame counts)

```bash
dotnet run --project src/Odx.Recorder -- --out C:/odx_capture/session1 --window Game.exe [--fps 60] [--encoder nvenc|x264] [--seconds 120]
```

Run `odx-input` and `odx-record` into the same `--out` folder. Grid times (`t_grid_qpc_ms`) are the per-frame clock for `aggregate_inputs`. Requires Windows 10 2004+ and ffmpeg on PATH (or `--ffmpeg`).

## Setup

```bash
python3 -m venv .venv
.venv/bin/pip install -e . pytest hypothesis
.venv/bin/pytest            # needs ffmpeg/ffprobe for video tests
```

## Validate a session

```bash
.venv/bin/client-validate path/to/<session-id> [more dirs...] [--json] [--no-video]
```

Exit code 1 when any error is found. Warnings (`c2w.roll`, `c2w.upright`) do not fail a session.

## Decisions pending with Client

- **Null encoding in CSV:** nulls are written as empty fields (`frames.NULL`). Spec v2 §1.5.5 distinguishes "null" from "empty string", which plain CSV cannot express; confirm the expected token.
- **Column-name case:** the spec uses `c2w_mRC` (§1.1.2.4, §1.5.3) but `C2W_M00` in the summary table (§1.1.3). Lowercase is used here.
- **Validation tolerances** not fixed by the spec (`validate.py` constants): rotation `1e-3`, principal point `4 px`, frame spacing `max(1 ms, 25%)`, level-roll warning `0.05`.
