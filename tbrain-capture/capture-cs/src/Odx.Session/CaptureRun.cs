using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Odx.Core;

namespace Odx.Session;

/// <param name="WindowProcess">Process name passed to odx record --window; null records the primary monitor.</param>
/// <param name="GameProfile">Game profile JSON used to build the session after recording; null skips the build.</param>
/// <param name="NtpServer">Passed to odx input --ntp; null or empty measures nothing (Windows clock only).</param>
public sealed record CaptureOptions(string OutputRoot, string? WindowProcess, int Fps, string? GameProfile, string? NtpServer);

/// <param name="Recording">odx record's final status (frame counts); null if it never reported one.</param>
/// <param name="ToolErrors">stderr lines of the capture tools (warnings such as an unreachable NTP server included).</param>
/// <param name="Build">Null when no game profile was selected.</param>
public sealed record RunResult(string SessionDir, bool CaptureOk, RecorderStatus? Recording, int InputEvents,
    IReadOnlyList<string> ToolErrors, BuildResult? Build)
{
    public bool Ok => CaptureOk && (Build is null || (Build.Error is null && Build.Issues.All(i => i.Severity != Issue.ErrorSeverity)));
}

/// <param name="Error">Why the session could not be built; null when it was built and validated.</param>
public sealed record BuildResult(BuildReport? Report, IReadOnlyList<Issue> Issues, string? Error);

/// <summary>
/// One recording: `odx input` and `odx record` run as child processes into the same session folder, both stopped through
/// a shared named event. If either child exits on its own (error, captured window closed), the other is stopped too.
/// After a clean stop the folder is built into a spec v2 session with the chosen game profile and validated.
/// </summary>
public sealed class CaptureRun
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);  // ffmpeg may still be draining frames

    private readonly EventWaitHandle _stop;
    private readonly List<string> _stderr = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _gameProfile;
    private RecorderStatus? _lastStatus;

    public string SessionDir { get; }
    /// <summary>Completes when odx record reports that capture is running; it can take seconds on a cold start.</summary>
    public Task Started => _started.Task;
    /// <summary>Latest odx record status (frame counters update every second); null before capture starts.</summary>
    public RecorderStatus? LastStatus => Volatile.Read(ref _lastStatus);
    public Task<RunResult> Completion { get; }

    private CaptureRun(CaptureOptions options)
    {
        SessionDir = Path.Combine(options.OutputRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _gameProfile = options.GameProfile;
        Directory.CreateDirectory(SessionDir);
        string stopName = $@"Local\odx-stop-{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);

        var inputArgs = new List<string> { "--out", SessionDir, "--stop-event", stopName };
        if (!string.IsNullOrWhiteSpace(options.NtpServer)) inputArgs.AddRange(["--ntp", options.NtpServer.Trim()]);
        var input = Launch("input", inputArgs);
        var recordArgs = new List<string> { "--out", SessionDir, "--fps", options.Fps.ToString(CultureInfo.InvariantCulture),
            "--stop-event", stopName, "--json-status" };
        recordArgs.AddRange(options.WindowProcess is string p ? ["--window", p] : ["--monitor"]);
        Process record;
        try
        {
            record = Launch("record", recordArgs, line =>
            {
                if (RecorderStatus.TryParse(line) is not { } status) return;
                Volatile.Write(ref _lastStatus, status);
                if (status.Event == RecorderStatus.Started) _started.TrySetResult();
            });
        }
        catch
        {
            _stop.Set();
            throw;
        }
        Completion = Task.Run(() => WaitAsync(input, record));
    }

    public static CaptureRun Start(CaptureOptions options) => new(options);

    public void Stop() => _stop.Set();

    private Process Launch(string command, IEnumerable<string> args, Action<string>? onStdout = null)
    {
        var psi = new ProcessStartInfo(ExternalTools.Odx())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = onStdout is not null,
        };
        psi.ArgumentList.Add(command);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) _stderr.Add($"odx {command}: {e.Data}");
        };
        proc.BeginErrorReadLine();
        if (onStdout is not null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout(e.Data); };
            proc.BeginOutputReadLine();
        }
        return proc;
    }

    private async Task<RunResult> WaitAsync(Process input, Process record)
    {
        await Task.WhenAny(input.WaitForExitAsync(), record.WaitForExitAsync());
        _stop.Set();
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await Task.WhenAll(input.WaitForExitAsync(timeout.Token), record.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            foreach (var p in new[] { input, record })
                if (!p.HasExited) p.Kill();
            lock (_stderr) _stderr.Add("a capture tool did not stop in time and was killed");
        }
        _stop.Dispose();

        bool captureOk = input.ExitCode == 0 && record.ExitCode == 0;
        var status = LastStatus is { Event: RecorderStatus.Stopped } stopped ? stopped : null;
        string inputCsv = Path.Combine(SessionDir, "input_events.csv");
        int inputEvents = File.Exists(inputCsv) ? Math.Max(0, File.ReadLines(inputCsv).Count() - 1) : 0;
        List<string> errors;
        lock (_stderr) errors = [.. _stderr];
        return new RunResult(SessionDir, captureOk, status, inputEvents, errors, captureOk ? BuildSession() : null);
    }

    private BuildResult? BuildSession()
    {
        if (_gameProfile is null) return null;
        try
        {
            var report = SessionBuilder.Build(SessionDir, GameProfile.Load(_gameProfile));
            return new BuildResult(report, SessionValidator.ValidateSession(SessionDir), null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or JsonException)
        {
            return new BuildResult(null, [], e.Message);
        }
    }
}
