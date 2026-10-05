# Multiplayer test with two players (CS2)

What to deploy, and where, to record a CS2 round with two players into a spec v3 session: each player's video, input
and packets, plus the game server's side (per-tick state, game events, net messages, per-frame camera).

| Guide | For |
|---|---|
| [cs2-server.md](cs2-server.md) | The CS2 dedicated server: install, Metamod, the `odx_netlog` plugin, configs, start |
| [orchestrator.md](orchestrator.md) | `odx-orchestrator` next to the CS2 server: options, access key, logs, sessions |
| [network.md](network.md) | How players reach the server: same LAN, port forwarding or a VPN; ports and firewall |
| [player-setup.md](player-setup.md) | Each player's PC: the package, CS2 with `-insecure`, own Steam account |
| [test-run.md](test-run.md) | One two-player round step by step, and what to check in the session |
| [troubleshooting.md](troubleshooting.md) | Problems already met and their fixes |

## What runs where

```
 Server machine (Windows)                                Player A / Player B (Windows, one each)
 ┌─────────────────────────────────────────┐            ┌──────────────────────────────────────┐
 │ CS2 dedicated server  udp/27015         │◄── game ───│ CS2 (launched with -insecure)        │
 │   Metamod + odx_netlog plugin        │            │                                      │
 │   SourceTV (demo per round)             │            │ Odx Capture (odx-app)             │
 │        ▲ RCON tcp/27015 (local only)    │            │   video, input, packets (pktmon)     │
 │        │ demo + plugin log (files)      │            │                                      │
 │ odx-orchestrator  tcp/5180, udp/12300   │◄── rooms, clock, uploads ─────────────────────────┘
 │   sessions → data\delivery\odx\...    │
 └─────────────────────────────────────────┘
```

- The **orchestrator must run on the CS2 server's machine**: it reads the server's demo and plugin log from disk, and the
  plugin's timestamps are that machine's clock.
- The two players must use **two different Steam accounts**: a player is matched to the server's data by the Steam account
  signed in on their PC.
- The server machine can be a third PC or one of the players' PCs (see [cs2-server.md](cs2-server.md#a-player-hosting-the-server)).

## Checklist

1. Server machine: CS2 dedicated server with Metamod, the plugin and the SourceTV configs ([cs2-server.md](cs2-server.md)).
2. Server machine: odx-orchestrator started with `--cs2-game-dir` ([orchestrator.md](orchestrator.md)).
3. Network: both players reach udp/27015, tcp/5180 and udp/12300 on the server ([network.md](network.md)).
4. A package built for the orchestrator's address, unzipped on both players' PCs ([player-setup.md](player-setup.md)).
5. Each player: CS2 with `-insecure`, signed in to their own Steam account.
6. Play a round ([test-run.md](test-run.md)) and check the session.

## Compliance

Our own dedicated server, started `-insecure`; players launch CS2 with `-insecure` (Valve's own option; such a client
cannot join VAC-secured servers until restarted without it). Nothing reads or changes the CS2 client: the plugin runs on
the server only, and the server runs from its own install. No public matchmaking. See `document/games/README.md`.
