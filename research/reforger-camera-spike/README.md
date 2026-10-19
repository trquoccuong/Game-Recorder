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
| `addon/OdxCameraSpike/Scripts/Game/OdxCameraSpike.c` | The logger. `modded class ArmaReforgerScripted` hooks `OnUpdate` (every frame) and: <br>• writes one line per frame to `$profile:camera_spike.csv`: frame, tick, third person (1 / 0, -1 without a controlled character), transform (4 rows), vertical FOV, near / far, screen and render size, and 12 world points fixed around the first camera position (relative to the camera) with their engine projection; <br>• draws a 64 px square in the top-left corner, black, white for one frame every 4–6 s (uneven on purpose, so flashes can be paired) |
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

**Game.** Steam → Arma Reforger → Properties → Launch options (or run `ArmaReforgerSteam.exe` with them; add `-window` for a window):

```
-addonsDir D:\project\client\research\reforger-camera-spike\addon -addons OdxCameraSpike -profile D:\odx-dev\reforger-profile
```

Or use `launcher\Launch-Odx.cmd` (a WinForms window: game folder, server `ip:port`, window or fullscreen, password,
profile folder). On Launch it mirrors the addon into the game's `addons\` and starts `ArmaReforgerSteam.exe` with the arguments
above plus `-client`. Run it from the repo, or copy `Launch-Odx.cmd`, `Launch-Odx.ps1` and the `OdxCameraSpike`
folder into the game folder of another PC: `launcher\pack.ps1` zips exactly those
(`D:\odx-dev\odx-reforger-launcher.zip`, `-Out` to change), to extract into the game folder. `-password` is passed only when filled in; it is unverified as a client parameter, and a
command-line `-server` has no password.

`-profile` puts `$profile:` on D:: it is the `profile\` folder inside it. The mod reports its state in the game's
`console.log` (`logs\<date>\` in the same folder), prefixed `TBC_CameraSpike:`: "logging to …" once a camera exists. Start a single-player scenario (Game Master or Conflict on Everon).

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
.venv/Scripts/python analyze.py D:\odx-dev\reforger-profile\profile\camera_spike.csv --capture D:\odx-dev\spike\rec1 --corner 6 36
```

`--corner X Y` is where the flash square sits in the video: `6 36` for a windowed capture on Windows 11 (title bar
31 px; the 64 px square is drawn in UI units, about 42 video px at 720p). Check a frame with
`ffmpeg -ss 10 -i video.mp4 -frames:v 1 f.png` if the window or the resolution differs.

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
| `Unknown operator '%'` | Enforce Script rejects `%` right after `)`: put the operand in an `int` first. The Script API reference is offline in `Arma Reforger Tools\Workbench\docs\` |
| `fewer than 20 visible point samples` | The points were placed around a spot you moved away from: stand still at the start, look around in place |
| `need 2+ flashes on both sides` | The square is hidden (HUD layer, UI scale), or `--corner` does not point inside it |
| G2 off by a constant factor | Projection in DPI-scaled units: compare `screen` and `render` |

## Status

Two runs on 2026-10-18, 1280×720 windowed, 120 s recorded at 60 fps, analysed with `--corner 6 36`:

- **rec1** offline: Game Master on Everon, game at ~40 fps.
- **rec3** offline, first and third person: Game Master on Everon, 16 view switches; 5937 frames first person,
  3603 third person.
- **rec2** multiplayer: Conflict on Everon on our own dedicated server, started from the command line with the mod (`-server … -addonsDir … -addons OdxCameraSpike`), client `-client 127.0.0.1:2001` with the same mod. Game near 60 fps.

| Goal | rec1 offline | rec2 multiplayer |
|---|---|---|
| G1 layout | **Pass.** Rows 0 / 1 / 2 = right / up / forward, row 3 = position, det +1, orthonormal to 2e-6; 0.0 px RMS against `ProjWorldToScreenNative` over 2098 samples (next-best layout 213 px) | **Pass.** Same layout, 0.0 px over 4896 samples (next-best 219 px) |
| G2 FOV | **Pass** at hip: fx = fy = 477.74, centre (640.0, 360.0), vFOV 74.0° | **Pass**, also zoomed: 880 samples at 50.8°–74° reproject at 0.0 px with the per-frame logged FOV; hip fit vFOV 73.9985°, fx/fy 1.00008 |
| G3 clock | 17 / 17 flashes, residual max 31 ms = **1.85 frames**, std 12 ms | 23 / 23 flashes, rate 0.999924, residual max 20 ms = **1.20 frames**, std 9 ms |
| G4 lag | Not run | Not run |

rec3 (views): G1 **pass in both views**: 0.0 px over 6453 first-person and 6553 third-person samples (next-best layout
231 px); the logged transform is the rendered camera, behind the character in third person. Third person has its own
FOV (72.0° here, up to ~77° in transitions) and the logged per-frame FOV matches it exactly; the single-f free fit
mixes views and is not meaningful for this run. G3: 20 / 20 flashes, residual max 20 ms = 1.21 frames, std 8 ms.

The log's first frame is skipped (engine projection not ready yet). G3 is inside plan Q6's 2 frames for live capture,
not 1: ticks are whole ms and the game frame is not locked to the recording grid; it improves as the game fps nears
the recording fps.

Raw logs and output: `D:\odx-dev\spike\rec{1,2,3}-camera_spike.csv`, `rec{1,2,3}-analysis.json`. Findings:
`document/games/arma-reforger.md` §3, §5, §6, §7.
