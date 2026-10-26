# Arma Reforger

> Rank in plan: 2 · Compliance: `pending review` · Last checked: 2026-10-16
> Back to [games index](./README.md) · Source: [software-plan.md §5.2](../software-plan.md#52-candidate-games-reference-list)

## 1. Verdict

Arma Reforger is the **richest multi-agent sandbox** on the list: infantry, crewed vehicles (driver + gunner + passengers), helicopters, large open terrain (Everon, Arland), up to 128 players. Bohemia ships an **official, documented modding route** (Enfusion Workbench + Enforce Script, free with the game) and an **official no-anti-cheat switch** (`"battlEye": false` in the server config). Both the server state and each player's camera can be read by our own script mod, with no memory reading and no injection.

Unlike CS2 there is **no official replay/demo**: everything must be logged live by a script mod (server state on the server, camera + FOV on each client), and video is recorded live by `odx record`.

Main weaknesses:
- **Licensing:** the EULA licenses the game for **non-commercial use only**. Commercial capture needs written permission from Bohemia (legal review, Q13). This is the gating item, more than the technology.
- **New work:** a server-side module (`Odx.Games.ArmaReforger`) and two script components (server logger, client camera logger) must be written; nothing of ours covers Enfusion today.
- **Clock alignment** of the client camera log with `odx record` frames: script code has no QPC clock (see §3, §7).
- Frequent game updates (roughly monthly) can break script mods; the dedicated server and clients must run the same build.

Recommendation: shortlist as the second FPS title and **contact Bohemia for a commercial licence first**; in parallel, a 3–5 day spike on the camera logger and its clock alignment.

## 2. Facts

| Item | Value |
|---|---|
| Engine | Enfusion (Bohemia, in-house); Reforger is the test bed for Arma 4 |
| Genre / view | Military sandbox FPS, first person (third person optional; servers can disable it with `disableThirdPerson`) + ground vehicles and helicopters |
| Release | Early access May 2022 (PC, Xbox); 1.0 on 16 Nov 2023; PS5 on 12 Dec 2024 |
| Steam app ids | Game **1874880**; dedicated server **1874900** (experimental 1890870); Arma Reforger Tools (Workbench) **1874910** |
| Players | `maxPlayers` 1–128 per server (default 64) |
| Dedicated server | Yes, native **Windows and Linux** binaries (`ArmaReforgerServer`), SteamCMD anonymous login; JSON config (`-config`) |
| Ports (defaults) | **2001/udp** game traffic (`bindPort` / `publicPort`); 17777/udp A2S query; 19999/udp RCON (BattlEye RCon protocol). No TCP. Enfusion netcode is UDP only |
| Server rate | Server simulation runs at the server's frame rate, capped by `-maxFPS` (60 recommended by hosts). No fixed tick rate is documented **[to verify]** |
| Anti-cheat | BattlEye, `gameProperties.battlEye` default `true`. Official route: `"battlEye": false`. Console players cannot join a server with BattlEye off; PC players can |
| Crossplay | `crossPlatform` / `supportedPlatforms` (PC, Xbox, PSN). Not needed: our servers are PC only with BattlEye off |
| Modding | Workbench + **Enforce Script** (C-like, OOP). All mods are server-defined: clients download the server's mod list automatically from the Bohemia Workshop on join; there are no client-only mods. Mods can be published **Unlisted** (not searchable, still usable by a server through its id) |
| Script I/O | `FileIO` writes to `$profile:` / `$logs:` (also on clients); `RestApi` / `RestContext` sends HTTP GET/POST (payload < 1 MB) |
| Replay | **None built in.** Community mods only ("Reforger Replays" by Seskel: server writes a `.bin` to its profile; "GRAD Spectator", Game Master camera). None exports per-POV camera |
| Client process | `ArmaReforgerSteam.exe` (Steam build) |
| Coordinates | **Left-handed, Y up**: X west→east, Y floor→sky, Z south→north; metres |
| Visuals | Modern (2022–2026), large terrains, day/night and weather |

## 3. Data route (official tools only)

**Path B-script — live capture with our own Enforce Script mod** (comparable to plan §6.2, but the camera comes from the game's own script API, not from memory). The mod is a normal Workshop mod (Unlisted) in the server's `mods` list; every client downloads and runs it. Its server part runs only where `Replication.IsServer()`; its client part only on the local player's machine.

| Spec field | Source |
|---|---|
| `video.mp4` per player | `odx record` (WGC) on each player PC, live; nothing injected |
| HUD-free | Client part of the mod hides the HUD through `SCR_HUDManagerComponent` (layers / `Show(false)` on every info display) and turns off nametags, hints, notifications, chat and weapon readouts. Interaction prompts and radial menus are drawn by other widgets and need separate handling **[to verify]**. Community "HUD Key" mod shows that most of the HUD can be hidden by script. Crosshair: Reforger has none by default in first person (ADS uses sights) **[to verify per difficulty setting]** |
| C2W + FOV per frame | Client part each frame: `GetGame().GetCameraManager().CurrentCamera()` → `CameraBase.GetWorldCameraTransform(out vector mat[4])` and `GetVerticalFOV()` (degrees, vertical), plus near/far planes. Written to `$profile:` as camera samples |
| Intrinsics | From the **vertical** FOV and the render resolution: `fy = (h/2) / tan(vfov/2)`, `fx = fy`, principal point at the centre — **verified by the spike** (fitted vFOV 73.998° vs logged 74°, fx/fy 1.00007, centre within 0.02 px at 1280×720). `GetVerticalFOV()` follows aiming: 880 zoomed samples (50.8°–74°) reproject at 0.0 px with the per-frame logged FOV (multiplayer run). PIP scopes render a second camera: frames while aiming through a PIP scope need a flag **[to verify]** |
| Axes / units | Enfusion: left-handed, X east, Y up, Z north, metres. `mat[0..2]` are the camera's right, up, forward axes (rows, det +1) and `mat[3]` its position — **verified by the spike**: with that layout and the logged FOV, 2098 samples reproject onto the engine's own `ProjWorldToScreenNative` at 0.0 px RMS (next-best layout 213 px); pose from the previous / next frame is off by a median 3.4 px, so transform and projection belong to the same frame; convert to the spec's axes in the converter |
| Camera clock | Script time is game time (`System.GetTickCount()`, ms since game start), not QPC. A one-frame flash marker at logged ticks, found again in `odx record`'s video, maps tick → QPC (plan §7.10.3 flash test), as for Warcraft III. **Spike:** offline 17 / 17 flashes paired, residual max 31 ms = 1.85 video frames (game ~40 fps); multiplayer 23 / 23, residual max 20 ms = 1.20 frames, std 9 ms (game near 60 fps). Inside plan Q6's 2 frames for live capture, not the 1-frame target. The marker must be paired by offset, not by order: a one-frame flash can be dropped by the recorder |
| `input_keys` / mouse dx/dy | `odx input` (Raw Input, separate process) on each player PC |
| `input_actions` | Profile bindings (key → action, `profiles/arma-reforger.json`), cross-checked with the server log's stance, weapon and vehicle-seat state. Reforger actions are context-dependent (same key on foot vs in a vehicle): the server log tells which context applied |
| `server_state.bin` | Server part, per server frame or at a fixed rate (e.g. 30 Hz): every player-controlled character and vehicle — position, orientation, velocity, stance, health, current weapon, vehicle + compartment (driver/gunner/cargo), faction, alive |
| `events.jsonl` | Server part hooks: `SCR_BaseGameMode.OnPlayerKilled` / `OnControllableDestroyed` (victim, killer, `Instigator`), damage manager hit events, compartment enter/exit, item pick-up/drop/transfer between inventories, vehicle collisions, base capture/supply events (Conflict), player connect/spawn |
| Event delivery | Server part writes JSONL to `$profile:` (collected by `odx game-server`) and can also `POST` round events to the orchestrator through `RestContext` |
| `packets.*` | pktmon on `udp/2001` with `protocol: raw`. Enfusion's replication protocol is undocumented and may be encrypted **[to verify]**; the server log is the decoded equivalent (Q12) |

**Out of scope:** reading client memory. Even with BattlEye off on our server, the client stays a BattlEye-capable title, and the script API already gives camera, FOV and state.

## 4. Interaction coverage (SOW §2)

| Interaction | How it shows up | Label source |
|---|---|---|
| Visibility | Spotting across fields and tree lines, ambushes | Server-side ray traces (`BaseWorld.TraceMove`) between eye positions + frustum from each client's logged FOV; vegetation occlusion needs the trace to hit foliage **[to verify]** |
| Pursuit | Vehicle chases, infantry flanking | Position/velocity series |
| Collision | Vehicle–vehicle, vehicle–infantry, vehicle–object contacts | Physics contact events on vehicles; velocity drop |
| Combat | Firefights, AT vs vehicle | Hit/damage events, `OnPlayerKilled` with instigator |
| Cooperation | Driver + gunner, squad movement, medic healing, resupply | Shared vehicle compartments; same-faction proximity; heal/supply events |
| Blocking | Vehicles blocking roads, infantry in doorways | Contact + velocity drop |
| Handoff | Magazine/item hand-over, dropping a weapon for a teammate, seat swaps, supply truck delivery | Inventory transfer events with different owners; compartment change events |
| Shared objects | Vehicles, supply crates, doors, capture points, radio relays | Ownership/usage events |
| Perspective consistency | Same firefight seen by both sides, crew of one vehicle | Mutual visibility windows; crew members share one vehicle transform |

Arma covers every SOW interaction type, including collisions and vehicle cooperation, which CS2 lacks.

## 5. Capture setup

**What our tools already do for this title:**
- `odx-app` / `odx input` + `odx record` on each player PC: input and video for any window (`window_process: ArmaReforgerSteam`).
- Player packets: pktmon on the profile's `network` ports (`udp/2001`), kept raw like CS2.
- Orchestrator rooms, time service, round start/stop, `odx merge` → v3 tree with an empty `server/` (no `IGameMerger` for Arma yet).

**What would need to be built:**
1. **Script mod `tbrain_capture`** (Enforce Script, Workbench), published Unlisted:
   - server part: state sampler + event hooks → `$profile:tbrain/<session>/state.jsonl`, `events.jsonl`; round start/stop read from a control file the agent writes in the server profile (or polled over `RestContext` from the orchestrator);
   - client part: HUD off, camera transform + vertical FOV per frame. **Camera log built (2026-10-20):**
     `capture/native/reforger-mod/TbrainCapture` (installed by TbrainReforger.exe; the server loads the same addon) logs
     `$profile:tbrain/<round>.csv` while odx-app's `$profile:tbrain/control.txt` names the round. Clock: no flash marker
     (it would show in the delivered video); each row carries `System.GetTickCount()` and `System.GetUnixTime()` (whole
     seconds of the Windows clock), whose rollovers bound UTC − tick, and `clock_samples.csv` maps the Windows clock onto
     QPC (`Odx.Capture/ReforgerCamera.cs`; ±3 ms on synthetic data, **not yet measured in game**). HUD off not built yet;
     per frame also the view: third person from `CameraHandlerComponent.IsInThirdPerson()` on the controlled entity (Script API;
     spike rec3: 16 switches tracked, both views reproject at 0.0 px; third-person FOV 72° vs 74° first person).
     The logged transform is the rendered camera in both views (behind the character in third person); third person has its own
     FOV setting (`CameraManager.GetThirdPersonFOV()`), so intrinsics stay per frame from `GetVerticalFOV()`.
   - Per working rule 3, the mod writes raw logs only; each log format is pinned by a fixture in `dotnet/fixtures/`.
2. **`Odx.Games.ArmaReforger`** (C#), like `Odx.Games.Cs2`:
   - `IGameServerRecorder` for `odx game-server`: starts/stops the mod's server log per round and keeps it in `<work>/<session>/server/`;
   - `IGameMerger` for `odx merge`: state + events → `server_state.bin`, `events.jsonl`;
   - converter for the client camera log (Enfusion axes → canonical C2W, vertical FOV → intrinsics, script time → QPC via the flash marker), feeding `odx build --camera`.
3. A client-side collector (**built**, `ReforgerCamera.Collect`: profile `camera_source: reforger-mod`; reads the profile
   folder from TbrainReforger.exe's settings): the camera log lives in the player's Reforger profile (`$profile:` is `<-profile dir>\profile\` when the game is started with `-profile`, verified; default `Documents\My Games\ArmaReforger\profile` **[to verify]**); odx-app must copy it into the capture folder at the end of a round.

**Server:** private dedicated server (Windows or Linux, SteamCMD 1874900), `"battlEye": false`, `crossPlatform: false`, `game.password` set, `visible: false`, `disableThirdPerson: false` (the client wants first- and third-person data, 2026-10-18), `-maxFPS 60`, the capture mod in `mods`; pinned build (auto-update off; clients on the same version).

**Server profile files read by `TbrainCapture`** (`$profile:tbrain/`, on AWS `/opt/reforger/profile/profile/tbrain/`; copies in
`capture/deploy/aws/reforger/`): `spawn_points.txt` (extra spawn points, [arma-reforger-spawn-points.md](./arma-reforger-spawn-points.md))
and `settings.txt` (`time_of_day hh:mm` stops the clock, `invincible 1` for players;
[arma-reforger-daytime-invincible.md](./arma-reforger-daytime-invincible.md)). No file = vanilla; editing one + restarting the
server needs no new addon.

**Pinned server builds (SteamDB depot manifests, 2026-10-19).** An old build is fetched by manifest; `download_depot` needs a
Steam account (anonymous login fails with `missing license for depot (No subscription)`), any account works since the server app
is free. Merge the data depot with one binary depot into one folder; on Linux, `chmod +x` the binaries.

| Build | Engine | Steam push (UTC) | 1874901 data (all OS) | 1874903 Windows | 1874902 Linux |
|---|---|---|---|---|---|
| 1.6.0.68 | 186660 | 2025-11-11 12:08 | `8347543458726324209` | `4419362214482487562` | `6069179217753103091` |
| 1.6.0.95 (last of 2025) | 187305 | 2025-12-11 12:02 | `2829118061341410821` | `4684538843916204560` | `6110613152296688874` |

```
download_depot 1874900 1874901 <data manifest>
download_depot 1874900 1874903 <Windows manifest>   (or 1874902 <Linux manifest>)
```

Run in SteamCMD (`+login <user>`) or the Steam client console (`steam://open/console`, output in
`Steam\steamapps\content\app_1874900\depot_<id>\`); DepotDownloader takes the same ids (`-app -depot -manifest -dir`).
Verified: `ArmaReforgerServer.exe` file version and the engine version in the log match the table for both Windows builds; the
1.6.0.68 Linux binary is ELF. A 1.6.0.68 client (engine 186660) joined the 1.6.0.68 command-line server; against 1.6.0.95 it
failed with `handshake timeout`. The client's own manifests (app 1874880) are not pinned here yet.

**Spike: servers and BattlEye (2026-10-18, game 1.8.0.13).**
- `-config` server: loads mods only from the config's Workshop `mods` list (`-config cannot be used together with addons!`). Direct Join resolves the server through Bohemia's backend: `publicAddress` must be a LAN or public IP, not `127.0.0.1` (`SERVER_NOT_FOUND`). A joining client reloads its addons to the server's list, so a mod loaded locally with `-addons` is dropped. **The camera mod must be on the Workshop (Unlisted) for this route.**
- Command-line server (`-server <scenario .conf> -bindPort 2001 -addonsDir … -addons …`, no `-config`): loads a local mod. It does not register with the backend; clients start with `-client <ip>:2001` and the same local mod, and keep it. No password or `gameProperties`. `BEServer_x64.dll` stays unloaded, even with a player connected: BattlEye is off, but implicitly, not through the documented `battlEye: false`. Spike only, not the production route.
- Linux command-line server (1.6.0.68 on AWS, 2026-10-20): a local mod's script folder must be lowercase `scripts/`. With `Scripts/` the addon is listed under "Loaded addons" but its scripts are not compiled (`Module: Game` one file short, different CRC32), and Windows clients with the mod are dropped right after `connected` with `script source code checksum does not match`. Windows ignores the case. In-game Direct Join to a command-line server never reaches it (no `authenticating` line): only `-client <ip>:2001` connects.
- Client started as `ArmaReforgerSteam.exe` directly: `BEClient_x64.dll` not loaded, no BattlEye service installed. Steam's Play and the `armareforger://` handler start `ArmaReforger_BE.exe -exe ArmaReforgerSteam.exe` (the BattlEye launcher). Production: players start the game the normal way; only the server turns BattlEye off.
- Virtual LAN (ZeroTier, 2026-10-19, server and client on one PC, so packets did not cross the tunnel):
  - `-config` server with `publicAddress` = the ZeroTier IP: registers with the backend at that address (`Server registered
    with address: 10.147.20.243:2001`, plus a Direct Join Code); Direct Join by IP finds it, the client connects from the
    ZeroTier address and the backend authenticates the player (`Authenticated player … identityId=…`). Needs internet, no
    port open to the internet.
  - Command-line server + `-client <ZeroTier IP>:2001`: connects with no backend at all (no registration, no
    `Authenticated player`; only `authenticating` → `connected`). Trade-offs: no password (`slots=…/256`), access is
    controlled by ZeroTier network membership; no Bohemia `identityId` (player identity from our agent/room); no
    `gameProperties`.
  - `steam://run/1874880//<args>` drops the arguments (process started with none, so no `-client`, no `-profile`); odx-app
    must start `ArmaReforgerSteam.exe` itself to pass `-client` / `-profile`.

**Players:** normal Steam clients join by IP:2001 with the password; the mod is downloaded automatically. Each PC runs odx-app (input, video, packets, clock sync).

## 6. Risks

| Risk | Level | Mitigation |
|---|---|---|
| EULA: non-commercial use only | High | Ask Bohemia for a commercial/data licence before production (Q13) |
| No replay: a failed capture cannot be re-rendered | Medium | Live QA in odx-app; keep raw script logs; community replay mod as a fallback for state only |
| Camera log ↔ video clock alignment (no QPC in script) | Medium | Spike: flash marker works, residual ≤ 1.85 frames (plan Q6 allows 2 live). Tighter: `-maxFPS` = recording fps, or a high-resolution clock if the API has one |
| Script FileIO / per-frame logging cost on clients | Medium | Buffered writes; measure frame time impact in the spike |
| Game updates break the mod; client/server version lock | Medium | Pin server build; mod CI in Workbench; update window per release |
| HUD elements outside the HUD manager (prompts, radial menus, PIP scopes) | Medium | Enumerate in spike; agree with Odyssey what counts as HUD (Q9) |
| Players need internet (Bohemia backend, Workshop downloads) on a `-config` server (confirmed: the backend authenticates each player); command-line server joins with no backend, fully offline client not tried | Low | Pre-download mods; document network needs in guides |
| Large maps: sparse interactions | Medium | Small scenarios (Capture & Hold, custom compact missions) to force contact |

## 7. Open questions

- Does Bohemia grant a commercial licence for gameplay data capture? (Q13)
- Script time → QPC: the flash marker gives ≤ 1.85 frames (spike, 2026-10-18); can a capped game fps or a high-resolution clock bring it under 1 frame?
- Does the image of frame N show the pose logged in frame N (render lag, spike G4)? Needs the camera converter, then `research/analyze-pose`.
- Third person: is it allowed in vehicles with `disableThirdPerson: false`? (Toggle: Numpad Enter, confirmed in game 2026-10-18; `NumEnter` in the profile, told from Enter by `odx input`.)
- FOV through a PIP scope: not checked separately (the spike did not record which sight was used).
- Is the replication traffic on 2001/udp encrypted? (decides whether player packets carry anything beyond timing)
- Does the native RCON work with `battlEye: false`? (only matters if we drive the server by RCON instead of the mod)
- Does a client started the normal way (Steam → `ArmaReforger_BE.exe -exe ArmaReforgerSteam.exe`, BattlEye loaded) join a `battlEye: false` server and keep a local mod, and does BattlEye leave `odx input` / `odx record` alone? Spike (2026-10-18) only covered the client started as `ArmaReforgerSteam.exe` directly, without BattlEye (see §5, "Spike: servers and BattlEye").
- Virtual LAN across two PCs: latency (direct vs relay), Windows Firewall on the virtual adapter (Public profile), MTU, and `pktmon` capturing on the ZeroTier adapter. The 2026-10-19 test ran on one PC.
- Command-line server: does a client with no internet start and join? Is third person on by default per scenario?
- Extra spawn points (~10 on Everon) through `TbrainCapture`, and whether to add invincibility: task in [arma-reforger-spawn-points.md](./arma-reforger-spawn-points.md).
- Always day and invincible players on the test server (script, settings file): task in [arma-reforger-daytime-invincible.md](./arma-reforger-daytime-invincible.md).
- Default PC bindings: third-party lists disagree (e.g. "Perform Action" on `Y` vs `F`); confirm in the game's Controls menu and record the bindings each player actually uses.

## 8. Sources

- Server config (official): https://community.bistudio.com/wiki/Arma_Reforger:Server_Config · https://community.bistudio.com/wiki/Arma_Reforger:Server_Hosting · https://community.bistudio.com/wiki/Arma_Reforger:Startup_Parameters
- Config reference / ports / BattlEye / crossplay (hosting providers): https://xgamingserver.com/docs/arma-reforger/server-config-reference · https://terabit.io/kb/arma-reforger-server-ports · https://xgamingserver.com/docs/arma-reforger/battleye · https://terabit.io/kb/arma-reforger-crossplay-server · https://terabit.io/kb/arma-reforger-rcon-admin-commands
- SteamCMD app ids: https://xgamingserver.com/tools/arma-reforger/steamcmd · https://steamcommunity.com/app/1874880/discussions/0/5019836142105153176/
- Script API: CameraBase https://community.bistudio.com/wikidata/external-data/arma-reforger/ArmaReforgerScriptAPIPublic/interfaceCameraBase.html · CameraManager https://community.bistudio.com/wikidata/external-data/arma-reforger/ArmaReforgerScriptAPIPublic/interfaceCameraManager.html · IEntity https://community.bistudio.com/wikidata/external-data/arma-reforger/EnfusionScriptAPIPublic/interfaceIEntity.html · FileIO https://community.bistudio.com/wikidata/external-data/arma-reforger/EnfusionScriptAPIPublic/interfaceFileIO.html · RestContext https://community.bistudio.com/wikidata/external-data/arma-reforger/EnfusionScriptAPIPublic/interfaceRestContext.html · REST usage https://community.bistudio.com/wiki/Arma_Reforger:REST_API_Usage · SCR_BaseGameMode https://community.bistudio.com/wikidata/external-data/arma-reforger/ArmaReforgerScriptAPIPublic/interfaceSCR__BaseGameMode.html · HUD manager https://arexplorer.zeroy.com/_s_c_r___h_u_d_manager_component_8c_source.html
- Coordinate system (left-handed, Y up): https://community.bistudio.com/wiki/Arma_Reforger:From_SQF_to_Enforce_Script · https://community.bistudio.com/wiki/Arma_Reforger:World_Editor:_Coords_Tool
- Input system: https://community.bistudio.com/wiki/Arma_Reforger:Input_Manager · https://reforger.armaplatform.com/news/new-controls-explained
- Default bindings (third-party, partly inconsistent): https://defkey.com/arma-reforger-shortcuts · https://tutorialtactic.com/blog/arma-reforger-shortcuts/ · https://keyboardcontrols.com/shortcuts/games/arma-reforger/ · https://gamepretty.com/arma-reforger-all-keyboards-gamepad-controls/
- Mods and Workshop: https://xgamingserver.com/docs/arma-reforger/mod-setup · https://community.bistudio.com/wiki/Arma_Reforger:Publishing_Process · https://reforger.armaplatform.com/news/workshop-licenses-and-ip-faq · HUD Key mod https://reforger.armaplatform.com/workshop/D34EF5B417354A02-HUDKey
- Replay / spectator mods: https://reforger.armaplatform.com/workshop/5EE13DDF41732F9C · https://github.com/gruppe-adler/GRAD-Spectator
- Client executable: https://community.bistudio.com/wiki/Arma_Reforger:Startup_Parameters
- Server performance (`-maxFPS`): https://xgamingserver.com/docs/arma-reforger/startup-parameters
- EULA and monetization: https://store.steampowered.com/eula/1874880_eula_0 · https://old.bohemia.net/monetization · https://community.bistudio.com/wiki/Intellectual_Property
- General: https://en.wikipedia.org/wiki/Arma_Reforger
