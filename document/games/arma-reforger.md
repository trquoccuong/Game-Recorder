# Arma Reforger

> Rank in plan: 2 · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md) · Source: [software-plan.md §5.2](../software-plan.md#52-candidate-games-reference-list)

| Item | Value |
|---|---|
| Engine | Enfusion (Bohemia) |
| Genre / view | FPS/TPS + vehicles |
| Server state (T0) | Official Enforce Script server mods |
| Client camera | Script mod reads camera transform + FOV per frame (T0/T1, no raw memory); HUD hideable by script |
| Anti-cheat — official no-AC route | BattlEye; official `"battlEye": false` in server config (also disables console crossplay) |
| Interactions | all types, incl. vehicles and collisions |
| Notes | Modern visuals, up to 128 players. No replay system found **[to verify]**. |

Detailed analysis not done yet: expand with the structure used in [cs2.md](./cs2.md) when the title is shortlisted.

## Sources

- https://community.bistudio.com/wiki/Arma_Reforger:Server_Config
- https://xgamingserver.com/docs/arma-reforger/battleye
