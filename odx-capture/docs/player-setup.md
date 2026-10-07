# Player PCs

Each player needs: Windows 10 2004+ or 11 (x64), CS2 installed and signed in to **their own** Steam account, and the
Odx Capture package built for the orchestrator's address. An NVIDIA GPU is used for encoding when there is one
(NVENC); otherwise x264 on the CPU.

## 1. Build the package

On the build machine (MSYS2 and the MSVC build tools, see `capture-cs/publish.ps1`), from `odx-capture/capture-cs/`:

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1 -Version 0.1.0 -Orchestrator http://<server>:5180 -AccessKey <access key>
```

`<server>` is the address players use ([network.md](network.md)). The result is
`dist\odx-capture-0.1.0-win-x64.zip` (about 220 MB); `-Orchestrator` and `-AccessKey` are written into its
`bin\odx-capture.json`, so players only type invite codes. To change them later, edit that file:

```json
{
  "time_server": "time.windows.com",
  "orchestrator": "http://<server>:5180",
  "access_key": "<access key>"
}
```

## 2. Install on each player's PC

1. Unzip the package anywhere (e.g. `D:\Odx`); nothing else to install.
2. Start `Odx Capture.exe`. The first start takes a while (Windows Defender scans the new files) and the app checks the
   video encoder once.
3. Recordings are kept in the package's `data\captures\` (a room's rounds in `data\captures\<session>\`; plan about
   2 MB per second of recording). Nothing is uploaded: after the test the rounds are copied together and the sessions to
   hand over are built by `odx merge` ([orchestrator.md](orchestrator.md#build-the-sessions)).

## 3. CS2

1. Steam → CS2 → Properties → Launch Options: `-insecure`. While it is set, this CS2 cannot join VAC-secured servers;
   remove it after the test.
2. Sign in to the player's own Steam account: two players on one account cannot be told apart in the server's data.
3. Start CS2, open the console (enable it in Settings → Game → Enable Developer Console) and `connect <server>:27015`.
4. Display: any resolution of at least 1280x720; the session gate needs at least 30 fps in the recording.

## 4. Packet capture

The CS2 profile records each player's game traffic (pktmon, built into Windows). It needs administrator rights, so the
app asks once per room with a UAC prompt. Declining is fine: that player's `packets.*` stay empty, the rest is recorded.
CS2 encrypts its traffic, so these files only hold sizes and timings; the decoded messages come from the server.

## 5. In the app

- **Create room** (the host): shows a 6-character invite code to send to the other player. Leave "LAN (ZeroTier)"
  unticked for CS2.
- **Join room** (the other player): type the invite code.
- The hotkey (F10 by default) or the main button:
  - the host starts the round once everyone is ready (3 s countdown on every screen) and ends it;
  - the other player toggles ready.
- After the round, a notice in the top right corner of the screen says where the round was kept ("✓ Round …: recorded,
  kept in …") or that its capture failed. The notices and the countdown never show in the video.
- A round cancelled during its countdown keeps its folder as `<session>.cancelled-<yyyyMMdd-HHmmss>`; the next round
  uses the same session id.
