# odx_netlog (CS2 server plugin)

CS2 encrypts its traffic (GameNetworkingSockets, AES-GCM), so packet capture on the players' machines only yields sizes.
This Metamod:Source 2.0 plugin runs on our own dedicated server and logs every net message the server exchanges with
each client, decoded by the engine itself.

- Outgoing: `INetChannel::SendNetMessage` (slot 39 of the `CNetChan` vtable, found through its MSVC RTTI in
  `networksystem.dll`). Entity snapshots, signon data and string tables do not go through it and are not logged; the
  world state comes from the server's SourceTV demo instead (odx-capture README, "CS2 game server").
- Incoming: an `INetworkMessageProcessingPreFilter` installed on each channel (`InstallMessageFilter`, slot 73).
- Already serialized buffers: `SendData` (slot 40), the signon data each joining client gets (server info, string tables,
  the game event list…), saved as bytes (`data`, base64, `bits` long) to `odx-netlog/senddata/<UTC start>.jsonl`; only
  the buffer is read, nothing is called on the engine.
- Output: `<game>/csgo/odx-netlog/<UTC start>.jsonl`, one object per message: `utc_us` (server Windows clock),
  `tick` (the server tick it was logged in; it puts the demo's ticks on the clock), `direction`, `slot`, `steamid`,
  `address`, `message`, `id`, `buf`, `text` (protobuf text format from the engine's binding; bytes fields such as
  `CCLCMsg_Move.data` stay serialized). `Odx.Games.Cs2` `Cs2ServerCapture` reads it; `tests/fixtures/cs2_netmessages.jsonl`
  is a sample (from before `tick` was added).

## Build (Windows, MSVC)

Needs hl2sdk (branch `cs2`) and the Metamod:Source source tree (headers only):

```bash
cmake -B build -DHL2SDK_CS2=D:/odx-dev/hl2sdk-cs2 -DMMSOURCE=D:/odx-dev/metamod-source
cmake --build build --config Release
```

## Install on a dedicated server

Never on a player's CS2 install: the server runs from its own directory with `-insecure`.

1. Metamod:Source 2.0 in `game/csgo/addons/metamod`, and `Game csgo/addons/metamod` added above `Game csgo` in that
   server's `game/csgo/gameinfo.gi`.
2. `odx_netlog.dll` → `game/csgo/addons/odx/bin/win64/`.
3. `game/csgo/addons/metamod/odx_netlog.vdf`:
   ```
   "Metamod Plugin"
   {
   	"alias"	"odx"
   	"file"	"addons/odx/bin/win64/odx_netlog"
   }
   ```
4. `game/bin/win64/cs2.exe -dedicated -insecure -usercon -port 27015 +rcon_password <password> +map de_dust2`; the
   console prints `[odx] logging net messages to … (server tick N)`. A new build can be swapped in while the server
   runs: `meta unload <id>`, copy the DLL, `meta load addons/odx/bin/win64/odx_netlog` (RCON or console).
