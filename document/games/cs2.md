# Counter-Strike 2 (CS2)

> Status: **candidate #1** · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md) · Plan: [software-plan.md §5](../software-plan.md#5-game-selection--compliance)

## 1. Verdict

CS2 is the strongest commercial fit for synchronized multi-POV capture. One server-recorded demo yields **N HUD-free POV videos with exact per-frame camera poses** through HLAE, and a server plugin gives authoritative state and events. **No client modification during play is needed.**

Main weaknesses:
- a narrow action set (one genre, few maps)
- Valve EULA questions for commercial use (legal review)
- frequent game updates that break server mods.

## 2. Facts

| Item | Value |
|---|---|
| Engine | Source 2 (Valve), 2023– |
| Genre / view | Tactical team FPS, first person |
| Players | 2–10 (5v5 competitive; Wingman 2v2 fits the "start with 2" SOW phase) |
| Dedicated server | Yes, SteamCMD app 730 |
| Server plugins | Metamod:Source + **CounterStrikeSharp** (C#/.NET) |
| Anti-cheat | VAC. Our own server runs with the **official `-insecure` launch flag** (plugins allowed; server not VAC-secured) |
| Replay | `.dem` demos recorded on the server (`tv_enable 1`, `tv_record`); contain every player's state per tick |
| Demo tooling | **HLAE** (`mirv_camio` camera export, HUD-free re-render); **demoparser2** (Rust/Python), **demoinfocs-golang** |
| Tick rate | 64 Hz with sub-tick input timing |
| Visuals | Modern (2023), the best-looking title with a mature replay pipeline |

## 3. Data route (official tools only)

**Path A — server demo + offline re-render** (plan §6.1). Nothing is injected into the game client during play.

| Spec field | Source |
|---|---|
| `video.mp4` per player | HLAE re-renders each player's POV from the server demo at fixed 60 fps, HUD off (`cl_drawhud 0`; crosshair/viewmodel per Q9) |
| C2W + FOV per frame | HLAE `mirv_camio` export in the same render pass, so pose and frame correspond 1:1 |
| Intrinsics | From the exported FOV. Source FOV is defined at 4:3 (Hor+): convert to the actual 16:9 horizontal FOV before computing `fx` **[to verify against HLAE export semantics]** |
| Axes / units | Source world is X forward, Y left, Z up (`quake` convention in `geometry.py`); units are inches → metres |
| `input_keys` / mouse dx/dy | `odx_input.exe` (separate process, Raw Input) on each player PC during the live match, QPC-stamped; aligned to demo ticks via server events |
| `input_actions` | Button state from the demo per tick (attack, jump, duck, use…) cross-checked with key log + bind config |
| `server_state.bin` | CounterStrikeSharp plugin: per-tick position, eye angles, velocity, health, armor, weapon, money, team, alive |
| `events.jsonl` | Plugin game events: round start/end, `player_hurt`, `player_death`, bomb plant/defuse/drop/pickup, weapon drop/pickup |
| `packets.*` | Not decodable from the closed netcode; the demo is the decoded equivalent (Q12) |

**Out of scope:** reading CS2 client memory. The client is VAC-protected, and the demo + plugin route already covers every field.

## 4. Interaction coverage (SOW §2)

| Interaction | How it shows up | Label source |
|---|---|---|
| Visibility | Peeking, holding angles | Plugin ray traces between eye positions (server-side trace) + frustum from exported FOV. Do **not** use `m_bSpotted` (unreliable, per awpy) |
| Pursuit | Rotations, chasing after a pick | Position/velocity series |
| Combat | Duels, trades | `player_hurt` / `player_death` |
| Cooperation | Trades, flashes for teammates, crossfires | Same-team proximity + utility events |
| Blocking | Body-blocking in corridors | Contact + velocity drop |
| Handoff | Weapon drops between teammates, bomb pass | Drop/pickup events with different owners |
| Shared objects | Bomb, doors, dropped weapons | Ownership events |
| Perspective consistency | Both duelists see the same fight | Mutual visibility windows |

## 5. Capture setup

1. Private dedicated server (Docker/Linux) with `-insecure`, CounterStrikeSharp, `tv_enable 1`; pinned build, auto-update off.
2. Players connect from normal clients; each PC runs `odx_input.exe` + NTP sync.
3. The plugin starts/stops `tv_record` and writes state and events per round (one round = one task, v3 §1).
4. Post-processing: HLAE re-render of each POV + `mirv_camio` export → `frames.csv`; join inputs; validate.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Valve EULA / commercial footage | Legal review before production (Q13) |
| CS2 updates break Metamod/CounterStrikeSharp | Pin server + client build; plugin CI |
| Re-rendered POV ≠ live pixels (Q8) | Calibration includes live-capture comparison samples |
| Input alignment (live keys vs demo ticks) | Sync via events + NTP; cross-check against demo button state |
| Narrow action distribution | Mix modes (Wingman, Retake, Deathmatch); add other titles |
| Re-render GPU cost | Benchmark HLAE throughput in Spike A |

## 7. Open questions

- Does HLAE `mirv_camio` export horizontal or vertical FOV, and in which axis convention? (Spike A)
- How much input detail is recoverable from server demos alone? (would remove the need for `odx_input` on clients)

## 8. Sources

- CS2 modded server / CounterStrikeSharp: https://github.com/kus/cs2-modded-server · https://github.com/roflmuffin/CounterStrikeSharp
- HLAE Source 2 commands: https://github.com/advancedfx/advancedfx/wiki/Source2:Commands · https://doc.hlae.site/commands/AfxHookSource/mirv_camio
- Demo parsers: https://github.com/LaihoE/demoparser · https://github.com/markus-wa/demoinfocs-golang
- awpy visibility notes: https://awpy.readthedocs.io/en/latest/examples/visibility.html
- EgoCS-400K (CS2 demo re-render dataset): https://arxiv.org/html/2606.18180v1
