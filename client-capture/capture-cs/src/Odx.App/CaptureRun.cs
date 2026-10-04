using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Odx.Core;

namespace Odx.App;

public sealed record RunResult(string SessionDir, bool Ok, string Summary);

/// <summary>
/// One recording: odx-input and odx-record run as child processes into the same session folder, both stopped through
/// a shared named event. If either child exits on its own (error, captured window closed), the other is stopped too.
/// </summary>
public sealed class CaptureRun
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);  // ffmpeg may still be draining frames

    private readonly EventWaitHandle _stop;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string SessionDir { get; }
    /// <summary>Completes when odx-record reports that capture is running; odx-record can take seconds on a cold start.</summary>
    public Task Started => _started.Task;
    public Task<RunResult> Completion { get; }

    private CaptureRun(AppSettings settings)
    {
        SessionDir = Path.Combine(settings.OutputRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(SessionDir);
        string stopName = $@"Local\odx-stop-{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);

        var input = Launch("odx-input", ["--out", SessionDir, "--stop-event", stopName]);
        var recordArgs = new List<string> { "--out", SessionDir, "--fps", settings.Fps.ToString(CultureInfo.InvariantCulture),
            "--stop-event", stopName };
        recordArgs.AddRange(settings.WindowProcess is string p ? ["--window", p] : ["--monitor"]);
        Process record;
        try
        {
            record = Launch("odx-record", recordArgs, line =>
            {
                if (line.StartsWith("recording ", StringComparison.Ordinal)) _started.TrySetResult();
            });
        }
        catch
        {
            _stop.Set();
            throw;
        }
        Completion = Task.Run(() => WaitAsync(input, record));
    }

    public static CaptureRun Start(AppSettings settings) => new(settings);

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
        return new RunResult(SessionDir, ok, summary.ToString().TrimEnd());
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
