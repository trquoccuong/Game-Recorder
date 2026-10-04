# Review: "Vane Ank Secret" — colleague's per-game collectors, QC Tool, RRD tools

> Reviewed 2026-10-04. Source: `/Users/quoccuong/Downloads/Vane Ank Secret` (7.2 GB, ~5,100 files). Static reading only; no executable or script from the folder was run. The only thing executed was **our** `odyssey-validate` and `ffprobe`, against the three sample sessions in `Test/`.
>
> Compared against: `document/videogame-data-capture-spec-v2.md` (v2.1), `document/software-plan.md` §1, §6.5, §7.10, and `odyssey-capture/src/odyssey_capture/validate.py`.

**Path legend:**

| Prefix | Path |
|---|---|
| `<B>` | `/Users/quoccuong/Downloads/Vane Ank Secret` |
| `<S>` | `/private/tmp/claude-501/-Users-quoccuong-Projects-odyssey/d5307e34-34b3-4e1a-b9f3-eb13478e68c3/scratchpad/vane` — source and docs extracted from zips, scratch only |

Rule followed throughout: memory offsets and signatures are not reproduced. Anything touching anti-cheat or cracked executables is marked *out of scope* and not detailed.

---

## 1. Summary

### 1.1 What it is

- A two-month (Apr–May 2026) effort by one developer that produced:
  - **7 game collectors**: GTA5, Skyrim, Fallout 4, Cyberpunk 2077, Witcher 3, Dying Light 2, ETS2;
  - a **QC validator** (CLI + GUI, about 70 check IDs);
  - an **RRD builder** for Rerun.
- **Everything targets "Odyssey Spec v1"**, not v2. That means a 4-file bundle with `key_binding.json`, and a v1 `session.json` that uses `game`, `vendor`, `generated_at`, `video{}`, `camera{}`, `input{}` and `frame_columns`. **No output from this folder passes v2.**
  - Running our validator on `<B>/Test/106c1341…` gives 15 errors: 12 missing v2 `session.json` fields, 8 missing distortion headers, `record_*_px` absent, and video frames ≠ CSV rows.

### 1.2 Maturity of the collectors

There are two generations:

| Generation | Games | Output | Pose quality |
|---|---|---|---|
| **v1-format** | ETS2, Fallout 4 ("New folder" May build) | `session.json` + `frames.csv` + `video.mp4` + `key_binding.json` | Correct RUB C2W rebuilt from forward + world-up. Fixed-fps resampling. Validator built in. |
| **Legacy** | GTA5, Skyrim, Cyberpunk, Witcher 3, Dying Light 2 | Single CSV with `Frame_ID,…,FOV_Deg,FOV_Axis,Keyboard_Input("+"/"NONE")…`; video left wherever OBS put it | **Broken C2W**: Cyberpunk, Witcher 3 and DL2 have det = −1 (third column is +forward). Skyrim writes the raw engine matrix. GTA5 mixes Y-up rotation with a Z-up position. DL2 writes identity and relies on VGGT SLAM. |

### 1.3 How it works

- **Pose comes from official modding APIs.** No memory reading in the current builds, except DL2 and the April Fallout 4 build:

  | Game | API |
  |---|---|
  | Fallout 4 | F4SE / CommonLibF4 |
  | Skyrim | SKSE / CommonLibSSE-NG |
  | GTA5 | ScriptHookVDotNet |
  | Cyberpunk 2077 | CET Lua |
  | Witcher 3 | WitcherScript |
  | ETS2 | SCS Telemetry SDK |

- **Video** is OBS driven over WebSocket v5 on every title. Sync is open-loop. Only ETS2 waits for OBS's first frame.
- **Input** is `GetAsyncKeyState` polling of 9–89 hard-coded keys. **Only ETS2 reads real mouse deltas** (Raw Input). The others use:
  - `GetCursorPos` differences (Fallout 4);
  - a proxy of camera yaw/pitch change ÷ 10, clamped to ±1 (Skyrim, Cyberpunk, Witcher 3, DL2);
  - the normalised game control value (GTA5).

### 1.4 Systematic errors

- **Fallout 4 / Skyrim FOV axis.** These games treat Bethesda's (horizontal) `worldFOV` as vertical. The resulting `fy` (771 px at 70°/1080p) is likely about 1.8× too small.
- **Rotation source.** Several collectors use body or vehicle orientation instead of the render camera:
  - Fallout 4 uses the actor angle;
  - ETS2 uses truck heading;
  - Cyberpunk uses player position + 1.8 m.
- **Video/CSV frame count.** Every `Test/` session has exactly **one more video frame than CSV rows** (272/271, 19/18, 354/353).

### 1.5 Most valuable assets to adopt

The **QC Tool's semantic checks** that our validator lacks:

- forward-vs-velocity check (M5);
- check that C2W columns are really RUB and not RFU (J3);
- detection of proxy mouse deltas (N7.7);
- `"NONE"` sentinel check;
- video fps, codec, per-game sanity ranges;
- dynamic-FOV detection.

**ETS2's design patterns** are also worth adopting:

- `FrameAccumulator` (union of keys / sum of deltas onto the fps grid);
- Raw Input `MouseTracker`;
- gating the clock on OBS's first frame;
- an in-tool validator that runs on stop.

### 1.6 Risks

| Risk | Detail |
|---|---|
| Compliance (out of scope) | References to cracked or emulated executables: SmartSteamEmu launcher comment in DL2; "cracked exe support" patch in the Fallout 4 CMake. An anti-cheat-related file is bundled in the GTA5 ScriptHookV zip. |
| Gameplay contamination | In-game cheat menus (god mode, noclip, super speed, teleport) alter motion dynamics. |
| Bot input | `Auto Fallout 4.ahk` produces input that the collectors record as human. |
| Redistribution | Third-party binaries are bundled: F4SE, SKSE, Address Library, ScriptHookV, NativeTrainer, CET, ffmpeg. |

**No credentials, tokens or keys were found.** OBS passwords are typed in by the user. The `.claude/settings.local.json` files leak only local paths.

### 1.7 RRD tools

- `Tbrain RRD Tools` is very likely the "RRD builder that exists elsewhere". **We do not rebuild it.**
- It needs a v2 loader before we can deliver with it.
- It is not a self-contained `rrd_creation.py` (`build_rrd_v1.py` imports `build_rrd_tbrain.py`).
- It logs no `Pinhole` or `Transform3D` camera, so Odyssey cannot visually verify the intrinsics.

---

## 2. Inventory

