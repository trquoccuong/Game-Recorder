# Game analyses

Per-game analyses split out of [software-plan.md §5](../software-plan.md#5-game-selection--compliance). The list is a **reference**: every title must pass the compliance gate (plan §5.3) before production capture.

**Ground rules for every title:**
- Only private servers using an official no-anti-cheat route, or titles without anti-cheat.
- Never public matchmaking.
- Anti-cheat bypass is out of scope.
- For anti-cheat-protected clients (CS2, CS:GO), use official data routes only: server plugins, demos/replays, external input and screen capture. No client memory reading.

## Detailed analyses

| Game | Genre | Route | Status |
|---|---|---|---|
| [Counter-Strike 2](./cs2.md) | Tactical FPS | Server demo + HLAE re-render + CounterStrikeSharp | Candidate #1 |
| [CS:GO (legacy)](./csgo.md) | Tactical FPS | Server demo + HLAE + SourceMod (frozen build) | Alternative to CS2 |
| [Warcraft III](./warcraft3.md) | RTS | Official map scripting (JASS/Lua) + external capture + `.w3g` | Needs Odyssey confirmation (Q14); Reforged 3.0 is always-online |
| [Arma Reforger](./arma-reforger.md) | Mil-sim FPS + vehicles | `battlEye: false` + our Enforce Script server/client mod (state, camera, FOV) + external capture | Licence (EULA non-commercial) needs Bohemia's written OK (Q13) |
| [Arma 3](./arma3.md) | Mil-sim FPS + vehicles | `BattlEye = 0` + SQF mod + `callExtension` DLL (state, camera) + external capture | Research item behind Reforger; same licence question (Q13) |

## Summary analyses

| Rank | Game | File |
|---|---|---|
| 3 | ARK: Survival Ascended | [ark-survival-ascended.md](./ark-survival-ascended.md) |
| 4 | Rust | [rust.md](./rust.md) |
| 5 | Assetto Corsa + CSP | [assetto-corsa.md](./assetto-corsa.md) |
| 5 | BeamNG.drive + BeamMP | [beamng.md](./beamng.md) |
| 6 | Insurgency: Sandstorm | [insurgency-sandstorm.md](./insurgency-sandstorm.md) |
| 7 | Garry's Mod | [garrys-mod.md](./garrys-mod.md) |
| 8 | Palworld | [palworld.md](./palworld.md) |
| 9 | Assetto Corsa Competizione | [assetto-corsa-competizione.md](./assetto-corsa-competizione.md) |
| 10 | Satisfactory / Valheim | [satisfactory.md](./satisfactory.md) · [valheim.md](./valheim.md) |
| 11 | Unity dedicated-server titles | [unity-dedicated-server-titles.md](./unity-dedicated-server-titles.md) |
| ref | GTA V / RDR2 / Cyberpunk / Hogwarts | [single-player-reference.md](./single-player-reference.md) |
| OSS | Xonotic, Unvanquished, Red Eclipse 2, Luanti, SuperTuxKart | [oss-fallback.md](./oss-fallback.md) |
| — | Excluded titles | [excluded.md](./excluded.md) |

## Template for a detailed analysis

1. Verdict
2. Facts (engine, players, server, plugins, anti-cheat, replay, visuals)
3. Data route: spec field → source
4. Interaction coverage (SOW §2)
5. Capture setup
6. Risks
7. Open questions
8. Sources
