# Client Game Data Capture

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

Odx is the vendor building game-data capture software for Client (client).

## Directory structure

```
document/                         # Specs and plans (Markdown, source of truth for requirements)
  README.md                       # Index + reading order
  sow-round-4-multiplayer.md      # Signed SOW
  videogame-data-capture-spec-v2.md   # Canonical per-session format + validation checks (§1.5)
  multiplayer-data-capture-spec-v3.md # Multiplayer extension of v2
  software-plan.md                # Architecture, modules, testing strategy (§7.10), roadmap
  games/                          # Per-game analyses + compliance ground rules
  reviews/                        # Reviews of sample/vendor data
  raw/                            # Original PDFs (do not edit)
client-capture/
  capture-cs/                     # C# .NET 8 — everything outside the game process
    src/Odx.Core/                 #   portable: Input/ (logs, IPC, keys), Video/ (grid, status), Formats/ (frames.csv, CSV), Geometry/
    src/Odx.Session/              #   capture host (runs odx input + odx record), session builder, validator (v2 §1.5)
    src/Odx.Cli/                  #   odx: input | record (Windows build) | build | validate
    src/Odx.Input/                #   `odx input` (Raw Input)
    src/Odx.Recorder/             #   `odx record` (WGC + ffmpeg)
    src/Odx.App/                  #   odx-app (WPF front end over Odx.Session)
    tests/Odx.Tests/              #   xUnit, incl. synthetic session + one mutator per validation rule
  games/                          # game profiles (session.json fields + key/button -> action bindings)
  extractor/                      # C++20 + Lua 5.4: in-game camera extractor DLL (camera_samples.csv), odx_synth, profiles
  schemas/                        # session.json JSON Schema (embedded in Odx.Session)
  tests/fixtures/                 # pinned log formats (input_events.csv, video_frames.csv)
research/
  analyze-pose/                   # Standalone Python pose audit (numpy), not part of the product
```

## Build & test

Toolchain: .NET 8 SDK, CMake ≥ 3.24 + MSVC (C++20), ffmpeg/ffprobe on PATH.

```bash
# C# (from client-capture/capture-cs/)
dotnet build && dotnet test

# C++ (from client-capture/extractor/; build capture-cs first so ctest can build + validate the synthetic capture)
cmake -B build && cmake --build build --config Debug
ctest --test-dir build -C Debug

# Self-contained package with ffmpeg (from client-capture/capture-cs/) -> dist/odx-capture-<ver>-win-x64.zip
powershell -ExecutionPolicy Bypass -File publish.ps1 -Version 0.1.0
```

## Working rules

1. **Specs in `document/` drive the code** — cite the spec section (e.g. `v2 §1.5`, `plan §7.10`) when implementing or changing behavior.
2. **Don't edit `document/raw/`** — the PDFs are originals; Markdown versions are the working copies.
3. **One implementation per format** — geometry, input aggregation, `frames.csv`, `session.json` and the validator exist only in C# (`Odx.Core` / `Odx.Session`). Capture tools, C++ extractor included, write raw logs only; each log format is pinned by a fixture in `tests/fixtures/` that the writer must reproduce byte for byte.
4. **Unresolved spec questions go to the README** — list them under "Decisions pending with Client" in `client-capture/README.md`; don't silently pick an interpretation.
5. **Compliance** — follow `document/games/README.md` ground rules: no anti-cheat bypass, no public matchmaking, no client memory reading on anti-cheat-protected titles.
6. **Code is the source of truth; comment only when necessary** — names and structure carry the "what". Write a comment only for what the code cannot say: why a choice was made, a non-obvious constraint (ABI layout, threading, loader lock), a magic number, a cross-language contract, or a spec reference. No comments that restate the function/class name, no section banners, no commented-out code. Delete a comment that has gone stale instead of leaving it wrong.

## Language

- Docs/comments: English
- Code/tech terms: English
