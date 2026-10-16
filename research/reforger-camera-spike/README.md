# reforger-camera-spike

Spike for Arma Reforger's camera route (`document/games/arma-reforger.md` §3, §7): can a script mod give every video
frame an exact C2W + intrinsics? Research only; nothing here is part of the product (`capture/`). The product converter
(`Odx.Games.ArmaReforger`, `odx build --camera`) is written once this answers the questions below.

| Goal | Question | Answered by | Pass |
|---|---|---|---|
| G1 layout | Which rows of `GetWorldCameraTransform(out vector mat[4])` are right / up / forward / position, handedness | Points projected with every row/column/sign assignment vs the engine's `ProjWorldToScreenNative` | One assignment ≤ 1 px RMS, the next one far worse; rows orthonormal |
| G2 FOV | Is `GetVerticalFOV()` the full vertical FOV of what is drawn, square pixels, centred principal point; does it follow zoom / optics | fx, fy, cx, cy fitted freely vs the logged FOV | Fitted vertical FOV within 0.1° of the logged one, fx/fy within 0.1 %, while aiming too |
| G3 clock | How `System.GetTickCount()` (ms since game start, not QPC) maps onto odx record's QPC | Corner flash at logged ticks found in `video.mp4`, paired, `QPC = a·tick + b` | ≥ 5 pairs, residual ≤ 1 video frame (plan Q6: ≤ 2 frames for live capture) |
| G4 lag | Does frame N's image show the pose logged in frame N (`OnUpdate` may run before the camera is applied) | Not here: needs the converter, then `research/analyze-pose` (pose ↔ video lag from image motion) | ≤ 1 frame, or a fixed offset to apply |

`addon/OdxCameraSpike/` is the mod: every frame it writes `$profile:odx/camera_spike.csv` (tick, transform, FOV,
near/far, screen and render size, 12 fixed world points relative to the camera and their engine projection) and shows
a 64 px square in the top-left corner, black, white on a flash frame every 4–6 s. `analyze.py` reads that log and,
with `--capture`, an `odx record` folder made at the same time.

The mod was written before the game was installed: the API calls come from the published Script API
(`CameraBase`, `System`, `ArmaReforgerScripted.OnUpdate`) and Bohemia's samples, but `WorkspaceWidget.CreateWidget`,
`FrameSlot` and `float.ToString` are unchecked. Fix whatever the first compile reports.

## Run

```bash
python -m venv .venv
.venv/Scripts/pip install -r requirements.txt   # also needs ffmpeg on PATH
.venv/Scripts/python analyze.py --selftest      # synthetic log + video: must print all checks true
```

1. Install **Arma Reforger** and **Arma Reforger Tools** (Workbench) from Steam into `D:\SteamLibrary`.
2. Open `addon/OdxCameraSpike/OdxCameraSpike.gproj` in Workbench and compile the scripts (Script Editor); or
   start the game with `-addonsDir D:\project\client\research\reforger-camera-spike\addon -addons OdxCameraSpike
   -profile D:\odx-dev\reforger-profile` **[flags to verify]**, which also puts `$profile:` on D:.
3. Start a single-player scenario (Game Master or Conflict, Everon), first person, borderless window. Wait until the
   corner square blinks.
4. Record at the same time from `capture/dotnet`:
   `odx record --out D:\odx-dev\spike\rec1 --window ArmaReforgerSteam --fps 60 --seconds 120`.
   Stand still at first (the 12 points are placed around the first camera position), then look around: slow pans,
   fast flicks, pitch up and down, aim down sights (zoom), freelook, a vehicle seat.
5. `.venv/Scripts/python analyze.py D:\odx-dev\reforger-profile\odx\camera_spike.csv --capture D:\odx-dev\spike\rec1`

## Reading the result

- `layout.axes` and `rms_px_with_logged_vfov` (G1); `runner_up_rms_px` shows how clear the choice was.
  `det_rows_012` +1 / −1 tells the handedness of the rows as written.
- `fit` and `vfov_fitted_deg` vs `vfov_logged_deg` (G2). If they disagree, compare `screen` with `render`:
  `ProjWorldToScreenNative` may work in a different pixel space than the rendered image.
- `clock.qpc_ms_per_tick_ms`, `qpc_at_tick_0_ms`, `residual_frames_max` (G3). `flash_lit_frames` [1] means a flash
  lasts one recorded frame; [] or a missing pair means the square is not visible in the recording.

Findings go to `document/games/arma-reforger.md` (§3 Data route, §7 Open questions).
