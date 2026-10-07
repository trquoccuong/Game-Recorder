# Orchestrator and odx-merge

`odx-orchestrator` runs the rooms and rounds: it tells every player's capture and the game server agent when to start and
stop, runs the time service everybody measures their clock against, and gives rooms a LAN when asked. It stores no files.
`odx-merge` receives each round's uploads (the players' captures, the game server's demo and log) and assembles the
session when the orchestrator asks, one round at a time.

They can run on the same machine or on two; neither needs to be next to the CS2 server (its agent, `odx game-server`,
uploads to odx-merge: [cs2-server.md](cs2-server.md#6-the-game-server-agent)). On a Linux server, `deploy/` runs both
behind HTTPS (`README.md`, "Deploy the orchestrator and odx-merge"). On Windows, both are in the package (`bin\`).

## Get them

`odx-orchestrator.exe` and `odx-merge.exe` are in the package (`bin\`), next to `games\cs2.json` and
`odx-capture.json`. Build the package with `capture-cs/publish.ps1` (see
[player-setup.md](player-setup.md#1-build-the-package)) or use one already built.

## Run

```powershell
$bin = 'D:\odx\odx-capture-0.1.0-win-x64\bin'
& "$bin\odx-merge.exe" --access-key <access key>
& "$bin\odx-orchestrator.exe" --game-profile "$bin\games\cs2.json" --merge http://127.0.0.1:5190 `
    --merge-public http://<this machine's address>:5190 --access-key <access key>
```

odx-orchestrator:

| Option | |
|---|---|
| `--game-profile` | The game every room plays: `games\cs2.json`. |
| `--merge` | odx-merge as this machine reaches it. Without it rounds are recorded but never assembled. |
| `--merge-public` | odx-merge as the players and the game server agent reach it (sent to them on connect); default: `--merge`. |
| `--access-key` | Or `ODX_ACCESS_KEY`. Every request must carry it; put the same key in the players' package and give it to odx-merge and the game server agent. Use one whenever the orchestrator is reachable from the internet. |
| `--http-port`, `--time-port` | Default 5180 (TCP) and 12300 (UDP). |
| `--fps` | The players' recording rate; default 30. |

odx-merge:

| Option | |
|---|---|
| `--out` | Optional. By default sessions go to the package's `data\delivery\odx\<mm-dd-yyyy>\counter-strike-2\<session>\` (what to hand over) and rounds are staged in `data\staging\` while everyone uploads; with `--out` both go under that folder (`<out>\.staging\`). Plan disk space: about 2 MB per second of video per player. |
| `--staging` | Where rounds are staged, when not under `--out`. |
| `--access-key` | Or `ODX_ACCESS_KEY`: the orchestrator's. |
| `--http-port` | Default 5190 (TCP). |
| `--min-valid`, `--max-rtt-ms` | Gate: valid players a session needs (default all, up to 3 players) and the largest median RTT to the time service (default 100 ms). |

The orchestrator also measures its own clock against `time_server` of the `odx-capture.json` next to it
(`time.windows.com` by default) and writes the result into each session.

## Check that they run

```powershell
curl.exe http://127.0.0.1:5180/status -H "Authorization: Bearer <access key>"
# {"rooms":[],"agents_outside_rooms":[],"game_server":"CS2 server"}
curl.exe http://127.0.0.1:5190/status -H "Authorization: Bearer <access key>"
# {"staged":[]}
```

`game_server` names the connected game server agent (null: none, and rounds have no server side). When players are
connected, the orchestrator's `/status` lists the rooms, their members and states (`idle`, `armed`, `recording`,
`uploading`).

## Log lines to know

| Process | Line | Meaning |
|---|---|---|
| orchestrator | `game server agent CS2 server (0.1.0) connected` | The CS2 server's side will be recorded from the next round on. |
| orchestrator | `room XXXXXX opened by <player>` | A host created a room; its invite code is XXXXXX. |
| orchestrator | `round <session>: N player(s), play starts in 3 s` | The host started a round; the game server agent starts SourceTV now. |
| orchestrator | `<player> done with <session>: uploaded` | That player's capture arrived on odx-merge. |
| orchestrator | `game server agent, <session>: …` | **Warning**: the server's side of that round did not arrive (the reason follows). |
| orchestrator | `odx-merge: cannot open <session>` | **Error**: odx-merge is unreachable; that round's uploads will be refused. |
| odx-merge | `session <session>: PASS, 2/2 players valid -> <folder>` | The session is built; warnings follow if any. The orchestrator logs the same line. |
| odx-merge | `<session>: [server.capture] SourceTV is not recording …` | No demo for this round. See [troubleshooting.md](troubleshooting.md#sourcetv-is-not-recording). |

## Rebuild a session

A round's inputs stay in odx-merge's `data\staging\<session>\` (players' uploads, the server's demo, log and clock
samples, the round's record and events). A newer version can build the session again from it
(`MultiplayerSession.Assemble(<staging>, <delivery>, "odx")` in `Odx.Session`); there is no command for that yet, so
keep the staging folders of rounds you may want to rebuild.
