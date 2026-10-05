// odx input --out <dir> [--pid <game_pid>] [--stop-event <name>] [--ntp <server>]
//
// Captures keyboard/mouse with the Raw Input API in its own process (RIDEV_INPUTSINK: keeps receiving while the game
// has focus) and never touches the game. Writes, QPC-stamped:
//   <dir>/input_events.csv   raw events (t_qpc_ms,kind,value,dx,dy)
//   <dir>/clock_samples.csv  QPC <-> UTC once per second, to align with server events / demo ticks
// --pid: stop when that process exits. --stop-event: stop when that named event is set (Odx.App).
// --ntp: also measure QPC -> UTC against that NTP server at start and every 15 s (<dir>/ntp_samples.csv); the
// Windows clock in clock_samples.csv is only as good as its own time sync. NTP failures are reported, not fatal.
// Without --ntp, the time_server of odx-capture.json next to odx is used, if set.
// Stop with Ctrl+C.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Odx.Core;
using static Odx.Input.Win32;

[assembly: SupportedOSPlatform("windows")]

namespace Odx.Input;

public static class InputCommand
{
    public static int Run(string[] args)
    {
        (string OutDir, int? Pid, string? StopEvent, string? Ntp)? opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (opts is null)
        {
            Console.Error.WriteLine("usage: odx input --out <dir> [--pid <game_pid>] [--stop-event <name>] [--ntp <server>]");
            return 2;
        }

        Directory.CreateDirectory(opts.Value.OutDir);
        using var events = new InputLogWriter(new StreamWriter(Path.Combine(opts.Value.OutDir, "input_events.csv")));
        using var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(opts.Value.OutDir, "clock_samples.csv")));

        // Message-only window using the built-in STATIC class; WM_INPUT is handled in the message loop below.
        IntPtr hwnd = CreateWindowExW(0, "STATIC", "odx-input", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var devices = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = hwnd },  // mouse
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = hwnd },  // keyboard
        };
        if (hwnd == IntPtr.Zero || !RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            Console.Error.WriteLine($"raw input registration failed: {Marshal.GetLastWin32Error()}");
            return 1;
        }

        uint loopThread = GetCurrentThreadId();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; PostThreadMessageW(loopThread, WM_QUIT, UIntPtr.Zero, IntPtr.Zero); };
        if (opts.Value.Pid is int pid)
        {
            var game = System.Diagnostics.Process.GetProcessById(pid);
            _ = Task.Run(() => { game.WaitForExit(); PostThreadMessageW(loopThread, WM_QUIT, UIntPtr.Zero, IntPtr.Zero); });
        }
        if (opts.Value.StopEvent is string stopEventName)
        {
            var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            _ = Task.Run(() => { stopEvent.WaitOne(); PostThreadMessageW(loopThread, WM_QUIT, UIntPtr.Zero, IntPtr.Zero); });
        }

        using var ntpStop = new CancellationTokenSource();
        var ntpLoop = opts.Value.Ntp is string ntpServer
            ? Task.Run(() => MeasureNtp(ntpServer, Path.Combine(opts.Value.OutDir, "ntp_samples.csv"), ntpStop.Token))
            : Task.CompletedTask;

        SetTimer(hwnd, 1, 1000, IntPtr.Zero);
        WriteClockSample();
        Console.WriteLine($"recording input to {opts.Value.OutDir} (Ctrl+C to stop)");

        var buffer = new byte[256];
        int headerSize = RawInputParser.HeaderSize(IntPtr.Size);
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WM_INPUT)
            {
                double t = QpcClock.NowMs();
                uint size = 0;
                GetRawInputData(msg.LParam, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
                if (size > buffer.Length) buffer = new byte[size];
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    if (GetRawInputData(msg.LParam, RID_INPUT, handle.AddrOfPinnedObject(), ref size, (uint)headerSize) == size)
                        foreach (var e in RawInputParser.Parse(buffer.AsSpan(0, (int)size), IntPtr.Size, t))
                            events.Write(e);
                }
                finally
                {
                    handle.Free();
                }
            }
            else if (msg.Message == WM_TIMER)
            {
                WriteClockSample();
            }
            DispatchMessageW(ref msg);  // lets DefWindowProc clean up WM_INPUT
        }

        events.Flush();
        clock.Flush();
        Console.WriteLine("stopped");
        ntpStop.Cancel();
        ntpLoop.Wait();
        return 0;

        void WriteClockSample()
        {
            clock.Write(QpcClock.NowMs(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            clock.Flush();
            events.Flush();
        }

        static async Task MeasureNtp(string server, string path, CancellationToken stop)
        {
            using var log = new NtpSampleWriter(new StreamWriter(path));
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    log.Write(SntpClient.Query(server, TimeSpan.FromSeconds(2)));
                    log.Flush();
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
                {
                    Console.Error.WriteLine($"NTP {server}: {e.Message}");
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        static (string OutDir, int? Pid, string? StopEvent, string? Ntp)? ParseArgs(string[] args)
        {
            string? outDir = null;
            int? pid = null;
            string? stopEvent = null, ntp = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                    case "--pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): pid = p; i++; break;
                    case "--stop-event" when i + 1 < args.Length: stopEvent = args[++i]; break;
                    case "--ntp" when i + 1 < args.Length: ntp = args[++i]; break;
                    default: return null;
                }
            }
            if (outDir is null) return null;
            return (outDir, pid, stopEvent, ntp ?? CaptureConfig.Load().TimeServer);
        }
    }
}
