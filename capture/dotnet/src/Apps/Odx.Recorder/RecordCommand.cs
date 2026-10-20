// odx record --out <dir> (--window <process name> | --monitor) [--fps 60] [--encoder auto|x264|nvenc] [--ffmpeg <path>]
//            [--cursor] [--audio] [--seconds N] [--stop-event <name>] [--json-status]
//
// Records a game window (or the primary monitor) with Windows.Graphics.Capture. Every captured frame carries its QPC
// capture time (SystemRelativeTime), the same clock as odx input. Frames are resampled onto a constant-rate grid
// (GridResampler) and piped to ffmpeg, so the MP4 is CFR and its frame N is row N of video_frames.csv. Writes:
//   <dir>/video.mp4         CFR H.264, no B-frames
//   <dir>/video_frames.csv  frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate
//   <dir>/recording.json    capture id, capture and screen size, fps, encoder, start time (UTC + QPC),
//                           counts of duplicated/dropped frames, audio (null without --audio)
//   <dir>/audio.m4a         --audio: AAC of what the default playback device plays, sample 0 at video frame 0
// Stop with Ctrl+C, --seconds, --stop-event (a named event set by Odx.App), or by closing the captured window.
// --json-status: stdout carries only RecorderStatus JSON lines (started, progress every second, stopped).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Odx.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Odx.Recorder;

public static class RecordCommand
{
    public static int Run(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts is null)
        {
            Console.Error.WriteLine("usage: odx record --out <dir> (--window <process name> | --monitor) [--fps 60] " +
                                    "[--encoder auto|x264|nvenc] [--ffmpeg <path>] [--cursor] [--audio] [--seconds N] [--stop-event <name>] [--json-status]");
            return 2;
        }
        if (!GraphicsCaptureSession.IsSupported())
        {
            Console.Error.WriteLine("Windows.Graphics.Capture is not supported on this system");
            return 1;
        }

