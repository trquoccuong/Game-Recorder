using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Odx.Core;

namespace Odx.Session;

/// <param name="Orchestrator">e.g. http://192.168.1.10:5180</param>
/// <param name="WindowProcess">Process name to record (odx record --window); null records the primary monitor.</param>
public sealed record AgentOptions(Uri Orchestrator, string PlayerId, string Name, string OutputRoot, string? WindowProcess);

/// <summary>
/// A player's capture machine under the orchestrator: on Start it records (sampling its clock against the orchestrator's
/// time service, checked once on Welcome), on Stop it lets the capture build and validate the session, uploads the folder
/// and reports Done. odx agent (headless) and odx-app's agent mode both run this.
/// </summary>
public sealed class AgentClient(AgentOptions options, Func<CaptureOptions, ICaptureRun> startCapture)
{
    private static readonly TimeSpan NtpTimeout = TimeSpan.FromSeconds(2), Retry = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HttpClient _http = new() { BaseAddress = options.Orchestrator, Timeout = TimeSpan.FromMinutes(30) };
    private ClientWebSocket? _socket;
    private string? _timeServer;
    private Arm? _armed;
    private string? _profilePath;
    private ICaptureRun? _run;
    private string? _runSessionId;

    /// <summary>Human-readable progress for a UI or console.</summary>
    public event Action<string>? Log;

    /// <summary>The capture in progress (odx-app shows its counters); null between sessions.</summary>
    public ICaptureRun? CurrentRun => _run;

    /// <summary>Runs until <paramref name="stop"/>, reconnecting every 5 s while the orchestrator is unreachable.</summary>
    public async Task RunUntilStoppedAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stop);
                if (!stop.IsCancellationRequested) Log?.Invoke("orchestrator closed the connection");
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException)
            {
                Log?.Invoke($"{options.Orchestrator}: {e.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await Task.Delay(Retry, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        _socket = socket;
        var wsUri = new UriBuilder(options.Orchestrator)
        {
            Scheme = options.Orchestrator.Scheme == "https" ? "wss" : "ws",
            Path = "/agents",
        }.Uri;
        await socket.ConnectAsync(wsUri, stop);
        await SendAsync(new Hello(options.PlayerId, options.Name, BuildInfo.Version), stop);
        Log?.Invoke($"connected to {options.Orchestrator} as {options.PlayerId}");

        var buffer = new byte[64 * 1024];
        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string? text = await ReceiveTextAsync(socket, buffer, stop);
            if (text is null) break;
            await HandleAsync(AgentMessage.Parse(text), stop);
        }
        _run?.Stop();
    }

    private async Task HandleAsync(AgentMessage message, CancellationToken stop)
    {
        switch (message)
        {
            case Welcome w:
                _timeServer = $"{options.Orchestrator.Host}:{w.TimePort}";
                MeasureClock();
                break;
            case Arm a:
                _armed = a;
                _profilePath = Path.Combine(Path.GetTempPath(), $"odx-agent-{options.PlayerId}-{a.SessionId}.json");
                await File.WriteAllTextAsync(_profilePath, a.GameProfileJson, stop);
                await SendAsync(new AgentState("armed", a.SessionId), stop);
                Log?.Invoke($"armed for {a.SessionId}");
                break;
            case Start s when _armed?.SessionId == s.SessionId:
                _ = Task.Run(() => RecordAsync(s, stop), stop);
                break;
            case Stop s when _runSessionId == s.SessionId:
                Log?.Invoke($"stop {s.SessionId} ({s.Reason})");
                _run?.Stop();
                break;
        }
    }

    private async Task RecordAsync(Start s, CancellationToken stop)
    {
        try
        {
            _run = startCapture(new CaptureOptions(options.OutputRoot, options.WindowProcess, _armed!.Fps, _profilePath, _timeServer));
            _runSessionId = s.SessionId;
        }
        catch (Exception e)
        {
            await SendAsync(new AgentState("error", e.Message), stop);
            await SendAsync(new Done(s.SessionId, false, e.Message), stop);
            return;
        }
        await SendAsync(new AgentState("recording", s.SessionId), stop);
        Log?.Invoke($"recording {s.SessionId} into {_run.SessionDir}");

        var result = await _run.Completion;
        await SendAsync(new AgentState("uploading", s.SessionId), stop);
        string? error = null;
        try
        {
            await UploadAsync(s.SessionId, result.SessionDir, stop);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            error = $"upload failed: {e.Message}";
        }
        bool ok = result.CaptureOk && error is null;
        await SendAsync(new Done(s.SessionId, ok, error ?? (result.CaptureOk ? null : "capture failed")), stop);
        await SendAsync(new AgentState("idle"), stop);
        Log?.Invoke($"{s.SessionId} done: {(ok ? "uploaded" : error ?? "capture failed")}");
        _run = null;
        _runSessionId = null;
    }

    private async Task UploadAsync(string sessionId, string dir, CancellationToken stop)
    {
        foreach (string file in Directory.GetFiles(dir))
        {
            await using var stream = File.OpenRead(file);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            string url = $"/sessions/{Uri.EscapeDataString(sessionId)}/players/{Uri.EscapeDataString(options.PlayerId)}/files/" +
                         Uri.EscapeDataString(Path.GetFileName(file));
            using var response = await _http.PutAsync(url, content, stop);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Checks the time service answers before any round; the capture itself samples it at start and every 15 s.</summary>
    private void MeasureClock()
    {
        var offsets = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                offsets.Add(SntpClient.Query(_timeServer!, NtpTimeout).SystemOffsetMs);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
            {
                Log?.Invoke($"time service {_timeServer}: {e.Message}");
            }
        }
        Log?.Invoke(offsets.Count > 0
            ? $"clock: server - this machine = {offsets.Order().ElementAt(offsets.Count / 2):F1} ms ({offsets.Count}/5 samples)"
            : $"clock: time service {_timeServer} does not answer; sessions will fail sync.clock");
    }

    private async Task SendAsync(AgentMessage message, CancellationToken stop)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        await _sendLock.WaitAsync(stop);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, stop);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal static async Task<string?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken stop)
    {
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try
            {
                r = await socket.ReceiveAsync(buffer, stop);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }
}
