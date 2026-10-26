# Arma Reforger: extra spawn points (task)

> Status: `in progress` (script and 20 points done, seen on the deploy map of a local 1.6.0.68 server; AWS deploy left) ·
> Written 2026-10-26 · Back to [arma-reforger.md](./arma-reforger.md)
> To be implemented and tested on a Windows PC with the game, Arma Reforger Tools (Workbench) and access to the AWS server.

## 1. Request

Add about **10 player spawn points** on Everon for the test scenario (Combat Ops Everon, on the AWS command-line server), so
rounds start from, and respawns happen at, different places instead of the scenario's own base only.

Why: more varied starting positions and routes in the captured gameplay. The server has no setting for this: spawn points are
`SCR_SpawnPoint` entities placed in the scenario's world. A server `config.json` cannot add them, and the command-line server
used here has no `gameProperties` at all ([arma-reforger.md §5](./arma-reforger.md#5-capture-setup), "Spike: servers and BattlEye").

## 2. Context

- Server: AWS `i-0d93e4c8464c9ea83` (`13.228.16.146`, usually stopped; `capture/deploy/aws/power.sh`), build **1.6.0.68** Linux,
  command-line server, scenario `{DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf`, local addons in `/opt/reforger/addons`,
  systemd unit `reforger`. Clients join with `-client 13.228.16.146:2001` (in-game Direct Join never reaches this server).
- Mod: `capture/native/reforger-mod/TbrainCapture` (today: the client camera log, `scripts/Game/TbrainCapture.c`). Server and
  clients load the same addon; TbrainReforger.exe installs it on the players' PCs (rebuilt by CI on each push to `main`,
  release `reforger-launcher`).
- Linux server: the script folder must stay lowercase `scripts/`, or the scripts are not compiled and clients are dropped with
  `script source code checksum does not match`.
- Any script change changes the checksum: server and every client must run the same version of the addon.
- Compliance (`document/games/README.md`): private server, our own script mod, no anti-cheat bypass. This task stays within it.

## 3. Design

All in `TbrainCapture`, server side only (authority), no change to the scenario files.

1. **Positions file**, read by the server when the game mode starts: `$profile:tbrain/spawn_points.txt`, one point per line,
   `x z` in world metres (optionally `x z name`), `#` comments. Height is not stored: the script takes the terrain height
   (`BaseWorld.GetSurfaceY(x, z)`) plus a small offset. Changing the points = edit the file + restart the server, no new addon
   (so no checksum change for the clients).
2. **Create the spawn points**: for each line, spawn the faction spawn point prefab
   (`Prefabs/MP/Spawning/SpawnPoint_<faction>.et`, see the wiki's General Game Mode Setup) with
   `GetGame().SpawnEntityPrefab(...)` at that position, on the server only (`Replication.IsServer()`). The prefab carries an
   `RplComponent`, so clients see the points; `SCR_SpawnPoint` registers itself in the spawn point list used by the deploy menu.
   Set the faction key to the players' faction if the prefab does not already carry it (`SCR_SpawnPoint.SetFactionKey`, to verify
   on 1.6.0.68), and the display name from the file so the deploy map shows it.
3. **Hook**: a `modded class` on the game mode (e.g. `SCR_BaseGameMode.OnGameStart` / `EOnInit`, whichever runs once on the
   server after the world is loaded on 1.6.0.68). Log each created point to `console.log` with a `TBC_SpawnPoints:` prefix, and
   a clear line when the file is missing or a line is malformed.
4. Players pick the point on the deploy screen map (Combat Ops uses the menu spawn logic, to verify). Random spawn is out of
   scope unless asked.

Not in the C# side: the positions file is server config, not a capture log, so no fixture is needed (working rule 3 covers logs
read by `Odx.*`).

## 4. Inputs needed before coding

1. **Prefab GUID** of the spawn point prefab: `Resource.Load` needs the full `{GUID}Prefabs/MP/Spawning/SpawnPoint_<faction>.et`.
   Find it in Workbench (Resource Browser → the prefab → copy resource name) on the 1.6.0.68 data, or in the server's `.pak`
   files. Not found on the web (2026-10-26).
2. **Players' faction** in Combat Ops Everon (US?), i.e. the faction key the points must carry. Wrong key = points invisible.
3. **The 10 positions** (`x z`). Do not guess them (sea, inside buildings, enemy AI objectives). Two ways:
   - walk to each place in game with the camera log on: `pos_x`, `pos_z` of `$profile:tbrain/<round>.csv` are world coordinates;
   - or read them in Workbench's World Editor on Everon.
   Keep them away from Combat Ops' objectives/AI if spawning inside them would kill players at once.

### 4.1 Found (2026-10-26, from the 1.6.0.68 data)

1. **Prefab:** `{5A2220DD0C16958B}Prefabs/MP/Spawning/SpawnPoint_US.et`. GUIDs read from `addons/data/resourceDatabase.rdb`
   (each record: path, NUL, 6 bytes, then the GUID as 8 little-endian bytes); the method gives the known
   `{DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf` and `{ECC61978EDCC2B5A}Missions/23_Campaign.conf`. Others:
   `SpawnPoint_USSR` `{4FEA25583D0975F7}`, `SpawnPoint_FIA` `{2B1758F237E4820E}`, `SpawnPoint_FFA` `{5392C4D80E4B18F8}`.