        var encoderName = Task.Run(() => EncoderChoice.Resolve(opts.Ffmpeg, opts.Encoder));
        CaptureInterop.UsePhysicalPixels();
        GraphicsCaptureItem item;
        IntPtr monitor, window = IntPtr.Zero;
        if (opts.Window is string processName)
        {
            var proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName))
                .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (proc is null)
            {
                Console.Error.WriteLine($"no window found for process '{processName}'");
                return 1;
            }
            window = proc.MainWindowHandle;
            item = CaptureInterop.ItemForWindow(window);
            monitor = CaptureInterop.MonitorOf(window);
        }
        else
        {
            item = CaptureInterop.ItemForPrimaryMonitor();
            monitor = CaptureInterop.PrimaryMonitor();
        }

        int frameWidth = item.Size.Width, frameHeight = item.Size.Height;
        int cropX = 0, cropY = 0, width = frameWidth, height = frameHeight;
        var (screenWidth, screenHeight) = CaptureInterop.MonitorSize(monitor);
        Directory.CreateDirectory(opts.OutDir);

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out ID3D11Device? device)
            .CheckError();
        using var d3d = device!;
        var context = d3d.ImmediateContext;
        var winRtDevice = CaptureInterop.WinRtDevice(d3d);
        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = opts.Cursor;
        try { session.IsBorderRequired = false; } catch (Exception) { /* not available before Windows 11 */ }

        FfmpegEncoder? encoder = null;
        using var frameLog = new VideoFrameLogWriter(new StreamWriter(Path.Combine(opts.OutDir, "video_frames.csv")));
        var resampler = new GridResampler(opts.Fps);

        var gate = new object();
        var stop = new ManualResetEventSlim();
        string? error = null;
        bool stopped = false;
        byte[] shown = [];   // frame currently on screen (last captured)
        byte[] incoming = [];
        ID3D11Texture2D? staging = null;

        // The video size is fixed by the first frame, not item.Size: a windowed game's item can be larger than its frames
        // (1296x759 item, 1282x752 frames, seen on Arma Reforger), so the encoder starts once the first frame is in.
        // A window is cropped to its client area: the video is the game's picture, without title bar and borders.
        FfmpegEncoder StartEncoder()
        {
            (cropX, cropY, width, height) = window == IntPtr.Zero
                ? (0, 0, frameWidth, frameHeight)
                : CaptureInterop.ClientArea(window, frameWidth, frameHeight);
            shown = new byte[width * height * 4];
            incoming = new byte[width * height * 4];
            return new FfmpegEncoder(opts.Ffmpeg, Path.Combine(opts.OutDir, "video.mp4"), width, height, opts.Fps,
                encoderName.Result);
        }
        double startQpcMs = QpcClock.NowMs();
        var startUtc = DateTimeOffset.UtcNow;
        double? firstFrameLatencyMs = null;
        long framesWritten = 0;
        AudioLoopback? audio = null;
        string? audioError = null;
        if (opts.Audio)
        {
            try
            {
                audio = new AudioLoopback(opts.Ffmpeg, Path.Combine(opts.OutDir, "audio.m4a"));
                audio.Start();
            }
            catch (Exception e)
            {
                audio?.Dispose();
                audio = null;
                audioError = e.Message;
                Console.Error.WriteLine($"audio not recorded: {e.Message}");
            }
        }

        void Emit(IReadOnlyList<GridTick> ticks)
        {
            foreach (var t in ticks)
            {
                if (framesWritten == 0) audio?.SetOrigin(t.TGridMs);
                encoder!.Write(shown);
                frameLog.Write(t);
                framesWritten++;
            }
        }

        framePool.FrameArrived += (pool, _) =>
        {
            lock (gate)
            {
                using var frame = pool.TryGetNextFrame();
                if (stopped || frame is null) return;
                if (encoder is null && (frame.ContentSize.Width != frameWidth || frame.ContentSize.Height != frameHeight))
                {
                    (frameWidth, frameHeight) = (frame.ContentSize.Width, frame.ContentSize.Height);
                    framePool.Recreate(winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
                    return;
                }
                encoder ??= StartEncoder();
                if (frame.ContentSize.Width != frameWidth || frame.ContentSize.Height != frameHeight)
                {
                    error = $"capture size changed to {frame.ContentSize.Width}x{frame.ContentSize.Height}; restart recording";
                    stop.Set();
                    return;
                }
                double t = frame.SystemRelativeTime.TotalMilliseconds;  // QPC-based, same clock as QpcClock.NowMs()
                firstFrameLatencyMs ??= QpcClock.NowMs() - t;

                using (var texture = CaptureInterop.Texture(frame.Surface))
                {
                    if (staging is null)
                    {
                        var desc = texture.Description;
                        desc.Usage = ResourceUsage.Staging;
                        desc.BindFlags = BindFlags.None;
                        desc.CPUAccessFlags = CpuAccessFlags.Read;
                        desc.MiscFlags = ResourceOptionFlags.None;
                        staging = d3d.CreateTexture2D(desc);
                    }
                    context.CopyResource(staging, texture);
                }
                var mapped = context.Map(staging, 0, MapMode.Read);
                try
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(mapped.DataPointer + (nint)((cropY + y) * (long)mapped.RowPitch + cropX * 4), incoming,
                            y * width * 4, width * 4);
                }
                finally
                {
                    context.Unmap(staging, 0);
                }

                try
                {
                    Emit(resampler.Advance(t));  // ticks before t still show the previous frame
                }
                catch (IOException e)
                {
                    error = $"ffmpeg pipe closed: {e.Message}";
                    stopped = true;
                    stop.Set();
                    return;
                }
                (shown, incoming) = (incoming, shown);
            }
        };
        item.Closed += (_, _) => stop.Set();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        if (opts.StopEvent is string stopEventName)
        {
            var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            _ = Task.Run(() => { stopEvent.WaitOne(); stop.Set(); });
        }

        session.StartCapture();
        if (opts.JsonStatus)
            Console.WriteLine(new RecorderStatus(RecorderStatus.Started, Width: width, Height: height, Fps: opts.Fps).ToJson());
        else
            Console.WriteLine($"recording {width}x{height} @ {opts.Fps} fps to {opts.OutDir} (Ctrl+C to stop)");
        using (var progress = opts.JsonStatus ? new Timer(_ =>
               {
                   RecorderStatus s;
                   lock (gate) s = new(RecorderStatus.Progress, framesWritten, resampler.Duplicates, resampler.DroppedSourceFrames);
                   Console.WriteLine(s.ToJson());
               }, null, 1000, 1000) : null)
        {
            if (opts.Seconds is double seconds) stop.Wait(TimeSpan.FromSeconds(seconds)); else stop.Wait();
        }

        double stopQpcMs = QpcClock.NowMs();
        lock (gate)
        {
            encoder ??= StartEncoder();
            if (!stopped)
            {
                stopped = true;
                try { Emit(resampler.Finish(stopQpcMs)); } catch (IOException e) { error ??= $"ffmpeg pipe closed: {e.Message}"; }
            }
            frameLog.Flush();
        }
        session.Dispose();
        framePool.Dispose();
        int ffmpegExit = encoder.Finish();
        encoder.Dispose();
        object? audioMeta = audioError is null ? null : new Dictionary<string, object?> { ["error"] = audioError };
        if (audio is not null)
        {
            int audioExit = audio.Finish(stopQpcMs);
            audioError = audio.Error;
            if (audioExit != 0) Console.Error.WriteLine($"audio ffmpeg exited with code {audioExit} {audioError}".Trim());
            audioMeta = new Dictionary<string, object?>
            {
                ["file"] = "audio.m4a",
                ["device"] = audio.Device,
                ["sample_rate"] = audio.SampleRate,
                ["channels"] = audio.Channels,
                ["start_qpc_ms"] = audio.OriginMs,
                ["ffmpeg_exit_code"] = audioExit,
                ["error"] = audioError,
            };
            audio.Dispose();
        }
        staging?.Dispose();

        var meta = new Dictionary<string, object?>
        {
            ["capture_id"] = Guid.NewGuid().ToString("N"),
            ["tool_version"] = BuildInfo.Version,
            ["width"] = width,
            ["height"] = height,
            ["screen_width"] = screenWidth,
            ["screen_height"] = screenHeight,
            ["fps"] = opts.Fps,
            ["encoder"] = encoderName.Result,
            ["source"] = opts.Window is null ? "monitor:primary" : $"window:{opts.Window}",
            ["cursor"] = opts.Cursor,
            ["started_utc"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["started_qpc_ms"] = startQpcMs,
            ["stopped_qpc_ms"] = stopQpcMs,
            ["frames"] = framesWritten,
            ["duplicate_frames"] = resampler.Duplicates,
            ["dropped_source_frames"] = resampler.DroppedSourceFrames,
            ["first_frame_capture_latency_ms"] = firstFrameLatencyMs,
            ["ffmpeg_exit_code"] = ffmpegExit,
            ["error"] = error,
            ["audio"] = audioMeta,
        };
        File.WriteAllText(Path.Combine(opts.OutDir, "recording.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        if (error is null && ffmpegExit != 0) error = $"ffmpeg exited with code {ffmpegExit}";
        if (opts.JsonStatus)
        {
            Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, framesWritten, resampler.Duplicates,
                resampler.DroppedSourceFrames, width, height, opts.Fps, error).ToJson());
        }
        else
        {
            Console.WriteLine($"stopped: {meta["frames"]} frames, {resampler.Duplicates} duplicated, " +
                              $"{resampler.DroppedSourceFrames} dropped, ffmpeg exit {ffmpegExit}");
        }
        if (error is not null) Console.Error.WriteLine(error);
        return error is null && ffmpegExit == 0 ? 0 : 1;
    }
}