| Folder | Purpose | Language / stack | Size | Source vs artifacts |
|---|---|---|---|---|
| `Auto Fallout 4.ahk` | Play-automation macro (see §8) | AutoHotkey v2 | 8 KB | source |
| `Fallout 4 Script Installer/` | Fallout 4 plugin installer (step 1) + launcher (step 2) | C# .NET 9 console (`installer_src/Program.cs`, 381 lines); payload dll | 918 MB | ~99% binaries: `Buoc_1_Cai_Plugin.exe` + 3 `.bak`, `v1/Fallout4_Combo.zip`, `New folder/Fallout4_Combo_v5.zip`, 101 MB `ffmpeg.exe`, `bin/obj` |
| `New folder/Fallout4AIDataCollector/` | **Canonical Fallout 4 collector source (May)** | C++23 F4SE plugin, CommonLibF4 + ImGui + MinHook; ~3.6k LOC `src/`, `tests/test_coords.cpp`, `tools/validate_session.py` | 1.3 GB | `build/` (1.3 GB) holds the artifacts; `Teleport.cpp/.h` are empty |
| `New folder/Fallout 4 Launcher/` | WPF launcher, embeds F4SE | C# WPF, 302 LOC | 585 MB | `bin/obj/publish_v2/.vs` |
| `Skyrim Script Installer/` | Skyrim SKSE collector + WPF installer | C++ CommonLibSSE-NG (`Recorder.cpp` 316 LOC, vendored `SKSEMenuFramework.h` 11k LOC); C# .NET 6 | 730 MB | `zips/` with SKSE, Address Library bins and a 51 MB `.pdb`; identical to `Source AI Collector.zip` Apr-10 copy |
| `GTA5 Script Installer/` | GTA5 ScriptHookVDotNet3 recorder + WPF installer | C# single file `GTA5Recorder/Class1.cs` (1,413 LOC) + LemonUI | 160 MB | `zips/ScriptHookV.zip` (also contains NativeTrainer.asi), `.vs`, `bin/obj` |
| `Cyberpunk 2077 Supporter/` | CET Lua mod + WPF companion | Lua `mods/collector/init.lua` (635 LOC, v4.7) + C# .NET 9 (1,053 LOC) | 418 MB | zip bundles the full CET `.asi`; `SAVEDATA1000` = Cyberpunk save (binary) |
| `The Witcher 3 Supporter/` | WitcherScript mod + WPF companion | `aiDataCollector.ws` (654 LOC) + **full replacement copies of vanilla `playerInput.ws` / `playerWitcher.ws`**; C# .NET 9 (1,328 LOC) | 312 MB | zip with bin/obj; build PDF is generic |
| `Dying Light 2 Supporter/` | DL2 collector (experimental) | C# .NET 6 WPF; 3 generations of injected C++ bridge; Cheat Engine Lua bridge; Python VGGT SLAM | 4.1 MB | bridge `build/*.obj/.dll`, vendored `.git`, duplicate zips |
| `Euro Truck Simulator 2 Supporter/` | **Most mature collector** | C# .NET 9 WPF (`Services/` ~2.8k LOC) + Launcher (314 LOC); SCS SDK `scs-telemetry.dll` | 1.2 GB | `dist/` 361 MB (self-contained exes + 101 MB ffmpeg), `publish/`, `ETS2AIDataCollector_v2_F6fix.zip` (binary only) |
| `QC Tool/` | Session QC validator (spec v1) | Python: `qc_validator.py` (1,674 LOC), `qc_gui.py` Tkinter (532 LOC); `rules_vi.md`, `checks_inventory.json` | 288 MB | `dist/qc_session.exe` (68 MB, bundles ffprobe), `dist.rar`, `build/`, `__pycache__` |
| `Tbrain RRD Tools/` | `.rrd` builder + simple validator | Python: `build_rrd_tbrain.py` (645), `build_rrd_v1.py` (114), `validate_session.py` (170), `.cmd` drag-drop wrappers | 100 KB | source only |
| `Source AI Collector.zip` | Archive of Apr-10 sources + installers for all games | nested zips + PDFs (17 entries) | 1.4 GB | mostly installers; only the Fallout 4 / Skyrim source was extracted to `<S>/SourceAICollector/` |
| `Test/` | 3 Fallout 4 sample sessions (v1 format), 18–353 frames | data | 45 MB | data |
| `savedata1000` | Game save (high-entropy binary, same size as the Cyberpunk one) | — | 11 MB | not collector output |

**Build PDFs.** The "Hướng dẫn build.pdf" files are all the same recipe: VS2022 → CMake/vcpkg or VS → drop the dll into the launcher's `zips/` → build the WPF launcher. Their .NET versions don't match the csproj files.

**Repo hygiene.** Source and multi-GB artifacts are mixed together. There is no VCS history.

---

## 3. Per-game collectors

### 3.1 Comparison matrix

| | Pose source | C2W correctness | FOV / intrinsics | Mouse dx/dy | Keys | Video / sync | Output |
|---|---|---|---|---|---|---|---|
| **ETS2** | SCS Telemetry SDK (official, shared memory) | ✅ RUB, det +1 — **but truck heading/pitch, not camera** | constant 75° from INI, "vertical", unverified | ✅ Raw Input (`RIDEV_INPUTSINK`) | 13 driving keys polled | OBS, **gated on first frame**, ffmpeg trim | v1 4-file bundle |
| **Fallout 4 (May)** | F4SE/CommonLibF4 API: camera-root position + **actor** yaw/pitch | ✅ RUB, det +1, roll dropped | runtime `worldFOV`, **mislabelled vertical** (Bethesda = horizontal) | ❌ `GetCursorPos` diff | 13 keys polled | OBS fire-and-forget, end trim | v1 4-file bundle |
| **Skyrim** | SKSE: raw camera-node rotation | ❌ engine matrix written as-is (not RUB) | INI FOV, horizontal written as vertical | ❌ yaw-diff proxy | 13 keys | OBS open-loop; pauses drop rows | legacy CSV |
| **GTA5** | SHVDN `GameplayCamera` pos/rot | ❌ Y-up rotation with Z-up translation | `GET_GAMEPLAY_CAM_FOV` (vertical ✅) | ~ normalised control value | 9 keys | OBS open-loop | legacy CSV |
| **Cyberpunk** | CET: camera forward; **player pos + 1.8 m** | ❌ det −1 (col2 = +forward) | settings FOV polled every 2 s, no ADS/zoom | ❌ yaw-diff proxy | 89 keys, separate C# clock | OBS open-loop, file IPC | legacy CSV |
| **Witcher 3** | WitcherScript `theCamera` (true camera pos/dir/FOV) | ❌ det −1 (col2 = +forward) | `GetFov()` labelled vertical | ❌ yaw-diff proxy | C# poll, separate clock | OBS open-loop, tails `scriptslog.txt` | legacy CSV |
| **DL2** | injected bridge / Cheat Engine (out of scope) — current build writes **identity** | ❌ identity; VGGT output stays in RDF, arbitrary scale | constant 60° | ❌ yaw-diff proxy | 15 keys | OBS open-loop | legacy CSV |

### 3.2 ETS2 (best reference implementation)

