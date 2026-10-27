# Software Plan for Multiplayer Data Collection — Odyssey Round 4

> Date: 2026-10-04 · Revision 2 (memory-reading extractor approach) · Based on: [SOW Round 4](./sow-round-4-multiplayer.md), [Capture Spec v2](./videogame-data-capture-spec-v2.md), [Multiplayer Spec v3](./multiplayer-data-capture-spec-v3.md) + research (memory-reading tooling, commercial & OSS games, capture tooling, prior art).
>
> Items marked **[to verify]** are research findings not yet confirmed against a primary source — they must be tested before being locked in.
>
> **Scope note:** the game lists here are a *reference*. Some titles may not be approved — by Odyssey, by the game's EULA, or by legal review. Every title carries a compliance status (§5.3) and must be cleared before production capture.

## Contents

1. [Executive summary](#1-executive-summary)
2. [Document analysis](#2-document-analysis)
3. [Conflicts & open questions for Odyssey](#3-conflicts--open-questions-for-odyssey)
4. [Research: lessons from prior art](#4-research-lessons-from-prior-art)
5. [Game selection & compliance](#5-game-selection--compliance)
6. [Proposed architecture](#6-proposed-architecture)
7. [Software modules](#7-software-modules)
8. [Tech stack](#8-tech-stack)
9. [Sizing: throughput, storage, cost](#9-sizing)
10. [Week-by-week roadmap (mapped to the SOW)](#10-week-by-week-roadmap)
11. [Staffing](#11-staffing)
12. [Risks & mitigations](#12-risks--mitigations)
13. [Immediate next steps (next 7 days)](#13-immediate-next-steps)
14. [References](#14-references)

---

## 1. Executive summary

**Problem:** Tbrain must deliver **20,000 hours** of synchronized multiplayer (multi-agent shared-state) gameplay data within **13 weeks** (deadline 2026-12-31).
- Per player: HUD-free POV video, per-frame C2W camera matrix + intrinsics, and per-frame keyboard/mouse input.
- Server side: per-tick state, events and network packets.
- On top of that: cross-agent interaction labels.

**Hardest constraints:**
- **A 1,000-hour calibration batch is due from week 2.**
- After that, a sustained pace of **1,800 hours/week** for 10 weeks.
- Camera pose, inputs and state must be **logged, not inferred**. Commercial games don't expose these, so we need our own extraction software.

**Core recommendation — build a reusable "Game Data Extractor" platform:**

1. **C++ core + per-game Lua profiles.**
   - The core is a DLL loaded into the game process (via ReShade add-on, ASI loader or injector). It owns:
     - the frame clock (`Present` hook)
     - video capture
     - Raw Input
     - signature scanning
     - axis conversion
     - writers that produce the canonical format
   - Each game is a **Lua profile**: where to find camera, players and events, which units and axes it uses, how to turn the HUD off, and how keys map to actions.
   - Adding a game means writing a profile, not a new app.
2. **Use the most stable data source available for each field.** In order:
   - official API, server plugin or demo
   - engine reflection framework (UE4SS, REFramework, CET, Script Hook, BepInEx)
   - GPU constant buffers (ReShade add-on)
   - raw memory via AOB signatures.
   External `ReadProcessMemory` polling is used **only for discovery and prototyping**: it isn't frame-synced and can return torn reads.
3. **Authoritative multiplayer state comes from the server, not client memory.**
   - Sources: server plugins (CounterStrikeSharp, AsaApi, Carbon/Oxide, BeamMP Lua, Arma Reforger Enforce Script) or demo files parsed after the match.
   - Client memory supplies only what the server can't: the rendered camera, FOV and local input.
4. **Capture raw data as richly as possible; derive labels later.** Every interaction label can be recomputed offline from server state, so calibration capture can start before the labeling engine exists.
5. **Run only where anti-cheat is absent or officially disabled.**
   - Examples: private servers with official flags such as `-insecure`, `-NoBattlEye`, `"battlEye": false`, `-NoEAC`; or single-player/offline modes.
   - We never connect to public matchmaking.
   - **Anti-cheat bypass is out of scope.** Each title is cleared by legal before production use.

---

## 2. Document analysis

### 2.1 How the three documents relate

```
SOW Round 4 (legal, volume, timeline, acceptance)
   └── requires data per ──► Spec v2 (canonical per-recording format)
                              └── extended by ──► Spec v3 (multiplayer: session, server, sync)
   └── adds its own deliverables: shared_events.csv, cross_agent_interactions.csv,
                                  shared_state_observations.csv  (NOT in v2/v3)
```

Spec v2's appendix already contains conversion notes for **GTA V, RDR2, Cyberpunk 2077 and Hogwarts Legacy**. Odyssey has evidently accepted memory/mod-extracted data from commercial games before. That supports the extractor approach.

Spec v3 §5, on the other hand, asks for games that are "self-hostable" and "open source, or have an open source engine to decode netcode". Commercial titles conflict with that criterion, so they need Odyssey's sign-off (Q12).

### 2.2 Consolidated data requirements (what the software must satisfy)

| Group | Requirement | Source | Software component |
|---|---|---|---|
| Video | 1080p/60 target, 720p/30 floor, HUD-free RGB, `video.mp4` | v3 §2, SOW §1 | Extractor core (capture) or demo re-render |
| Frame metadata | `frames.csv` with every v2 column (null if missing), C2W row-major RUB, world RFU Z-up, intrinsics **per frame** | v2 §1.1.2 | Extractor core + Lua profile |
| Input | `input_keys`, `input_actions` (pipe), `input_mouse_buttons`, `dx/dy` accumulated per frame, `"0.0"` when no mouse movement | v2 §1.1.2.6 | Raw Input + action mapping in profile |
| Metadata | `session.json` (schema 2020-12, `input_mouse_convention`), `player.json` (name, ping, ID, offset, RTT) | v2, v3 | Session orchestrator |
| Binary | `frames.bin` (full-precision floats), `packets.bin/txt`, `schema.bin.gz`, `server_state.bin` | v3 | Server plugin + Serializer |
| Server | Per-tick state (pos, orientation, health, weapon, alive…), `events.jsonl` (UTC) | v3 §3 | Server plugin / demo parser |
| Sync | NTP + offset/RTT measured at session start + post-hoc check via events; session valid if ≥K/N streams valid | v3 §4 | Clock sync + Sync validator |
| Task boundaries | By round; stop recording when a player dies and cannot respawn | v3 §1 | Orchestrator (event-driven) |
| Interaction labels | Timelines: visibility, pursuit, collision, combat, cooperation, blocking, handoff, shared-object; perspective-consistency samples | SOW §2 | Labeling engine |
| QA | 15% of each batch includes a `.rrd` (Rerun) + generating script | v2 §1.4 | RRD builder |
| Delivery | Cloudflare R2, path `<vendor>/<mm-dd-yyyy UTC upload>/<game>/<session-id>/` | v2 §1.1.1 | Uploader |
| Volume | 1,000 hrs calibration → 1,800 hrs/week (weeks 3–12) → 1,000 hrs final | SOW §5 | Ops dashboard / hours accounting |

### 2.3 Design consequences drawn from the documents

- **One clock for video and CSV.**
  - Video frame count must equal CSV row count exactly.
  - `timestamp_ms` must increase evenly.
  - So the extractor samples camera/input **inside the same `Present` hook** that captures the video frame.
- **`input_actions` must be semantic actions.** The profile resolves keys to actions by reading the game's own bindings (e.g. UE5 Enhanced Input mappings, `IS_CONTROL_PRESSED` in RAGE), or by hooking triggered actions.
- **Intrinsics must change per frame** when FOV changes (zoom). They must come from the **unjittered** projection or the gameplay camera FOV. TAA/DLSS/FSR add a per-frame sub-pixel jitter to the render projection matrix.
- **Axis conversion per engine:**
  - UE: left-handed, Z-up, X-forward, cm.
  - Unity: left-handed, Y-up.
  - RAGE: right-handed, Z-up.
  - Source: X-forward, Y-left, Z-up, inches.
  - Each profile declares its convention; the core converts to RFU/RUB and checks `det = +1` (see v2 appendix §2).
- **One round = one task.** The orchestrator listens to server events to start/stop.

---

## 3. Conflicts & open questions for Odyssey

These items **directly change the workload**, so they should be settled in writing at kickoff. SOW §6 locks the data scheme after the QA sample.

| # | Issue | Why it matters | Proposal |
|---|---|---|---|
| Q1 | **What does "20,000 hours" count?** Per-agent video hours, or session hours (each session has N POVs)? | **2–4×** difference in effort, storage and headcount | Settle on per-agent POV video hours |
| Q2 | Player count: the SOW says start with **2**, then **3–4**. Spec v3 says **4–10** | Affects game and map choice | Calibration with 2 players, then 3–4 |
| Q3 | `shared_events.csv`, `cross_agent_interactions.csv`, `shared_state_observations.csv` **have no schema** | Can't be accepted without a schema | Tbrain proposes schemas (§7.7) for approval in week 1 |
| Q4 | The SOW asks for "key bindings", but v2 **removed** `key_binding.json` | Avoid rejection over a missing file | Per-frame `input_keys` + `input_actions`; optional bind dump in `player.json` |
| Q5 | SOW §6 mentions a "**2,000-QA sample**", while calibration is 1,000 hours | Unclear when the scheme locks | Ask: 2,000 samples or hours, and when it locks |
| Q6 | Sync tolerance and **K** in "K/N valid streams" are undefined | Sync gate can't be automated | Propose ≤ 1 frame (16.7 ms) for demo re-render, ≤ 2 frames for live capture; K = N for 2 players, K = N−1 for ≥ 4 |
| Q7 | Formats of `frames.bin`, `server_state.bin`, `schema.bin.gz` are unspecified | Odyssey must be able to decode | Propose FlatBuffers/Protobuf with schema in `schema.bin.gz` |
| Q8 | For demo-replay titles (CS2), the re-rendered POV is **the server-recorded view**, not the exact pixels the player saw live | Affects action → frame correctness | Explain the tradeoff; include live-capture samples in calibration for comparison |
| Q9 | HUD-free: keep or remove the **viewmodel** (weapon in hand)? | Definition of "HUD-free" | Default: keep viewmodel; remove crosshair, HUD, text |
| Q10 | In continuous-respawn modes, what is a "round"? | Task boundaries | Use round-based modes or fixed time windows |
| Q11 | Game/action distribution is proposed by Tbrain and approved by Odyssey (SOW §2) | The game list is needed early | Send the shortlist in §5 in week 1 |
| **Q12** | **Spec v3 §5 asks for open-source / decodable netcode, but the best-looking titles are commercial** | Commercial titles need explicit sign-off; `packets.bin/txt` may be impossible to decode for closed netcode | Propose: commercial titles allowed when authoritative state comes from a server plugin or demo; `packets.*` delivered as raw + whatever is decodable, or waived per title |
| **Q13** | Does Odyssey's data licence cover footage from commercial games, and are there titles they refuse? | Legal exposure | Get Odyssey's allow/deny list before capture |
| **Q14** | Does Odyssey want RTS data (top-down camera, cursor-driven control), e.g. Warcraft III? | Decides whether to invest in a second genre with a different camera model | Ask before the Warcraft III spike |

---

## 4. Research: lessons from prior art

| Project | How data was captured | Lesson for us |
|---|---|---|
| **reshade_cv** | ReShade add-on (C++) + small per-game mod scripts; exports camera matrices + depth for Cyberpunk (CET), RE2R/RE3R (REFramework), Witcher 3, HZD, Crysis | **Closest existing design to ours**: "C++ core + per-game script". Study before building |
| **Matrix-Game 2.0** | Script Hook V recording plugin → MP4 + per-frame CSV (actions, camera, pose); plus Unreal Engine scenes | Script Hook path proven for GTA V at scale |
| **DeepGTAV / PreSIL / GTAVisionExport** | Script Hook + depth/stencil extraction | Mature GTA tooling to borrow from |
| **EgoCS-400K** (2026, CS2) | Pro demos **re-rendered** per POV through the game client, tied to demo ticks; parsed with demoparser2 | 10 POVs synchronized by construction. Best route to perspective-consistency |
| **Solaris** (NYU, 2026, Minecraft) | Controller bots + real "camera bot" clients; server plugin mirrors state | Scalable headless pattern; timestamps still aligned in post-processing |
| **MIRA** (2026, Rocket League) | 4 bot players, 10k hours | Bot data skews the action distribution; rare actions are missing |
| **MultiWorld** (2026, It Takes Two) / **PLAICraft** (2025) | Real humans, synchronized inputs with timestamps | Humans give natural cooperation; a single clock domain helps |
| **Hunyuan-GameCraft** | Poses **estimated** from video (SfM/VIPE), not logged | Counter-example: inferred poses are noisy. Log from the game |
| **awpy** (CS2) | Visibility = BVH + raycast; in-game `isSpotted` flag **unreliable** | Compute visibility ourselves (frustum + multi-ray) |

No standard published detector exists for pursuit, blocking or handoff. We write our own heuristics, version them and validate with human spot-checks.

---

## 5. Game selection & compliance

### 5.1 Data-access tiers (how each field is obtained)

| Tier | Source | Stability across game patches | Typical use |
|---|---|---|---|
| **T0** | Official API / server plugin / demo file (CounterStrikeSharp, AsaApi, Carbon, BeamMP Lua, Enforce Script, CS2 `.dem`, sim telemetry) | Highest | Authoritative multiplayer state, events, sometimes camera |
| **T1** | Engine reflection framework (UE4SS, REFramework, CET, Script Hook V/RDR2, BepInEx/MelonLoader, GMod Lua) | High — fields are reached by name | Client camera, FOV, HUD off, input actions |
| **T2** | GPU constant buffers via ReShade add-on | Medium–high | Exact view/projection the GPU used; engine-agnostic |
| **T3** | Raw memory via AOB signatures (RIP-relative), several fallback patterns per field | Low — re-verify every patch | Last resort for fields not covered above |
| — | External `ReadProcessMemory` polling | Not frame-synced | **Discovery/prototyping only** (Cheat Engine, ReClass.NET, x64dbg) |

### 5.2 Candidate games (reference list)

> Per-game analyses live in [`games/`](./games/README.md): detailed files for [CS2](./games/cs2.md), [CS:GO](./games/csgo.md) and [Warcraft III](./games/warcraft3.md), summary files for the rest.

| Rank | Game | Engine / view | Server state (T0) | Client camera | Anti-cheat — official no-AC route | Interactions | Notes |
|---|---|---|---|---|---|---|---|
| **1** | **Counter-Strike 2** | Source 2, FPS | CounterStrikeSharp (C#) plugins; `.dem` + demoparser2 / demoinfocs | **HLAE** re-renders each player's POV from the demo, HUD off, and exports camera pose + FOV per frame (`mirv_camio`) | VAC; official `-insecure` server flag | visibility, pursuit, combat, cooperation, body-blocking, weapon/bomb handoff, shared objects | Best fit for synced POVs. Narrow action set and limited maps; Metamod breaks on updates → pin builds |
| **2** | **Arma Reforger** | Enfusion, FPS/TPS + vehicles | Official Enforce Script server mods | Script mod reads camera transform + FOV per frame (T0/T1, **no raw memory**); HUD hideable by script | BattlEye; official `"battlEye": false` | **all types**, incl. vehicles & collisions | Modern visuals, 128 players. No replay found **[to verify]** |
| **3** | **ARK: Survival Ascended** | UE5, FPS/TPS | AsaApi C++ plugins | UE4SS (T1) **[to verify on ASA]** or ReShade cbuffer (T2) | BattlEye; official `-NoBattlEye` | combat, pursuit, cooperation (taming, building), blocking, shared objects | **Best visual fidelity** |
| **4** | **Rust** | Unity, FPS | Carbon/Oxide C# — server already knows **every player's eye pose + input state** | Server pose + client screen recording (sync by timestamp) | EAC stays on with Carbon/Oxide; `server.secure false` **[to verify]** | all types; strong building/blocking & item handoff | Clients stay unmodded — lowest client risk |
| **5** | **Assetto Corsa + CSP + AssettoServer** / **BeamNG.drive + BeamMP** | Vehicle sims | AssettoServer C# plugins / BeamMP Lua (`MP.GetPositionRaw`) | CSP Lua SDK / BeamNG Lua | None | pursuit, collision (BeamNG soft-body), blocking | Cleanest no-AC scripting routes for vehicles |
| 6 | Insurgency: Sandstorm | UE4, FPS | Official mod SDK / mutators | UE4SS (T1) | EAC; official `-NoEAC` | combat, cooperation, visibility | |
| 7 | Garry's Mod | Source 1 | Lua server | Lua client (`render.GetViewSetup`) | VAC per server; Lua is official | all types | Dated visuals |
| 8 | Palworld | UE5, TPS | Official mod loader (Windows server) | UE4SS Lua | None significant **[to verify]** | combat, cooperation, shared objects | |
| 9 | Assetto Corsa Competizione | UE4, vehicle | Official shared memory + broadcasting SDK | Replays re-shot from any car | None | racing collisions, blocking | Fallback with official replays |
| 10 | Satisfactory / Valheim / Arma 3 | UE5 / Unity / RV4 | SML / BepInEx / SQF | Same frameworks | None / optional | co-op building, combat | Lower priority |
| 11 | Other Unity titles with dedicated servers: V Rising, 7 Days to Die, Unturned, SCP: Secret Laboratory, Sons of the Forest | Unity (Mono or IL2CPP) | BepInEx / Oxide-style plugins on the server | Unity bridge (§7.9) | Varies per title **[to verify]** | combat, cooperation, building/blocking | Check backend + obfuscation before committing |
| alt | **CS:GO (legacy)** | Source 1, FPS | SourceMod plugins; GOTV `.dem` | HLAE re-render + `mirv_camexport` | VAC; official `-insecure` | same as CS2 | Frozen build: never breaks; older visuals. See [games/csgo.md](./games/csgo.md) |
| RTS | **Warcraft III** | Blizzard engine, top-down RTS | Official map scripting (JASS/Lua) from an observer slot; `.w3g` replays | Map script logs local camera; external screen capture | Reforged 3.0 always-online (no LAN); Legacy 1.29 for LAN | visibility (fog of war), body-blocking, cooperation, handoff, shared objects | Needs Odyssey confirmation (Q14). See [games/warcraft3.md](./games/warcraft3.md) |
| ref | GTA V / RDR2 / Cyberpunk 2077 / Hogwarts Legacy | RAGE / REDengine / UE4 | **Single-player only** (Script Hook closes RDR2 if Online starts) | Script Hook natives (`GET_FINAL_RENDERED_CAM_*`), CET, UE4SS | Story/offline only | — | Already in spec v2. **Not multiplayer-capable** for this SOW; reuse the profiles for single-agent work only |
| OSS | Xonotic / Unvanquished / Red Eclipse 2 / Luanti / SuperTuxKart | OSS engines | Engine patch + server demo | Engine patch | None | varies | Meets spec v3 §5 literally. Keep as fallback if Odyssey refuses commercial titles (Q12) |

**Excluded:** Valorant, The Finals, Hell Let Loose, Battlefield 6, Marvel Rivals (no self-hosting, kernel anti-cheat); Rocket League (EAC required even for private matches since Apr 2026); Squad (no official no-EAC route found).

### 5.3 Compliance status per title (to fill in with legal)

Every title passes this gate before production capture:

| Check | Owner |
|---|---|
| EULA/ToS: modding, reverse engineering, commercial use of footage | Legal |
| Official no-anti-cheat route exists and is used (no bypass) | Tech lead |
| Runs only on our private servers / offline; never public matchmaking | Ops |
| On Odyssey's allow list (Q13) | Account lead |
| Player consent & licence for recorded gameplay | Ops / Legal |

Status values: `approved` / `pending review` / `not allowed`. Only `approved` titles go into production. Others can be prototyped internally on private setups for reference.

**Recommendation:**
- **Calibration (2 players): CS2**, the fastest path to synchronized, HUD-free, exact-pose POVs, *if* Q12/Q13 allow it. Otherwise use **Arma Reforger** (scripted camera, no raw memory).
- **Weeks 3–4:** add Arma Reforger or ARK: SA for visuals, vehicles and cooperation.
- **Steady state:** Rust and one vehicle sim for collision/blocking diversity.
- Cap at **3–4 games**: each profile costs ~1–2 weeks plus maintenance after every game patch.

---

## 6. Proposed architecture

### 6.1 Three capture paths

| | **Path A: Demo re-render** (CS2) | **Path B: Live in-process extractor** (most games) | **Path C: Server pose + plain screen recording** (Rust-style) |
|---|---|---|---|
| Video | Re-rendered offline via HLAE, 1080p60, HUD off, strict CFR | Captured in the `Present` hook (back buffer → NVENC) | Recorded on player PC by `odx-record` (WGC, §7.4.1) |
| Camera pose | Exported per rendered frame | Lua profile via T1/T2/T3 sampled in the same hook | Server-side eye pose per tick |
| Cross-player sync | Built in: one demo, one tick clock | NTP + offset/RTT + server events | NTP + offset/RTT; interpolate server pose to frame time |
| Input | Demo usercmds + client Raw Input log | Raw Input in the core + action mapping | Server input state + client Raw Input |
| Client modification | None during play | Extractor DLL loaded into the game | None |
| Main risk | Rendered frame ≠ live pixels (Q8) | Patch breakage, jitter/axis bugs | Pose/frame alignment error |

### 6.2 Game Data Extractor — internal design

```
 ┌──────────────────────────── Game process ──────────────────────────────┐
 │  Loader: ReShade add-on | ASI loader | injector                         │
 │  ┌───────────────────── Extractor Core (C++ DLL) ────────────────────┐  │
 │  │ Present hook (Kiero/MinHook or ReShade event) = frame clock       │  │
 │  │  ├─ capture back buffer → NVENC (or QPC bridge to external rec.)  │  │
 │  │  ├─ call profile.sample(frame) → camera, fov, players, actions    │  │
 │  │  ├─ Raw Input thread (QPC-stamped) → accumulate per frame         │  │
 │  │  └─ push snapshot → lock-free ring buffer → writer thread         │  │
 │  │ Services: AOB/RIP signature scan · reflection bridges · cbuffer   │  │
 │  │           reader (ReShade) · math (C2W, K, axes, de-jitter)       │  │
 │  │ Embedded LuaJIT (sol2) ── loads ──► profiles/<game>.lua           │  │
 │  └───────────────────────────────────────────────────────────────────┘  │
 │  Optional bridges: UE4SS · REFramework · CET · Script Hook · BepInEx    │
 └────────────────────────────────┬────────────────────────────────────────┘
                                  │ frames.csv/bin, video.mp4, input log, metrics
                                  ▼
                       Capture Agent (Windows service): clock sync,
                       local validation, upload, control-plane link
```

**What a Lua profile contains:**

```lua
-- profiles/ark_sa.lua (illustrative)
return {
  game = "ARK: Survival Ascended", engine = "ue5", build = "pinned-build-id",
  axes = { world = "FRU_left_handed_zup", units_to_m = 0.01 },
  camera = {
    source = "ue4ss",                      -- or "cbuffer", "signature"
    path = "PlayerController.PlayerCameraManager",
    fov_axis = "horizontal",
    dejitter = true,
  },
  hud_off = { "showhud", widgets = { "WBP_HUD*" } },
  actions = { source = "enhanced_input" }, -- read mapping contexts
  mouse_convention = { maps_to = "camera_look_velocity",
                       dx_positive = "right", dx_negative = "left",
                       dy_positive = "down",  dy_negative = "up" },
  signatures = { },                        -- T3 fallbacks, several per field
}
```

### 6.3 System overview

```
 Player PCs (extractor + agent) ──┐            ┌── Game server (private, official no-AC flag)
                                  │            │    server plugin: state per tick, events, packets
                                  ▼            ▼
                 Control Plane (FastAPI) + Ops Dashboard + Prometheus/Grafana
                                  │
                                  ▼
       Post-processing pipeline (Prefect/Celery, GPU workers for re-render)
       ingest → [Path A: re-render] → build frames → validate → label
              → rrd (15%) → package + manifest → upload to Odyssey R2
                                  │
                                  ▼
                  QA Console (Rerun + spot-check UI) · Delivery reports
```

### 6.4 Design principles

1. **Record raw once, export many times.** Keep demos, server state, input logs and raw extractor snapshots, so every deliverable can be regenerated.
2. **Reflection over offsets.** Prefer T0/T1. Raw signatures (T3) are fallbacks with several patterns per field, tested against each new game build.
3. **Pin game builds.** Disable auto-update on capture machines and servers. A game patch triggers profile re-verification before capture resumes.
4. **Verify geometry by reprojection.** Every profile ships a test that projects known world points (other players' positions from the server) into the captured image. This catches axis, handedness, row/column-major, jitter and FOV-axis bugs.
5. **Idempotent by `session_id`**, with versioned extractor, profile, labeler and schema recorded in metadata.
6. **Fail fast at the edge:** validate on the player machine right after each round.

### 6.5 Languages & process boundaries

| Where it runs | Language | Components |
|---|---|---|
| **Inside the game process** | **C++** (pure native; never load the .NET runtime into a game) | Extractor DLL for Path B only (`Present` hook, in-process reads). Frozen until a Path B title is shortlisted |
| **Outside the game, on player PCs** | **C# (.NET 8)** | `odx-input` (Raw Input → `input_events.csv` + `clock_samples.csv`), `odx-record` (WGC, per-frame QPC → `video.mp4` + `video_frames.csv`), Capture Agent |
| **Game servers** | Dictated by each game | CounterStrikeSharp (C#), SourceMod (SourcePawn), Warcraft III map scripts (JASS/Lua), etc. |
| **Post-processing** | **C# (.NET 8)** | Build `frames.csv` (single implementation, `Odx.Core`), validator (`odx-validate`, source of truth for the spec), demo/replay parsing. Runs on player PCs (local validation before upload) and on Linux servers. Python only where its ecosystem is required: `.rrd` via `rerun-sdk` (no C# SDK) and ML-based labeling |

Rules at the boundaries:
1. **C++ ↔ C# talk through IPC between processes** (mailslot, named pipe, shared memory, files). P/Invoke only for small native helpers, with C-ABI functions and POD structs; no C++/CLI.
2. **Fixed wire formats with tests on both sides.** Example: the 49-byte `InputMsg` (C++ `extractor/src/win/common.hpp` ↔ C# `Odx.Core.InputMsg`). Structs use matching packing (`#pragma pack(1)` ↔ `StructLayout(Pack = 1)`); strings travel as fixed-size UTF-8.
3. **One clock:** QPC everywhere (`QueryPerformanceCounter` in C++, `Stopwatch.GetTimestamp()` in C#). `clock_samples.csv` maps QPC to UTC for alignment with server events.
4. **One implementation per format:** capture tools write raw logs only; `frames.csv` is built once, in C# (`Odx.Core`), and checked by `odx-validate`. Shared fixtures (e.g. `tests/fixtures/input_events.csv`) pin each log format across languages.
5. All binaries are x64.

---

## 7. Software modules

### 7.1 Extractor Core (C++ DLL) — the most important module

| Function | Approach |
|---|---|
| Loading | ReShade add-on (preferred: gives `present` event, back buffer, depth, cbuffers), ASI loader, or injector |
| Frame clock | Hook `IDXGISwapChain::Present` (or the ReShade event); keep hook work < 0.5 ms |
| Video | Copy back buffer → NVENC (HEVC/AV1), CFR, no B-frames, fixed GOP, MP4; one CSV row per encoded frame |
| Camera | Call the profile → camera position/rotation/FOV via T0–T3; convert to C2W (RFU/RUB) + `fx,fy,cx,cy` for the **output** resolution |
| De-jitter | Use the unjittered projection or gameplay FOV; if only the render cbuffer is available, record and subtract the TAA jitter |
| Pipelining | Account for render-thread lag (UE: 1–2 frames). Prefer the matrices of the frame being presented (cbuffer) when available |
| Input | Raw Input (`RIDEV_INPUTSINK`), QPC-stamped; accumulate dx/dy per frame; union of held and tapped keys; actions resolved by the profile |
| HUD off | Profile-defined: console commands (`showhud`), widget hiding (UMG), natives (`DISPLAY_HUD(false)`), or ReShade draw-call skipping by shader hash |
| Signature scan | AOB patterns with RIP-relative resolution, several fallbacks per field |
| Safety | Frame generation off, frame cap 60, VRR off; refuse to run unless the game build matches the profile's pinned build |

### 7.2 Game Profiles (Lua) + discovery toolkit

- One profile per game, using the schema in §6.2. Each profile ships with:
  - a reprojection test
  - an axis unit test (known poses: facing north, east, pitch ±45° → `det = +1`, `m20 ≈ 0`, `m21 > 0`)
  - the pinned build id
- **Discovery workflow** (documented runbook, internal tools only):
  1. RenderDoc/PIX frame capture → locate the cbuffer with view/projection, camera position and FOV.
  2. Cheat Engine / ReClass.NET → find the CPU-side copy (scan camera position, move, rescan; check orthonormal rows; `P00/P11 = 1/aspect`).
  3. x64dbg "find what writes" → build AOB signatures for T3 fallbacks.
  4. Prefer an existing reflection framework path if one exists (UE4SS `FindFirstOf("PlayerController")`, REFramework `via.Camera`, Script Hook `GET_FINAL_RENDERED_CAM_COORD/ROT/FOV`, Unity `Camera.main.cameraToWorldMatrix`).
- **Patch response:** CI job that launches each pinned game build, checks every signature/path resolves, and runs the reprojection test. Target: profile fixed within 1–2 days of a game update.

### 7.3 Server Plugins (per game)

| Game | Framework | Emits |
|---|---|---|
| CS2 | CounterStrikeSharp (C#) + `.dem` recording (`tv_enable`) | Per-tick player state, round/kill/bomb events; demo for Path A |
| Arma Reforger | Enforce Script server mod | Entity state, vehicle state, events |
| ARK: SA | AsaApi (C++) | Player/dino/structure state, combat, taming events |
| Rust | Carbon/Oxide (C#) | Eye pose + input state per tick, building, item transfers |
| AC / BeamNG | AssettoServer (C#) / BeamMP (Lua) | Vehicle pose, contacts, positions |

All plugins write the same internal format: `server_state.bin` (FlatBuffers/Protobuf + `schema.bin.gz`), `events.jsonl` (UTC), and `packets.*` where decodable (Q12). They also expose round events to the orchestrator.

### 7.4 Capture Agent (Windows service on the player machine)

- **C# (.NET 8)** service, deployed to every player PC (§6.5). Runs `odx-input` and `odx-record` (§7.4.1) as child processes.
- Responsibilities:
  - Register with the control plane.
  - Install and verify the pinned game build, extractor and profile.
  - Apply the standard config (resolution, VRR/V-Sync off, frame cap, fixed sensitivity, HUD settings).
  - Clock sync: Meinberg NTP, plus 4-timestamp pings to the game server, keeping the lowest-RTT samples. Write `clock_offset_ms` and `rtt_ms` to `player.json`, and map QPC → UTC every second.
  - Validate locally, then upload via rclone multipart, writing a SHA-256 manifest last.
  - Push metrics: dropped frames, hook time, encoder lag, disk, backlog.

#### 7.4.1 Live capture tools: `odx-input` and `odx-record` (implemented in `odyssey-capture/capture-cs`)

Both tools run **outside the game process**, and stamp every sample with **QPC**. They write into the same session folder, so their outputs line up without any extra sync step.

| Tool | Captures | Output files |
|---|---|---|
| `odx-input` | Keyboard + mouse via Raw Input (`RIDEV_INPUTSINK`) in its own process, so the game's own raw-input registration is never taken over | `input_events.csv` (`t_qpc_ms,kind,value,dx,dy`), `clock_samples.csv` (QPC ↔ UTC once per second) |
| `odx-record` | Game window (`--window <exe>`) or primary monitor via Windows.Graphics.Capture. Each frame's `SystemRelativeTime` is its QPC capture time | `video.mp4`, `video_frames.csv` (`frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate`), `recording.json` |

**Timing model of `odx-record`:**
1. WGC delivers frames at a variable rate (only when the window updates).
2. `GridResampler` (`Odx.Core`, unit-tested) maps them onto a constant-rate grid: tick *k* (t0 + k/fps) shows the most recent captured frame with time ≤ the tick.
   - Repeats are flagged `duplicate = 1`.
   - Frames replaced before any tick are counted as `dropped_source_frames`.
3. Each frame is cropped to an even size and converted to NV12 on the GPU (D3D11 video processor, BT.601 limited range), read back one frame later (no wait on the GPU), and written to ffmpeg by its own thread (the capture callback never waits for the encoder). Encoder (`auto`): `h264_nvenc`, else `h264_mf` (Media Foundation: Intel Quick Sync, AMD), else `libx264`; no B-frames, GOP 120.
   - Measured 2026-10-27 (i7-8750H, 1080p30, ffmpeg CPU): BGRA + x264 1.8 cores; NV12 + x264 1.2; NV12 + `h264_mf` 0.22. `h264_mf` repeats more frames in its first 1–2 s (encoder start-up on the GPU that also draws the desktop).
4. Result: a CFR MP4 whose frame *N* is row *N* of `video_frames.csv`. This is the "first-frame gate + fixed-grid resample" pattern from the ETS2 collector review.
5. `--obs <host:port>` (odx-app: "Record with OBS", for players who use OBS): OBS records instead, with the player's scene, output and audio settings, started and stopped over obs-websocket 5 (`ObsRecorder`, `Odx.Core`). The file's first frame is the one OBS renders when `StartRecord` arrives, frame *k* at t0 + k/fps, so t0 is the midpoint of the `StartRecord` round trip; `odx record` reports started once `GetRecordStatus` counts frames (the first-frame gate of the ETS2 review, [vane-ank-secret.md](./reviews/vane-ank-secret.md) recommendation 11). `outputDuration` is not a clock for t0: it starts counting ~1.1 s after `StartRecord`, once the output is up. OBS's file is remuxed into `video.mp4` (no re-encode) and deleted; `video_frames.csv` has one row per frame at t0 + k/fps, `duplicate` 0 (OBS does not say which frames repeat). OBS's encoder settings (B-frames, VFR) are the player's. Measured 2026-10-27 (OBS on this PC, Display Capture, 1280x720 at 30 fps, a full-screen window flashing at logged QPC times, 3 recordings of 10–30 s): t0 from `StartRecord` within 0–20 ms of the flashes (under a frame, display latency included), every run; t0 from `outputDuration` 1.09–1.11 s late; t0 from `StopRecord` − frames/fps 60–75 ms late (kept in `recording.json` as a cross-check). OBS also needs ~1.1 s after `StartRecord` before `odx record` says started.

**How `frames.csv` is built (C#, `Odx.Core`, single implementation):**
- frame times: `t_grid_qpc_ms` from `read_video_frames`;
- inputs: `read_input_events`, folded per frame by `aggregate_inputs`, with actions mapped from the game profile;
- camera columns: from the source with the matching QPC time (extractor or server pose), or null for action-only titles;
- `session.json`: UTC start from `recording.json` and `clock_samples.csv`.

Both log formats are pinned by fixtures (`tests/fixtures/*.csv`): the writers must reproduce them byte for byte and the readers must parse them.

**Windows acceptance checks before production:**

| Check | Pass criterion |
|---|---|
| Frame count | `ffprobe` packet count of `video.mp4` == rows of `video_frames.csv` |
| Clock assumption | `first_frame_capture_latency_ms` in `recording.json` is small and positive (a few ms to tens of ms). This confirms `SystemRelativeTime` and `QueryPerformanceCounter` share a timebase |
| Throughput | At a steady 60 fps game: `duplicate_frames` and `dropped_source_frames` are near zero over 10 minutes |
| Input ↔ video | Flash/marker test with the test game (§7.10.3) or motion correlation (`research/analyze-pose/analyze_pose.py`): lag ≤ 1 frame |
| Game unaffected | No input loss or frame-time regression in game with both tools running |

**Known limits / next steps:**
- Each frame is copied GPU → CPU and piped as BGRA to ffmpeg (~500 MB/s at 1080p60). This is the simplest correct path. If it bottlenecks, move to GPU colour conversion + Media Foundation / NVENC with D3D11 surfaces.
- Resizing the window mid-recording stops the recording with an error.
- Requires Windows 10 2004+ and ffmpeg (PATH or `--ffmpeg`).
- Not yet built: input-mapping profile → `input_actions`, F8 segment events, and the `frames.csv` / `session.json` builder that joins these logs (C#, on top of `Odx.Core`).

### 7.5 Session Orchestrator & Control Plane

- Dedicated servers in Docker (Linux where supported, Windows otherwise), one per match, with the build pinned and the official no-AC flag set.
- Session state machine: `scheduled → lobby → warmup → round_n (recording) → round_end → … → finalize`. Round events drive start/stop.
- When a player is permanently dead (cannot respawn), stop that player's stream (v3 §1).
- **FastAPI** + PostgreSQL control plane; agents connect over WebSocket. Commands: `arm`, `start_at=UTC+3s`, `stop`, `upload`, `health`.
- Player scheduling and matchmaking by game, map and mode to hit the approved action distribution. Bots are optional, capped, and flagged `is_bot`.
- Dashboard (Grafana + small web page):
  - hours recorded, validated and delivered vs. the 1,800/week target
  - errors by type
  - game, map and action distribution
  - profile health per game build.

### 7.6 Post-processing Pipeline

1. **Ingest:** verify manifest and checksums.
2. **Re-render (Path A only):** HLAE playback per player, HUD off, 1080p60, per-frame camera export.
3. **Build frames:**
   - join extractor snapshots + input log + server state → `frames.csv` (every column, correct nulls) and `frames.bin`
   - Path C: interpolate server eye pose to frame timestamps.
4. **Validate:**
   - every check in v2 §1.5 (JSON Schema, structure, C2W, intrinsics, input, ffprobe video)
   - v3 sync gate (align player events with server events, report offset in ms)
   - reprojection check on sampled frames.
5. **Labeling** (§7.7).
6. **RRD builder:**
   - random 15% per batch (seed recorded)
   - one `world/<pid>` branch per player (Transform3D + Pinhole + VideoFrameReference + input Scalars)
   - timelines `frame`, `wall`, `server_tick`
   - ships `rrd_creation.py` with a pinned `rerun-sdk`.
7. **Package & upload:** exact v3 tree, manifest, rclone to Odyssey's R2.

### 7.7 Labeling Engine (SOW-specific deliverables)

Proposed schemas for Odyssey's approval (Q3). Every table includes `session_id`, `server_tick`, `utc_ms`, `labeler_version`, `confidence`.

| File | One row = | Key columns |
|---|---|---|
| `shared_events.csv` | One event on the shared timeline | `event_id, type, start_tick, end_tick, start_utc, end_utc, actors[], object_id, position_xyz, source(server/derived)` |
| `cross_agent_interactions.csv` | One interaction between 2 agents over a window | `interaction_id, type(visibility/pursuit/collision/combat/cooperation/blocking/handoff/shared_object), agent_a, agent_b, start/end tick, metrics(json)` |
| `shared_state_observations.csv` | One observation (agent, tick, entity) | `observer_id, target_id, tick, in_frustum, visible_ratio, distance, screen_xy (via C2W + K)` |
| `perspective_samples.csv` (proposed addition) | One event seen by ≥ 2 agents | `event_id, observers[], frame_ids per observer` |

Detectors v1 (rule-based, configurable thresholds):
- **Visibility:**
  - frustum test (FOV from intrinsics), then multi-ray occlusion (head, chest, feet) → `visible_ratio`
  - rays are cast through a server-side trace where the plugin API offers one, or against an exported collision mesh (awpy-style BVH)
  - never trust in-game "spotted" flags.
- **Pursuit:** sustained ≥ N s; A's velocity toward B (cos > θ); A sees B; B moving away.
- **Combat:** damage/kill events + engagement window (encounter detection).
- **Collision / blocking:** bounding-box contact + sharp velocity drop of B toward A.
- **Handoff / shared-object:** ownership change of an object (bomb, weapon, item, ball) within a proximity window.
- **Cooperation:** same team + proximity + shared objective, or explicit support events (heal, revive, build, tame).
- **Perspective-consistency:** events with ≥ 2 observers where `visible_ratio > τ`.

Label QA: weekly stratified human spot-checks; precision reported per label type.

### 7.8 QA Console & Reporting

- Rerun for engineering, plus a small web UI showing player POVs side by side with label overlays and reprojected markers.
- AFK/idle detection, glitch detection (black frames, freezes), action-distribution skew.
- SOW acceptance tracking:
  - the 3-business-day window per batch (deemed accepted after it)
  - one cure per rejected deliverable
  - hours reconciliation for invoicing.

### 7.9 Unity Bridge & Unity-specific guidance

Unity games are among the easiest targets: Unity keeps full reflection metadata, so the extractor reads fields **by class/field name** instead of raw offsets (tier T1).

#### 7.9.1 Two scripting backends

| | **Mono** | **IL2CPP** |
|---|---|---|
| How to recognize | `<Game>_Data/Managed/Assembly-CSharp.dll` | `GameAssembly.dll` + `global-metadata.dat` |
| Reading game code | dnSpy / ILSpy decompile almost to source | Il2CppDumper / Cpp2IL recover class, field and method names |
| Calling from C++ | `mono_*` exports (`mono_get_root_domain`, `mono_class_from_name`, `mono_runtime_invoke`, …) | `il2cpp_*` exports (`il2cpp_domain_get`, `il2cpp_class_from_name`, `il2cpp_runtime_invoke`, …) |
| C# mod frameworks | BepInEx 5, MelonLoader, Harmony | BepInEx 6 (pre-release), MelonLoader + Il2CppInterop |
| Stability across game updates | Names stable | Stable unless obfuscated (e.g. Beebyte) or `global-metadata.dat` encrypted — then names change per build |

**Design:**
- Add a **Unity bridge** to the Extractor Core. It detects the backend and calls the `mono_*` / `il2cpp_*` exports directly, so BepInEx is not required.
- Lua profiles address Unity objects by name (e.g. `UnityEngine.Camera`, `fieldOfView`), the same way Cheat Engine's Mono/IL2CPP dissector works.
- When complex game-logic patching is needed, a C# BepInEx/MelonLoader plugin may be used instead; it sends snapshots to the core over shared memory.

#### 7.9.2 Camera & intrinsics

- **`Camera.cameraToWorldMatrix`** uses the OpenGL convention (camera looks along −Z). Its columns are already right/up/backward = **RUB**, as spec v2 requires; only the world axes need remapping.
- **Unity world:** left-handed, Y-up (X right, Y up, Z forward), usually metres.
  - Map to RFU: `X_rfu = x`, `Y_rfu = z`, `Z_rfu = y`.
  - The swap also converts left- to right-handed. Apply it to all three rotation axes and the position, then check `det = +1`.
- **FOV:** `Camera.fieldOfView` is **vertical**, in degrees → `fy = H / (2·tan(fov/2))`, `fx = fy`.
- **TAA jitter:** with TAA in HDRP/URP, `projectionMatrix` is jittered. Use **`Camera.nonJitteredProjectionMatrix`**.
- **Physical camera:** when `usePhysicalProperties` is on, `lensShift` offsets `cx, cy`.
- **Picking the right camera:**
  - `Camera.main` may not be the camera being rendered. Take the camera with `targetTexture == null` and the highest `depth`.
  - With a URP camera stack, take the base camera; ignore overlay and UI cameras.
  - With **Cinemachine**, the final pose exists only after `LateUpdate`, so sample in a render callback, not in `Update`.

#### 7.9.3 Frame sync (differs from other engines)

The Unity API may only be called on the **main thread**. Calling `Camera.main` from the `Present` hook (render thread) is unsafe and can crash the game.

1. Sample camera, input and state on the main thread in a render callback:
   - built-in pipeline: `Camera.onPreRender`
   - URP/HDRP: `RenderPipelineManager.beginCameraRendering`.
2. Tag the sample with `Time.frameCount` and push it to the ring buffer.
3. The `Present` hook captures the image and pairs it with the matching sample in frame order. With graphics jobs enabled, the render thread lags ~1 frame; account for that offset.
4. Verify with the reprojection test, as for every profile.

#### 7.9.4 HUD off

- **UGUI:** disable every `Canvas` (`canvas.enabled = false`). Screen Space – Overlay canvases are not drawn by a camera, so `cullingMask` does not hide them.
- **UI Toolkit:** `UIDocument.rootVisualElement.style.display = None`.
- **World-space UI** (nameplates, health bars above heads) is a World Space canvas; the profile lists these to disable explicitly.
- Check first for a built-in HUD toggle or console command.

#### 7.9.5 Input → semantic actions

- **New Input System:** read the `InputActionAsset` for action names (`Jump`, `Fire`…), then subscribe to `action.performed` or read `IsPressed()` each frame. This is an excellent source for `input_actions`.
- **Legacy Input Manager:** hook `Input.GetButton*` / `GetAxis` to see which actions the game queries.
- **Rewired** (common third-party plugin): read `ReInput.players` and their actions.
- Mouse deltas still come from Raw Input in the core, as for other engines.

#### 7.9.6 Multiplayer depends on the networking stack

| Networking | Self-hosted server | Authoritative state |
|---|---|---|
| Game ships its own dedicated server: Rust, Valheim, V Rising, 7 Days to Die, Unturned, SCP: Secret Laboratory, Sons of the Forest | Yes (SteamCMD) | BepInEx / Carbon / Oxide **on the server**. Many are headless Mono builds → easy |
| Mirror / FishNet / Netcode for GameObjects | Usually, if a dedicated build exists | Server plugin |
| **Photon** (PUN/Fusion), Steam P2P (e.g. Lethal Company, Content Warning) | Usually **no**: cloud relay or player-hosted | No authoritative server; state is assembled from the host client → weaker sync and reliability |

Photon/P2P titles remain possible but rank lower.

#### 7.9.7 Anti-cheat & Unity-specific risks

- Same rule as the rest of the plan. Many Unity games use EAC (Rust) or BattlEye (Tarkov). Run only on private servers with an official no-AC flag, or on titles without AC; exclude titles with no official route.
- Unity-specific risk: **obfuscated IL2CPP** or an encrypted `global-metadata.dat`. Class/field names then change per build and must be remapped after every update. Assess this at game-selection time.

#### 7.9.8 Example Unity profile

```lua
-- profiles/valheim.lua (illustrative)
return {
  game = "Valheim", engine = "unity", backend = "mono", build = "pinned-build-id",
  axes = { world = "unity_left_handed_yup", units_to_m = 1.0 },
  camera = {
    source = "unity",                     -- Unity bridge (mono_* / il2cpp_* exports)
    pick = "base_camera",                 -- targetTexture == null, highest depth
    c2w = "cameraToWorldMatrix",          -- already RUB
    projection = "nonJitteredProjectionMatrix",
    fov_axis = "vertical",
    sample_on = "beginCameraRendering",   -- main thread; paired with Present by frameCount
  },
  hud_off = { canvases = "all", ui_toolkit = true, world_space = { "Nameplate*" } },
  actions = { source = "legacy_input" },  -- or "input_system", "rewired"
  mouse_convention = { maps_to = "camera_look_velocity",
                       dx_positive = "right", dx_negative = "left",
                       dy_positive = "down",  dy_negative = "up" },
}
```

### 7.10 Testing strategy

Principle: **most tests must not need a real game, and every measurement must be compared against a known ground truth.** Five layers, from cheap and fast to expensive.

#### 7.10.1 Layer 1 — Pure-logic unit tests (no game, seconds in CI)

- **Axis conversion & C2W:**
  - Property-based tests (e.g. Hypothesis) over thousands of random rotations: `det = +1`, `R·Rᵀ = I`, bottom row `(0,0,0,1)`, round-trip conversion returns the input.
  - Known-pose fixtures per engine (UE, Unity, RAGE, Source): facing north, facing east, pitch ±45°.
- **Intrinsics:** vertical ↔ horizontal FOV, lens shift, de-jitter.
- **Per-frame input aggregation:**
  - Given a QPC-stamped event stream, dx/dy sums are exact.
  - Taps shorter than one frame are not lost.
  - Frames without mouse movement write `"0.0"`, not null.
- **Writers:** `frames.csv` has every column, nulls in the right places, `|` as delimiter.

#### 7.10.2 Layer 2 — Session fixture corpus (validator & pipeline)

- Generate one valid reference session by script, then derive **one variant per rule in v2 §1.5, each violating exactly that rule**. Examples: missing column, gap in `frame_id`, `det = −1`, video frame count ≠ CSV rows, naive timestamp.
- The validator must catch **100%** of the variants and raise no false positives on the valid session.
- **Golden sessions:**
  - Record real raw data once (extractor snapshots, input log, server state, demo).
  - The whole pipeline then runs offline and deterministically from it: build frames → label → `.rrd` → package.
  - CI compares outputs against golden files, with no game required.

#### 7.10.3 Layer 3 — In-house test game (the foundation)

A small multiplayer game built by us in **Unity** (plus a UE5 variant) where the ground truth is known 100%:

| Test | How | What it proves |
|---|---|---|
| **Marker reprojection** | Scene with ArUco/AprilTag markers and coloured blocks at known world coordinates. Project them with the extracted C2W + K and detect them in the video with OpenCV; error in pixels | Catches axis, handedness, row/column-major, FOV-axis, jitter and off-by-one-frame bugs in one test |
| **Frame ↔ CSV row** | The game draws a small QR code in a corner encoding `frameCount` + QPC; decode it from every video frame and compare with the CSV row | Video frame N corresponds exactly to CSV row N |
| **Multi-client sync** | The server broadcasts a "flash" event; every client changes screen colour at once; measure the frame where the flash starts in each POV | Measured cross-POV sync error — the evidence for the tolerance in Q6 |
| **Known input** | Inject a fixed input script with `SendInput` (dx = +100 for 10 frames, W, clicks…) | `frames.csv` records exactly those values; camera turns the right way → `input_mouse_convention` verified automatically |
| **Build matrix** | Same project built as Mono and IL2CPP, TAA on/off, URP and HDRP, with/without Cinemachine | Every code path of the Unity bridge is exercised |

We own this game, so there are no EULA or anti-cheat constraints, and it runs in CI. It also doubles as a demo for Odyssey during calibration.

#### 7.10.4 Layer 4 — Automated smoke test per real game (`profile-check`)

Runs on the **pinned game build** and after every game update:

1. Launch the game, load a map from the command line, play ~60 s with a bot or scripted input.
2. Assert every signature and reflection path resolves.
3. Assert invariants:
   - matrices are orthonormal
   - FOV is within a sane range
   - pressing W moves the position along the camera's forward direction
   - moving the mouse right turns yaw to the right.
4. Reproject other players' positions (from the server plugin) onto the image and export overlay images for a quick human pass in Rerun.
5. Report pass/fail. A failure blocks capture on that build.

#### 7.10.5 Layer 5 — "Session in a box" (end-to-end, no human players)

- One GPU machine runs at once: the Docker game server, 2–4 small-window clients driven by bots or scripted input, the Capture Agent and the control plane.
- Full cycle: round-driven start → capture → validate → label → `.rrd` → upload to a **test R2 bucket**.
- Runs nightly on a **self-hosted Windows GPU runner**. The Python pipeline runs on ordinary Linux CI.

#### 7.10.6 When to run what

| When | What | Needs a game? |
|---|---|---|
| Every commit | Layers 1 + 2 | No |
| Every PR touching the extractor | Layer 3 (test game) | Our own game |
| Nightly + every game update | Layers 4 + 5 | Real game, pinned build |
| Every production batch | Validator + sync gate + ~1% visual spot-check with overlays | No (captured data) |

**Priority:** if only one thing is built first, build the **Unity test game with markers, the frame QR code and the flash event** (~2–3 days). It answers the three hardest questions: is the pose correct, does each frame match its CSV row, and are the POVs synchronized.

---

## 8. Tech stack

| Layer | Choice | Rationale |
|---|---|---|
| Capture tools (outside the game) | **C# / .NET 8**: `odx-input` (Raw Input via P/Invoke), `odx-record` (WGC + Vortice D3D11 + ffmpeg/NVENC), agent | Easier than C++, full Win32/WinRT access, same language as CounterStrikeSharp (§6.5) |
| Extractor core (Path B only) | **C++20**, MinHook/SafetyHook, Kiero, ReShade add-on SDK, D3D11/12 + Vulkan | Standard for in-process hooking; frozen until a Path B title is shortlisted |
| Profile scripting (Path B only) | Lua 5.4 embedded in the core (64-bit integers for addresses) | Hot-reloadable profiles; frozen together with the extractor |
| Engine bridges | UE4SS (Lua/C++), REFramework (Lua), CET (Lua), Script Hook V/RDR2 (C++), BepInEx/MelonLoader (C#) | Reflection > offsets |
| Unity bridge | C++ calls to `mono_*` / `il2cpp_*` exports; Il2CppDumper / Cpp2IL / dnSpy for discovery; BepInEx/MelonLoader optional | Name-based access without offsets (§7.9) |
| Discovery (internal only) | RenderDoc, PIX, Cheat Engine, ReClass.NET, x64dbg, Frida | Find fields and build signatures |
| Server plugins | CounterStrikeSharp (C#), AsaApi (C++), Carbon/Oxide (C#), Enforce Script, AssettoServer (C#), BeamMP (Lua) | Per game |
| Demo tooling | HLAE, demoparser2 (Rust/Python), demoinfocs-golang | CS2 re-render + parsing |
| Encode | NVENC via NVENC SDK or FFmpeg (HEVC ~10 Mbps; AV1 on RTX 40+) | Hardware, CFR |
| Agent | C# (.NET 8) Windows service | Same stack as the capture tools |
| Backend | Python 3.12, FastAPI, PostgreSQL, Redis | Small team |
| Pipeline | Prefect (or Celery) | DAG + retries |
| Data | Polars / DuckDB, pyarrow, `jsonschema` Draft 2020-12, ffprobe | Validation at scale |
| Binary serialization | FlatBuffers or Protobuf | Schema → `schema.bin.gz` |
| Viz | `rerun-sdk` (pinned; research reports 0.38.x **[to verify]**) | Required by spec |
| Storage | Cloudflare R2 + rclone | Required by spec |
| Clock | Meinberg NTP (Windows), chrony (Linux), offset/RTT pings | w32time default too coarse |
| Monitoring | Prometheus + `windows_exporter` + Grafana + Alertmanager | Standard |

---

## 9. Sizing

Assumption: "hours" = per-agent POV video hours (Q1).

| Item | Estimate |
|---|---|
| Capture pace | 1,800 hrs/week ≈ 257 hrs/day |
| Players | ~30 effective hrs/week each → **~60 active players**, +20–30% buffer → **~75–80 people** |
| Overcapture | With ~15% rejection → **~2,100 raw hrs/week** |
| Video | HEVC 1080p60 ~10 Mbps ≈ 4.5 GB/hr → ~8–9 TB/week; **~90–110 TB** total |
| Upload | ~110 Mbps sustained for live capture (Paths B/C). Path A uploads only demos (MB/hr) |
| R2 | ~$0.015/GB-month → ~$1,500/month at 100 TB (internal storage) |
| Re-render farm (Path A) | ~12.5 continuous GPU streams at 1× realtime → **~6–10 GPUs** **[to verify by HLAE benchmark]** |
| Player PCs (Path B) | GPU must sustain 1080p60 + NVENC + hook overhead; RTX 3060-class or better **[to verify per title]** |
| `.rrd` | 15% × video size → ~15 TB extra |

If Q1 means "session hours", multiply by players per session.

---

## 10. Week-by-week roadmap

SOW signed 2026-09-30; ~1 week ramp-up → SOW week 1 ≈ Oct 5, week 13 ≈ Dec 28–31.

| SOW week | Phase | Software goals | Milestone / Exit criteria |
|---|---|---|---|
| **0–1** (Oct 4–11) | Spike + MVP | **Spike A (CS2):** private `-insecure` server + CounterStrikeSharp logger + demo → HLAE re-render 1080p60 HUD-off with per-frame camera export; measure render speed. **Spike B (extractor):** C++ core skeleton (ReShade add-on, Present hook, NVENC, Raw Input, LuaJIT) + first UE profile (ARK: SA or Insurgency) with reprojection test. **MVP:** v2 validator, `frames.csv` builder, uploader. Send Odyssey: game shortlist with compliance status, label schemas, Q1–Q13 | Go/No-go per path by day 3. One 2-player session end-to-end through the validator and viewable in Rerun |
| **1–2** (to ~Oct 18) | Calibration | Bulk 2-player capture on the first approved title; re-render farm v0 or extractor v0 on player PCs; packaging + 15% `.rrd`; hours dashboard; labeler v0 (visibility, combat, handoff) | **Deliver 1,000 calibration hours** in small batches; apply feedback |
| **3–4** | Ramp | Capture Agent fleet rollout; round-driven orchestrator; 3–4 players; 2nd title profile + server plugin; labeler v1 (all 8 types + perspective samples); QA console; profile CI | 1,800 hrs/week; validator pass ≥ 95%; scheme locked |
| **5–8** | Steady I | 3rd title; patch-response runbook proven; AV1; alerting; action-distribution tracking; label precision reports | Stable 1,800 hrs/week; capture → delivery ≤ 72 hrs |
| **9–12** | Steady II | Bulk rework tooling (re-render, re-label); hardening; decoding docs for Odyssey | ~19,000 hrs cumulative by end of week 12 |
| **13** | Wrap-up | Final QA/QC, final 1,000-hour drop, hand over docs + scripts, archive | Complete before 2026-12-31 |

**Fallbacks:**
- If commercial titles are refused (Q12/Q13), switch to the OSS list (Xonotic first): same pipeline, with an engine patch replacing the extractor profile.
- If a title's profile breaks on a game patch mid-production, keep capturing on the pinned build (auto-update disabled) and fix the profile in parallel.

---

## 11. Staffing

| Role | Count | Responsibilities |
|---|---|---|
| Tech lead / architect | 1 | Settle Q1–Q14, architecture, compliance gate with legal |
| Capture tools engineer (C#) | 1–2 | `odx-input`, `odx-record`, Capture Agent, clock sync |
| In-game extractor (C++ / Lua / engine frameworks) | 0–2 | Only if a Path B title is shortlisted: hooks, signatures, profiles, patch response |
| Server plugin engineer (C#/C++/Lua) | 1 | Server plugins, demo tooling (HLAE, demoparser2) |
| Backend / pipeline / DevOps | 2 | Control plane, agent, Prefect, render farm, R2, monitoring |
| Data / labeling | 1 | Labeling engine, validator, distribution analysis |
| QA / data ops | 1–2 | Spot-checks, QA console, acceptance tracking, player coordination |

About **7–11 people** in total (upper end only if a Path B title is shortlisted), plus ~75–80 players and coordinators, plus part-time legal review.

---

## 12. Risks & mitigations

| Risk | Level | Mitigation |
|---|---|---|
| Missing the 1,000-hour calibration in week 2 | **High** | Start with the path that needs least client work (CS2 demo re-render or Rust server pose); capture raw first, label later |
| Commercial titles refused by Odyssey or legal (Q12/Q13) | **High** | Clear the list in week 0; OSS fallback ready |
| Misreading the hours definition (Q1) | **High** | Settle it in writing in week 0 |
| Game patch breaks a profile mid-production | High | Pin builds, disable auto-update, profile CI, reflection (T0/T1) over raw offsets, multiple signatures per field |
| Anti-cheat / account bans | High | Only private servers with official no-AC flags or titles without AC; never public matchmaking; no bypass work |
| Wrong intrinsics from TAA/DLSS jitter or FOV axis | Medium | De-jitter rule per profile; horizontal vs vertical FOV declared; reprojection test |
| Axis/handedness bugs (det = −1, flipped pitch, cm vs m) | Medium | Known-pose unit tests + reprojection + v2 §1.5.3 checks |
| Render-thread lag (pose one frame off) | Medium | Prefer cbuffer matrices of the presented frame; measure the offset per engine with a reprojection test |
| Closed netcode → `packets.*` not decodable | Medium | Agree waiver or "raw + partial decode" per title (Q12) |
| Label schemas rejected after much capture | High | Schema approval in week 1; labels recomputable from raw data |
| Re-render ≠ live POV (Q8) | Medium | Calibration includes live samples for comparison |
| Bot dominance, missing rare actions | Medium | Cap bot ratio; action-distribution dashboard; player quests |
| Dropped frames / hook overhead on player PCs | Medium | Hook work < 0.5 ms; frame cap; reject sessions with skipped frames |
| Player staffing / AFK | Medium | AFK detection; pay per valid hour; 25% buffer |

---

## 13. Immediate next steps

1. **Send Odyssey** Q1–Q14, the reference game list with compliance status, and the draft label schemas. Prioritize Q1, Q3, Q12 and Q13.
2. **Legal:** start EULA review for the top 5 titles (CS2, Arma Reforger, ARK: SA, Rust, AC/BeamNG).
3. **Spike A — CS2 (3 days):**
   - Private server with `-insecure` + CounterStrikeSharp logging state and events.
   - Record a demo of a 2-player match.
   - Re-render each POV with HLAE at 1080p60, HUD off, exporting the camera (`mirv_camio`).
   - Measure render speed and frame-count exactness; parse the demo with demoparser2.
4. **Spike B — Extractor core (in parallel):**
   - ReShade add-on skeleton: Present hook, back buffer → NVENC, Lua profiles; input from `odx-input --forward` (C#, §6.5).
   - First profile for one UE title via UE4SS bridge **and** via cbuffer, to compare.
   - Reprojection test against server-reported player positions.
   - Study reshade_cv's design first.
5. **Test game (2–3 days, §7.10.3):** Unity multiplayer scene with ArUco markers, frame QR code, flash sync event and scripted input; Mono + IL2CPP builds. It becomes the first target of Spike B.
6. **In parallel:** v2 §1.5 validator + JSON Schema (valid schema already in [spec v2](./videogame-data-capture-spec-v2.md)), R2 uploader with manifest, Rerun builder skeleton.
7. **Windows smoke test of `odx-input` + `odx-record`** against the acceptance checks in §7.4.1, then build the C# `frames.csv` / `session.json` builder that joins their logs.
8. **Ops:** recruit and schedule the first player cohort (~20 people); standardize PC configuration (GPU class, VRR off, frame cap).
9. **Proposed repo layout:**

```
odyssey-capture/
├── capture-cs/              # C# (.NET 8): odx-input, odx-record (WGC), capture agent (outside the game)
├── extractor/               # C++ (Path B only, frozen): in-game DLL, Lua profile host, math
│   ├── bridges/             # UE4SS / REFramework / CET / ScriptHook / BepInEx / Unity (mono, il2cpp) bridges
│   └── profiles/            # <game>.lua + reprojection & axis tests
├── capture-cs/src/Odx.Core, Odx.Validate   # C#: frames builder, validator (source of truth)
├── server-plugins/          # cs2-css/, asa-api/, rust-carbon/, reforger/, beammp/
├── demo-tools/              # HLAE scripts, demo parsing
├── agent/                   # Windows capture agent
├── control-plane/           # FastAPI + dashboard + orchestrator
├── pipeline/                # Prefect flows: re-render, build, validate, label, rrd, package
│   ├── validator/
│   ├── labeling/
│   └── rrd/
├── test-game/               # Unity (+ UE5) ground-truth test game: markers, frame QR, flash sync
├── tests/                   # unit tests, session fixture corpus, golden sessions
├── schemas/                 # session_schema.json, label CSV schemas, flatbuffers
└── docs/                    # discovery runbook, patch-response runbook, compliance register
```

---

## 14. References

**Memory-reading & engine frameworks**
- RE-UE4SS: https://github.com/UE4SS-RE/RE-UE4SS · Lua API: https://docs.ue4ss.com/lua-api/global-functions/findfirstof.html
- REFramework: https://github.com/praydog/REFramework · https://cursey.github.io/reframework-book/
- Cyber Engine Tweaks: https://github.com/maximegmd/CyberEngineTweaks
- Script Hook V / RDR2: https://dev-c.com/gtav/scripthookv/ · https://www.dev-c.com/rdr2/scripthookrdr2/ · Natives: https://docs.fivem.net/natives/
- BepInEx: https://github.com/BepInEx/BepInEx · UnityExplorer: https://github.com/sinai-dev/UnityExplorer
- Unity docs: Camera.cameraToWorldMatrix https://docs.unity3d.com/ScriptReference/Camera-cameraToWorldMatrix.html · Camera.nonJitteredProjectionMatrix https://docs.unity3d.com/ScriptReference/Camera-nonJitteredProjectionMatrix.html
- Il2CppDumper: https://github.com/Perfare/Il2CppDumper · Cpp2IL: https://github.com/SamboyCoding/Cpp2IL · MelonLoader: https://github.com/LavaGang/MelonLoader
- ReShade add-on API: https://crosire.github.io/reshade-docs/ · Kiero: https://github.com/Rebzzel/kiero
- Cheat Engine Lua: https://wiki.cheatengine.org/index.php?title=Lua · ReClass.NET: https://github.com/ReClassNET/ReClass.NET · x64dbg: https://x64dbg.com · Frida: https://frida.re
- reshade_cv (prior art): https://github.com/jasonbunk/reshade_cv
- DeepGTAV: https://github.com/David0tt/DeepGTAV · GTAVisionExport: https://github.com/umautobots/GTAVisionExport

**Games & server frameworks**
- CS2 modded server / CounterStrikeSharp: https://github.com/kus/cs2-modded-server · https://github.com/roflmuffin/CounterStrikeSharp
- HLAE (Source 2 commands): https://github.com/advancedfx/advancedfx/wiki/Source2:Commands
- CS2 demo parsers: https://github.com/LaihoE/demoparser · https://github.com/markus-wa/demoinfocs-golang
- Arma Reforger server config: https://community.bistudio.com/wiki/Arma_Reforger:Server_Config
- ARK: SA server config: https://ark.wiki.gg/wiki/Server_configuration · AsaApi: https://ark-server-api.com/resources/asa-server-api.31/
- Rust Carbon: https://wiki.facepunch.com/rust/Carbon
- CSP Lua SDK: https://github.com/ac-custom-shaders-patch/acc-lua-sdk · BeamMP: https://docs.beammp.com/scripting/server/latest-server-reference/
- Insurgency: Sandstorm server admin: https://mod.io/g/insurgencysandstorm/r/server-admin-guide
- Rocket League EAC change: https://dotesports.com/rocket-league/news/bakkesmod-rocket-league-anti-cheat-update

**Capture tooling**
- FFmpeg ddagrab: https://ayosec.github.io/ffmpeg-filters-docs/8.0/Sources/Video/ddagrab.html
- WGC `SystemRelativeTime`: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime
- Raw Input / high-DPI mouse: https://learn.microsoft.com/windows/win32/dxtecharts/taking-advantage-of-high-dpi-mouse-movement
- Windows time high accuracy: https://learn.microsoft.com/en-us/windows-server/networking/windows-time-service/configuring-systems-for-high-accuracy
- Meinberg NTP: https://www.meinbergglobal.com/english/sw/readme-ntpinstaller.htm
- Rerun video: https://rerun.io/docs/reference/video · PyPI: https://pypi.org/project/rerun-sdk/
- R2 pricing / limits / rclone: https://developers.cloudflare.com/r2/pricing · https://developers.cloudflare.com/r2/platform/limits · https://developers.cloudflare.com/r2/examples/rclone/
- windows_exporter: https://github.com/prometheus-community/windows_exporter

**Prior art (datasets)**
- EgoCS-400K: https://arxiv.org/html/2606.18180v1
- Solaris: https://arxiv.org/abs/2602.22208
- MIRA: https://arxiv.org/abs/2607.05352
- MultiWorld: https://arxiv.org/html/2604.18564v1
- PLAICraft: https://arxiv.org/abs/2505.12707
- Matrix-Game: https://arxiv.org/abs/2506.18701 · https://arxiv.org/abs/2508.13009
- Hunyuan-GameCraft: https://arxiv.org/abs/2506.17201
- awpy visibility: https://awpy.readthedocs.io/en/latest/examples/visibility.html
- Odyssey-2: https://odyssey.systems/introducing-odyssey-2

**OSS fallback games**
- Xonotic `sv_autodemo_perclient`: https://xonotic.org/doxygen/darkplaces/sv__main_8c.html
- Unvanquished: https://github.com/Unvanquished/Unvanquished/releases · Luanti: https://blog.luanti.org/2025/10/05/5.14.0-released/
