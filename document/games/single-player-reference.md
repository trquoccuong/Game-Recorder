# Single-player reference titles: GTA V, RDR2, Cyberpunk 2077, Hogwarts Legacy

> Status: **not usable for this SOW** (no multiplayer) · Last checked: 2026-10-04
> Back to [games index](./README.md)

These four titles already appear in spec v2's appendix (conversion notes for earlier single-agent deliveries). They are **single-player only** for our purposes: Script Hook RDR2, for example, closes the game if Online starts. So they cannot produce multi-agent sessions.

| Game | Engine | Camera route (single-player) |
|---|---|---|
| GTA V | RAGE | Script Hook V natives (`GET_FINAL_RENDERED_CAM_COORD/ROT/FOV`) |
| RDR2 | RAGE | Script Hook RDR2 natives |
| Cyberpunk 2077 | REDengine 4 | Cyber Engine Tweaks (Lua) |
| Hogwarts Legacy | Unreal Engine 4 | UE4SS |

Use: reference for single-agent work and for testing profile conversions only.
