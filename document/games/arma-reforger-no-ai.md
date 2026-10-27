# Arma Reforger: no AI on the test server (task)

> Status: `in progress` (tested on a local 1.6.0.68 server without players, §3; with players and AWS left) ·
> Written 2026-10-27 · Back to [arma-reforger.md](./arma-reforger.md)

## 1. Request

No AI characters on the map of the test server (Combat Ops Everon), to lower the server's load and the lag players see.

## 2. Design

Combat Ops spawns its AI through the Scenario Framework (`SCR_ScenarioFrameworkSlotAI`, `SCR_ScenarioFrameworkQRFSlotAI`
in `Coop_CombatOps_Eden.ent`). Vanilla has a switch, `-disableAI` on the command line (`operating.disableAI` in a `-config`
file): it sets the AI world's limit to 0, but the AI spawned while the world loads stay (§3). The AWS server's command line
is in its systemd unit, outside the repo, so the switch goes into `TbrainCapture`'s `settings.txt` with the other test
server settings ([arma-reforger-daytime-invincible.md](./arma-reforger-daytime-invincible.md) §3):

```
# 0 = no AI characters on the map; no line or 1 = the scenario's AI
ai 0
```

At game start (`OnGameStart`, server only) `TBC_Settings.DisableAI()` sets `AIWorld.SetAILimit(0)` and removes every AI
character (`AIWorld.GetAIAgents`, each agent's controlled entity, unless a player controls it), then again every 2 s for
the spawners that do not ask the limit. Tasks that need AI (kill, clear area) have nobody to fight; players still spawn
and move as before.

## 3. Results

Local command-line server, Windows, 1.6.0.68, Combat Ops Everon, no player; a test-only script logging the AI world every
20 s (`GetCurrentAmountOfLimitedAIs`, `GetAILimit`):

| Run | AI limit | AI on the map |
|---|---|---|
| Vanilla | 1500 | 8, from the start |
| `-disableAI` | 0 | 2, from the start |
| `ai 0` | 0 | 0: 1 removed at game start, 3 at +5 s, 2 at +2 min (a spawner that ignores the limit) |

No script error from the addon; time of day and spawn points still applied.

Not tested yet:

1. With players: a player's character is never removed (spawn, respawn), and server FPS with players vs vanilla.
2. AWS: copy the addon to `/opt/reforger/addons` (lowercase `scripts/`) and `capture/deploy/aws/reforger/settings.txt` to
   `/opt/reforger/profile/profile/tbrain/`, restart `reforger`; push to `main` so CI rebuilds TbrainReforger.exe, players
   reinstall it. → check: `TBC_Settings: AI limit 0` in the server log, a client joins without the checksum error.
