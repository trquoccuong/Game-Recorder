# odx record does not stop: END after ~1 min 4 s, capture failed (issue)

> Status: `investigating` (cause not confirmed, no code change yet) ·
> Written 2026-10-27 · Code read on macOS only, not reproduced on Windows

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
