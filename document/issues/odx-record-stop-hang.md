# odx record does not stop: END after ~1 min 4 s, capture failed (issue)

> Status: `fixed` (hang: deadlock at the first video frame, §6) · stutter not reproduced as a recorder fault ·
> Written 2026-10-27 · Reproduced and fixed on Windows 2026-10-27

## 1. Symptom

Reported by testers on 2026-10-26 and again on 2026-10-27: F10 starts recording, F10 again stops it, but odx-app shows END
only ~1 min 4 s later, then reports that the capture failed. The video that comes out also stutters (2026-10-27).

The 2026-10-26 report led to `fc1dcae` (end the video input before the audio one). The 2026-10-27 report is on a build
that has that fix, so the stop still hangs somewhere else.

## 2. What is certain

- 1 min 4 s = `CaptureRun.StopTimeout` (60 s, `capture/dotnet/src/Odx.Capture/CaptureRun.cs`) + a few seconds of
  teardown. After the stop event, `odx record` or `odx input` did not exit; CaptureRun killed it and added
  `a capture tool did not stop in time and was killed` to the tool errors.
- A killed `odx record` never writes `recording.json`, so the session is not built (`captureOk` is false).

## 3. Suspected causes

1. **Audio pipe (`--audio`).** `AudioLoopback.Run` writes PCM into the named pipe while holding `_gate`. If ffmpeg stops
   reading the audio input, the thread blocks in `_pipe.Write` and `AudioLoopback.Finish` waits forever in
   `_thread.Join()`. `fc1dcae` closed one path to this (audio padding written before video EOF); another may remain.
2. **Encoder slower than real time.** With x264 (no NVENC) on a CPU also running the game, `encoder.Write` blocks in
   `FrameArrived` while holding `gate`. Each call then emits more grid ticks than the last, so the backlog grows. At stop
   the main thread first waits for `gate`, then `resampler.Finish` writes the rest. A ~1 min recording can take longer
   than 60 s to drain. Not audio related. Fits the stutter too: while `FrameArrived` is blocked, WGC (2-buffer frame
   pool) drops frames and the resampler repeats the last one, so the video jumps.
3. **ffmpeg waiting for audio.** Audio is written only up to `now - LagMs` (100 ms) and the pipe has no buffer, so
   ffmpeg may hold back reading video until audio catches up; `FrameArrived` blocks in the meantime, with the same frame
   drops. Would explain stutter that appears only with audio on.
4. `odx input` hanging instead: not ruled out.

## 4. Windows test (to do)

On a Windows PC with the current package, the game running, ~1 min of play per recording. Note the result of each step
in the table below.

1. **Machine:** CPU, GPU, Windows build; odx-app's idle status (NVENC or not).
2. **odx-app, audio on:** F10, play ~1 min, F10. Time from F10 to END; copy the full status text after END (lines
   `odx record:` / `odx input:`); does the video stutter?
3. **odx-app, audio off:** same as 2.
4. **For each session folder** (2 and 3): `recording.json` if there is one (`encoder`, `frames`, `duplicate_frames`,
   `dropped_source_frames`, `ffmpeg_exit_code`, `error`) and the duplicate count in `raw\video_frames.csv`:

   ```powershell
   $rows = Import-Csv raw\video_frames.csv; "$(($rows | ? duplicate -eq 1).Count) / $($rows.Count) duplicated"
   ```

   Long runs of `duplicate = 1` rows = the recorder was blocked, not the game.
5. **Command line, from the package folder** (game running, stop each with Ctrl+C after ~30 s; time from Ctrl+C to the
   `stopped:` line):

   ```
   odx record --out C:\tmp\a --monitor --fps 30 --audio
   odx record --out C:\tmp\b --monitor --fps 30
   odx record --out C:\tmp\c --monitor --fps 30 --encoder x264
   ```

   If the PC has NVIDIA, also `--encoder nvenc` without audio, to compare with x264.
6. Task Manager during 2: CPU % of `ffmpeg.exe` and of the game.

| Step | Audio | Encoder | F10 → END | Stutter | Duplicated / frames | Errors |
|---|---|---|---|---|---|---|
| 2 | on | | | | | |
| 3 | off | | | | | |
| 5a | on | | | | | |
| 5b | off | | | | | |
| 5c | off | x264 | | | | |

**Reading the result:**

- Audio off is smooth and stops at once, audio on hangs/stutters → cause 1 or 3 (audio path).
- Both hang/stutter, worse with x264, ffmpeg near 100 % CPU → cause 2 (encoder too slow).
- Command line stops fast but odx-app does not, and the errors name `odx input` → cause 4.

## 5. Next

Pick the fix once §4 says which cause it is. Whatever the cause, the stop must not depend on ffmpeg draining an unbounded
backlog or reading a pipe forever.

## 6. Result (Windows, 2026-10-27)

Machine: i7-8750H, GTX 1060 (driver 496.13), Windows build 26200, 1920x1080, ffmpeg 9.0.2, 30 fps. The gyan.dev ffmpeg on
PATH needs NVIDIA driver 610+ for NVENC, so every run used x264 (the packaged build needs 471+).

**Hang: a deadlock, a variant of cause 1.** Reproduced with `odx record --window ArmaReforgerSteam --audio` started
right after the game loaded (1 of 1, then 1 of 1 again); the stop never came and the process had to be killed. Stack
(`dotnet-stack`) of the hung process:

- capture thread (`FrameArrived`, holding the video `gate`): `Emit` → `AudioLoopback.SetOrigin` (holding `_gate`) →
  `Place` → `PipeStream.Write`, blocked;
- audio thread: `AudioLoopback.Run`, waiting for `_gate`;
- main thread (stop): waiting for `gate`.

At the first grid tick `SetOrigin` wrote the audio held since `audio.Start()` into the pipe before the first video frame
was written. ffmpeg had already opened the audio pipe but reads no more audio until the first video frame is in, so the
write never completed. It happens only when ffmpeg connects before that tick and more audio is held than ffmpeg's probe
reads, hence intermittent, and likelier on a game that has just started.

Fix: `SetOrigin` only sets the origin; the audio thread writes the held packets (`PlaceEarly`), so no pipe write happens
while the video lock is held. After the fix: 4 of 4 runs on a freshly started game stopped in ~1 s; each video.mp4 has
its AAC track (30.000 s video, 29.99–30.005 s audio, game music audible).

**Other runs, none hung (stop 1–5.5 s after the end of 60 s):** monitor and window, audio on (process and device) and
off, x264, with an ffplay 1080p60 "game" and with Arma Reforger.

**Stutter:** with timing probes on `encoder.Write` (> 200 ms) and on gaps between captured frames, no write was ever
blocked. Duplicate runs lined up with gaps in the frames Windows delivered (the callback was not late), i.e. the source
did not present new frames (Reforger's menu right after loading, an idle desktop). One early ffplay run had 10 s of
duplicates without audio and could not be repeated. Not a recorder fault on this machine; if testers still see stutter
with this fix, step 4 of §4 on their machine is the next check.
