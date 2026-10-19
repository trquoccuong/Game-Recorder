# Warcraft III (Reforged / Classic / Legacy 1.29)

> Status: **candidate (RTS, needs Odyssey confirmation)** · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md)

## 1. Verdict

Warcraft III is a **rich multi-agent environment** (fog of war, body-blocking, team play, item handoff, shared neutrals) with an **official map-scripting route**: World Editor triggers in JASS, plus Lua on Reforged. A custom map can log state, events and visibility, and can hide the UI, without touching game memory.

It is **not a natural fit for the spec's camera model**:
- The POV is a top-down RTS camera driven by edge-scrolling and the minimap, not an egocentric camera.
- Mouse input is mostly `cursor_position`.

Recommendation: **confirm with Odyssey that RTS data is wanted (new question Q14)** before investing. If yes, it is a strong second genre.

**Platform warning:** Reforged **Patch 3.0 (Sept 2026) made the client always-online and removed LAN**. Offline/LAN play is only available through the Classic Client or the **Legacy TFT 1.29** client. This decides which scripting language and hosting model we can use (see §3).

## 2. Facts

| Item | Value |
|---|---|
| Engine | Proprietary Blizzard engine (2002; Reforged remaster 2020) |
| Genre / view | Real-time strategy, top-down perspective camera |
| Players | 2–12 (Classic 1.29), up to 24 (Reforged) |
| Netcode | **Deterministic lockstep**: clients exchange commands, every client simulates the full game. No dedicated server; one player hosts |
| Hosting | Reforged 3.0: Battle.net only (always-online, no LAN). Classic / Legacy 1.29: LAN + offline |
| Anti-cheat | Battle.net-side protection on online play. Our route uses only official map scripts + external screen capture, so nothing interacts with it |
| Map scripting | JASS (all versions); **Lua** on Reforged (1.31+); UI frame natives (`BlzHideOriginFrames`, `BlzFrameSetVisible`) on 1.31+ |
| Replays | `.w3g`: player **actions** only (lockstep), re-simulated by the client. Parsers: **w3gjs** (TS), w3grs/w3rs (Rust), wc3v (browser viewer) |
| Visuals | Classic: 2002. Reforged HD: 2020 remaster |

## 3. Version choice

| | Reforged 3.0 (current) | Legacy TFT 1.29 / Classic Client |
|---|---|---|
| Network | Always-online, Battle.net custom games (password lobbies) | **LAN / offline** on our own network |
| Scripting | JASS + **Lua**; UI frame API for HUD removal | JASS only; UI natives absent in 1.29 **[to verify per version]** |
| HUD-free video | `BlzHideOriginFrames(true)` + hide remaining frames | Harder: cinematic mode / letterbox tricks **[to verify]** |
| Visuals | HD (preferred for world models) | Classic only |
| Ops risk | Depends on Battle.net availability; Blizzard ToS on hosted games | Self-contained |

**Recommendation:** Reforged with private password-protected custom games, *if* Odyssey wants HD visuals and legal clears it. Otherwise Legacy 1.29 on LAN for a self-contained setup, accepting the classic look and weaker HUD control.

## 4. Data route (official map scripting + external capture)

Every client runs the same deterministic simulation, so **each client's map script sees the full game state**. "Server-side" state can therefore be logged by any client, or by a dedicated **observer slot** we control.

| Spec field | Source |
|---|---|
| `video.mp4` per player | External screen recorder on each PC (WGC/ddagrab, plan §7.4); no injection |
| HUD-free | Map script hides the UI on start (Reforged: `BlzHideOriginFrames(true)` + `BlzFrameSetVisible` on remaining frames). Selection circles / health bars: decide with Odyssey whether they count as HUD (Q9) |
| Camera pose (C2W) | Map script reads the **local player's** camera each tick (camera target X/Y, distance, angle of attack, rotation, FOV via the camera-field natives). These calls are local/asynchronous, so they must only be *logged*, never fed back into synced game logic |
| Intrinsics | Camera FOV field (default 70° **[to verify]**; vertical vs horizontal **[to verify]**) |
| Axes / units | WC3 world: X east, Y north, Z up in game units. Close to RFU; confirm handedness with a reprojection test |
| Mouse convention | `maps_to = cursor_position` (unit control), with `camera_pan_position` segments for edge-scrolling (spec v2 `input_mouse_convention_segments`) |
| `input_keys` / mouse | `odx_input.exe` on each PC (separate process, Raw Input) |
| `input_actions` | Player **orders** (move, attack, cast, build, item use) from map-script order events, or from the `.w3g` action stream |
| `server_state.bin` | Observer-slot map script: per-tick dump of all units (owner, type, position, facing, HP/mana, current order) |
| `events.jsonl` | Unit death, damage, hero level, item pickup/drop/give, building start/finish, shop purchases, game start/end |
| Visibility labels | **Native fog of war**: `IsUnitVisible(unit, player)` per player per tick. Ground-truth visibility, better than any FPS raytrace heuristic |
| Replay | `.w3g` per game, parsed with w3gjs for actions; also re-simulated in-client for QA |
| `packets.*` | Lockstep has no server packets; the `.w3g` action stream is the decoded network content (Q12) |

