# A two-player test round

Before: the server, its agent and the orchestrator run ([cs2-server.md](cs2-server.md),
[orchestrator.md](orchestrator.md)), both
players have the package and CS2 with `-insecure` ([player-setup.md](player-setup.md)).

## Steps

1. Server console (or RCON): `mp_warmup_end`, and bots if wanted (`bot_add_t`, `bot_add_ct`). Check that SourceTV
   records: `tv_record replays/check`, a file `replays\check.dem` appears within a second or two, then `tv_stoprecord`.
2. Both players: CS2 → `connect <server>:27015`, each on a team.
3. Player A: Odx Capture → **Create room**, accept the packet capture prompt, send the invite code to player B.
4. Player B: **Join room** → the code, accept the prompt, press F10 (ready ✓).
5. Player A: press F10. A 3 s countdown shows on both screens; both start recording.
6. Play at least 30 s (the gate needs 10 s). To exercise the whole pipeline: kills, damage, a death (the frames after it
   get no camera), the bomb.
7. Player A: press F10 to end the round. Both see "✓ Round …: recorded, kept in …" in the top right corner; the
   orchestrator logs `round <session>: 2/2 players kept their recording`.
8. Copy the round into one folder ([orchestrator.md](orchestrator.md#build-the-sessions)): each player's
   `data\captures\<session>\` to `<test>\<session>\players\<pid>\`, the game server agent's `<work>\<session>\server\` to
   `<test>\<session>\server\`, and `round.json`, `server_events.jsonl`, `game_profile.json` from any of them to
   `<test>\<session>\`.
9. `odx merge <test>` prints `PASS  <session>  2/2 players valid, 2 required -> <folder>` (or FAIL with the issues).

## What to check in the session

```
data\delivery\odx\<mm-dd-yyyy>\counter-strike-2\<session>\        (on the machine that ran odx merge)
  session.json  validation.json  schema.bin.gz
  server\  server_state.bin  events.jsonl  packets.bin  packets.txt  capture\(demo.dem, netmessages.jsonl, senddata.jsonl)
  players\<player-a>\  player.json  video.mp4  frames.csv  frames.bin  events.jsonl  packets.bin  packets.txt  capture\
  players\<player-b>\  …
```

| Check | Where | Expect |
|---|---|---|
| Gate | `validation.json` | `pass: true`, `valid_players` 2, no `server.capture` warning |
| Both players linked to the server | `session.json` → `players[].steam_id` | two different ids |
| Server recorded | `session.json` → `server` | `server_state.records` > 0, `game_event_names` set, no `problems` |
| Clock | `players\*\player.json` → `ping_ms`, `qa.checks` `sync.clock` | pass (median RTT ≤ 100 ms) |
| Server clock | `session.json` → `server.clock_sync` | `sample_count` > 0, `rtt_ms` low (the agent's samples of the CS2 machine's clock) |
| Camera per frame | `player.json` → `frame_stats.frames_with_camera`, check `floor.camera` | ≥ 90 % of frames; `sync.input_camera` pass (correlation ≥ 0.8) |
| Deaths | `player.json` → `frame_cleanliness.not_first_person_frame_ranges` | the frames from a death to the round's end |
| Events | `server\events.jsonl`, `players\*\events.jsonl` | `player_death` with `attacker` / `userid` naming the players by `player_id`, each with `video_time_ms` and `frame_index` |
| Server packets | `server\packets.txt` | the `peer` column names both players |

`server_state.bin`, `frames.bin` and `packets.bin` are length-delimited protobuf (`odx.capture.v1`, schema in
`schema.bin.gz`); `session.json` → `decoding` says how to read them.
