# Unity titles with dedicated servers

> Rank in plan: 11 (group) · Compliance: `pending review` per title · Last checked: 2026-10-04
> Back to [games index](./README.md) · Unity approach: [software-plan.md §7.9](../software-plan.md#79-unity-bridge--unity-specific-guidance)

Titles: **V Rising, 7 Days to Die, Unturned, SCP: Secret Laboratory, Sons of the Forest** (plus [Rust](./rust.md) and [Valheim](./valheim.md), which have their own files).

| Item | Value |
|---|---|
| Engine | Unity (Mono or IL2CPP; check per title) |
| Server state (T0) | BepInEx / Oxide-style plugins on the dedicated server; many are headless Mono builds |
| Client camera | Unity bridge (`Camera.cameraToWorldMatrix`, `nonJitteredProjectionMatrix`) |
| Anti-cheat | Varies per title **[to verify]** |
| Interactions | combat, cooperation, building/blocking |

Before shortlisting any title, check:
1. Scripting backend (Mono vs IL2CPP).
2. Whether IL2CPP is obfuscated, or `global-metadata.dat` is encrypted.
3. Networking stack: a dedicated server is required; Photon/P2P titles rank lower.
4. Anti-cheat and any official route to run without it.
