# Rust

> Rank in plan: 4 · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md) · Source: [software-plan.md §5.2](../software-plan.md#52-candidate-games-reference-list)

| Item | Value |
|---|---|
| Engine | Unity |
| Genre / view | FPS |
| Server state (T0) | Carbon/Oxide C# server plugins: the server knows every player's eye pose and input state per tick |
| Client camera | Server-side eye pose + plain client screen recording (sync by timestamp); clients stay unmodded |
| Anti-cheat — official no-AC route | EAC stays on with Carbon/Oxide; whether `server.secure false` is official **[to verify]** |
| Interactions | all types; strong building/blocking and item handoff |
| Notes | Lowest client-side risk: no client modification at all. Pose/frame alignment relies on NTP + sync markers. |

Detailed analysis not done yet: expand with the structure used in [cs2.md](./cs2.md) when the title is shortlisted.

## Sources

- https://wiki.facepunch.com/rust/Carbon
