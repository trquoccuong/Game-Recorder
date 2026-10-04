# BeamNG.drive + BeamMP

> Rank in plan: 5 · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md) · Source: [software-plan.md §5.2](../software-plan.md#52-candidate-games-reference-list)

| Item | Value |
|---|---|
| Engine | BeamNG (soft-body) |
| Genre / view | Vehicle sim |
| Server state (T0) | BeamMP server Lua 5.3 (`MP.GetPositionRaw` per player) |
| Client camera | Game Lua fully open; BeamNG.tech camera sensors |
| Anti-cheat — official no-AC route | None |
| Interactions | soft-body collisions, pursuit, blocking |
| Notes | Best collision physics; pair with AC1 for vehicle diversity. |

Detailed analysis not done yet: expand with the structure used in [cs2.md](./cs2.md) when the title is shortlisted.

## Sources

- https://docs.beammp.com/scripting/server/latest-server-reference/
- https://documentation.beamng.com/beamng_tech/sensors/camera/
