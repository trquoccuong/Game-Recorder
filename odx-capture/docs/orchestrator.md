# Orchestrator and odx merge

`odx-orchestrator` runs the rooms and rounds: it tells every player's capture and the game server agent when to start and
stop, runs the time service everybody measures their clock against, and gives rooms a LAN when asked. It stores no files:
when a round is over it sends each participant the round's files (`round.json`, `server_events.jsonl`,
`game_profile.json`), which they keep next to their recording.

It does not need to be next to the CS2 server (its agent, `odx game-server`, only connects to it:
[cs2-server.md](cs2-server.md#6-the-game-server-agent)). On a Linux server, `deploy/` runs it behind HTTPS (`README.md`,
"Deploy the orchestrator"). On Windows, it is in the package (`bin\`).

The sessions to hand over are built afterwards by `odx merge`, from the rounds copied together by hand
([below](#build-the-sessions)).

## Get it

`odx-orchestrator.exe` is in the package (`bin\`), next to `odx.exe`, `games\cs2.json` and `odx-capture.json`. Build
the package with `capture-cs/publish.ps1` (see [player-setup.md](player-setup.md#1-build-the-package)) or use one already
built.

## Run

```powershell
$bin = 'D:\odx\odx-capture-0.1.0-win-x64\bin'
& "$bin\odx-orchestrator.exe" --game-profile "$bin\games\cs2.json" --access-key <access key>
```

| Option | |
|---|---|
| `--game-profile` | The game every room plays: `games\cs2.json`. |
| `--access-key` | Or `ODX_ACCESS_KEY`. Every request must carry it; put the same key in the players' package and give it to the game server agent. Use one whenever the orchestrator is reachable from the internet. |
| `--http-port`, `--time-port` | Default 5180 (TCP) and 12300 (UDP). |
| `--fps` | The players' recording rate; default 30. |

The orchestrator also measures its own clock against `time_server` of the `odx-capture.json` next to it
(`time.windows.com` by default) and writes the result into each round's record.

## Check that it runs

```powershell
curl.exe http://127.0.0.1:5180/status -H "Authorization: Bearer <access key>"
# {"rooms":[],"agents_outside_rooms":[],"game_server":"CS2 server"}
```

`game_server` names the connected game server agent (null: none, and rounds have no server side). When players are
connected, `/status` lists the rooms, their members and states (`idle`, `armed`, `starting`, `recording`), the rounds
still waiting for their participants (`pending_sessions`) and the finished ones (`sessions`: `session_id`, `players`,
`kept`).

## Log lines to know

| Line | Meaning |
|---|---|
| `game server agent CS2 server (0.1.0) connected` | The CS2 server's side will be recorded from the next round on. |
| `room XXXXXX opened by <player>` | A host created a room; its invite code is XXXXXX. |
| `round <session>: N player(s), play starts in 3 s` | The host started a round; the game server agent starts SourceTV now. |
| `<player> is <state>, left out of <session>` | **Warning**: that player was still busy with the previous round. |
| `game server agent, <session>: …` | **Warning**: the server's side of that round was not kept (the reason follows). |
| `round <session>: 2/2 players kept their recording` | Everyone is done; the round's files went to every participant. |

## Build the sessions

Each player keeps a round in the package's `data\captures\<session>\`, the game server agent in `<work>\<session>\`.
For now they are copied by hand (upload to a server is a later step) into one folder per round, on any machine with the
package (or the `net10.0` build of `odx`):

```
<test>\<session>\
  round.json  server_events.jsonl  game_profile.json     (from any participant: all got the same)
  players\<pid>\                                          (each player's data\captures\<session>\)
  server\                                                 (the game server's <work>\<session>\server\)
```

```powershell
& "$bin\odx.exe" merge D:\odx\test [--out DIR]
# PASS  <session>  2/2 players valid, 2 required -> ...\data\delivery\odx\<mm-dd-yyyy>\counter-strike-2\<session>
```

It builds every session folder in `<test>` (a folder with `round.json`; `<test>` itself may be one) into
`<out>\odx\<mm-dd-yyyy>\<game>\<session>\`, by default under the package's `data\delivery\` (what to hand over), and
prints PASS/FAIL per session with its issues (e.g. `[warning] server.capture: SourceTV is not recording …`, see
[troubleshooting.md](troubleshooting.md#sourcetv-is-not-recording)). Exit code 1 if any session fails, 2 on bad usage or
when there is no session. Plan disk space: about 2 MB per second of video per player.

Keep the copied rounds: a newer version can build the sessions again from them with the same command.
