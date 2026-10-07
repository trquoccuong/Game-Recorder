# Multiplayer test with two players (CS2)

What to deploy, and where, to record a CS2 round with two players into a spec v3 session: each player's video, input
and packets, plus the game server's side (per-tick state, game events, net messages, per-frame camera).

| Guide | For |
|---|---|
| [cs2-server.md](cs2-server.md) | The CS2 dedicated server: install, Metamod, the `odx_netlog` plugin, configs, start, its agent (`odx game-server`) |
| [orchestrator.md](orchestrator.md) | `odx-orchestrator` and `odx-merge`: where they run, options, access key, logs, sessions |
| [network.md](network.md) | How players reach the server: same LAN, port forwarding or a VPN; ports and firewall |
| [player-setup.md](player-setup.md) | Each player's PC: the package, CS2 with `-insecure`, own Steam account |
| [test-run.md](test-run.md) | One two-player round step by step, and what to check in the session |
| [troubleshooting.md](troubleshooting.md) | Problems already met and their fixes |

## What runs where

```
 CS2 server machine (Windows)              Orchestrator + merge (Linux or Windows)       Player A / B (Windows)
 ┌──────────────────────────────────┐     ┌───────────────────────────────────────┐     ┌──────────────────────────┐
 │ CS2 dedicated server  udp/27015  │◄────┼───────────────── game ────────────────┼─────│ CS2 (-insecure)          │
 │   Metamod + odx_netlog plugin │     │                                       │     │                          │
 │   SourceTV (demo per round)      │     │ odx-orchestrator  tcp/5180, udp/12300 │◄────│ Odx Capture (odx-app) │
 │     ▲ RCON tcp/27015 (local)     │     │   rooms, rounds, clock                │     │   video, input, packets  │
 │ odx game-server ─────────────────┼────►│                                       │     │                          │
 │   start/stop, clock samples      │     │ odx-merge  tcp/5190                   │◄────│   uploads after a round  │
 │   uploads demo + plugin log ─────┼────►│   assembles → sessions                │     │                          │
 └──────────────────────────────────┘     └───────────────────────────────────────┘     └──────────────────────────┘
```

- **odx game-server runs on the CS2 server's machine**: it drives SourceTV over RCON and reads the demo and the plugin log
  from disk. It samples that machine's clock against the orchestrator's time service, so the server's times land on the
  same clock as the players'.
- The orchestrator only coordinates; odx-merge does the heavy part (every player's video, the demo) one round at a time.
  They can share a machine (`deploy/`, or both executables of the package) or not.
- The two players must use **two different Steam accounts**: a player is matched to the server's data by the Steam account
  signed in on their PC.
- For a small test everything can run on one PC, even one of the players' (see [cs2-server.md](cs2-server.md#a-player-hosting-the-server)).

## Checklist

1. CS2 server machine: CS2 dedicated server with Metamod, the plugin and the SourceTV configs ([cs2-server.md](cs2-server.md)).
2. odx-merge and odx-orchestrator started, the orchestrator with `--merge` ([orchestrator.md](orchestrator.md)).
3. CS2 server machine: `odx game-server` connected to the orchestrator ([cs2-server.md](cs2-server.md#6-the-game-server-agent)).
4. Network: players reach udp/27015 on the CS2 server; players and the CS2 server machine reach tcp/5180, tcp/5190 and
   udp/12300 on the orchestrator's machine ([network.md](network.md)).
5. A package built for the orchestrator's address, unzipped on both players' PCs ([player-setup.md](player-setup.md)).
6. Each player: CS2 with `-insecure`, signed in to their own Steam account.
7. Play a round ([test-run.md](test-run.md)) and check the session.

## Compliance

Our own dedicated server, started `-insecure`; players launch CS2 with `-insecure` (Valve's own option; such a client
cannot join VAC-secured servers until restarted without it). Nothing reads or changes the CS2 client: the plugin runs on
the server only, and the server runs from its own install. No public matchmaking. See `document/games/README.md`.
