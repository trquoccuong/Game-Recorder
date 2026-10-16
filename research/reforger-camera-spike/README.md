# reforger-camera-spike

Can a script mod give every video frame of Arma Reforger an exact camera pose (C2W) and intrinsics, on our clock?
This spike answers that before any product code is written (`document/games/arma-reforger.md` §3, §7). Research only:
nothing here ships in `capture/`.

- [Questions](#questions)
- [Components](#components)
- [Install](#install)
- [Run](#run)
- [Read the result](#read-the-result)
- [Troubleshooting](#troubleshooting)
- [Status](#status)

## Questions

| Goal | Question | How it is answered | Pass |
|---|---|---|---|
| **G1** layout | Which rows of `GetWorldCameraTransform(out vector mat[4])` are right / up / forward / position; handedness | Logged world points projected with every row / column / sign assignment, compared with the engine's own `ProjWorldToScreenNative` | One assignment ≤ 1 px RMS, the next one far worse; rows orthonormal |
| **G2** FOV | Is `GetVerticalFOV()` the full vertical FOV of what is drawn; square pixels; centred principal point; does it follow zoom and optics | fx, fy, cx, cy fitted freely, compared with the logged FOV | Fitted vertical FOV within 0.1° of the logged one, fx/fy within 0.1 %, also while aiming |
| **G3** clock | How `System.GetTickCount()` (ms since game start, not QPC) maps onto `odx record`'s QPC | A corner square flashed at logged ticks, found again in `video.mp4`; `QPC = a·tick + b` fitted over the pairs | ≥ 5 pairs, residual ≤ 1 video frame (plan Q6 allows 2 for live capture) |
| G4 lag | Does frame N's image show the pose logged in frame N (`OnUpdate` may run before the camera is applied) | Not in this spike: needs the converter, then `research/analyze-pose` (pose ↔ video lag from image motion) | ≤ 1 frame, or a fixed offset |

## Components

| Path | What it is |
|---|---|
| `addon/OdxCameraSpike/OdxCameraSpike.gproj` | The Workbench project of the mod; depends on Arma Reforger (`58D0FB3206B6F859`) |
| `addon/OdxCameraSpike/Scripts/Game/OdxCameraSpike.c` | The logger. `modded class ArmaReforgerScripted` hooks `OnUpdate` (every frame) and: <br>• writes one line per frame to `$profile:odx/camera_spike.csv`: frame, tick, transform (4 rows), vertical FOV, near / far, screen and render size, and 12 world points fixed around the first camera position (relative to the camera) with their engine projection; <br>• draws a 64 px square in the top-left corner, black, white for one frame every 4–6 s (uneven on purpose, so flashes can be paired) |
| `analyze.py` | Reads the log (and with `--capture` an `odx record` folder) and prints G1–G3 as JSON. `--selftest` builds a synthetic log and video with known answers and checks it finds them |
| `requirements.txt` | numpy |

## Install

### 1. Arma Reforger and Arma Reforger Tools

1. In Steam, buy / install **Arma Reforger** (app 1874880). Install it into `D:\SteamLibrary`: in the install dialog
   pick that library (Steam → Settings → Storage adds it if it is missing). About 25–30 GB **[to verify]**.
2. Steam → Library → filter **Tools** → install **Arma Reforger Tools** (Enfusion Workbench, app 1874910, free with the
   game) into the same library.
3. Start the game once, from Steam: let it finish its first-run setup, set **Display mode: Borderless** and note the
   resolution. Quit.

### 2. Analysis tools

From this folder:

```bash
python -m venv .venv
.venv/Scripts/pip install -r requirements.txt
.venv/Scripts/python analyze.py --selftest      # needs ffmpeg on PATH; must end with every check true
```

`odx record` comes from `capture/dotnet` (build it as in the [root README](../../README.md#build-and-test)).

## Run

### 1. Load the mod

Either way works; Workbench shows compile errors, the game only logs them.

**Workbench.** Start **Arma Reforger Tools** → in the Enfusion Workbench launcher, **Add Existing** →
`addon/OdxCameraSpike/OdxCameraSpike.gproj` → **Open**. Open the **Script Editor** and compile
(**Build → Compile and Reload Scripts** **[menu to verify]**); fix what it reports. Then open a world in the
**World Editor** (e.g. Everon) and **Play**.

**Game.** Steam → Arma Reforger → Properties → Launch options **[flags to verify]**:

```
-addonsDir D:\project\client\research\reforger-camera-spike\addon -addons OdxCameraSpike -profile D:\odx-dev\reforger-profile
```

`-profile` puts `$profile:` (the log) on D:. Start a single-player scenario (Game Master or Conflict on Everon).

### 2. Record

1. Spawn, switch to first person, wait until the top-left square blinks.
2. From `capture/dotnet`, start the recording of the game window:
   ```bash
   dotnet run --project src/Apps/Odx.Cli -f net10.0-windows10.0.22621.0 -- record --out D:\odx-dev\spike\rec1 --window ArmaReforgerSteam --fps 60 --seconds 120
   ```
3. During the 2 minutes: stand still for a few seconds (the 12 points are placed around the first camera position),
   then slow pans, fast flicks, pitch up and down, aim down sights (zoom), free look (Alt), a vehicle seat.
4. Quit the scenario so the log is flushed.

### 3. Analyse

```bash
.venv/Scripts/python analyze.py D:\odx-dev\reforger-profile\odx\camera_spike.csv --capture D:\odx-dev\spike\rec1
```

## Read the result

| Field | Meaning |
|---|---|
| `layout.axes`, `rms_px_with_logged_vfov` | G1: the rows (or columns) that are right / up / forward, and the pixel error with the logged FOV |
| `runner_up_rms_px` | How clear the choice was: the best wrong assignment should be far worse |
| `det_rows_012`, `orthonormal_max_err` | Handedness of the rows as written (+1 / −1); rotation sanity |
| `fit`, `vfov_fitted_deg` vs `vfov_logged_deg` | G2: free fit of fx, fy, cx, cy against the logged FOV |
| `screen` vs `render` | Pixel space of `ProjWorldToScreenNative` vs the rendered image; a mismatch explains a G2 failure |
| `clock.qpc_ms_per_tick_ms`, `qpc_at_tick_0_ms` | G3: the mapping tick → QPC |
| `clock.residual_frames_max` | G3: worst pairing error, in video frames |
| `clock.flash_lit_frames` | How many recorded frames a flash lasts ([1] expected) |

Write the findings into `document/games/arma-reforger.md` (§3 Data route, §7 Open questions).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| No `camera_spike.csv` | Mod not loaded (check the game's `console.log` in the profile folder), or no camera yet (main menu) |
| Compile errors in `ShowFlash` or `F()` | `WorkspaceWidget.CreateWidget`, `FrameSlot` and `float.ToString(-1, 6)` were written before the game was installed; replace them with what the Script Editor offers |
| `fewer than 20 visible point samples` | The points were placed around a spot you moved away from: stand still at the start, look around in place |
| `need 2+ flashes on both sides` | The square is hidden (HUD layer, UI scale) or the recording does not include the window's top-left corner |
| G2 off by a constant factor | Projection in DPI-scaled units: compare `screen` and `render` |

## Status

Written before the game was installed: the API comes from the published Script API (`CameraBase`, `System`,
`ArmaReforgerScripted`) and Bohemia's samples; `--selftest` passes; nothing has run in the game yet.