Base path: `<B>/Euro Truck Simulator 2 Supporter/ETS2AIDataCollector/ETS2AIDataCollector/`

**Pose and coordinates**
- The official `scs-telemetry.dll` plugin publishes shared memory, read through `SCSSdkClient`.
- Position is the head position in world space, falling back to the truck (`Services/Recorder.cs:461-493`).
- Rotation is the **truck** heading/pitch (`Recorder.cs:481-482`). Head-look and exterior cameras are ignored, and roll is discarded (`Services/Coords.cs:10-11`).
- `Coords.cs:15-18` maps SCS (x, y-up, z-south) → RFU `(x, −z, y)`. The "left-handed" comment at line 5 is wrong, but the maths is correct.
- `BuildC2W` (`Coords.cs:38-69`) builds `[right | up | back | pos]` with `right = f × ẑ`, `up = r × f`. Intrinsics follow the spec formula (`Coords.cs:72-81`).
- **Bug:** per-row intrinsics use the INI width/height (`Recorder.cs:502`), while `session.json` uses OBS's (`Recorder.cs:295-301`).

**Sync**
- Recording starts with `StartRecord`, then polls `GetRecordStatus` every 10 ms until `outputDuration > 0`, and only then starts the `Stopwatch` (`Recorder.cs:145-158`). This is the only collector with head-of-stream gating.
- The SDK is polled every `1000/fps` ms (`Recorder.cs:115`). It is not tied to the game frame.
- `FrameAccumulator.cs` resamples to a synthetic grid `k·1000/fps` (`:62-66`). It takes the union of keys, sums the deltas and keeps the latest camera. When ticks are late it emits duplicate camera rows. `Flush` is never called.
- The last `StopTrimMs` = 1000 ms is dropped to remove the stop key-press (`Recorder.cs:43-48, 228`).
- The video is trimmed with `ffmpeg -t … -c copy` (`VideoTrimmer.cs:41-62`). Stream copy cuts at keyframes, so the trim is inexact.

**Input:** Raw Input mouse with `Interlocked` accumulation (`MouseTracker.cs:72-128`). Keys use `GetAsyncKeyState` at tick rate (`Recorder.cs:545-573`). The key → action map is `KeyBindings.cs:9-29`.

**Output and self-validation**
- Output folder: `Documents/AIDataCollector_ETS2/<uuid>/`.
- The validator runs on stop (`SessionWriter.cs:136-339`). Checks S2, S3 and S5 are hard-coded to pass (`:287-318`).