**Log output from map scripts:** map scripts have no general file API. Candidates are the `Preload` file-writing mechanism used for save/load systems, or syncing data to the observer slot and dumping from there. **This is the main technical unknown; spike it first.**

**Sync:** game time is identical on all clients (lockstep). Align video to game time with a start marker: the map script triggers a one-frame fade/flash at a known game time on every client, and the recorder detects it (plan §7.10.3 flash test).

## 5. Interaction coverage (SOW §2)

| Interaction | WC3 example | Label source |
|---|---|---|
| Visibility | Fog of war, scouting, invisibility | `IsUnitVisible` per player (exact) |
| Pursuit | Hero chases, creep-jacking | Unit positions + attack/move orders |
| Combat | Army fights, hero duels | Damage/death events |
| Cooperation | 2v2 / 4v4 team fights, shared control, healing | Allied units in the same fight; ally-targeted spells |
| Blocking | **Body-blocking / surrounding heroes** (iconic WC3 micro) | Unit pathing + collision proximity |
| Handoff | Item give/drop between allies; gold/lumber tribute | Item ownership events; resource transfer |
| Shared objects | Shops, neutral creep camps, gold mines, taverns | Ownership/usage events |
| Perspective consistency | One fight seen on several players' screens | Camera frustum of each player covering the same units |

## 6. Spec fit issues

| Issue | Impact | Proposal |
|---|---|---|
| Top-down RTS camera, not egocentric | Odyssey may not want RTS data | Ask (Q14) before the spike |
| Cursor-driven control | `input_mouse_dx/dy` are less meaningful; cursor position matters more | Mouse convention `cursor_position`; propose extra cursor columns (`additionalProperties` allows it) |
| Camera moves by jumps (minimap clicks, hotkeys) | Discontinuous C2W | Expected for RTS; flag jump frames in metadata |
| No server; host is a player | v3 assumes a dedicated server | Observer slot acts as the state logger; `server/` folder from the observer |
| UI-heavy game | HUD-free needs script control | Reforged frame API; agree with Odyssey what counts as HUD |

## 7. Risks

| Risk | Level | Mitigation |
|---|---|---|
| Odyssey doesn't want RTS | High | Ask first (Q14) |
| Reforged always-online (since 3.0): dependence on Battle.net, ToS on hosted games | High | Legal review; Legacy 1.29 LAN fallback |
| Getting logs out of map scripts (no file API) | High | Spike `Preload`-based writing and observer-sync |
| Async camera reads causing desyncs if misused | Medium | Read-and-log only; never use local values in synced logic; desync test in spike |
| Blizzard EULA / commercial footage | Medium | Legal review (Q13) |
| Classic visuals (1.29) | Medium | Prefer Reforged HD if allowed |

## 8. Effort estimate

- **Spike (3–4 days):**
  - logging path out of a map script
  - UI hiding
  - camera-field logging + reprojection test
  - observer-slot state dump
  - flash sync marker.
- **Production map kit (1–2 weeks):** a script library injected into a set of standard melee maps (2v2 / 4v4) plus `.w3g` parsing into the canonical events.

## 9. Sources

- Reforged 3.0 always-online, LAN removed: https://www.pcgamer.com/games/rts/warcraft-3-reforged-adds-always-online-requirement-removes-lan-mode/ · https://infinitestart.com/2026/09/warcraft-3-reforged-removes-lan/ · https://www.dtgre.com/2026/09/warcraft-3-reforged-3-0-lan-offline-play.html
- Reforged overview (Lua scripting): https://en.wikipedia.org/wiki/Warcraft_III:_Reforged · https://www.pcgamingwiki.com/wiki/Warcraft_III:_Reforged
- UI frames (`BlzHideOriginFrames`): https://www.hiveworkshop.com/threads/ui-originframes.316034/ · https://www.hiveworkshop.com/pastebin/e23909d8468ff4942ccea268fbbcafd1.20598
- Replay parsers: https://github.com/PBug90/w3gjs · https://github.com/tanghyd/w3grs · https://github.com/aesteve/w3rs · https://github.com/jblanchette/wc3v
- Lua tooling: https://sourceforge.net/projects/w3luaenv/