internal sealed record Options(string OutDir, string? Window, double Fps, string Encoder, string Ffmpeg, bool Cursor,
    bool Audio, double? Seconds, string? StopEvent, bool JsonStatus)
{
    public static Options? Parse(string[] args)
    {
        string? outDir = null, window = null, encoder = "auto", ffmpeg = ExternalTools.Ffmpeg(), stopEvent = null;
        bool monitor = false, cursor = false, audio = false, jsonStatus = false;
        double fps = 60;
        double? seconds = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--out": outDir = Next(); break;
                case "--window": window = Next(); break;
                case "--monitor": monitor = true; break;
                case "--fps" when double.TryParse(Next(), System.Globalization.NumberStyles.Float, inv, out var f): fps = f; break;
                case "--encoder": encoder = Next(); break;
                case "--ffmpeg": ffmpeg = Next(); break;
                case "--cursor": cursor = true; break;
                case "--audio": audio = true; break;
                case "--seconds" when double.TryParse(Next(), System.Globalization.NumberStyles.Float, inv, out var s): seconds = s; break;
                case "--stop-event": stopEvent = Next(); break;
                case "--json-status": jsonStatus = true; break;
                default: return null;
            }
        }
        if (outDir is null || encoder is null || ffmpeg is null || (window is null) == !monitor || fps <= 0) return null;
        return new Options(outDir, window, fps, encoder, ffmpeg, cursor, audio, seconds, stopEvent, jsonStatus);
    }
}
