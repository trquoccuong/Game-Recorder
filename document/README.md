# Odyssey — Documents

Markdown versions of the source PDFs in [`raw/`](./raw).

| Document | Version / Date | Summary |
|---|---|---|
| [SOW — Round 4 Multiplayer](./sow-round-4-multiplayer.md) | Signed 2026-09-30 | Odyssey × Tbrain: 20,000 hrs of multi-agent shared-state gameplay data, 13 weeks, delivered by 2026-12-31 |
| [Game Data Capture Spec v2](./videogame-data-capture-spec-v2.md) | v2.1 · 2026-07-24 | Canonical per-session format: `session.json`, `frames.csv` (C2W camera, intrinsics, inputs), `video.mp4`, validation checks |
| [Multiagent Game Data Capture Spec v3](./multiplayer-data-capture-spec-v3.md) | v3.1 · 2026-08-16 | Multiplayer extension of v2: session grouping, server-side capture, cross-player sync |
| [Software Plan](./software-plan.md) | Rev 2 · 2026-10-04 | Plan for a C++/Lua game data extractor + capture pipeline: architecture, game list & compliance, timeline, risks, open questions |

Reading order: SOW → Spec v2 (base format) → Spec v3 (multiplayer layers on top of v2).
