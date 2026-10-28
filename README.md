# Client — Odx game data capture

Odx builds the software that records multiplayer gameplay. Every session follows the client's canonical format: one video per
player, per-frame camera pose and intrinsics, input, and in multiplayer the server's state and every player on one clock.

- [Repository layout](#repository-layout)
- [Components](#components)
- [Install a development machine](#install-a-development-machine)
- [Build and test](#build-and-test)
- [First runs](#first-runs)
- [Where to read next](#where-to-read-next)

## Repository layout

```
client/
├── document/            Specs, SOW, plan, per-game analyses (source of truth; not tracked in git)
├── capture/             The product
│   ├── dotnet/          C# (.NET 10): every tool outside the game process — OdxCapture.sln
│   │   ├── src/         Libraries (Odx.Core, Protocol, Capture, Agent, Merge)
│   │   │   ├── Apps/    Executables (odx-app, launcher, odx, odx-orchestrator, odx input / odx record hosts)
│   │   │   └── Games/   Per-game modules (Cs2, Csgo, Wc3)
│   │   ├── tests/       xUnit (Odx.Tests)
│   │   ├── fixtures/    Pinned log formats shared by the C# readers and the C++ writers
│   │   ├── ffmpeg/      Minimal ffmpeg build script (shipped with the package)
│   │   └── publish.ps1  Self-contained Windows package
│   ├── native/          C++: in-game camera extractor (extractor/), CS2 server plugin (cs2-plugin/)
│   ├── profiles/        Game profiles (bindings, window, network)
│   ├── config/          odx-capture.json (deployment config), session_schema.json
│   ├── deploy/          Orchestrator on a Linux server (Docker + Caddy); aws/ (EC2 scripts), gcp/ (Windows VM)
│   └── guides/          How-to guides for a multiplayer test
└── research/            Standalone tools, not part of the product
    ├── analyze-pose/            Pose audit of captured sessions (Python)
    └── reforger-camera-spike/   Arma Reforger camera spike (Enforce Script mod, OdxReforger.exe launcher, Python)
```

## Components

| Component | What it does | Where | Built from |
|---|---|---|---|
| **Odx Capture** (`Odx Capture.exe` → `odx-app`) | The players' desktop app: records video + input (+ packets), joins rooms, uploads nothing; English / Vietnamese UI | each player's PC | `src/Apps/Odx.Launcher`, `src/Apps/Odx.App` |
| **`odx`** | Command-line tools in one executable: `input`, `record` (Windows), `build`, `validate`, `merge`, `game-server`, `simulate-game` | any PC; Linux build without `input` / `record` | `src/Apps/Odx.Cli` |
| `odx input` | Keyboard / mouse with Raw Input in its own process → `input_events.csv`, `clock_samples.csv`, `ntp_samples.csv` | player's PC | `src/Apps/Odx.Input` |
| `odx record` | The game window or a monitor (Windows Graphics Capture) → `video.mp4` through ffmpeg, `video_frames.csv` | player's PC | `src/Apps/Odx.Recorder` |
| `odx build` / `odx validate` | Capture folder → spec v2 session (`frames.csv`, `session.json`); every v2 §1.5 check | any PC | `src/Odx.Capture` |
| **`odx-orchestrator`** | Multiplayer coordination: rooms, rounds (start / stop every capture), time service, room LANs, SQLite state | one reachable server | `src/Apps/Odx.Orchestrator` |
| `odx game-server` | The game server's agent: records the server's side of every round (CS2: SourceTV demo, plugin log) | game server machine | `src/Odx.Agent`, `src/Games/*` |
| `odx merge` | Rounds copied from every participant → spec v3 sessions + sync gate | tester's PC | `src/Odx.Merge`, `src/Games/*` |
| Game modules | What only one game knows: server recording and reading (CS2), packet decoders (CS:GO, Warcraft III) | inside `odx` | `src/Games/Odx.Games.*` |
| **Camera extractor** | `odx_extractor.dll`, inside the game: a Lua profile finds the camera, writes `camera_samples.csv` | player's PC (titles without anti-cheat) | `native/extractor` |
| **odx_netlog** | Metamod:Source 2.0 plugin on our CS2 server: every net message, decoded by the engine | CS2 server | `native/cs2-plugin` |
| Game profiles | `session.json` fields the capture cannot observe, key → action bindings, window, traffic to record | shipped as `bin/games/` | `profiles/*.json` |

Single player: `odx input` + `odx record` (+ extractor) → `odx build` → `odx validate`.
Multiplayer: the orchestrator starts and stops every player's capture and the server's recording at once; the tester
copies each round's parts into one folder and runs `odx merge`. Details: [`capture/README.md`](capture/README.md).

## Install a development machine

Windows 10 2004+ / 11, x64. Commands for PowerShell with [winget](https://learn.microsoft.com/windows/package-manager/).

| # | Tool | Needed for | Install |
|---|---|---|---|
| 1 | Git | everything | `winget install Git.Git` |
| 2 | .NET 10 SDK (≥ 10.0.400, `capture/dotnet/global.json`) | all C# | `winget install Microsoft.DotNet.SDK.10` |
| 3 | Visual Studio 2022 Build Tools, workload "Desktop development with C++" (MSVC + Windows SDK) | `native/`, the NativeAOT launcher in the package | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| 4 | CMake ≥ 3.24 | `native/` | `winget install Kitware.CMake` |
| 5 | ffmpeg + ffprobe on `PATH` | the video tests (encode fixtures, cross-check the MP4 reader) | `winget install Gyan.FFmpeg` |
| 6 | MSYS2 at `C:\msys64` (or `MSYS2_ROOT`) | only `publish.ps1`: builds the shipped ffmpeg once | `winget install MSYS2.MSYS2`, then in the MSYS2 shell: `pacman -S make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf` |
| 7 | Python 3.12 | only `research/` | `winget install Python.Python.3.12` |
| 8 | ZeroTier One | optional: rooms "with LAN" (odx-app offers to install it) | `winget install ZeroTier.ZeroTierOne` |
| 9 | Docker | optional: running `capture/deploy/` locally (it targets a Linux server) | Docker Desktop |

Open a new terminal after installing, then check:

```powershell
git --version; dotnet --list-sdks; cmake --version; ffmpeg -version; ffprobe -version
```

Everything else is fetched by the builds: NuGet packages, and for `native/extractor` Lua 5.4, MinHook and doctest
(CMake FetchContent, so the first configure needs internet). `native/cs2-plugin` also needs hl2sdk (branch `cs2`) and the
Metamod:Source source tree: [`capture/native/cs2-plugin/README.md`](capture/native/cs2-plugin/README.md).

## Build and test

```bash
# C# — from capture/dotnet/
dotnet build
dotnet test                       # ~180 tests; the video tests need ffmpeg/ffprobe

# C++ extractor — from capture/native/extractor/ (build capture/dotnet first: ctest runs odx build + odx validate)
cmake -B build
cmake --build build --config Debug
ctest --test-dir build -C Debug

# Players' package — from capture/dotnet/ (needs MSYS2 the first time) -> dist/odx-capture-<version>-win-x64.zip
powershell -ExecutionPolicy Bypass -File publish.ps1 -Version 0.1.0

# Arma Reforger launcher (any OS) -> research/reforger-camera-spike/launcher/OdxReforger/bin/Release/net48/OdxReforger.exe
dotnet build -c Release research/reforger-camera-spike/launcher/OdxReforger
```

CI (`.github/workflows/ci.yml`) runs the C# build and tests, then the extractor with ctest (Windows), and builds
`OdxReforger.exe` (Linux). Players get it from `.github/workflows/release-reforger.yml`: push a tag `reforger-v<version>`
(or Run workflow) and the exe is attached to a release; deploy the same commit's addon to the server. The players' package is built by
`.github/workflows/release-capture.yml`: push a tag `capture-v<version>` (or Run workflow) and the zip, pointed at the
orchestrator in the repository variable `ODX_ORCHESTRATOR` and secret `ODX_ACCESS_KEY`, is attached to a release.

## First runs

```bash
# from capture/dotnet/
dotnet run --project src/Apps/Odx.App                                               # the players' app
dotnet run --project src/Apps/Odx.Orchestrator -- --game-profile ../profiles/example-fps.json   # a local orchestrator
dotnet run --project src/Apps/Odx.Cli -f net10.0 -- validate path/to/<capture-dir>  # check a capture
```

A full two-player test (CS2 server, orchestrator, network, players): [`capture/guides/`](capture/guides/README.md).

## Where to read next

| To | Read |
|---|---|
| Understand the data format and checks | `document/videogame-data-capture-spec-v2.md`, `document/multiplayer-data-capture-spec-v3.md` |
| See the plan, modules and testing strategy | `document/software-plan.md` |
| Pick or add a game | `document/games/README.md` (compliance ground rules first) |
| Use or change a tool | [`capture/README.md`](capture/README.md) |
| Set up a multiplayer test | [`capture/guides/README.md`](capture/guides/README.md) |
| Deploy the orchestrator | [`capture/README.md`, "Deploy the orchestrator"](capture/README.md#deploy-the-orchestrator-linux-server), [`capture/deploy/aws/README.md`](capture/deploy/aws/README.md) (EC2 + Reforger server), [`capture/deploy/gcp/README.md`](capture/deploy/gcp/README.md) |
| Contribute | [`CLAUDE.md`](CLAUDE.md): working rules (specs drive the code, one implementation per format, compliance) |