2. **Faction:** the players' (only playable) faction in Combat Ops Everon is **FIA**, not US: the first test with US points
   showed only the scenario's own point, because the deploy map shows a player only their faction's points
   (`SCR_SpawnPoint.c` ~292). The script therefore takes the playable factions from the faction manager
   (`SCR_Faction.IsPlayable()`) and spawns one point per playable faction, with `SpawnPoint_US` / `_USSR` / `_FIA` (their flag
   icon; `SpawnPoint_FIA` is `m_sFaction "FIA"`, Everon flag) or `SpawnPoint_Base` plus the key for another faction. The world
   uses `SCR_MenuSpawnLogic` (players pick points on the deploy map).
3. **Script API on 1.6.0.68** (sources in `core/data.pak` and `data002.pak`): `SCR_BaseGameMode.OnGameStart()` (protected,
   `IsMaster()` on the server), `SCR_SpawnPoint.SetSpawnPointName` / `SetFactionKey`, `BaseWorld.GetSurfaceY(x, z)`,
   `Game.SpawnEntityPrefab(Resource, BaseWorld, EntitySpawnParams)`, `FileHandle.ReadLine`, `string.Split` / `ToFloat`.

### 4.2 Built

`TbrainCapture/scripts/Game/TbrainSpawnPoints.c`: §3 as designed, a point per playable faction (§4.1), name from the file or
`Tbrain <n>`, height = terrain + 0.5 m. File example:

```
# x z [name], world metres
7028 8386 Test A
7012.5 8376.7
```

Run on a local Windows 1.6.0.68 command-line server (`-server {DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf … -addons
TbrainCapture`, profile `D:\odx-dev\reforger-spawn-test\profile`): the scripts compile, the log shows
`TBC_SpawnPoints: Test A (FIA) at <7028, 98.9062, 8386>`, `Tbrain 2 (FIA) at <7012.5, 97.561, 8376.7>`, a malformed line
reported, `2 position(s) for 1 playable faction(s)`, no script error. Note: this command-line server ignores `-bindPort` and
listens on 2001. A client joining it saw both points on the deploy map once they carried FIA.

### 4.3 The points: 20, spread over the main island

Asked for 20 points spread evenly over the map, instead of 10 picked by hand.
[`capture/deploy/aws/reforger/spawn_points.txt`](../../capture/deploy/aws/reforger/spawn_points.txt), made by:

1. A survey addon ([`research/reforger-spawn-survey`](../../research/reforger-spawn-survey/README.md): addon, picker, map,
   and the survey data) on the local server: every 100 m over the world bounds (15 748 samples), keep a point
   when the terrain is ≥ 3 m above the ocean, a downward trace (entities, world, ocean) from 60 m above hits nothing above the
   ground (no building, tree, rock), the surface normal is within ~22° of vertical and the terrain within ±6 m varies by
   ≤ 2 m: 2919 points.
2. Main island only (the largest group linked within 450 m: 2800 points), then k-means with k = 20 (k-means++ seed, fixed
   random seed) and the candidate nearest each centre: nearest neighbours 949–2025 m apart.

All 20 created on the local server (`20 position(s) for 1 playable faction(s)`). Not known to the survey: Combat Ops'
objectives and enemy AI; a point that drops players among them is to be moved by hand. Point 19 (south-east, scattered land)
to check in game.

## 5. Steps and checks (on Windows)

1. Get the GUID and faction (§4). → check: Workbench shows the prefab; the faction key matches the base spawn point of the scenario.
2. Implement §3 in `TbrainCapture` (new file `scripts/Game/TbrainSpawnPoints.c`, same style as `TbrainCapture.c`).
   → check: the addon compiles in Workbench without errors.
3. Local test: Workbench or a local command-line server on Windows (`-server {DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf
   -addonsDir … -addons TbrainCapture -profile …`) with a 2–3 point file, client `-client 127.0.0.1:2001`.
   → check: `TBC_SpawnPoints:` lines in the server log; the points appear on the deploy map; spawning at each one works; after a
   death the menu offers them again.
4. Fill the real 10 points; test again locally. → check: all 10 on the map, none in water or inside geometry.
5. Deploy: copy the addon to `/opt/reforger/addons` (lowercase `scripts/`), `capture/deploy/aws/reforger/spawn_points.txt`
   to the server profile's `tbrain/` (`/opt/reforger/profile/profile/tbrain/`), restart `reforger`. Push to `main` so CI rebuilds TbrainReforger.exe; players reinstall it.
   → check: a Windows client joins without the checksum error and sees the 10 points.
6. Record one short round with odx-app and check the camera log still works (spawn change must not break `TBC_CameraLog`).
7. Update [arma-reforger.md §5](./arma-reforger.md#5-capture-setup) (what was built, the GUID, the file format) and the AWS
   memory/notes; set this file's status to `done`.

## 6. Related, not decided

- **Invincibility (god mode):** no vanilla option for regular players; the Workshop mod "GM invincible unlim ammo" only covers
  players with full Game Master rights (and Workshop mods cannot be downloaded by this command-line server). It could be added to
  `TbrainCapture` the same way (server-side damage off for players), **but** deaths are part of the combat interactions the
  client wants (`OnPlayerKilled` in `events.jsonl`): confirm the purpose (testing only?) before building it.

## 7. Sources

- Respawn Setup (spawn logic, deploy menu): https://community.bistudio.com/wiki/Arma_Reforger:Respawn_Setup
- General Game Mode Setup (`SpawnPoint_FFA/US/USSR.et`, `SCR_RespawnSystemComponent`, `SCR_FactionManager`):
  https://community.bistudio.com/wiki/Arma_Reforger:General_Game_Mode_Setup
- GM invincible unlim ammo: https://reforger.armaplatform.com/workshop/D52E7B438F1A6C09
