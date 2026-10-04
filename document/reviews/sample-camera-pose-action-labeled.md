# Review: Sample Gameplay Capture — "Camera pose + Action-labeled"

> Source: `~/Downloads/Sample Gameplay Capture/Camera pose + Action-labeled` (1.3 GB) · Reviewed 2026-10-04
> Related: [sample-action-labeled-only.md](./sample-action-labeled-only.md) · [vane-ank-secret.md](./vane-ank-secret.md) · [luis-farm-secret.md](./luis-farm-secret.md)
> Method: automated audit of all 20 sessions (matrix sanity, axis layout, forward-vs-motion, units, timing, inputs) plus video-based checks on the 10 sessions that include video. Script: `scratchpad/analyze_pose.py` (to be ported into the validator, §5).

## 1. What it is

20 sessions from 9 games, captured with camera pose. No session in this folder is in spec v2 format. There are two older formats (plus the Action-only format of the sibling folder, which makes three):

| Format | Games | Files per session | Notes |
|---|---|---|---|
| **A — legacy CSV** (Vane's collectors) | Cyberpunk 2077, Fallout 4, GTA V, Skyrim, Witcher 3 | `<uuid>-<name>.csv` (+ `.mp4`) | Columns `Frame_ID, Timestamp_ms, C2W_M.., FOV_Deg, FOV_Axis, Keyboard_Input, Mouse_Buttons, Mouse_Delta_X/Y`; empty = `NONE`; keys joined with `+` (`W+A`); UTF-8 BOM; **no actions, no session.json** |
| **B — spec v1** | BeamNG, Fallout London (`game: "Fallout4"`), Monster Hunter Rise, Monster Hunter World | `<uuid>-session.json`, `<uuid>-key_binding.json`, `<uuid>-frames.csv` (+ `.mp4` for FALD) | `session.json` has `spec_version: "v1"`, nested `video`/`camera`/`input` blocks; `frames.csv` is lowercase with PINHOLE intrinsics and empty-field nulls, **without distortion columns**; `key_binding.json` = `{bindings: {key: action}}` |
| (C — Luis, sibling folder) | — | `session.json`, `frames.csv`, `key_bindings.json` | v2 `session.json`, literal `null`, uppercase `C2W_M..`, `keys: [{key, action}]` |

All files are UUID-prefixed. Session folder names follow `<GAME>-B<batch>-S<session>` or `-SS<session>`.

## 2. Per-game results

Legend: ✅ correct · ⚠️ questionable · ❌ wrong. "fx (video)" is the horizontal focal length estimated from image motion: phase-correlation shift ÷ tan(camera yaw step), on rotation-dominated frames.

| Game (sessions) | Format | det / axis layout | Forward vs motion | Intrinsics | Mouse input | Timing / video |
|---|---|---|---|---|---|---|
| **BeamNG** (2) | B | ✅ det +1, RUB (col 1 = up) | ✅ −col2 agrees 84–95% | Dynamic FOV 65–73° per frame ✅ (no video to verify) | ✅ real (integer deltas) | ❌ BNG-B28 runs at **~50 fps** (20 ms steps) while declaring 60 |
| **Fallout London** (2) | B | ✅ det +1, RUB | ✅ 87–90% | ⚠️ fx 771 (70° taken as vertical); video estimate inconsistent (1015 / 647, weak correlation) | ✅ real (integer) but weakly correlated with camera (r ≈ −0.1) | ❌ video frames ≠ rows (+3, +2); pose↔video weak |
| **Monster Hunter Rise** (2) | B | ✅ det +1, RUB | ⚠️ 40–54% (third-person camera, expected) | fx 651 (no video) | ⚠️ integer, but correlation with camera yaw is **exactly −1.00**: likely derived from the camera and rounded | ✅ 60 fps grid |
| **Monster Hunter World** (2) | B | ✅ det +1, RUB | ✅/⚠️ 64–98% (third person) | fx 1083 (no video) | ✅ plausible real (r −0.91…−0.94) | ✅ |
| **Cyberpunk 2077** (2) | A | ❌ **det −1** (column 2 = +forward) | consistent with col2 = forward | ❌ **FOV labelled vertical is horizontal**: fx(video) 1167 vs 1144 if horizontal, 644 if vertical | ❌ **proxy**: tiny floats, r = −1.00 with camera yaw at lag 0 | ❌ video frames ≠ rows (−5, −2); first timestamp 16 ms, not 0 |
| **Fallout 4** (2) | A | ❌ det +1 but **column 2 is world-up** (raw engine layout, not RUB); roll up to 0.16 | n/a (wrong basis) | ❌ FA4-B14: fx(video) 1196 ≈ horizontal (1144); FA4-B28: 894, between the two (inconclusive) | ❌ proxy (r −0.98…−1.00) | ❌ video frames ≠ rows (−3, −6) |
| **Skyrim** (2) | A | ❌ column 2 is world-up (raw engine layout) | n/a | ❌ SK-B11: fx(video) 1155 ≈ horizontal (1144) vs 644 if vertical | ❌ mostly proxy | ❌ SK-B06 is **30 fps** (video and rows); frames ≠ rows (−2, −4) |
| **GTA V** (2) | A | ❌ det +1 but **column layout differs between sessions** (col 0 ≈ up in S100, col 2 ≈ up in S94); \|m20\| up to 1.0 | ❌ inconsistent: −col2 in S100, +col1 in S94 | ⚠️ 50° vertical → 1158; video 908–1008 (ratio 0.78–0.87, not horizontal) | ❌ proxy (r +0.91…+0.98) | ❌ frames ≠ rows (−4) |
| **Witcher 3** (2) | A | ❌ **det −1** (col2 = +forward) | consistent with col2 = forward | 60° "vertical" (no video) | ❌ proxy (r = −1.00) | ❌ **not 60 fps**: WTC-B31 ~90 fps (11 ms steps), WTC-B64 ~64 fps with 31 ms gaps |

Cross-cutting:

- **Video frame count ≠ CSV rows in all 10 videos** (−6 … +3).
- **Units:** Bethesda titles move ~80–480 units/s, so game units are not metres. BeamNG, Cyberpunk, Witcher, GTA V and MH Rise are in metres.
- **Key names** are not normalized: `LShift` / `LeftShift`; arrow keys `Left`/`Right` collide with mouse-button names; legacy files join keys with `+`.
- **Two different key-binding schemas** have been delivered: `key_binding.json {bindings: {}}` (v1) and `key_bindings.json {keys: [{key, action}]}` (Luis).

## 3. Answers to open questions

| Question | Answer |
|---|---|
| **Vane Q1** — Bethesda FOV axis | **Horizontal**, confirmed from video for Skyrim (SK-B11) and Fallout 4 (FA4-B14). Recorded intrinsics (labelled vertical) are **~1.8× too small**. The same bug is present in **Cyberpunk** |
| **Vane Q3** — were det −1 / non-RUB sessions delivered? | They are in this delivered-format sample: Cyberpunk and Witcher (det −1), Fallout 4, Skyrim and GTA V (non-RUB layouts). Assume they were delivered |
| **Vane Q5** — video = rows + 1? | Not systematic: mismatches range from −6 to +3 frames across all legacy and v1 videos |
| Vane review — proxy mouse | Confirmed by correlation: legacy `Mouse_Delta` is the camera rotation itself (\|r\| ≈ 1.00 at lag 0, sub-unit floats). Monster Hunter Rise's integer deltas also correlate at exactly −1.00, which is suspicious |
| Format history | Three generations exist: legacy CSV → spec v1 (May 2026) → v2 `session.json` (Luis, Aug 2026). None of these samples are full v2 |

## 4. What this means

1. **Delivered camera data is mostly unusable as-is.** 5 of 9 games (10 of 20 sessions) have a wrong basis or det −1, and at least 3 games have intrinsics off by ~1.8×. Only BeamNG and the spec-v1 Monster Hunter / Fallout London sessions have a correct RUB basis.
2. **A conversion pass is required** before any reuse or redelivery (Vane review recommendation 16): rebuild C2W from the true forward and up vectors, fix the FOV axis, convert units, add actions, write v2 `session.json`, and re-check frame counts.
3. **Proxy mouse data should not be presented as player input.** Mark it, or drop the mouse columns, for those games.

## 5. Implications for odyssey-capture (validator rules)

| New rule | What it would have caught here | Severity |
|---|---|---|
| `c2w.column_basis` — column 1 must be most aligned with world up; column 2 must be the negated look direction | Fallout 4, Skyrim, GTA V layouts | error |
| `c2w.forward_vs_velocity` — −col2 vs horizontal motion (with backward-walk exception, third-person tolerance) | GTA V inconsistency, det −1 forward | warning (error for first person) |
| **`camera.fov_from_video`** — estimate fx from image shift ÷ yaw step and compare with recorded fx (ratio outside 0.85–1.15 → flag) | FOV-axis bug in Skyrim, Fallout 4, Cyberpunk | warning (needs rotation-dominated frames) |
| **`input.mouse_proxy`** — \|corr(mouse dx, camera yaw)\| > 0.99 at lag 0 with non-integer deltas | Legacy proxy mouse; flags Monster Hunter Rise for review | warning |
| `frames.frame_interval` vs declared fps (already exists) | BeamNG-B28 50 fps, Witcher 90/64 fps, Skyrim 30 fps | error |
| `video.frame_count` (already exists) | All 10 videos | error |
| `units.speed_range` per profile | Bethesda units vs metres | warning |
| `input.key_name_canonical` | `LeftShift` vs `LShift`, arrow vs mouse names, `+` joins | warning |

The video-based checks (`camera.fov_from_video`, pose↔video lag) are the most valuable addition. They verify pose and intrinsics against the pixels without any ground truth from the game.