**Install and launch**
- The launcher extracts the SDK plugin into `bin\win_x64\plugins\` (`ETS2 Launcher/…/MainWindow.xaml.cs:121-189`).
- The collector re-implements this with Steam registry and `libraryfolders.vdf` detection (`Services/SdkPluginInstaller.cs:29-139`).
- Hotkeys are F6/F7/F9/F10. There is no upload.

### 3.3 Fallout 4

Base path: `<B>/New folder/Fallout4AIDataCollector/src/`

**Lineage.** Fallout 4 started as a near-copy of Skyrim, which was itself ported from GTA5 (`<B>/Skyrim Script Installer/SkyrimAIDataCollector/README.md:4`). The May build is a rewrite. The April build (`<S>/SourceAICollector/F4src_apr10`) wrote the raw engine matrix and had a memory-scan fallback; both were removed in May.

**Pose and FOV**
- Pose (`Recorder.cpp:119-145`) mixes two sources:
  - position comes from the camera root node;
  - rotation comes from the **actor's** `data.angle`.
- This mix is wrong in third person, in furniture or terminals, during kill-cams and with head-bob.
- `Coords.cpp:7-47` builds forward from yaw/pitch, then `[r | u | −f | p]`. It is correct and gravity-aligned. Because roll is never written, the m20/m21 checks pass by construction.
- **FOV:** `ComputeIntrinsics(fovVertical…)` (`Coords.cpp:49-54`) is fed `worldFOV`, which is horizontal in Bethesda engines.
  - `Test/*/frames.csv` shows `fy = 771.2` at 70°/1080p.
  - If 70° is horizontal, the true value is `fx = fy ≈ 960/tan 35° ≈ 1371`.
  - **Confirm with a reprojection test before trusting any Fallout 4 data.**
- Intrinsics resolution mismatch: per-row values use the INI width/height (`Recorder.cpp:186`), while `session.json` uses OBS's (`Recorder.cpp:364`). This is the same bug as ETS2.
- `intrinsics_at_min_fov` uses only `fovMin` (`:412`).
- **Units** are Bethesda units (≈1.43 cm). No scale is declared.

**Input**
- Mouse uses `MouseTracker.cpp:12-26` (`GetCursorPos` diff). The game recentres and clips the cursor, so the output is mostly 0 with spikes. In `Test/106c1341` the values are only positive, up to 97 px/frame.
- `KeyBindings.h:11-34` is a hard-coded default map that ignores user rebinds. X2 maps to "Forward", the same as W. `key_count: 13` is hard-coded (`SessionWriter.cpp:121`).

**Sync:** OBS `StartRecord` is sent on a detached thread *after* the clock starts (`Recorder.cpp:293-299`), so the start offset is uncorrected. The `outputPath` is moved into the session with retries, and the end is trimmed (`Recorder.cpp:304-403`, `VideoTrimmer.cpp:83-131`).

**Version and tests**
- Only runtime 1.10.163 is supported (`Plugin.cpp:18-35`). That is the pre-next-gen build used by the "Fallout London" mod.
- CommonLibF4 is pulled at `GIT_TAG master` (`CMakeLists.txt:27-28`), i.e. unpinned.
- `CMakeLists.txt:33-42` patches CommonLibF4 for a cracked executable. **This is a compliance red flag; out of scope.**
- `tests/test_coords.cpp` copies the maths instead of linking `Coords.cpp`.

**Install (two steps)**
- Step 1, `installer_src/Program.cs`:
  - runs elevated;
  - searches hard-coded C:/D:/E: paths, including "Fallout London" folders, with no Steam lookup;
  - cleans VirtualStore and makes a backup;
  - copies the plugin, ini, `fmt`, `spdlog`, `minhook` and `ffmpeg.exe`.
- Step 2, the WPF launcher: extracts F4SE and runs `f4se_loader.exe`.
- The user configures OBS manually (`<B>/Fallout 4 Script Installer/New folder/HUONG_DAN.txt`).

**Extras**
- An ImGui menu through a D3D11 `Present` hook (`D3DHook.cpp:270-348`). The hook is used only for the menu and the tick, not for capture.
- Cheats: god mode, speed, carry weight, one-hit kill (`Cheats.h:6-14`).
- A "Beacon" sync flash appears in the INI/README but is **not implemented**.

### 3.4 Skyrim

Base path: `<B>/Skyrim Script Installer/SkyrimAIDataCollector/src/`

**Pose**
- Main-update hook via Address Library + trampoline (`Plugin.cpp:131-140`).
- Writes `cameraRoot->world.rotate` directly as C2W (`Recorder.cpp:125-129`), with no RUB conversion.
- Treats column 2 as forward (`:131-136`), which is the wrong axis for a Bethesda node. This is the source of the "m21 < 0 on 53% of frames" legacy bug that the QC Tool's TC6 targets.

**FOV:** taken from INI `fDefaultWorldFOV`, labelled vertical (`:29-39, 173`).

**Sync:** paused frames are skipped while OBS keeps recording (`:273-275`), so the CSV and video drift apart.

**Install:** the WPF installer picks SKSE 1.5.97 or 1.6.1170 by exe version and installs Address Library bins. The README claims "1.6.1179+", which disagrees. One bundled `.bin` has a 2026 date unlike the rest, so it is possibly modified.

### 3.5 GTA5

Base path: `<B>/GTA5 Script Installer/GTA5_Script_Installer/GTA5Recorder/Class1.cs`

**Pose and FOV**
- Uses `GameplayCamera.Position/Rotation` and `GET_GAMEPLAY_CAM_FOV`, which is vertical and correctly labelled (`:952-954`).
- `MTX.Build` (`:214-229`) lays out the rotation for Y-up while the translation is Z-up. At yaw = pitch = 0 the camera "looks down".
- The correct helper `Rot2Dir` (`:231-236`) exists but is unused.
- Scripted and cinematic cameras are ignored.

**Input:** mouse from `GET_CONTROL_NORMAL` LookLeftRight/UpDown (`:1264-1265`). It is normalised, not counts. Of the three Fallout 4 / Skyrim / GTA5 collectors, this is the best mouse source.

**Other issues**
- The CSV is buffered in a `StringBuilder` until stop, so a crash loses the session.
- UI strings are "RC4"-obfuscated with the key in the file (`:24-146`). This adds opacity without any security.
- Targets `net9.0-windows`, while SHVDN3 hosts .NET Framework 4.8. **Loading is unverified.**
- Cheats: god mode, noclip, super speed/jump, never wanted, vehicle spawn, weather/time.
- `zips/ScriptHookV.zip` bundles NativeTrainer and an anti-cheat-related `args.txt` (out of scope).

### 3.6 Cyberpunk 2077

Base path: `<S>/cp77/` (extracted from the zip).

**Pose (`cet/.../mods/collector/init.lua`)**
- Forward comes from `GetCameraSystem():GetActiveCameraForward()`.
- **Position is player + 1.8 m**, not the camera (`:252-277`).
- `BuildCameraToWorldMatrix` puts +forward in column 2 (`:289-295`, verified), so **det = −1**.
- FOV comes from the settings variable, polled every 2 s (`:237-247`). ADS and zoom changes are missed.

**Sampling:** limited to ≤ 60 Hz per `onUpdate` (`:493-495`) on `os.clock()`.

**IPC and input**
- Lua and C# talk through `sync.txt`/`command.txt` file IPC, polled every 30 ms (`Cyberpunk 2077 Supporter/MainWindow.xaml.cs:531-558, 217-261`).
- C# runs its own 60 Hz key loop (`:693-746`) and **merges by frame_id across two independent clocks** (`:769-826`). This guarantees drift whenever fps < 60.
- `_inputFrames` is accessed from several threads with no lock.

**Install:** overwrite-extracts the full CET into the game root (`:382-441`). Uninstall deletes recorded sessions.

**Context:** spec v2 §2.5 already flags a ~110 h Cyberpunk sync incident ("wall time from game clock"). The QC Tool encodes it as G10.6.

### 3.7 The Witcher 3

Base path: `<S>/w3/The witcher 3 Supporter/The witcher 3 Supporter/`

**Pose**
- Uses the true camera API: `theCamera.GetCameraPosition/Direction/GetFov` on a 1 ms timer (`Files/aiDataCollector.ws:199-225`, `Files/playerWitcher.ws:445, 11888-11891`).
- Same det −1 column bug as Cyberpunk (`aiDataCollector.ws:220-222`).

**IPC:** `LogChannel` lines are tailed from `scriptslog.txt` (`MainWindow.xaml.cs:879-1045`). This requires `-debugscripts` and INI edits (`:588, 708-733`).

**Install**
- **Overwrites vanilla `playerInput.ws`/`playerWitcher.ws`**, with backups (`:540-573`). This conflicts with other mods and breaks on game patches.
- Patches `input.settings` (`:745-766`).
- Script variables persist into savegames (`playerWitcher.ws:149-152`).

**Extras:** an "UnlockAll" that adds quest facts and 50k gold (`aiDataCollector.ws:248-264`), plus 90+ teleports.

### 3.8 Dying Light 2

Base path: `<B>/Dying Light 2 Supporter/DL2AIDataCollector/`

**Pose**
- The current `DL2AIDataCollector/Recorder.cs:180-207` writes an identity rotation and zero translation, expecting SLAM to fill them in.
- The pose bridges (DLL injection, Direct3D 12 hooks, heap scanning, Cheat Engine) are **out of scope**.
- Even when the bridge is active, the matrix is Y-up and not RUB (`:196-206`).

**SLAM (`SLAMProcessor/dl2_slam.py`)**
- Seeks OpenCV frames at random (`:70-96`), which is inaccurate.
- Runs VGGT-1B on all frames in one batch (`:99-138`), which runs out of memory beyond a few hundred frames.
- Writes `inv(w2c)` in RDF convention with first-frame origin and arbitrary scale (`:141-160`). The VGGT intrinsics are discarded.
- **Per v2 and software-plan §1 ("logged, not inferred"), this is not deliverable.**

**Compliance:** the INI hard-codes `D:\DL2\…` and mentions a SmartSteamEmu launcher (`DL2AIDataCollector.ini:31-34`), which indicates a non-legitimate game copy.

### 3.9 Cross-cutting

**Code duplication**
- The OBS client and its auth handshake exist in ≥ 6 copies (C++ ×2, C# ×4).
- The INI loader, key tables, yaw-diff proxy, CSV header and plugin installer are copy-pasted across games.
- Fixes such as `FrameAccumulator`, `SessionWriter` and the validator reached only ETS2 and Fallout 4.

**Robustness**
- Sessions are buffered in memory until stop (GTA5, Cyberpunk, Skyrim), so a crash loses the session.
- No collector checks the foreground window or `LLKHF_INJECTED`.

---

## 4. QC Tool vs our validator

**What it is.** `<B>/QC Tool/qc_validator.py` targets **Spec v1** (`:6`, `rules_vi.md` title).
- It has 10 check groups and 4 severity levels: BLOCKER, MAJOR, MINOR, INFO.
- It supports `--strict`, `--batch`, `--csv-only` and JSON output.
- There is a Tkinter GUI with drag-drop (`qc_gui.py`, with the worker thread at `:344`).
- The PyInstaller exe bundles ffprobe (`build_exe.cmd`, `_find_ffprobe` at `qc_validator.py:243`).

**Doc drift**
- README `TOL_VIDEO_SYNC_S = 0.05` vs code `0.15` (`:140`).
- `checks_inventory.json` says `total_checks: 55` but lists 70.

**Its rule list, by group:**

| Group | Rules |
|---|---|
| **S1** files | S1.1, S1.3–S1.6 |
| **J2** v1 session schema | `axis_convention == "RFU"`, `world_coordinate_system`, vendor placeholder (J2.1), UUID v4 (J2.8), `frame_columns` == header (J2.5), `has_*` vs CSV, `duration_match` |
| **J3** axis convention | `column_basis` (`:686-735`) and `axis_convention_data` 3-way check (`:737-800`) |
| **C3** CSV schema | 23 required columns, snake_case, order, no unknown columns, frame_count |
| **F4** matrix | det, orthonormal, m20, m21 > 0, bottom row, ids, monotonic, gap 0.7–1.3× |
| **M5** forward vs velocity | `:946-1082` |
| **I6** intrinsics | I6.1–I6.9 |
| **N7** input | N7.1/2 "NONE", N7.3 delimiter, N7.5 actions = bindings(keys), N7.7 mouse pixel-int (`:1280-1314`), N7.8 motion ratio |
| **V8** video | duration ±0.15 s, `r_frame_rate` ≈ fps, resolution, codec |
| **K9** | `key_binding.json` |
| **G10** per-game | `GAME_CONFIGS` (`:79-122`): game type, `c2w_m23` height range, FOV range, Cyberpunk-before-2026-04-06 warning |

### 4.1 Checks they have that we lack (worth porting)

| Their ID | Check | Value for us | Suggested rule id |
|---|---|---|---|
| **M5.1/M5.2** | Forward (−col2) vs velocity over a 10-frame lag. Adaptive speed threshold (median × 0.3). Reclassifies as intentional backward via `S`/`Backward` actions or `vehicle_gear < 0`. Thresholds per game type: driving ≤ 5% inverted, FPS ≤ 10% (`:946-1082`) | **High.** Catches forward-sign and axis errors that det/orthonormal can't (ETS2 bug TC2). Needs `input_actions`, which v2 has | `c2w.forward_vs_velocity` (warning → error per game type) |
| **J3.column_basis** | mean\|m21\| ≈ 1 and mean\|m22\| ≈ 0, otherwise the "RFU columns" bug (`:686-735`) | **High.** Cheap; catches col1/col2 swaps that keep det = +1 | `c2w.column_basis` |
| **J3.axis_convention_data** | Up axis by smallest position range vs camera-up axis (`:737-800`) | Medium. Catches Y-up position with Z-up rotation (the GTA5 bug) | `c2w.world_up_consistency` (warning) |
| **N7.7** | Mouse delta max < 1.5 means a proxy (angular/normalised); non-integer flagged (`:1280-1314`) | **High.** Detects the yaw-diff proxies used by 4 of 7 collectors. Keep magnitude as a warning; drop the int requirement (v2 says float, unit unspecified) | `input.mouse_delta_magnitude` (warning) |
| **N7.1/N7.2** | `"NONE"` sentinel in keys/buttons | **High.** Our `_check_input` misses `input_mouse_buttons == "NONE"` (`validate.py:180-188`). v2 §2.3 names "NONE" as legacy GTA format | add to `input.no_input_null` / new `input.null_sentinel` |
| **N7.3** | `+` as delimiter | Medium. We check only `,` and empty tokens (`validate.py:186`). Note the v2 example `W\|+\|Shift` treats `+` as a key, so make it a **warning** only when `+` is glued to other text (`W+Shift`) | extend `input.delimiter` |
| **V8.2** | Video `r_frame_rate` ≈ `session.fps` | Medium. We don't check stream fps; VFR/CFR mismatches are common with OBS | `video.fps` |
| **V8.4** | Codec h264/hevc | Low (warning) | `video.codec` |
| **I6.6** | `camera_model` uniform across frames | Low | `camera.model_uniform` (warning) |
| **I6.7** | Dynamic-FOV detection (info) | Low; useful in the QA report | info |
| **I6.9** | FOV from `fy` and **ffprobe height** vs declared | Partly covered by our `camera.principal_point` against `record_*_px` + `video.resolution`. Skip | — |
| **G10.4/G10.5** | Per-game `c2w_m23` height range and FOV range | Medium. Spec v1.5.3 asks for a "reasonable height". Put it in per-game profile config (fits our Lua-profile design) | `c2w.height_range`, `camera.fov_range` (warning) |
| **G10.6** | Cyberpunk pre-2026-04-06 wall-clock warning | Low; pattern of known-incident rules | — |
| **J2.1 / J2.8** | Vendor placeholder; session_id UUID v4 | Low; cheap | `session.vendor_placeholder`, `session.id_format` (warning) |
| Severity model | BLOCKER/MAJOR/MINOR/INFO + `--strict`, `--batch`, GUI, bundled ffprobe, details dict per check | Medium. `--batch` and a per-check details payload help production QA (software-plan §7.8) | CLI |
| Forward thresholds by game type | `FORWARD_THRESHOLDS` (`:124-131`) | Put in game profile | — |

### 4.2 Checks we have that they lack

These are mostly v2-specific. Their tool would **pass sessions Odyssey rejects.**

| Our rule (`validate.py`) | Gap in their tool |
|---|---|
| `session.schema` (v2 JSON Schema: `vendor_name`, `game_title`, `created_at_utc`/`ended_at_utc`, `record_*`/`screen_*` px, `localization`, `platform`, `input_mouse_convention` + segments if/then rules) | Validates the v1 schema only |
| `session.timezone`, `session.end_after_start`, `session.duration_units`, `session.duration_timestamps`, `session.frame_count_fps` (`:58-76`) | v1 has none of these fields |
| `frames.headers` incl. **input columns and 8 distortion columns required** (`frames.py:12`) | Treats input columns as optional; doesn't require distortion headers (`qc_validator.py:50-66`). **Rejects unknown columns** (C3.4), whereas v2 doesn't forbid extras |
| `frames.timestamp_start` (≈ 0), `frames.timestamp_end` (≈ `duration_ms`) (`:102-106`) | Only `duration_match` on `last − first` |
| `camera.sparse_nulls` (`:123-125`) | No null semantics at all |
| `input.keys_without_actions`, `input.no_input_null` (`:181-184`) | N7.5 needs `key_binding.json`, which v2 removed |
| `input.mouse_delta` (`"0.0"` not null) | Not checked |
| **`video.frame_count` exact** (`:227-228`) | **Missing.** They compare duration only. The `Test/` sessions have video = rows + 1 and pass their V8.1 (0.055 s < 0.15 s) |
| `video.duration` vs `duration_seconds` | Compares against the CSV span instead |

**Thresholds differ:**

| Threshold | Theirs | Ours |
|---|---|---|
| m20 | 1e-2 (`TOL_M20`) | 0.05 |
| Principal point | 1 px | 4 px |
| Frame gap | 0.7–1.3× | max(1 ms, 25%) |

All are reasonable. Their m20 at MAJOR is stricter than ours (warning). v2 says "near zero for level cameras", so ours as a warning is right.

---

## 5. Tbrain RRD Tools and `validate_session.py`

**`build_rrd_tbrain.py`** is the legacy loader plus the renderer.
- Reads the PascalCase legacy CSV and converts it on load (`:168-221`):
  - FOV + axis → fx/fy (`_intrinsics_from_fov`, `:157-165`, handles both axes correctly);
  - `"NONE"`/`+` → `|`;
  - normalised mouse → px.
- Pairs CSV and MP4 by a filename-stem heuristic (`:140-155, 224-238`).
- `build_rrd` (`:338-612`) writes:
  - `ViewCoordinates.RFU` world;
  - `AssetVideo` + `VideoFrameReference` aligned on **video PTS** in `session_time`;
  - 3D trajectory coloured by speed, forward arrows, a 2D top-down map;
  - time series for speed, FOV, angular velocity, acceleration, mouse dx/dy and input density;
  - teleport/anomaly points, a key overlay, a `TextLog` of input changes;
  - a 3-column blueprint.
- Marker sizes scale with the trajectory span, so it works for both cm and m worlds.

**`build_rrd_v1.py`** is a v1 loader that imports `build_rrd_tbrain` (`:28-32`). The output is named `<first-8-of-uuid>.rrd`, not `session.rrd`.

**Conformance with v2 §1.4**
- ✅ Video plus frame-synced metadata.
- ❌ The deliverable `rrd_creation.py` must be one script; this is two files with a `sys.path` hack.
- ❌ No v2 loader (session fields differ).
- ❌ No `rr.Pinhole`/`Transform3D` per frame, so the camera frustum and intrinsics can't be inspected against video.
- ❌ Output name.
- ⚠️ Video is time-aligned by PTS while CSV rows use `timestamp_ms`. The off-by-one frame in §6 would show as an unnoticed ~16 ms skew.
- ⚠️ The whole MP4 is read into memory; there is a 2 GB warning (`:383-388`).

**`validate_session.py`** is a 17-check subset of the QC Tool for v1:
- vendor and axis strings;
- frame_count;
- det/ortho/m20/m21;
- forward-vs-velocity (> 10% inverted fails, no backward reclassification, `:93-126`);
- video/CSV duration ±0.05 s (`:128-148`);
- key_binding coverage.

Its problems:
- Fails fast on the first error (`sys.exit` in `fail()`).
- Crashes with a `KeyError` if input columns are absent (`:153`).
- Uses `int()` on `timestamp_ms` (`:129`), which breaks on float timestamps.
- Its 0.05 s tolerance **would fail** the `Test/` sessions (diff 0.055 s), contradicting the QC Tool's 0.15 s.

**Overlap with ours:** our validator supersedes it for v2. Only the forward-vs-velocity idea is worth keeping, and the QC Tool's M5 is the better version.

**Recommendation:** treat these tools as the existing RRD builder (per user memory). **Do not rebuild.** Ask the owner for:
1. a v2 loader;
2. a single-file `rrd_creation.py`;
3. per-frame `Pinhole` + `Transform3D` (RUB → `ViewCoordinates.RUB`) logged under `world/camera`, so the video projects into 3D.

---

## 6. Output conformance vs Odyssey spec v2

Based on the Fallout 4 May / ETS2 writers (`SessionWriter.cpp:10-128`, `SessionWriter.cs:29-113`) and the `Test/` samples.

### 6.1 `session.json`

| v2 field | Their output | Status |
|---|---|---|
| `vendor_name` | `vendor: "Tbrain"` | ❌ rename |
| `game_title` | `game: "Fallout4"` | ❌ rename; use the human title |
| `session_id` | UUID v4 | ✅ |
| `created_at_utc` / `ended_at_utc` | only `generated_at` (written at end) | ❌ missing; need record start/end in UTC |
| `duration_ms` | int (`4499` = last row ts) | ⚠️ present; equals `(N−1)/fps`, not the full duration (`N/fps` = 4517) |
| `duration_seconds` | — | ❌ |
| `fps`, `frame_count` | ✅ | ✅ (271 vs 60 × 4.499 = 269.9, within 2) |
| `record_width_px/height_px` | `video.width/height` (nested) | ❌ move to top level |
| `screen_width_px/height_px` | — | ❌ |
| `localization` | — | ❌ |
| `platform` | — | ❌ (`"PC"`) |
| `input_mouse_convention` (+ segments) | `input.mouse_drives_camera` bool | ❌ needs `maps_to` + 4 directions. ETS2 = `vehicle_steering`/`none`; Fallout 4 = `camera_look_velocity`, right/left, down/up (Test data: dx>0 ↔ yaw right, corr ≈ −0.7…−0.9 on yaw angle, so direction is consistent) |
| extras (`schema_version`, `axis_convention`, `camera{}`, `input{}`, `frame_columns`) | present | allowed (`additionalProperties: true`) |

### 6.2 `frames.csv`

| v2 requirement | Their output | Status |
|---|---|---|
| Column names | lowercase `c2w_m00`… | ✅ same as ours (spec §1.1.3 shows `C2W_M00`; open question for Odyssey, shared with us) |
| All headers incl. `camera_radial_k1..k6`, `camera_tangential_p1..p2` | omitted | ❌ add null columns |
| Null encoding | empty string | same as ours (open question) |
| `frame_id` 0..N−1 | ✅ | ✅ |
| `timestamp_ms` | integer ms on a synthetic grid (16/17 alternating) | ✅ monotonic; ✅ ts[0] = 0 |
| C2W RUB, det +1 | Fallout 4 / ETS2 ✅ (det ∈ [0.999999, 1.000001], m20 ≡ 0, m21 ≥ 0.978) | ✅ numerically; ❌ semantically (actor/truck rotation; roll dropped) |
| Intrinsics per frame | ✅ constant | ❌ Fallout 4 / Skyrim FOV axis likely wrong; INI vs recorded resolution |
| `input_keys`/`input_actions` pipe, null when none | ✅ pipe, empty | ✅ |
| `input_mouse_buttons` | ✅ | ✅ |
| `input_mouse_dx/dy` `"0.0"` | `0.0000` | ✅ value; ❌ source (Fallout 4 cursor-diff, others proxy) |
| Legacy collectors (GTA5/Skyrim/Cyberpunk/Witcher 3/DL2) | `Frame_ID…FOV_Deg,FOV_Axis,Keyboard_Input("+"/"NONE")` | ❌ entirely; needs conversion à la v2 §2.3, plus fixing det −1 |

### 6.3 `video.mp4`

| Check | Their output | Status |
|---|---|---|
| Resolution | 1920×1080 h264 60/1 | ✅ |
| Frame count == rows | 272 vs 271, 19 vs 18, 354 vs 353 | ❌ **systematic +1**. Trim is `duration + 1 frame` with stream copy (ETS2 `VideoTrimmer.cs:41-62`; Fallout 4 `VideoTrimmer.cpp:83-131`) |
| Duration | 4.554 s vs 4.499 s | ✅ within 1 s |

**Unverifiable statically:** the true start offset between OBS's first frame and CSV t = 0 in Fallout 4, Skyrim, GTA5, Cyberpunk and DL2. With the open-loop start this can be tens to hundreds of ms. That is the same class of bug as Odyssey's Cyberpunk incident.

### 6.4 Missing deliverables

- `session.rrd` and `rrd_creation.py` (see §5).
- R2 upload path `<vendor>/<mm-dd-yyyy>/<game>/<session-id>/`: no uploader exists here. Per memory, ours is elsewhere.

---

## 7. Strengths worth adopting

**Data-path patterns**

1. **Gate the clock on OBS's first frame.** ETS2 `Recorder.cs:145-158`: start the stopwatch only once `GetRecordStatus.outputDuration > 0`. This is a cheap fix for start offset. Our WGC recorder with per-frame QPC is better, but it is a useful fallback if we ever drive OBS.
2. **Fixed-grid resampler.** ETS2 `FrameAccumulator.cs` and Fallout 4 `FrameAccumulator.cpp:12-51`: union of keys, sum of mouse deltas, latest camera. This is the same contract as our `frames.aggregate_inputs`. Use their tests as cross-check fixtures.
3. **Rebuild C2W from forward + world-up** rather than trusting engine matrices (`Coords.cpp:17-47`, `Coords.cs:38-69`). This matches v2 §2.2. Keep roll only where the engine has it.
4. **Stop-gesture trim.** Drop the last `StopTrimMs` of rows and video so the stop hotkey isn't in the data (ETS2 `Recorder.cs:43-48`).
5. **Validator inside the recorder, run on stop** (`SessionWriter.cs:136-339`). This gives gamers immediate feedback. We should call our Python validator from the Capture Agent post-stop.

**QA tooling**

6. **QC Tool semantic checks**: M5, J3, N7.7, N7.1, V8.2, G10 (see §4.1), plus `--batch`, severity levels, JSON details and the GUI.
7. **RRD visual QA layout** (`build_rrd_tbrain.py:338-612`): trajectory map, speed/FOV/angular-velocity series, teleport and anomaly markers, input text log. This is good for the ~1% human spot-check in software-plan §7.10.6.

**Install and capture plumbing**

8. **Installer hardening** (Fallout 4 `installer_src/Program.cs`): refuse while the game runs, back up the plugins folder, preserve the user INI and rewrite only targeted keys, clean VirtualStore, put dependency DLLs in the game root. Also ETS2's Steam registry + `libraryfolders.vdf` discovery (`SdkPluginInstaller.cs:29-139`).
9. **Raw Input with `RIDEV_INPUTSINK`** (ETS2 `MouseTracker.cs:72-128`). Same approach as our odx-input; confirms the choice.
10. **Per-game API choice.** Witcher 3's `theCamera.*` and GTA5's `GameplayCamera` + `GET_GAMEPLAY_CAM_FOV` are the right official data sources. If these titles become Path-A/B candidates, keep the data source and discard the matrix code.

---

## 8. Weaknesses and risks

**Correctness**
- det −1 matrices (Cyberpunk, Witcher 3, DL2).
- Non-RUB matrices (Skyrim, GTA5).
- Actor/vehicle rotation used instead of the camera (Fallout 4, ETS2).
- Player + 1.8 m used as the camera position (Cyberpunk).
- Likely horizontal FOV labelled vertical (Fallout 4, Skyrim).
- Constant, unverified FOV (ETS2 75°, DL2 60°).
- Intrinsics from INI resolution rather than the recorded one.
- Proxy mouse deltas in 5 of 7 collectors.

**Sync**
- Open-loop OBS start in 6 of 7 collectors.
- Merging two independent clocks by frame_id (Cyberpunk, Witcher 3).
- Paused frames dropped while video continues (Skyrim).
- Stream-copy trim, giving the +1 frame.
- Telemetry polled by timer, not by `Present`. Software-plan §1 warns against this: no frame lock, torn reads.
- DL2's seqlock is never checked.

**Code quality**
- ≥ 6 copies of the OBS client; copy-pasted INI, key tables and installers.
- Fixes applied to only 1–2 games.
- Unsynchronised cross-thread lists.
- Whole-session in-memory buffers.
- Dead config (`EnableBeacon`), empty `Teleport.cpp`, tests that copy instead of linking.
- Unpinned `GIT_TAG master`.
- GBs of build artifacts and `.bak` exes mixed with source.
- Doc drift (thresholds, versions).

**Hard-coded paths**
- `C:\Users\PC\…` in `.cmd` and `.ini` files (`Tbrain RRD Tools/*.cmd`, `QC Tool/build_exe.cmd`, `New folder/…/Fallout4AIDataCollector.ini:33`).
- `D:\source\repos\…` in the GTA5 csproj, `D:\DL2\…` in the DL2 INI.
- The Fallout 4 installer's C:/D:/E: list.
- `.claude/settings.local.json` in QC Tool, RRD Tools, ETS2 and Fallout 4: they leak local paths and project names, but **no secrets**.

**Secrets:** none found (sweep covered `.cs/.cpp/.h/.lua/.ws/.ini/.json/.txt/.md/.ahk/.py/.cmd`). OBS passwords are user-entered.

**Legal and compliance**
- **Out of scope, not detailed:**
  - the cracked-exe support patch (Fallout 4 `CMakeLists.txt:33-42`);
  - SmartSteamEmu reference (`DL2AIDataCollector.ini:33`);
  - anti-cheat-related `args.txt` + NativeTrainer in `GTA5_Script_Installer/zips/ScriptHookV.zip`;
  - DL2 injection and Cheat Engine bridges.
- Redistribution of F4SE, SKSE, Address Library, ScriptHookV, CET and ffmpeg without licence review.
- Overwriting vanilla Witcher 3 scripts.
- Version lock to the Fallout 4 1.10.163 / "Fallout London" build.

**Gameplay contamination**
- God mode, noclip, super speed, teleport and one-hit kill menus (Fallout 4 `Cheats.h:6-14`; GTA5; Witcher 3 UnlockAll) change the motion and interaction distribution.
- None of this is recorded in `session.json`.

**Bot automation**
- `Auto Fallout 4.ahk` (AHK v2, active only when Fallout 4 is in the foreground):
  - toggles a constant-rate synthetic camera turn (relative mouse events at a fixed small step and period);
  - latches movement keys;
  - runs a scripted attack/reload/walk loop.
- No collector rejects injected input (`LLKHF_INJECTED`/`RI_MOUSE` source), so these sessions are **indistinguishable from human play in the CSV**. Constant dx and fixed-period key runs would bias a world model and may breach Odyssey's human-gameplay expectation.
- Treat any session from this tooling as suspect unless it is filtered.

**Scope**
- Everything is **single-player only**. There is no server state, no multi-POV sync and no cross-agent anything, so nothing here covers Round 4 multiplayer requirements.

---

## 9. Recommendations for odyssey-capture

### P0 — validator rules to add (cheap, high value; Layer-2 fixtures per software-plan §7.10.2)

1. **`c2w.forward_vs_velocity`**: port M5 (`qc_validator.py:946-1082`).
   - Includes the adaptive threshold and backward reclassification via `input_actions`/`input_keys` and an optional vehicle-gear column.
   - Thresholds per game type come from the game profile.
   - Severity: warning by default, error for driving sims.
2. **`c2w.column_basis`**: port J3 (`:686-735`). Error when mean\|m22\| ≈ 1 and mean\|m21\| ≈ 0.
3. **`input.null_sentinel`**: flag `"NONE"`/`"none"`/`"null"` strings in keys/actions/buttons. This closes our current gap for `input_mouse_buttons`.
4. **`input.mouse_delta_magnitude`** (warning): fires when max\|dx\|, \|dy\| < 1.5 over a session with camera rotation. This catches proxy deltas.
5. **`video.fps`** (r_frame_rate/avg_frame_rate vs `fps`, and a VFR flag) and **`video.codec`** (warning).
6. Add a **fixture for the +1 video-frame case**, using a trimmed copy of `Test/106c1341…` converted to v2. Use it as a regression test that `video.frame_count` catches what the QC Tool misses.

### P1 — validator ergonomics and profile-driven sanity

7. `--batch` mode over a delivery folder.
8. A per-issue `details` payload (frames, stats) in the JSON for the QA console.
9. Per-game profile ranges (`c2w_m23` height, FOV range, forward thresholds), reused from `GAME_CONFIGS` (`:79-122`) as starting values. Note Bethesda units are cm-scale.
10. A **bot/injection heuristic** (warning): long runs of identical non-zero `input_mouse_dx`, or perfectly periodic key toggles. Pair it with odx-input recording the `LLKHF_INJECTED` / Raw Input device handle and a `session.json` extra such as `input_injected_frames`.

### P1 — capture

11. Keep our design: WGC per-frame QPC plus `Present`-synchronised extraction. **Do not adopt OBS open-loop.** If OBS is ever used, copy the ETS2 first-frame gate and re-encode or timestamp-cut instead of `-c copy`.
12. Use a **render-camera source, never actor/vehicle rotation**, and assert that the FOV axis comes from the profile. Add Layer-3 marker reprojection specifically for Bethesda FOV-axis semantics before any Fallout 4 / Skyrim work.
13. Record a **`gameplay_modifiers`** extra in `session.json` (cheats, teleports, speed multipliers), or forbid such modifiers during capture.

### P2 — reuse and porting

14. **RRD:** do not rebuild. Request a v2 loader, a single-file `rrd_creation.py`, and per-frame `Pinhole`/`Transform3D` from the owner. Our pipeline invokes it.
15. **ETS2:** if ETS2/ATS is shortlisted, reuse `Coords.cs` + the SDK path (official telemetry, low legal risk) but switch rotation to head/camera, add a real FOV source, and fix the INI-vs-recorded resolution.
16. **Legacy data:** any sessions already captured with GTA5, Skyrim, Cyberpunk, Witcher 3 or DL2 need a conversion pass. Converting means:
    - rebuilding C2W from the forward vector (column 2 = −forward);
    - fixing the FOV axis;
    - pipe-delimiting inputs;
    - writing the v2 `session.json`.
   Do not deliver them as-is.
17. **Avoid outright:**
    - DL2 SLAM (inferred pose violates "logged, not inferred");
    - memory bridges and Cheat Engine;
    - overwriting vanilla game scripts;
    - bundling trainers or cracked-exe patches;
    - in-memory session buffering.

### Multiplayer Round 4 — patterns

- Nothing here is multiplayer. Transferable bits:
  - the per-game official-API extractor approach (Script Hook / CET / SKSE-style reflection maps to our Path-A/B tiering);
  - file/WebSocket IPC patterns between an in-game script and a companion app;
  - the installer UX.
- Cross-POV sync and server state must come from our own design (QPC + `clock_samples.csv`, server plugins, flash-event test).
- Don't inherit the frame-id merge across clocks; it is exactly the failure mode multi-client sync must avoid.

---

## 10. Open questions for the author

1. **Fallout 4 / Skyrim FOV:** was `worldFOV` validated as vertical (e.g. by reprojection)? Our analysis says Bethesda FOV is horizontal, so the delivered `fy` would be about 1.8× too small.
2. Why use **actor `data.angle`** rather than the camera node rotation (Fallout 4), and **truck heading** rather than head/camera (ETS2)? How much delivered data is third-person or uses the interior head-look?
3. Were any **legacy-format** sessions (det −1 Cyberpunk/Witcher 3, GTA5 Y-up/Z-up mix, Skyrim raw matrix) delivered to Odyssey? Which batches, and were they converted?
4. What measured **start offset** is there between OBS's first frame and CSV t = 0 on Fallout 4/Skyrim/GTA5? Do you have the ETS2 "Sync diag" logs?
5. Why does every `Test/` session have **video = rows + 1**? Is it deliberate (trim to duration + 1 frame)?
6. Was `Auto Fallout 4.ahk` used during **production** capture? If so, which sessions? Were cheats (god mode, speed, noclip, teleport) active during delivered sessions?
7. Which game copies and builds were used? (Fallout 4 1.10.163 / "Fallout London", the DL2 launcher reference, the cracked-exe patch.) **Legal needs this before any reuse.**
8. Does the **RRD builder in use** match `Tbrain RRD Tools`, or is there a newer v2 version? Who owns it, and can it emit `Pinhole`/`Transform3D` and a single-file `rrd_creation.py`?
9. Where are `QC_TOOL_SPEC.md` and `BUGFIX_CAPTURE_TOOL.md` (referenced in `qc_validator.py:9` and `validate_session.py:3`)? They contain Odyssey's issue list ("Issue 1 Derek", "Issue 4" OBS finalize).
10. Is there a VCS repo? Which of the Fallout 4 builds (`v1`, `New folder`, `Fallout4_Combo_v5`) is what gamers actually run?
11. Did the GTA5 recorder (`net9.0-windows` under SHVDN3) actually load in game?
