# Network

Who must reach what (the CS2 server machine and the orchestrator's machine can be the same):

| Port | Protocol | On | What | Reached by |
|---|---|---|---|---|
| 27015 | UDP | CS2 server | CS2 game traffic | players |
| 5180 | TCP | orchestrator | Rooms and rounds (WebSocket), status | players, the game server agent |
| 12300 | UDP | orchestrator | Time service (clock sync) | players, the game server agent |
| 9993 | UDP | ZeroTier controller (`deploy/`) | Rooms created with a LAN (not needed for CS2) | players |
| 27015 | TCP | CS2 server | RCON (game server agent → CS2 server) | **nobody else**: same machine only |
| 27020 | UDP | CS2 server | SourceTV broadcast | nobody (the demo is recorded locally) |

Nothing is uploaded: every player and the game server agent keep their recordings, and the rounds are copied together
by hand for `odx merge` ([orchestrator.md](orchestrator.md#build-the-sessions)).

## Option A: everyone on the same LAN

Simplest. Use the server's LAN address (e.g. `192.168.1.20`): players connect CS2 to `192.168.1.20:27015` and the package
points at `http://192.168.1.20:5180`. Only the Windows firewall needs to let the traffic in (below).

## Option B: players over the internet, port forwarding

On the server's router, forward to the server machine's LAN address:

- UDP 27015 (CS2 server)
- TCP 5180, UDP 12300 (orchestrator)

Use the router's public address (or a dynamic DNS name) in CS2 (`connect <public address>:27015`) and in the package
(`http://<public address>:5180`). Start the orchestrator with `--access-key` and build the package with the same key:
otherwise anyone who finds the ports can open rooms.

Players behind the same router as the server use the LAN address instead (many routers do not loop the public address
back).

## Option C: a VPN (ZeroTier or Tailscale)

When the router cannot forward ports: put the server and both players in one ZeroTier network (or Tailscale tailnet),
created by hand in its web console, and use the server's VPN address everywhere (`connect 10.x.x.x:27015`,
`http://10.x.x.x:5180`). The "LAN (ZeroTier)" option of a room creates a network for the room's members only, without
the CS2 server, so it does not help here.

Latency matters: the gate needs a median RTT to the time service of at most 100 ms (`sync.clock`).

## Windows firewall on the server machines

The first start of `cs2.exe` and `odx-orchestrator.exe` asks whether to allow them; allow private (and
public, for option B) networks. Or add the rules up front, as administrator, on the machine that runs each:

```powershell
netsh advfirewall firewall add rule name="CS2 server" dir=in action=allow protocol=UDP localport=27015
netsh advfirewall firewall add rule name="Odx orchestrator" dir=in action=allow protocol=TCP localport=5180
netsh advfirewall firewall add rule name="Odx time service" dir=in action=allow protocol=UDP localport=12300
```

## Check from a player's PC

```powershell
curl.exe http://<server>:5180/status -H "Authorization: Bearer <access key>"   # answers JSON
```

In CS2's console, `connect <server>:27015` must join the server. The time service is checked by the app itself: after
joining a room the round's `player.json` shows `ping_ms` and `clock_sync`.
