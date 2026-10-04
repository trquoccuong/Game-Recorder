# Review: "Luis Farm Secret" — GameAICollector v2

> Reviewed 2026-10-04. Source: a local copy of a colleague's folder (outside this repo). No executables were run.
> Paths below are relative to that folder unless prefixed with `odyssey-capture/` or `document/`.
> Odyssey references: `document/videogame-data-capture-spec-v2.md` (v2.1) and `document/multiplayer-data-capture-spec-v3.md` (v3.1). Plan references: `document/software-plan.md`.

## 1. Summary

- **What it is:** *GameAICollector v2*, a single Windows exe (C# / .NET 8 WinForms, about 6.5k lines) that records **keyboard/mouse input plus OBS video** for any single-player PC game and writes Odyssey v2 sessions (`session.json`, `frames.csv`, `video.mp4`), optionally with `key_bindings.json` and `session.rrd`.
- **It does not capture camera data.** Every C2W and intrinsics column is written as the literal `null` (QA decision A6, `V2CollectorKit/02_QUYET_DINH_QA.md`). There is no memory reading, no injection, no mod or script hook and no telemetry SDK. Per-game differences are limited to a JSON binding profile.
- **Maturity:** a working production prototype, used for real deliveries in July 2026:
  - Borderlands TPS: 734 video frames == 734 CSV rows, ALL PASS.
  - One delivery (BLPS) failed downstream QA because input led the video by about 1 s. The tool was then fixed (§6).
  - The heavy engineering went into **input↔video sync verification**, not into data extraction.
- **Who it's for:** non-technical operators. It has hotkeys (F6–F10), a tray icon, a Vietnamese UI and logs, and a self-validation report after each take.
- **Relevance to us:** high for **Path A (input + video only)**, **QA/sync checks** and **lessons learned from downstream QA**. None for camera/pose, the server side or v3 multiplayer.

## 2. Inventory

Total 1.9 GB, 6,307 files. **More than 95% of that is vendored toolchain or build output.**

| Path | Size | What |
|---|---|---|
| `.dotnet/` | 725 MB | Local .NET SDK 8.0.422 (vendored; contains machine-ID cache files) |
| `.nuget/`, `NuGet/`, `.appdata/` (NuGet v3 cache), `.templateengine/`, `AppData/`, `Microsoft/` (PowerShell cache) | ≈390 MB | Toolchain caches (noise) |
| `GameAICollector_src/bin`, `obj`, `publish` | 553 MB | Build outputs, plus a published exe and a `writetest-session` sample |
| `GameAICollector_src/Resources/ffmpeg.exe` | 96 MB | Bundled ffmpeg, embedded into the exe as a resource |
| `GameAICollector_src/*.cs`, `Services/*.cs`, `Resources/rrd_creation.py`, `default_profile.json` | ≈0.3 MB | **The actual source** (≈6.5k LOC) |
| `GameAICollector_v2/` | 171 MB | Release: `GameAICollector.exe` (98 MB single-file), `uv.exe` (73 MB), `GameAICollector.ini`, `HUONG_DAN.txt` (operator guide), `game_profile.json` (currently Batman AK), 10 per-game JSONs |
| `V2CollectorKit/` | 116 KB | Project "memory": spec summary, QA decisions, tool design, new-game checklist, profiles (identical to `GameAICollector_v2/*.json`), `rrd_creation.py`, deprecated `sync_check.py` |
| `.git/`, `.agents/` | empty | **No git history.** The `.git` directory is empty, so the evolution below is reconstructed from docs and file mtimes |
| `.claude/settings.local.json` | 1 KB | Claude Code allow-list referencing earlier collectors (ETS2, It Takes Two) |

**Evolution, reconstructed (all 2026):**

1. **Before 07-12:** separate per-game collectors existed: ETS2 (via the official SCS telemetry SDK), It Takes Two, "AC" and RE5. Their sources were **deleted on 07-13**, and their useful code was folded into v2 (`00_TONG_QUAN.md`).
2. **07-12:** QA decisions locked.
3. **07-13:** v2 built. Validated with a self-test and a real Borderlands take. F8 segments and `rrd_creation.py` added.
4. **07-16:** downstream DQ finding: **input led video by 1067 ms**. Fixes:
   - warmup trim
   - `measured_offset_ms` / `capture_latency_ms` fields
   - `key_bindings.json`
   - a C# motion-correlation sync check replacing the Python/OpenCV `sync_check.py`
5. **07-17:** per-game `sync.signal` (keyboard steering for truck sims).
6. **07-18:** validator expanded to mirror the downstream DQ's 8 checks, 9 new profiles added, sync-flash ("clapperboard") alignment added.
7. **07-21:** active profile switched to Batman AK.

## 3. Architecture

```
F7 ─► Recorder.StartAsync ─► OBS WebSocket: StartRecord ─► wait first frame ─► measure OBS warmup (½RTT) ─► Stopwatch t=0
          │                                                           └► (optional) create white color source for sync flash
          ▼
   RecordLoop (Task.Delay(1)): poll GetAsyncKeyState for profile keys/buttons (OR-accumulate)
          │                    Raw Input (message-only window) sums mouse dx/dy
          └► every n·(1000/fps) ms → write one frames.csv row (29 camera cols = "null")
F7 ─► StopAsync: trim last 1 s of CSV ─► OBS StopRecord ─► move file ─► A/V alignment:
          ├ PRIMARY  flash: detect brightness spikes, vote offset, cut pre-roll off video + CSV
          └ FALLBACK warmup: ffmpeg re-encode skipping N warmup frames, keep exactly rows frames, recount
       ─► session.json (+key_bindings.json) ─► optional motion-correlation auto-align ─► Validate() ─► log PASS/FAIL
```

| Component | File | Role |
|---|---|---|
| UI / tray / hotkeys | `MainForm.cs` (1013 LOC), `HotkeyPoller.cs`, `UiKit.cs`, `LogForm.cs` | Operator UI. Hotkeys are polled with `GetAsyncKeyState` because `RegisterHotKey` fails in fullscreen. Includes Import/Export profile, "Test" / re-validate (`MainForm.cs:640`) and "Create .rrd" (`:615`) |
| Recorder | `Services/Recorder.cs` (1221 LOC) | Session lifecycle, record loop, finalize pipeline |
| Input | `Services/ProfileInputReader.cs:30-67`, `Services/MouseTracker.cs:91-135`, `RawInputWindow.cs:11-29` | Key and button state polling; Raw Input mouse deltas on a dedicated `HWND_MESSAGE` window |
| Video | `Services/ObsWebSocket.cs` (OBS WS v5: StartRecord/StopRecord/GetRecordStatus/GetVideoSettings/CreateInput…), `Services/VideoTrimmer.cs` | OBS records the video. ffmpeg re-encodes it to exactly N frames and counts frames by decoding |
| Sync | `Services/SyncAnalyzer.cs` (857 LOC), `Services/SessionAligner.cs` | Flash detection and voting; motion↔input cross-correlation; drift check; telemetry shift |
| Output / QA | `Services/SessionWriter.cs` | 36-column header, `session.json`, `key_bindings.json`, sync metadata, `Validate()` (S1–S22, G1) |
| Profiles | `Services/GameProfile.cs` | JSON model, validation and VK resolution |
| .rrd | `Services/RrdBuilder.cs` + `Resources/rrd_creation.py` | Extracts the embedded script and runs it with `uv run` / `python` / `py` |

**Session lifecycle details:**

- The session ID is typed by the operator or generated as a GUID. It is sanitized and de-duplicated with `-2`, `-3`… (`Recorder.cs:910-933`).
- The output folder is `Documents\AIDataCollector_<game_title>\<session-id>\`.
- A single-instance mutex is used (`Program.cs:57`).
- `--selftest` and `--writetest` are built-in smoke modes (`Program.cs:39-111`).
- **The tool does not upload to R2.** Upload is a manual step and is listed as "pending".

## 4. Per-game profile model

Each profile is one JSON file per game, imported through the UI and copied to `game_profile.json` next to the exe. Schema (`GameProfile.cs:15-80`, validated at `:249-335`):

| Field | Meaning |
|---|---|
| `schema_version`, `game_title` (path-safe; also used as the R2 `<game>` segment and folder name), `platform`, `localization`, `notes` | Identity |
| `input_mouse_convention` {maps_to, maps_to_other, dx/dy ±} | Copied verbatim into `session.json`. The validator enforces the v2 if/then rules |
| `segment_maps_to` (+`_other`) | `maps_to` written for F8-marked frame ranges (`input_mouse_convention_segments`) |
| `keys[]` {`key` (spec label, no L/R), `vks[]` (WinForms `Keys` names or VK ints), `action` or null} | **Static 1:1 key→action map** |
| `mouse_buttons[]` {`button` Left/Right/Middle/X1/X2, `action`} | Buttons also emit actions (QA-A4) |
| `sync` {`signal`: mouse/steering/auto/none, `steer_left_action`, `steer_right_action`} | Which input the sync analyzer correlates against the video |

**How camera, FOV and position are obtained:** they are not, for any game. All 10 profiles (SnowRunner, Borderlands TPS, Batman AK, Yakuza 0, Mass Effect 2, ATS, Payday 2, Just Cause 3, Outer Worlds, Watch Dogs 2) are input-only:

- All use `maps_to=camera_look_velocity` with no invert-Y.
- All are `"GIA DINH"` (assumed default bindings, "to be confirmed in Options").
- `sync.signal` is `auto` for the driving and mixed titles, and `mouse` for the FPS/TPS titles.

The only telemetry-based technique in the lineage was the deleted ETS2 collector, which used the official SCS telemetry SDK (an official API/telemetry technique). That code is not present. Nothing in the folder reads game memory or bypasses anti-cheat.

## 5. Output format vs Odyssey spec — conformance

| Item | GameAICollector v2 | Status |
|---|---|---|
| Delivery files | `session.json`, `frames.csv`, `video.mp4`; `.rrd` and `rrd_creation.py` created manually for the 15% QA sample | **Matches** (v2 §1.1.2) |
| Extra file `key_bindings.json` | Optional (UI toggle, default **off**, `RecorderSettings.cs:37`). Required by downstream DQ "req #6" even though spec v2 *removed* it | **Differs**: conflicting requirements |
| R2 path / upload | Not implemented (manual). Rule: `<game>` = `game_title` verbatim | **Missing** |
| `session.json` required fields | All 16 written (`SessionWriter.cs:59-79`) | Matches |
| `created_at_utc` / `ended_at_utc` | `yyyy-MM-ddTHH:mm:ssZ`, **second precision** (`:64-65`). `ended = created + duration_ms` (`:44`) | Matches v2 tolerance (1 s). **Too coarse for v3 cross-POV sync**. See bug B1 |
| `duration_ms` | `last_ts − first_ts` = (N−1)·period | Matches (frame_count ≈ fps·dur is within ±2) |
| `fps`, `record_*_px` | From OBS `GetVideoSettings`. **Silent fallback to 1920×1080@INI fps** if OBS doesn't answer (`Recorder.cs:167-174`) | Matches, with a risk |
| `screen_*_px` | Primary monitor only, fallback 1920×1080 (`Recorder.cs:935-949`) | Risk on multi-monitor setups |
| `input_mouse_convention(_segments)` | From profile; segments via F8, clamped/shifted on every trim (`Recorder.cs:371-401, 506-510, 720-745`; `SessionAligner.cs:88-127`) | **Matches**, well done |
| Extra top-level fields | `generated_by`, `measured_offset_ms`, `capture_latency_ms` (downstream DQ req #2/#3) | Allowed (`additionalProperties: true`) |
| frames.csv header | 36 columns, **`C2W_M00..M33` uppercase** (§1.1.3 table), other columns lowercase | Matches §1.1.3. Our `odyssey-capture` uses lowercase `c2w_mRC` (§1.1.2.4), so a decision is needed |
| Null encoding | Literal `null` in every empty cell (QA-A1). Accepted by downstream DQ | Our `frames.NULL = ""` differs. Same pending question in `odyssey-capture/README.md` |
| C2W 16 cols, `camera_model`, fx/fy/cx/cy, k1–k6, p1–p2 | Always `null` | **Missing data** (headers present, which the spec allows) |
| `frame_id`, `timestamp_ms` | 0..N−1. Timestamps are **nominal** `round(n·1000/fps)` from a software Stopwatch, not real frame times | Passes v2 checks. Not "logged" frame time |
| `input_keys` / `input_actions` | Pipe-delimited, sorted, `null` when empty. Only **profile-declared** keys are recorded. Actions are a static rename of keys | Format matches. **Semantics weak**: no contextual actions, which was v2's stated reason for `input_actions` |
| `input_mouse_buttons` | Pipe-delimited, `null` when empty | Matches |
| `input_mouse_dx/dy` | Raw Input counts summed per row, `"0.0"` sentinel, invariant culture | Matches. The absolute-move flag is ignored (`MouseTracker.cs:110-114`) |
| Video frame count == rows | ffmpeg re-encode `-frames:v N`, decode-count, then truncate CSV if the video is shorter (`Recorder.cs:478-488`, `VideoTrimmer.cs:20-116`) | Matches by construction. The validator does not re-count independently |
| v2 §1.5 camera checks (det, orthonormal, intrinsics) | Skipped (S7 asserts all-null) | n/a |
| **v3** (`player.json`, `frames.bin`, packets, `server/`, `events.jsonl`, round-triggered start/stop, cross-player sync) | None | **Missing entirely** (single-player tool) |

**Known bugs and defects (verified by reading the code):**

- **B1: wall-clock offset after flash alignment.**
  - The flash path cuts about 3 s of pre-roll off the CSV and video (`Recorder.cs:651-661`).
  - But `session.json` still uses `_createdAtUtc` from before the pre-roll (`Recorder.cs:216, 515-516`).
  - As a result, `created_at_utc`/`ended_at_utc` are about 3 s earlier than the delivered frame 0.
  - This is invisible to v2 checks but fatal for v3 multi-POV alignment.
- **B2: input sampling is tied to `Task.Delay(1)` with no `timeBeginPeriod`** (`Recorder.cs:264-298`).
  - On Windows this ticks at about 15.6 ms, so keys are sampled at about 64 Hz, and taps shorter than about 16 ms can be lost. `GetAsyncKeyState` uses only the 0x8000 bit (`ProfileInputReader.cs:67`).
  - When the loop wakes late and emits several rows in one pass, only the first row gets the accumulated keys and mouse delta. The others get `null`/`0.0`, because the sets are cleared per row (`:305-334`). Held keys can therefore flicker to `null`.
  - Mouse deltas are binned by wake time rather than by frame time, giving about ±1 frame of jitter.
- **B3: "measured" sync fields are not always measured.**
  - When the flash path is used, `measured_offset_ms` is set to 0 by definition (`Recorder.cs:547`).
  - Weak or unmeasurable takes also get 0 (`:777-783`).
  - `capture_latency_ms` is forced to 0 whenever it is ≤150 ms (`SessionWriter.cs:199`), contradicting the kit doc, which defines it as the raw latency.
  - The values are stored as magnitudes, so the sign is lost.
  - Downstream consumers can't distinguish "verified 0" from "unknown".
- **B4: the flash only proves tool-clock ↔ OBS-compositor alignment.**
  - The white source is composited by OBS (`ObsWebSocket.cs:264-337`), not rendered by the game.
  - So the game's own input→photon latency (several frames) is not measured, yet the session is reported as "deterministic |lag|≈0" (`SessionWriter.cs:503-504`).
  - The brightness threshold `max(median+60, 170)` (`SyncAnalyzer.cs:793`) can also miss the flash in bright scenes, for example SnowRunner snow.
- **B5: validator gaps.**
  - There is no JSON Schema validation (types and ranges).
  - There is no independent check that video frame count equals CSV rows.
  - The timezone check accepts any string containing `+` (`SessionWriter.cs:413`).
  - S1 only checks that files exist. The `--writetest` sample passes ALL with a 4-byte stub `video.mp4` (`Program.cs:98`).
- **B6: lossy re-encode, possibly twice.**
  - The video is re-encoded with libx264 crf18 veryfast (`VideoTrimmer.cs:61-73`).
  - It can be re-encoded a second time by `AlignByVideoTrimAsync` (`Recorder.cs:828-863`).
  - With ffmpeg's default CFR behaviour, a VFR source would be silently dup/dropped to reach N frames, which breaks the per-frame correspondence while counts still match.
- **B7: doc/code drift and non-persistent settings.**
  - The kit says auto-sync is "default ON", but the code default is `false` (`RecorderSettings.cs:45`).
  - The key-bindings, auto-sync and flash toggles are not persisted to the INI (`SettingsLoader.cs`), so they reset on every launch.

## 6. Sync & quality approach

- **Frame timing:** CSV rows are clocked by a `Stopwatch` with a fractional period. Rounding the period to an int previously caused 58.8 fps drift. The OBS video is then forced to the same count. Correspondence relies on OBS producing CFR at the same nominal rate. Alignment is fixed at the start, either from a one-time warmup measurement (NTP-style: warm the WebSocket path, add ½RTT, `Recorder.cs:202-217`) or from the flash.
- **Input aggregation:** keys and buttons are OR-ed over the inter-row interval; mouse deltas are summed. The interval is sampled by polling, not by events (B2).
- **Sync flash:** four blinks at 0.5, 1.0, 1.8 and 2.8 s. The spacing is non-uniform so that any subset of detected flashes still yields a unique offset. Offsets are voted with a tolerance of ±1 frame and a support of at least 2. The pre-roll is then trimmed from both streams (`Recorder.cs:225-249, 607-678`; `SyncAnalyzer.cs:728-838`).
- **Motion-correlation grounding** (fallback and verification, `SyncAnalyzer.cs`):
  1. ffmpeg decodes 160×90 grayscale frames from sampled 5 s windows.
  2. Global motion is estimated per frame by integral-projection matching.
  3. That motion is cross-correlated with mouse dx/dy, or with a ±1 steering track built from the steer actions, over a ±2 s range.
  4. A window counts only if at least 12% of its frames have live input. The result counts only if a **majority of windows agree**; otherwise it is reported as "weak" with offset 0.
  5. A drift check splits the take into 3–5 segments: ≤50 ms is ok, 50–150 ms is drift, >150 ms means recapture.
- **QA rules mirrored from downstream DQ** (`05_CHECKLIST_GAME_MOI.md`):
  - `action_video_grounding` (|lag| ≤150 ms; **failing one session returns the whole batch**)
  - `session_json_v31_contract`
  - `session_json_schema_safe`
  - `frames_csv_schema_safe`
  - `key_bindings_json_schema_safe`
  - `normalization_error_free`
  - `required_game_artifacts_present`
  - `timestamp_monotonicity`
  - a naming finding: `game_title` must match across `session.json`, `key_bindings.json` and the R2 path
- **Validation IDs:** S1–S22 plus G1 (`SessionWriter.cs:245-554`). G1 rejects any session whose grounding was not verified on the delivered data.
- **Operator rules:**
  - Takes must include clear camera turns or steering, otherwise sync can't be measured.
  - The operator holds still for about 4 s of pre-roll.
  - The last 1 s is trimmed (the F7 keypress noise).
  - The flash needs Display Capture with the game in borderless mode.

## 7. Strengths worth adopting

1. **Downstream DQ check list and its failure history** (`05_CHECKLIST_GAME_MOI.md`, `00_TONG_QUAN.md` §"Sửa theo tbrain Data Quality findings"). These are concrete acceptance gates that our §1.5-only validator (`odyssey-capture/src/odyssey_capture/validate.py`) does not cover yet: grounding lag, sync metadata fields, naming consistency and normalization.
2. **Input↔video grounding check** (`SyncAnalyzer.cs`). The design is solid: windowed sampling, a minimum active-input fraction, majority voting, a steering-signal fallback and a drift check. It ports naturally to Python/numpy as a validator check and gives independent verification of video↔CSV alignment, complementing plan §7.10.3.
3. **Non-uniform clapperboard plus voting** (`Recorder.cs:228-238`, `SyncAnalyzer.FlashOffset`). The same idea as the plan's flash/QR tests. To measure end-to-end latency, the flash must be rendered *in the game* (test game) or the recorder must timestamp frames with QPC.
4. **Binding profile schema** (`GameProfile.cs`, `04_GAME_PROFILES/README.md`):
   - key label vs VK list (L/R merge)
   - `action: null`
   - `segment_maps_to`
   - per-game `sync.signal`
   - strict validation on import

   This is a good starting schema for the input-mapping part of our profiles. Note that our extractor profiles are Lua (plan §7.2).
5. **F8 segment marking with trim-aware clamping and shifting.** This is a cheap, practical way to produce `input_mouse_convention_segments` for mixed on-foot/vehicle games before any automatic detection exists.
6. **Hard-won Windows and OBS lessons** (`03_THIET_KE_TOOL.md` §"Bug/kinh nghiệm"):
   - Use a fractional frame period.
   - Put Raw Input on a message-only window.
   - Poll hotkeys with `GetAsyncKeyState`, because `RegisterHotKey` fails in fullscreen.
   - Don't toggle `ShowInTaskbar`.
   - OBS `StopRecord` may omit `outputPath`; scan the record directory as a fallback.
   - Retry until the OBS file handle is released.
   - Encoding pitfalls with Vietnamese `.cs` files.
7. **Format decisions that downstream already accepted:** literal `null` and uppercase `C2W_M..` headers. This is evidence for the two "pending" items in `odyssey-capture/README.md`.
8. **Self-validation after every take and a "re-validate an old session" button** (`MainForm.cs:640`). Good operator UX that we should reproduce in the Capture Agent.

## 8. Weaknesses / risks

- **Scope:**
  - No camera, pose or intrinsics.
  - No server state.
  - No multiplayer or v3 layers.
  - No R2 upload.
  - Single-player only, run on one machine by one operator.
- **Timing model:**
  - Rows are synthetic timer ticks, not captured frame times.
  - Input is polled rather than event-stamped (B2).
  - Alignment is a one-time estimate per take.
  - This is the opposite of our plan's QPC event-log approach (plan §6.5 rules 3–4).
- **Data-integrity risk:** offsets written as 0 when unverified (B3), and claims of "deterministic" sync that exclude game latency (B4).
- **Code quality:**
  - No unit tests (only `--writetest`) and no git history.
  - God classes: `Recorder.cs` and `MainForm.cs` together exceed 2.2k LOC.
  - UI and log strings are hard-coded in Vietnamese.
  - `MouseTracker.cs` looks decompiler-generated (mangled locals, numeric constants), which suggests part of the source was recovered from a binary.
  - Several silent fallbacks to 1920×1080.
  - The 96 MB ffmpeg is embedded in the exe.
  - Build outputs and SDK caches are stored next to the source.
- **Hardcoded or environment paths:** the docs reference a specific Windows user profile and `E:\` game paths. The output path is fixed to `Documents\AIDataCollector_<game>`.
- **Secrets:** **no credentials, tokens or R2 keys were found.**
  - The OBS password is empty in `GameAICollector.ini`.
  - Personal and machine identifiers are present and should be stripped before the folder is shared further:
    - a Windows account name in doc paths and `.claude/settings.local.json`
    - .NET machine-ID cache files under `.dotnet/`
- **Legal / compliance (high):**
  - Profile notes and the kit overview identify the Borderlands TPS build as a **Steam-emulator ("Goldberg") build** and the ATS build as a **repack from a piracy site** (`GameAICollector_v2/BorderlandsThePreSequel.json` and `AmericanTruckSimulator.json` `notes`; `V2CollectorKit/00_TONG_QUAN.md`).
  - Data captured from unlicensed copies must not enter Tbrain/Odyssey deliveries.
  - This must go through the plan §5.3 legal clearance. Delivered BLPS sessions should be audited.
- **Anti-cheat:** not applicable. Capture is fully passive and external (OBS + `GetAsyncKeyState` + Raw Input), and the titles were played offline or single-player. Nothing to flag. Any future online use would fall under plan §1 point 5: never on public matchmaking, and anti-cheat bypass is out of scope.

## 9. Recommendations for `odyssey-capture`

**Reuse / port (priority order):**

1. **P0: Settle the CSV conventions.** Confirm with Odyssey whether to switch to literal `null` and uppercase `C2W_M..`. This tool's accepted deliveries are the evidence. Then update `frames.NULL`, `C2W_COLUMNS` and the fixtures in one change.
2. **P0: Extend the Python validator with the downstream DQ gates:**
   - a grounding check (port `SyncAnalyzer` windowed correlation plus majority vote plus drift, with ffmpeg decode and numpy)
   - `measured_offset_ms` / `capture_latency_ms` presence and bounds
   - `game_title` ↔ R2-path naming
   - normalization
   - JSON-Schema validation (we already have `schemas/session_schema.json`)
   - an independent video-frame count

   Add one fixture variant per new rule (plan §7.10.2). Ask for the DQ document first (Q1).
3. **P1: Input-mapping profile in `capture-cs`.** Adopt the `keys[]{key,vks,action}`, `mouse_buttons[]`, `segment_maps_to` and `sync.signal` fields. Keep `odx-input` event-based (QPC Raw Input); actions are mapped in Python when building `frames.csv`, so we keep one implementation per format.
4. **P1: Segment hotkey in `odx-input`.** F8 should write `segment_start` / `segment_end` events to `input_events.csv`. Python turns them into `input_mouse_convention_segments` using the existing frame-binning (`frames.aggregate_inputs`).
5. **P2: Clapperboard as a verification tool**, not as the source of alignment. Use it in the Unity test game, rendered in-game, and as a fallback check. Primary alignment should come from per-frame QPC timestamps of a WGC recorder (plan §6.5).
6. **P2 (only if we need OBS before the WGC recorder exists):** reuse the OBS WebSocket client, the warmup/½RTT measurement, the `outputPath` fallback and the file-ready wait. Store `created_at_utc` with milliseconds, re-based on the delivered frame 0.

**Avoid:**

- timer-ticked synthetic rows and polling-based input (B2)
- writing 0 for unmeasured offsets (B3)
- second-precision UTC
- shifting the pre-roll without moving `created_at` (B1)
- double lossy re-encodes (B6)
- static key→action renames passed off as "semantic actions"
- embedding ffmpeg in the exe
- **any data from unlicensed game builds**

## 10. Open questions for the author

1. What is the "tbrain Data Quality — Delivery Requirements & Findings" document? Who runs its 8 checks? Can we get it, including the definitions of `session_json_v31_contract` and `action_video_grounding`?
2. Was `key_bindings.json` (DQ req #6) required by Odyssey despite spec v2 removing it, or only by internal DQ?
3. Did Odyssey explicitly confirm literal `null` and uppercase `C2W_M..`, or was that only inferred from passing DQ?
4. How many hours were delivered, at what pass rate, and were BLPS and later batches accepted after the warmup/flash fixes?
5. Were the Borderlands TPS and ATS sessions that were delivered recorded on licensed copies? Which titles were actually delivered?
6. Did any of the deleted collectors (ETS2 via SCS SDK, It Takes Two, AC, RE5) produce camera/C2W data? Is their source or conversion logic recoverable? (The ETS2 telemetry→RFU conversion would be directly useful.)
7. Was the flash-measured offset ever compared with the in-game input→photon latency? What residual lag does the motion check show on flash-aligned takes?
8. Was key flicker or missed taps observed in practice (B2)? What timer resolution did capture machines run at?
9. Is there a git history elsewhere? Was `MouseTracker.cs` recovered with a decompiler?
10. OBS setup in production: CFR? encoder? Game Capture or Display Capture? Multi-monitor rigs?
