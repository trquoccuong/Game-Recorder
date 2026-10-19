# Arma 3

> Rank in plan: 10 · Compliance: `pending review` · Last checked: 2026-10-16
> Back to [games index](./README.md) · Source: [software-plan.md §5.2](../software-plan.md#52-candidate-games-reference-list)

## 1. Verdict

Arma 3 has the **best official data route of any commercial title on the list**, and possibly the worst licence.

Technically everything we need is official, documented and in-engine:
- BattlEye is a server switch (`BattlEye = 0;` in `server.cfg`), and clients may launch without it.
- SQF scripts read the **exact render camera** (`positionCameraToWorld`, `getObjectFOV`, `getResolution`) and the full server state (positions, vehicles, crew, inventory, `HandleDamage` / `Killed` / `Fired` event handlers).
- `callExtension` loads our own C++ DLL in the game process: the documented way to write logs out of SQF. OCAP2 does exactly this for server-side replays.
- The game is huge in scope: infantry, ground vehicles, helicopters, boats, crewed vehicles. It covers every SOW §2 interaction, including **collision** and **handoff**, better than any FPS on the list.

There is no client memory reading: the camera comes from our own signed client mod through the scripting API.

Main weaknesses:
- **EULA §3.2: "non-commercial", "entertainment and recreation purposes" only**, explicitly excluding training use. Capturing data for a client is very likely outside the consumer licence. **Legal review and a written licence from Bohemia (Q13) come first**; nothing below matters without them.
- 2013 visuals (Real Virtuality 4). Bohemia's successor line is Arma Reforger (Enfusion, rank 2) and Arma 4 (2027).
- No native replay. Pose and video come from the live client (Path B-like), so there is no HUD-free re-render as with CS2.
- Closed UDP netcode; `packets.*` stay raw (Q12).

Recommendation: keep it as a research item behind **Arma Reforger**, which offers the same official script route with modern visuals. Ask Bohemia about licensing for both titles in one request.

## 2. Facts

| Item | Value |
|---|---|
| Engine | Real Virtuality 4 (Bohemia Interactive), 2013– |
| Genre / view | Military sim: FPS/TPS (`cameraView` = `INTERNAL`, `EXTERNAL`, `GUNNER` optics, `GROUP`) + land/air/sea vehicles with crew roles |
| Status | Still maintained in its 13th year: **2.20** (2025, Steam Game Recording); **2.22** (11 Aug 2026, netcode improvements, new scripting commands and server events; 32-bit, Win 7/8 deprecated). Steam: about 7–11k concurrent players in mid-2026 **[to verify, third-party charts]** |
| Players | Up to ~100+ per server, mission-defined **[to verify the hard cap]** |
| Dedicated server | Yes, free, SteamCMD **app 233780** (`arma3server_x64.exe`; Linux build too). The Steam account does not need to own the game |
| Ports (UDP) | **2302 game** (VON included), 2303 Steam query, 2304 Steam port, 2305 VON (reserved, unused), 2306 BattlEye. All shift with `-port=` |
| Client process | `arma3_x64.exe` (`arma3battleye.exe` is the BattlEye wrapper the launcher uses when BE is on) |
| Anti-cheat | BattlEye. **Official off switch on the server: `BattlEye = 0;`** (wiki: "Enables or disables the BattlEye anti-cheat engine. Requires installed BattlEye on server and clients joining the server"). The client launcher can start without BE |
| Addon signatures | `verifySignatures = 2` (default: v2 `.bisign` only) checks client `.pbo`s against the server's `keys/*.bikey`. We sign our client mod with our own key (Arma 3 Tools / DSSignFile) and put the `.bikey` on our server. Keeps signature checks on; `0` would also be official |
| Scripting | SQF in missions and addons; server-only addons via `-serverMod=`; client addons via `-mod=` |
| Extensions | `callExtension` loads `<name>_x64.dll` (Windows) / `.so` (Linux). Blocking call, **10,240-byte return limit**, warning above 1 s. With BattlEye on, a client extension must be BE-whitelisted. With BE off it just loads. Since 2.12 the engine passes the caller's Steam ID, mission and server name (`RVExtensionContext`) |
| Replay | **None native.** Community: **OCAP2** (server addon + Go extension → JSON/PostgreSQL/SQLite + web viewer): positions, vehicles, fired/hit/killed, markers, chat. A 2D after-action viewer, not a POV re-render |
| World coordinates | X west→east, Y south→north, Z up, **metres**. Several Z references: **ASL is the only absolute one**. ATL is above terrain, AGL is ATL over land and ASLW over sea. `positionCameraToWorld` returns AGL |
| Visuals | 2013 engine with continuous updates; large open terrains (Altis 270 km², Stratis, Malden, Tanoa, Livonia) |

## 3. Data route (official tools only)

**Live capture + script logging.** Nothing is injected beyond our own signed addon and its `callExtension` DLL, both loaded by the engine's official mechanisms on a server with BattlEye off. No memory reading, no hooks.

| Spec field | Source |
|---|---|
| `video.mp4` per player | `odx record` (WGC) on each player PC, window `arma3_x64`. HUD-free: server difficulty (`forcedDifficulty` with `CustomDifficulty` flags: `weaponCrosshair=0`, `groupIndicators=0`, `weaponInfo=0`, `stanceIndicator=0`, `commands=0`, `waypoints=0`, `tacticalPing=0`, `deathMessages=0`, `thirdPersonView=1`: the client wants first- and third-person data, 2026-10-18; `2` would allow it in vehicles only) plus client mod `showHUD [false,…]` (hides the remaining HUD, the action menu and command bar) **[to verify which elements survive, e.g. action menu, hint popups]** |
| C2W per frame | Client addon, `addMissionEventHandler ["Draw3D", …]` (once per rendered frame): origin `AGLToASL positionCameraToWorld [0,0,0]`. Axes from `positionCameraToWorld` of `[1,0,0]`, `[0,1,0]`, `[0,0,1]` minus the origin. The command takes **config order** `[right, up, forward]`. `getCameraViewDirection player` is a cross-check. Per frame also the view, `cameraView`: `INTERNAL` (first person), `EXTERNAL` (third person), `GUNNER` (optics / gunner sight), `GROUP` (commander view); the pose is the rendered camera in every view, behind the character in third person **[to verify in a spike, as for Reforger]** |
| Intrinsics | `getResolution` gives `fovTop` / `fovLeft` (profile FOV, engine default 0.75 / 1.333 at 16:9) and the screen size. `getObjectFOV` gives the optics/zoom FOV (wiki: does **not** include the user's custom FOV, so scale by `fovTop / 0.75`). Treat `fovTop` as tan(vertical half-angle) (0.75 → 73.7° vertical) **[to verify with a reprojection test]**. Lock the player profile FOV in our setup |
| Camera log | The addon passes each frame's pose to our extension DLL (`callExtension`), which stamps QPC and appends `camera_samples.csv` in the pinned extractor format (`dotnet/fixtures/camera_samples.csv`). `odx build --camera` then aligns it to frames as for Path B. Raw log only, per working rule 3 |
| Axes / units | X east, Y north, Z up, metres. Right-handed and already the spec's world frame; `units_per_meter = 1`. Use ASL (absolute) everywhere |
| `input_keys` / mouse dx/dy | `odx input` (separate process, Raw Input) on each PC, QPC-stamped |
| `input_actions` | Profile bindings + key log. Cross-check with addon logs: `inputAction "<name>"` per frame, stance (`stance`), weapon (`currentWeapon`, `currentMuzzle`), vehicle role (`assignedVehicleRole`), `Fired` EH |
| `server_state.bin` | Server-only addon (`-serverMod`) samples every unit/vehicle each frame or at a fixed rate: `getPosASLVisual` / `getPosASL`, `vectorDir` / `vectorUp`, `eyePos`, `velocity`, `damage`, stance, weapon, vehicle + crew roles, inventory changes. Written through a server `callExtension` DLL (OCAP2's extension is an MIT-licensed reference) |
| `events.jsonl` | Server event handlers: `HandleDamage` / `Hit` / `HitPart` (shooter, hit selection, ammo), `Killed` / `MPKilled` / mission `EntityKilled`, `Fired`, `GetInMan` / `GetOutMan`, `Take` / `Put` (handoff), `PlayerConnected` / `PlayerDisconnected`, round start/end from our mission logic |
| Visibility labels | Server: `checkVisibility [eyePosA, eyePosB]` (0..1, accounts for smoke/foliage) or `lineIntersectsSurfaces` between `eyePos`es, plus frustum from each player's logged camera |
| `packets.*` | UDP 2302 captured raw by pktmon. Closed RV netcode, no public decoder (Q12); the server log is the decoded equivalent |

**Clock.** The server and client DLLs stamp their own QPC/UTC; `diag_tickTime` (timeGetTime) and `time` are game-side only. Use our DLL timestamps, never SQF times, for alignment.

**Out of scope:** reading client memory, BattlEye-protected public servers, the extractor's signature-scan route. The script API makes them unnecessary.

## 4. Interaction coverage (SOW §2)

| Interaction | How it shows up | Label source |
|---|---|---|
| Visibility | Peeking from cover, smoke, foliage, long sightlines | `checkVisibility` between eye positions + frustum from logged cameras |
| Pursuit | Infantry chases, vehicle pursuit over open terrain | Position/velocity series (ASL) |
| Collision | **Vehicle–vehicle and vehicle–infantry impacts**, a strength of this title | `HandleDamage` with collision source (no ammo, vehicle source) + contact distance **[to verify how collision damage is reported]** |
| Combat | Firefights, AT vs vehicles, air vs ground | `Fired`, `HitPart`, `HandleDamage`, `Killed` |
| Cooperation | Crewed vehicles (driver + gunner + commander), squad movement, medic/revive | Same vehicle (`crew`), same group, proximity |
| Blocking | Body-blocking in doorways, vehicles blocking roads | Contact + velocity drop |
| Handoff | Magazine/weapon given through inventory, dropped and picked up | `Put` / `Take` with different owners on the same container |
| Shared objects | Vehicles (seat changes), ammo crates, doors, static weapons | `GetInMan` / `GetOutMan`, crate `Take` / `Put`, door animation state |
| Perspective consistency | Several players around the same fight or vehicle | Overlapping frusta + mutual `checkVisibility` windows |

## 5. Capture setup

What our tools do today vs what Arma 3 needs:

| Part | Today | Needed |
|---|---|---|
| Player video + input | odx-app (`odx record` + `odx input`) works unchanged: profile `window_process: arma3_x64` | `profiles/arma3.json` |
| Player packets | pktmon on the profile's `network` ports, kept `raw` | Nothing (`udp/2302`) |
| Player camera | `odx build --camera` reads `camera_samples.csv` | **New:** client addon (SQF `Draw3D` → `callExtension`) + extension DLL writing `camera_samples.csv` / `extractor.json`. C++ next to `native/extractor/`, signed `.pbo` + our `.bikey` |
| Server side | `odx game-server` only knows CS2 (`Odx.Games.Cs2`: RCON, SourceTV, plugin log) | **New `Odx.Games.Arma3`**: an `IGameServerRecorder` (start/stop logging per round) and an `IGameMerger` (server log → `server_state.bin`, `events.jsonl`). BattlEye RCon is unavailable with `BattlEye = 0`, so round control goes through our server extension (e.g. a local named pipe the agent opens) **[design to settle]** |
| LAN | ZeroTier rooms not needed: players connect to the server's IP | — |

Steps:
1. Dedicated server (SteamCMD 233780, Windows or Linux), `server.cfg`: `BattlEye = 0;`, `verifySignatures = 2;` with our `.bikey`, `password`, `forcedDifficulty = "Custom"` with the HUD flags above, `persistent = 0`. Pinned build (Steam beta branch or `requiredBuild`), auto-update off.
2. Our mission (one round = one task, v3 §1): spawn, objectives, end condition; respawn per Q10. The server-only addon starts and stops logging on the agent's signal.
3. Players: Arma 3 launcher with **BattlEye off**, `-mod=@tbrain_capture`, fixed profile FOV and resolution; odx-app in a room records input + video + packets.
4. Post: `odx merge` with `Odx.Games.Arma3`; camera samples onto frames; validate.

## 6. Risks

| Risk | Mitigation |
|---|---|
| **EULA §3.2 (non-commercial; entertainment only; "training" use excluded)**: capture for a paying client is likely not covered | Legal review + written licence from Bohemia before any capture (Q13); ask together with Reforger |
| Old visuals reduce the data's value | Prefer Reforger for production; Arma 3 only if Reforger is blocked |
| `callExtension` is blocking; per-frame calls cost FPS | Buffer in the DLL, batch writes on a worker thread, return immediately; measure FPS impact |
| HUD elements that difficulty + `showHUD` don't hide (action menu, hints, scroll menu) | Inventory the HUD in a spike; agree what counts as HUD (Q9) |
| Weapon inertia / sway: the view is not purely mouse-driven | Expected; the camera log is the truth, mouse is only the input trace |
| FOV semantics (`fovTop`, `getObjectFOV`) misread | Reprojection test against known world points in the spike |
| Game update breaks the addon/extension | Pin the build on server and clients; addon CI |
| Long sessions / no natural rounds in sandbox missions | Round-based missions or fixed windows (Q10) |

## 7. Open questions

- Will Bohemia license Arma 3 (and Reforger) footage + state capture for ML data delivered to a client? (Q13; contact via Bohemia licensing **[to verify the right address]**)
- Is `fovTop` the tangent of the vertical half-angle, and does `getObjectFOV` include the profile FOV? (spike, reprojection test)
- Does `Draw3D` fire once per presented frame, close enough to Present for ≤1-frame pose alignment? (flash test, plan §7.10.3)
- Which HUD elements remain with all difficulty flags off + `showHUD false`?
- Third person: does `positionCameraToWorld` follow the external camera exactly, and is its FOV `getResolution`'s or its own? Default toggle key: Numpad Enter ("Switch View", `NumEnter` in the profile) **[to verify in the game's Controls]**; is `GROUP` view allowed with `thirdPersonView=1`, and should it be blocked?
- How is vehicle collision damage reported in `HandleDamage` (source, projectile) for collision labels?
- Is the 2302 game traffic encrypted? It decides whether a decoder is even worth exploring (Q12).

## 8. Sources

- Server config (`BattlEye`, `verifySignatures`, `forcedDifficulty`, `allowedFilePatching`): https://community.bistudio.com/wiki/Arma_3:_Server_Config_File
- Dedicated server (app 233780, ports 2302–2306, Linux): https://community.bistudio.com/wiki/Arma_3:_Dedicated_Server
- Startup parameters (`-mod`, `-serverMod`, `-connect`, `-noLogs`): https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters
- Difficulty settings (HUD flags): https://community.bistudio.com/wiki/Arma_3:_Difficulty_Settings · `showHUD`: https://community.bistudio.com/wiki/showHUD
- Camera: https://community.bistudio.com/wiki/positionCameraToWorld · https://community.bistudio.com/wiki/getCameraViewDirection · https://community.bistudio.com/wiki/cameraView · https://community.bistudio.com/wiki/getObjectFOV · https://community.bistudio.com/wiki/getResolution
- Positions (ASL/ATL/AGL, axes, config order): https://community.bistudio.com/wiki/Position
- Extensions: https://community.bistudio.com/wiki/callExtension · https://community.bistudio.com/wiki/Extensions
- Event handlers: https://community.bistudio.com/wiki/Arma_3:_Event_Handlers · https://community.bistudio.com/wiki/Arma_3:_Mission_Event_Handlers
- Default controls: https://community.bistudio.com/wiki/Arma_3_Field_Manual_-_Infantry_Controls · https://defkey.com/arma-3-shortcuts
- OCAP2: https://github.com/OCAP2/addon · https://github.com/OCAP2/extension · https://github.com/OCAP2/web
- EULA (updated 20/08/2026, §3.1–3.2): https://www.bohemia.net/community/licenses/arma3-end-user-license · Content usage rules: https://www.bohemia.net/community/game-content-usage-rules
- Status: https://arma3.com/news/year-in-arma-3-2025 · https://guided.news/en/news/arma-3-update-2-22-ghost-hawk-netcode/ · https://steamcharts.com/app/107410 · Arma 4 (2027): https://www.wargamer.com/arma-4/release-date
- BI wiki pages were read through web.archive.org snapshots (2025) because the live wiki blocks automated access.
