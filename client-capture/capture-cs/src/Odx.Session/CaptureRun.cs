using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Odx.Core;

namespace Odx.Session;

/// <param name="WindowProcess">Process name passed to odx-record --window; null records the primary monitor.</param>
/// <param name="GameProfile">Game profile JSON used to build the session after recording; null skips the build.</param>
public sealed record CaptureOptions(string OutputRoot, string? WindowProcess, int Fps, string? GameProfile);

public sealed record RunResult(string SessionDir, bool Ok, string Summary);

/// <summary>
/// One recording: odx-input and odx-record run as child processes into the same session folder, both stopped through
/// a shared named event. If either child exits on its own (error, captured window closed), the other is stopped too.
/// After a clean stop the folder is built into a spec v2 session with the chosen game profile and validated.
/// </summary>
public sealed class CaptureRun
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);  // ffmpeg may still be draining frames

    private readonly EventWaitHandle _stop;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _gameProfile;
    private RecorderStatus? _lastStatus;

    public string SessionDir { get; }
    /// <summary>Completes when odx-record reports that capture is running; odx-record can take seconds on a cold start.</summary>
    public Task Started => _started.Task;
    /// <summary>Latest odx-record status (frame counters update every second); null before capture starts.</summary>
    public RecorderStatus? LastStatus => Volatile.Read(ref _lastStatus);
    public Task<RunResult> Completion { get; }

    private CaptureRun(CaptureOptions options)
    {
        SessionDir = Path.Combine(options.OutputRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _gameProfile = options.GameProfile;
        Directory.CreateDirectory(SessionDir);
        string stopName = $@"Local\odx-stop-{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);

        var input = Launch("odx-input", ["--out", SessionDir, "--stop-event", stopName]);
        var recordArgs = new List<string> { "--out", SessionDir, "--fps", options.Fps.ToString(CultureInfo.InvariantCulture),
            "--stop-event", stopName, "--json-status" };
        recordArgs.AddRange(options.WindowProcess is string p ? ["--window", p] : ["--monitor"]);
        Process record;
        try
        {
            record = Launch("odx-record", recordArgs, line =>
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

    private Process Launch(string tool, IEnumerable<string> args, Action<string>? onStdout = null)
    {
        var psi = new ProcessStartInfo(ExternalTools.CaptureTool(tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = onStdout is not null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) _stderr.AppendLine($"{tool}: {e.Data}");
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
            lock (_stderr) _stderr.AppendLine("a capture tool did not stop in time and was killed");
        }
        _stop.Dispose();

        bool ok = input.ExitCode == 0 && record.ExitCode == 0;
        var summary = new StringBuilder(Describe());
        lock (_stderr) summary.Append(_stderr);
        if (ok) ok = BuildSession(summary);
        return new RunResult(SessionDir, ok, summary.ToString().TrimEnd());
    }

    private bool BuildSession(StringBuilder summary)
    {
        if (_gameProfile is null)
        {
            summary.AppendLine("No game profile selected: session not built (run odx-build later).");
            return true;
        }
        try
        {
            var report = SessionBuilder.Build(SessionDir, GameProfile.Load(_gameProfile));
            var issues = SessionValidator.ValidateSession(SessionDir);
            int errors = issues.Count(i => i.Severity == "error");
            summary.AppendLine($"Validation {(errors == 0 ? "PASS" : "FAIL")}: {errors} errors, {issues.Count - errors} warnings");
            foreach (var i in issues.Take(5)) summary.AppendLine($"  [{i.Severity}] {i.Rule}: {i.Message}");
            if (report.UnboundKeys.Count > 0)
                summary.AppendLine("Keys not bound in the game profile (left out): " + string.Join(", ", report.UnboundKeys.Keys));
            return errors == 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or JsonException)
        {
            summary.AppendLine($"Could not build the session: {e.Message}");
            return false;
        }
    }

    private string Describe()
    {
        var sb = new StringBuilder();
        string recordingJson = Path.Combine(SessionDir, "recording.json");
        if (File.Exists(recordingJson))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(recordingJson));
            var r = doc.RootElement;
            sb.AppendLine($"{r.GetProperty("frames")} frames ({r.GetProperty("width")}x{r.GetProperty("height")} @ " +
                          $"{r.GetProperty("fps")} fps), {r.GetProperty("duplicate_frames")} duplicated, " +
                          $"{r.GetProperty("dropped_source_frames")} dropped");
        }
        string inputCsv = Path.Combine(SessionDir, "input_events.csv");
        if (File.Exists(inputCsv))
            sb.AppendLine($"{Math.Max(0, File.ReadLines(inputCsv).Count() - 1)} input events");
        return sb.ToString();
    }
}
