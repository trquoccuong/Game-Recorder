# Plan: four apps, local recordings, merge per game

Status: proposed 2026-10-07, not started. Overview page: https://claude.ai/artifact/UTnVDJbYXcV941tmo48d2C

## Goal

Split the multiplayer capture (spec v3) into four parts that each do one job, and test the whole path locally before
anything is uploaded:

| # | Part | Where | Does |
|---|---|---|---|
| 1 | **Odx Capture** (player app) | each player's PC, Windows | records the player's side of every round and keeps it locally |
| 2 | **odx-orchestrator** | any reachable machine, Linux or Windows | rooms, start/stop, time service, LAN; stores no files |
| 3 | **Odx Game Server** | the game server machine, Windows | CS2 dedicated server + `odx game-server`; records the server's side and keeps it locally |
| 4 | **Merge** (later: upload too) | a laptop now, a server later | builds the delivery from copied folders, with a shared core and one module per game |

Upload is out of scope for now: recordings are copied by hand into one folder and merged there.

## The local test

1. The orchestrator starts a round; every player's app and the room's game server start after the same countdown and
   sample their clocks against the time service.
2. The round ends; the orchestrator sends every participant the round record (players, times, stop reasons), the
   round's events and the game profile.
3. Each player's app builds and validates its recording (v2 §1.5) and keeps it in `data\captures\<session>\`.
4. The game server agent keeps the demo, the plugin log and its clock samples in `<work>\<session>\server\`.
5. The tester copies them into one folder:

   ```
   <test>/<session>/
     round.json  server_events.jsonl  game_profile.json     (from any participant: all got the same)
     players/<pid>/                                          (each player's data\captures\<session>\)
     server/                                                 (the game server's <work>\<session>\server\)
   ```

   This is today's staging layout, so `MultiplayerSession.Assemble` reads it unchanged.
6. `odx merge <test> [--out DIR]` builds every session in it into `delivery/<vendor>/<date>/<game>/<session>/` and
   prints each gate result.

## The four parts

### 1. Odx Capture (odx-app + odx agent)

Today: `Odx.App` (WPF) and `odx agent` (Odx.Cli) both drive `AgentClient`; after each round the agent uploads to odx-merge.

- One app: odx-app gains a headless mode (`--headless --room CODE --player ID [--auto-ready]`) that replaces
  `odx agent`; the CLI keeps `input`, `record`, `build`, `validate`.
- After a round: no upload. Write `round.json`, `server_events.jsonl` and `game_profile.json` (from the orchestrator's
  new `round_record` message) next to the recording, plus `manifest.json` (file names, sizes, SHA-256) for the later
  upload.
- The room status shows the local result (PASS/FAIL of the player's own checks); the session's gate comes only from merge.

### 2. odx-orchestrator

- Drop `MergeClient`, `--merge`, `--merge-public` and `Welcome.MergeUrl`; no assemble step and no `session_result`.
- At the end of a round (and on cancel), send `round_record` to every player in it and to the game server agent.
- Several game servers: each game server agent says which server it is; a room is bound to one of them (step 6).

### 3. Odx Game Server (CS2 dedicated + odx game-server)

- `GameServerAgent` keeps each round in `<work>\<session>\` (with the round record) instead of uploading it; a cancelled
  round is deleted as today.
- One package and one setup script for CS2 + Metamod + `odx_netlog` + the agent, with the known fixes
  (`deploy/gcp/setup.ps1`, `docs/troubleshooting.md`).
- Later: several CS2 instances per machine (own directory via hard links, ports, RCON, one agent each). With 2-player
  matches one 8 vCPU / 32 GB machine should hold about 15 (estimate, to load-test).

### 4. Merge

- `odx merge` command (Odx.Cli, or its own `odx-merge` executable) over local folders; the ASP.NET `MergeHost` goes.
- Core (every game), today in `MultiplayerSession`: player recordings, clock alignment, round record and events,
  `session.json` v3, the gate, the delivery layout.
- Game module per game, chosen by the game profile; CS2 first, from `Cs2ServerCapture` and `ServerCamera`:

  ```csharp
  interface IGameMerger
  {
      string Game { get; }
      bool HasServerSide(string stagedServerDir);
      ServerSide WriteServer(string stagedServerDir, string sessionServerDir, RoundRecord round);
  }
  ```

  `ServerSide` holds the server's `session.json` fields, a camera track per player and the problems found.
  A game without a module still gets a valid session from the players and the round record; only `server/` is empty.
- Same repository and the shared `Odx.Core` / `Odx.Session` (working rule 3: one implementation per format); it can get
  its own solution and deploy later.

## Steps

Each step ends with `dotnet test` passing; the end-to-end tests change with it.

1. **Orchestrator sends `round_record`; agents keep rounds locally.** Player agent and game server agent write the round
   record, events and profile next to their recordings and stop uploading.
   Verify: a test with a fake game server and two agents leaves two player folders and a server folder in the staging
   layout.
2. **`odx merge <folder>`.** Assemble every session folder in it.
   Verify: step 1's folders, copied into one, merge to the same session the server-side assembly produced (PASS, same
   files).
3. **Remove the merge server path.** `MergeHost`, `MergeClient`, the upload endpoints, `Welcome.MergeUrl`,
   `session_result`; update `deploy/` (orchestrator only), the docs and the README.
   Verify: tests pass; `deploy/compose.yaml` runs the orchestrator alone.
4. **Game module.** Introduce `IGameMerger`; move `Cs2ServerCapture` / `ServerCamera` behind the CS2 module.
   Verify: the CS2 tests produce byte-identical sessions; a session of the synthetic game (no module) still passes.
5. **One player app.** Headless mode in odx-app; `odx agent` removed; manifest written.
   Verify: the multiplayer test runs with headless odx-app instances; the manifest's hashes match the files.
6. **Several game servers.** Agents identify their server; rooms are bound to one; per-instance setup.
   Verify: two rooms record at once on two fake servers, each session gets its own server side.
7. **Two-player test on real machines** (`docs/test-run.md`), merged on a laptop.

## Later

- Upload: where to (our server or S3 / R2 directly), uploader in the player app, how long to wait for missing players,
  deleting local copies, a token per player. Merge then runs as a worker on the uploaded `raw/`.
- Project 4 in its own repository (the shared library as a package) if the teams split.
