# Orchestrator next to the CS2 server

`odx-orchestrator` runs the rooms, the time service the players measure their clocks against, the uploads, and builds
each round into a session. For CS2 it also drives the server's SourceTV and reads the demo and the plugin log, so it runs
**on the CS2 server's machine** (the Linux deployment in `deploy/` has no CS2 server next to it; use it for games without
a server side).

## Get it

`odx-orchestrator.exe` is in the package (`bin\`), next to `games\cs2.json` and `odx-capture.json`. Build the package
with `capture-cs/publish.ps1` (see [player-setup.md](player-setup.md#1-build-the-package)) or use one already built.

## Run

```powershell
# The RCON password from cs2-server.md, alone in a file only this user can read.
Set-Content -Path D:\odx\rcon.txt -Value '<RCON password>' -NoNewline

$bin = 'D:\odx\odx-capture-0.1.0-win-x64\bin'
& "$bin\odx-orchestrator.exe" --game-profile "$bin\games\cs2.json" `
    --cs2-game-dir D:\cs2-server\game\csgo --cs2-rcon 127.0.0.1:27015 --cs2-rcon-password-file D:\odx\rcon.txt `
    --access-key <access key>
```

| Option | |
|---|---|
| `--out` | Optional. By default sessions go to the package's `data\delivery\odx\<mm-dd-yyyy>\counter-strike-2\<session>\` (what to hand over) and rounds are staged in `data\staging\` while players upload; with `--out` both go under that folder (`<out>\.staging\`). Plan disk space: about 2 MB per second of video per player. |
| `--game-profile` | The game every room plays: `games\cs2.json`. |
| `--cs2-game-dir` | The server's `game\csgo` folder (where `replays\` and `odx-netlog\` are). |
| `--cs2-rcon` | The server's RCON address; default `127.0.0.1:27015`. |
| `--cs2-rcon-password-file` | Or the `ODX_CS2_RCON_PASSWORD` environment variable. |
| `--access-key` | Or `ODX_ACCESS_KEY`. Every request must carry it; put the same key in the players' package. Use one whenever the orchestrator is reachable from the internet. |
| `--http-port`, `--time-port` | Default 5180 (TCP) and 12300 (UDP). |

The orchestrator also measures its own clock against `time_server` of the `odx-capture.json` next to it
(`time.windows.com` by default) and writes the result into each session.

## Check that it runs

```powershell
curl.exe http://127.0.0.1:5180/status -H "Authorization: Bearer <access key>"
# {"rooms":[],"agents_outside_rooms":[]}
```

When players are connected, `/status` lists the rooms, their members and states (`idle`, `armed`, `recording`,
`uploading`).

## Log lines to know

| Line | Meaning |
|---|---|
| `room XXXXXX opened by <player>` | A host created a room; its invite code is XXXXXX. |
| `round <session>: N player(s), play starts in 3 s` | The host started a round; SourceTV starts recording now. |
| `CS2 server: <session>: SourceTV is not recording …` | **Error**: no demo for this round. See [troubleshooting.md](troubleshooting.md#sourcetv-is-not-recording). |
| `<player> done with <session>: uploaded` | That player's capture arrived. |
| `session <session>: PASS, 2/2 players valid -> <folder>` | The session is built; warnings follow if any. |
| `cannot assemble <session>: …` | The session could not be built; the staging folder is kept for a rebuild. |

## Rebuild a session

A round's inputs stay in `data\staging\<session>-<id>\` (players' uploads, the server's demo and log). A newer version
can build the session again from it (`MultiplayerSession.Assemble(<staging>, <delivery>, "odx")` in `Odx.Session`); there
is no command for that yet, so keep the staging folders of rounds you may want to rebuild.
