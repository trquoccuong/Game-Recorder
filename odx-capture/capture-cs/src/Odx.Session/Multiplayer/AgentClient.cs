using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Session;

/// <param name="Orchestrator">e.g. http://192.168.1.10:5180</param>
/// <param name="WindowProcess">Process name to record (odx record --window); null: the game profile's window, else the monitor.</param>
/// <param name="IsHost">Starts and ends rounds (<see cref="AgentClient.ToggleRoundAsync"/>) instead of taking part in ready checks.</param>
/// <param name="AutoReady">Says ready on connect and after every round (headless capture machines).</param>
public sealed record AgentOptions(Uri Orchestrator, string PlayerId, string Name, string OutputRoot, string? WindowProcess,
    bool IsHost = false, bool AutoReady = false);

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
    private string? _profileWindow;
    private readonly object _gate = new();
    private ICaptureRun? _run;
    private string? _runSessionId, _startingSessionId, _stopReason;
    private CancellationTokenSource? _stopBeforeStart;

    /// <summary>Human-readable progress for a UI or console.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// A round starts after this countdown (ms). The capture starts with the countdown, so the video holds the moment play
    /// begins (round_start_frame). The token is cancelled if the round is stopped before the countdown ends.
    /// </summary>
    public event Action<int, CancellationToken>? Countdown;

    /// <summary>Whether this player said it is ready for the next round; cleared when play starts (after the countdown).</summary>
    public bool IsReady { get; private set; }

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
        IsReady = false;
        await SendAsync(new Hello(options.PlayerId, options.Name, BuildInfo.Version, MachineId, options.IsHost), stop);
        Log?.Invoke($"connected to {options.Orchestrator} as {options.PlayerId}");
        if (options.AutoReady) await SetReadyAsync(true, stop);

        var buffer = new byte[64 * 1024];
        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string? text = await ReceiveTextAsync(socket, buffer, stop);
            if (text is null) break;
            await HandleAsync(AgentMessage.Parse(text), stop);
        }
        StopCapture();
    }

    public async Task SetReadyAsync(bool ready, CancellationToken stop)
    {
        IsReady = ready;
        await SendAsync(new Ready(ready), stop);
        Log?.Invoke(ready ? "ready" : "not ready");
    }

    /// <summary>
    /// Host control (a player in place of a game server plugin): starts a round, with a countdown on every player, when none
    /// is running, otherwise ends it.
    /// </summary>
    public async Task ToggleRoundAsync(int countdownMs, CancellationToken stop)
    {
        var status = await _http.GetFromJsonAsync<JsonObject>("/status", stop);
        object e = status?["current_session"] is null
            ? new { type = GameEvents.RoundStart, countdown_ms = countdownMs, by = options.PlayerId }
            : new { type = GameEvents.RoundEnd, by = options.PlayerId };
        using var response = await _http.PostAsJsonAsync("/game/events", e, stop);
        response.EnsureSuccessStatusCode();
    }

    private static string MachineId =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16]
            .ToLowerInvariant();

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
                _profileWindow = JsonNode.Parse(a.GameProfileJson)?["window_process"]?.GetValue<string>();
                _profilePath = Path.Combine(Path.GetTempPath(), $"odx-agent-{options.PlayerId}-{a.SessionId}.json");
                await File.WriteAllTextAsync(_profilePath, a.GameProfileJson, stop);
                await SendAsync(new AgentState("armed", a.SessionId), stop);
                Log?.Invoke($"armed for {a.SessionId}");
                break;
            case Start s when _armed?.SessionId == s.SessionId:
                if (s.CountdownMs == 0) IsReady = false;
                else _ = Task.Delay(s.CountdownMs, stop).ContinueWith(_ => IsReady = false, TaskScheduler.Default);
                lock (_gate)
                {
                    _stopReason = null;
                    _startingSessionId = s.SessionId;
                    _stopBeforeStart = CancellationTokenSource.CreateLinkedTokenSource(stop);
                }
                _ = Task.Run(() => RecordAsync(s, stop), stop);
                break;
            case Stop s when s.SessionId == _runSessionId || s.SessionId == _startingSessionId:
                Log?.Invoke($"stop {s.SessionId} ({s.Reason})");
                lock (_gate) _stopReason = s.Reason;
                StopCapture();
                break;
        }
    }

    /// <summary>
    /// The player's choice, else the game's window from the profile when it is running, else the primary monitor (the
    /// session's capture.window check then flags it).
    /// </summary>
    private string? WindowToRecord()
    {
        if (options.WindowProcess is not null || _profileWindow is null) return options.WindowProcess;
        var running = System.Diagnostics.Process.GetProcessesByName(_profileWindow);
        bool open = running.Any(p => p.MainWindowHandle != IntPtr.Zero);
        foreach (var p in running) p.Dispose();
        if (open) return _profileWindow;
        Log?.Invoke($"{_profileWindow} is not running: recording the primary monitor");
        return null;
    }

    /// <summary>A Stop can arrive during the countdown or while the capture is starting; both end up here.</summary>
    private void StopCapture()
    {
        lock (_gate)
        {
            _stopBeforeStart?.Cancel();
            _run?.Stop();
        }
    }

    private async Task RecordAsync(Start s, CancellationToken stop)
    {
        CancellationToken stopped;
        lock (_gate) stopped = _stopBeforeStart!.Token;
        if (s.CountdownMs > 0) Countdown?.Invoke(s.CountdownMs, stopped);
        ICaptureRun run;
        try
        {
            run = startCapture(new CaptureOptions(options.OutputRoot, WindowToRecord(), _armed!.Fps, _profilePath, _timeServer,
                NtpIntervalS: 1));
        }
        catch (Exception e)
        {
            await EndWithoutCaptureAsync(s.SessionId, e.Message, stop);
            return;
        }
        lock (_gate)
        {
            _run = run;
            _runSessionId = s.SessionId;
            _startingSessionId = null;
            if (stopped.IsCancellationRequested) run.Stop();
        }
        await SendAsync(new AgentState("recording", s.SessionId), stop);
        Log?.Invoke($"recording {s.SessionId} into {run.SessionDir}");

        var result = await run.Completion;
        if (Volatile.Read(ref _stopReason) == "cancelled")
        {
            lock (_gate)
            {
                _run = null;
                _runSessionId = null;
            }
            await EndWithoutCaptureAsync(s.SessionId, "round cancelled, recording kept locally only", stop);
            return;
        }
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
        if (options.AutoReady) await SetReadyAsync(true, stop);
        lock (_gate)
        {
            _run = null;
            _runSessionId = null;
        }
    }

    private async Task EndWithoutCaptureAsync(string sessionId, string reason, CancellationToken stop)
    {
        lock (_gate) _startingSessionId = null;
        await SendAsync(new Done(sessionId, false, reason), stop);
        await SendAsync(new AgentState("idle"), stop);
        Log?.Invoke($"{sessionId}: {reason}");
        if (options.AutoReady) await SetReadyAsync(true, stop);
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
