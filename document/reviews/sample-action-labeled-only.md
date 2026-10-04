# Review: Sample Gameplay Capture — "Action-labeled only"

> Source: `~/Downloads/Sample Gameplay Capture/Action-labeled only` (713 MB) · Reviewed 2026-10-04
> Related: [luis-farm-secret.md](./luis-farm-secret.md) (the tool that produced these sessions) · [vane-ank-secret.md](./vane-ank-secret.md)
> The sibling folder `Camera pose + Action-labeled` was not reviewed here.

## 1. What it is

10 delivered-format sessions from 5 single-player games, produced by GameAICollector (`"generated_by": "Tbrain_GameDataCollector"`), recorded 2026-08-06 → 08-10:

| Game | Sessions | Video in sample |
|---|---|---|
| Mad Max | MM-110-S40, MM-141-S65 | both |
| Payday 2 | PD2-B149-S62, PD2-B181-S63 | none |
| SnowRunner | SNR2-B100-S1, SNR2-B100-S5 | none |
| The Outer Worlds | TOW-B26-S63, TOW-B61-S54 | B61-S54 only |
| Watch Dogs 2 | WD2-111-S03, WD2-146-S02 | none |

Each session folder holds `session.json`, `frames.csv`, `key_bindings.json` and (sometimes) `video.mp4`. Each is ~2 minutes (6,906–7,680 frames at 60 fps).

## 2. Findings

### Format actually delivered

- **Null encoding:** literal `null` in every empty cell, including all 29 camera columns and empty `input_keys` / `input_actions` / `input_mouse_buttons`. `input_mouse_dx/dy` use `0.0`.
- **Header case:** uppercase `C2W_M00 … C2W_M33`; every other column is lowercase.
- **`key_bindings.json` is part of the delivery:** `{schema_version: "1.0", game_title, platform, keys: [{key, action}, …]}`, with 15–21 bindings per game. Spec v2 removed this file, but it was still shipped.
- **Extra `session.json` fields:** `generated_by`, `capture_latency_ms`, `measured_offset_ms` (allowed by `additionalProperties: true`).
- **Camera tier:** no camera data at all (0 camera rows). The folder name suggests "action-labeled only" is a recognised delivery tier, separate from "camera pose + action-labeled".

### Validation

- With their conventions (`null` token, uppercase headers mapped to lowercase), **all 10 sessions pass every v2 §1.5 check in our validator** (session, structure, input).
- Video (3 files): H.264, 1920×1080, CFR 60/1, frame count == CSV rows exactly. This is consistent with the re-encode-to-N-frames step described in the Luis review.

### Timing

- `timestamp_ms` is an exact synthetic grid, `round(i × 1000/60)`: steps of 17, 17, 16. Rows are generated on a fixed grid, not from real frame times (confirms Luis review B2).
- `duration_ms` = last `timestamp_ms` (no extra frame interval). Our writer uses last + 1 interval; both are within tolerance.
- `created_at_utc` has second precision (confirms Luis review B1).
- `measured_offset_ms` is `0.0` in 8/10 sessions, and 28.5 / 6.4 in the two Outer Worlds sessions. `capture_latency_ms` is always `0.0`. Zeros therefore do not mean "measured zero" (confirms Luis review B3).

### Independent input→video lag measurement

Global horizontal image shift per frame (phase correlation on 160×90 grayscale), cross-correlated with `input_mouse_dx` over ±60 frames:

| Session | Best lag | Correlation | Reading |
|---|---|---|---|
| TOW-B61-S54 | +1 frame (+17 ms) | 0.36 | Well aligned; video trails input by ~1 frame, which is expected render latency |
| MM-110-S40 | +1 frame | 0.15 | Weak signal (third person / driving); inconclusive |
| MM-141-S65 | −4 frames (−67 ms) | 0.16 | Weak signal; inconclusive. A negative lag would mean video leads input, so worth a manual look |

The method works where the mouse drives the camera (first person). It needs a different signal for vehicles and third person (e.g. steering keys vs. flow, or camera-relative flow).

### Input semantics

- **Actions are binding labels, not single semantic actions:** `Aim/Block`, `Jump/Dodge`, `Attack/Power Attack`, `Punch / Shoot`, `Sprint / Brake`, `Roll / Evade / Boost`, `Jump/Handbrake`. A static key→label table cannot tell which meaning was active.
- **Action names contain spaces and `/`**, with inconsistent spacing (`Punch / Shoot` vs `Aim/Block`).
- **Key names are not normalized across games:** `LControlKey` (Mad Max, .NET `Keys` enum name) vs `Ctrl` (Payday 2).
- Mouse buttons map to actions (`Left` → Fire/Attack, `Right` → Aim). No frame has keys without actions.

## 3. Answers to open questions from the earlier reviews

| Question | Answer from this sample |
|---|---|
| Luis Q2: is `key_bindings.json` required? | It **was delivered** with every session. Who required it is still unknown |
| Luis Q3: literal `null` + uppercase `C2W_M..`? | **Confirmed as the delivered format.** Still no written confirmation from Odyssey that it is required |
| Luis Q7: real input→video lag? | ~1 frame on the one first-person sample with a clear signal; Mad Max inconclusive. Recorded offsets are mostly placeholder zeros |
| Luis Q8: polling / timer rows? | Rows sit on a synthetic 60 fps grid (timer-generated). Missed taps cannot be judged from the output alone |
| Luis Q10: OBS output settings? | Final files are H.264 CFR 60, 1080p, frame-exact to the CSV (after re-encode) |
| Vane Q5: video = rows + 1? | Not present here: this is specific to Vane's collectors |
| Spec Q (camera optional?) | Action-only deliveries existed as a tier in earlier rounds. Round 4's SOW still requires camera matrices |

Still open: Luis Q1 (DQ document), Q4 (hours / pass rate; session names like `B181` hint at batch numbering), Q6 (deleted collectors), Q9 (git history).

## 4. Implications for odyssey-capture

1. **Conventions:** switch `frames.NULL` to `"null"` and the C2W headers to uppercase, unless Odyssey says otherwise. This is the format that has been delivered and accepted. Make the validator accept the delivered convention.
2. **Keep shipping `key_bindings.json`** (same schema) until Odyssey confirms it is no longer needed. Generate it from the input-mapping profile (P1).
3. **Normalize key names** in one table (our `KeyNames`: `Ctrl`, not `LControlKey`). Add a validator warning for non-canonical key names.
4. **Action vocabulary:** one semantic action per token, no spaces or `/`. Ambiguous bindings should resolve to the context-specific action, or be logged as the raw binding plus a context segment. Add a validator warning for `/` inside action tokens.
5. **Offsets:** write `measured_offset_ms` only when measured (null otherwise). Add the motion-correlation grounding check, as a warning, for first-person sessions. The prototype above is ~30 lines of numpy.
6. **Timing:** use real per-frame timestamps (WGC QPC) and millisecond UTC. The synthetic grid hides real frame-time jitter.
