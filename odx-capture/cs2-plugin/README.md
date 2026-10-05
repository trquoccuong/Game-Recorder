# odx_netlog (CS2 server plugin)

CS2 encrypts its traffic (GameNetworkingSockets, AES-GCM), so packet capture on the players' machines only yields sizes.
This Metamod:Source 2.0 plugin runs on our own dedicated server and logs every net message the server exchanges with
each client, decoded by the engine itself.

- Outgoing: `INetChannel::SendNetMessage` (slot 39 of the `CNetChan` vtable, found through its MSVC RTTI in
  `networksystem.dll`). Entity snapshots, signon data and string tables do not go through it and are not logged yet.
- Incoming: an `INetworkMessageProcessingPreFilter` installed on each channel (`InstallMessageFilter`, slot 73).
- Output: `<game>/csgo/odx-netlog/<UTC start>.jsonl`, one object per message: `utc_us` (server Windows clock),
  `direction`, `slot`, `steamid`, `address`, `message`, `id`, `buf`, `text` (protobuf text format from the engine's
  binding; bytes fields such as `CCLCMsg_Move.data` stay serialized).

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
4. `game/bin/win64/cs2.exe -dedicated -insecure -port 27015 +map de_dust2`; the console prints
   `[odx] logging net messages to …`.
