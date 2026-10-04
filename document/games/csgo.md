# Counter-Strike: Global Offensive (CS:GO, legacy build)

> Status: **candidate (alternative to CS2)** · Compliance: `pending review` · Last checked: 2026-10-04
> Back to [games index](./README.md) · Compare: [cs2.md](./cs2.md)

## 1. Verdict

CS:GO is a **frozen, stable** Source 1 build, with the same official data route as CS2: server demo + HLAE re-render + server plugin. Because the build never changes, plugins and configs never break, and the demo tooling is the most mature in the Source ecosystem.

**Trade-off vs CS2:** older visuals (2012–2023 Source 1) and no official matchmaking. Neither matters for private-server capture. Recommend it as a **low-risk second Counter-Strike source** or a calibration fallback if CS2 tooling breaks after an update.

## 2. Facts

| Item | Value |
|---|---|
| Status | Replaced by CS2 in Sept 2023. Still available as the **`csgo_legacy`** frozen build: all features except official matchmaking. In 2026 the legacy branch got its own (unlisted) App ID |
| Engine | Source 1 |
| Genre / view | Tactical team FPS, first person |
| Players | 2–10 (Wingman 2v2, Competitive 5v5); more on community modes |
| Dedicated server | Yes. SteamCMD app 740 (legacy branch); the server may report "out of date" but legacy clients can still connect |
| Server plugins | Metamod:Source + **SourceMod** (SourcePawn), mature and stable on the frozen build |
| Anti-cheat | VAC. Our own server runs with the official `-insecure` flag |
| Replay | GOTV `.dem` recorded on the server (`tv_enable 1`, `tv_record`) |
| Demo tooling | **HLAE** (Source 1: `mirv_camexport`, `mirv_movie*`), CS Demo Manager (plays CS:GO demos on the legacy branch), demoinfocs-golang (CS:GO support in older major versions **[to verify]**), older awpy releases |
| Tick rate | 64 or 128 (server setting) |
| Visuals | Dated compared with CS2 |

## 3. Data route (official tools only)

Same as [CS2](./cs2.md) Path A:

| Spec field | Source |
|---|---|
| `video.mp4` per player | HLAE re-render per player POV from the GOTV demo with `host_framerate 60`, `cl_drawhud 0` |
| C2W + FOV per frame | HLAE `mirv_camexport start <file>.bvh 60` in the same pass (BVH camera motion). Convert BVH → C2W in a small importer |
| Intrinsics | Source `fov_desired` is defined at 4:3 (Hor+), so 90 at 4:3 ≈ 106.26° horizontal at 16:9. Use the rendered FOV from the export if available **[to verify]** |
| Axes / units | Source: X forward, Y left, Z up (`quake` convention); inches → metres |
| Inputs | `odx_input.exe` (separate process) on player PCs during live play, plus demo button states |
| `server_state.bin` / `events.jsonl` | SourceMod plugin: per-tick player state; `round_start/end`, `player_hurt`, `player_death`, bomb and item events |
| Visibility labels | SourceMod `TR_TraceRay` between eye positions, plus frustum test |

**Out of scope:** reading CS:GO client memory. The client is VAC-protected, and the demo + plugin route covers every field.

## 4. Interaction coverage

Same coverage as CS2 (see [cs2.md §4](./cs2.md#4-interaction-coverage-sow-2)): visibility, pursuit, combat, cooperation, body-blocking, weapon/bomb handoff, shared objects, perspective-consistency samples from mutual-visibility duels.

## 5. CS:GO vs CS2

| | CS:GO (legacy) | CS2 |
|---|---|---|
| Build stability | **Frozen**: no breakage | Frequent updates break Metamod/CSS |
| Visual fidelity | Older | **Modern** |
| Camera export | `mirv_camexport` (BVH, mature) | `mirv_camio` |
| Server plugin | SourceMod (very mature) | CounterStrikeSharp (younger) |
| Player population for hire | Smaller, but irrelevant on private servers | Large |
| Odyssey's likely preference | Lower (older look) | Higher |

## 6. Risks

| Risk | Mitigation |
|---|---|
| Odyssey may reject the older visuals | Ask in calibration; use mainly as a fallback |
| Valve EULA / commercial footage | Legal review (Q13) |
| Legacy server "out of date" warnings / future delisting | Archive server + client builds internally |
| BVH export precision / axis conversion | Reprojection test (plan §7.10) on a calibration map |

## 7. Open questions

- Does Odyssey accept CS:GO footage alongside CS2, or only one Counter-Strike?
- Exact FOV semantics in `mirv_camexport` output (horizontal vs vertical, 4:3 base). (spike)

## 8. Sources

- CS:GO legacy build: https://www.hltv.org/news/37221/csgo-remains-available-on-steam-in-frozen-build · https://escorenews.com/en/csgo/news/51650-valve-adds-cs-go-legacy-version-after-release-of-cs2-it-has-all-features-except-official-matchmaking
- Legacy server setup: https://steamcommunity.com/sharedfiles/filedetails/?id=3680627349
- CS Demo Manager playback: https://cs-demo-manager.com/docs/guides/playback
- HLAE `mirv_camexport`: https://github.com/advancedfx/advancedfx/wiki/Source:mirv_camexport · https://github.com/advancedfx/advancedfx/wiki/Source:Commands
