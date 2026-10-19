# Assetto Corsa (AC1) + CSP + AssettoServer

> Rank in plan: 5 · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md) · Source: [software-plan.md §5.2](../software-plan.md#52-candidate-games-reference-list)

| Item | Value |
|---|---|
| Engine | Kunos (AC1) |
| Genre / view | Vehicle sim |
| Server state (T0) | AssettoServer C# plugins + server-side CSP Lua |
| Client camera | CSP Lua SDK exposes camera and sim state; official shared memory gives car physics; replays |
| Anti-cheat — official no-AC route | None |
| Interactions | pursuit, collision, blocking, overtakes |
| Notes | Clean no-anti-cheat scripting route for vehicle data. |

Detailed analysis not done yet: expand with the structure used in [cs2.md](./cs2.md) when the title is shortlisted.

## Sources

- https://github.com/ac-custom-shaders-patch/acc-lua-sdk
