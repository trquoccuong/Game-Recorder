using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Session;

/// <param name="Orchestrator">e.g. http://192.168.1.10:5180</param>
/// <param name="WindowProcess">Process name to record (odx record --window); null: the game profile's window, else the monitor.</param>
/// <param name="RoomCode">Invite code of the room to join; with <paramref name="CreateRoom"/> instead, a new room is opened
/// with this player as its host. After a reconnect the agent joins the same room again.</param>
/// <param name="AutoReady">Says ready whenever it is idle in a room between rounds (headless capture machines).</param>
/// <param name="AccessKey">The orchestrator's access key (access_key in odx-capture.json), sent with every request.</param>
/// <param name="Lan">With <paramref name="CreateRoom"/>: the new room gets a virtual LAN (ZeroTier).</param>
public sealed record AgentOptions(Uri Orchestrator, string PlayerId, string Name, string OutputRoot, string? WindowProcess,
    string? RoomCode = null, bool CreateRoom = false, bool AutoReady = false, string? AccessKey = null, bool Lan = false);

/// <param name="Status">"joining", "ok", "zerotier_missing" (not installed, or its tray app never ran), "error".</param>
/// <param name="Address">This machine's IP on the room's LAN once "ok"; the error text for "error".</param>
public sealed record LanState(string NetworkId, string Status, string? Address = null);

/// <summary>
/// A player's capture machine under the orchestrator, in one room: on Start it records (sampling its clock against the
/// orchestrator's time service, checked once on Welcome), on Stop it lets the capture build and validate the session, uploads
/// the folder and reports Done. odx agent (headless) and odx-app's room mode both run this.
/// </summary>
public sealed class AgentClient(AgentOptions options, Func<CaptureOptions, ICaptureRun> startCapture)
{
    private static readonly TimeSpan NtpTimeout = TimeSpan.FromSeconds(2), Retry = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HttpClient _http = new()
    {
        BaseAddress = options.Orchestrator,
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { Authorization = options.AccessKey is { } key ? new AuthenticationHeaderValue("Bearer", key) : null },
    };
    private ClientWebSocket? _socket;
    private string? _timeServer;
    private Arm? _armed;
    private string? _profilePath;
    private string? _profileWindow;
    private PacketCaptureSpec? _armedNetwork;
    private readonly object _gate = new();
    private ICaptureRun? _run;
    private string? _runSessionId, _startingSessionId, _stopReason;
    private CancellationTokenSource? _stopBeforeStart;
    private string? _roomCode = options.RoomCode?.Trim().ToUpperInvariant();
    private string? _lanNetwork;

    /// <summary>Human-readable progress for a UI or console.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// A round starts after this countdown (ms). The capture starts with the countdown, so the video holds the moment play
    /// begins (round_start_frame). The token is cancelled if the round is stopped before the countdown ends.
    /// </summary>
    public event Action<int, CancellationToken>? Countdown;

    /// <summary>Raised with the room's latest state, or null once out of it (left, closed by the host).</summary>
    public event Action<RoomUpdate?>? RoomChanged;

    /// <summary>
    /// The orchestrator's game asks for its traffic to be recorded (spec v3 §2.3); the owner then sets <see cref="Packets"/>
    /// (odx-app: the elevated pktmon helper).
    /// </summary>
    public event Action<PacketCaptureSpec>? PacketCaptureWanted;

    /// <summary>Records each round's game traffic into packets.pcapng; null records none.</summary>
    public IPacketCapture? Packets { get; set; }

    /// <summary>Raised when the orchestrator refuses a room request (unknown invite code, not the host…).</summary>
    public event Action<string>? RoomError;

    /// <summary>Progress of joining the room's virtual LAN; null once out of it.</summary>
    public event Action<LanState?>? LanChanged;

    /// <summary>A round's capture went to the orchestrator (session id, uploaded, why not).</summary>
    public event Action<string, bool, string?>? RoundUploaded;

    /// <summary>The orchestrator assembled a round this player recorded.</summary>
    public event Action<SessionResult>? SessionResultReceived;

    /// <summary>Whether this player is ready for the next round; the orchestrator clears it when play starts.</summary>
    public bool IsReady { get; private set; }

    public RoomUpdate? Room { get; private set; }

    public bool IsHost => Room?.HostPlayerId == options.PlayerId;

    /// <summary>The capture in progress (odx-app shows its counters); null between sessions.</summary>
    public ICaptureRun? CurrentRun => _run;

    /// <summary>
    /// Runs until <paramref name="stop"/>, reconnecting every 5 s while the orchestrator is unreachable. A refused access key
    /// ends it (retrying cannot help) and is reported through <see cref="RoomError"/>.
    /// </summary>
    public async Task RunUntilStoppedAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stop);
                if (!stop.IsCancellationRequested) Log?.Invoke("orchestrator closed the connection");
            }
            catch (UnauthorizedAccessException e)
            {
                Log?.Invoke(e.Message);
                RoomError?.Invoke(e.Message);
                return;
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
        socket.Options.CollectHttpResponseDetails = true;
        if (options.AccessKey is { } key) socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        _socket = socket;
        var wsUri = new UriBuilder(options.Orchestrator)
        {
            Scheme = options.Orchestrator.Scheme == "https" ? "wss" : "ws",
            Path = "/agents",
        }.Uri;
        try
        {
            await socket.ConnectAsync(wsUri, stop);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(
                $"{options.Orchestrator} refused the access key (access_key in odx-capture.json)");
        }
        IsReady = false;
        await SendAsync(new Hello(options.PlayerId, options.Name, BuildInfo.Version, MachineId, SignedInSteamId()), stop);
        Log?.Invoke($"connected to {options.Orchestrator} as {options.PlayerId}");
        if (_roomCode is not null) await SendAsync(new JoinRoom(_roomCode), stop);
        else if (options.CreateRoom) await SendAsync(new CreateRoom(options.Lan), stop);

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
    /// Host control (in place of a game server plugin): starts a round, with a countdown on every member, when none is
    /// running, otherwise ends it.
    /// </summary>
    public Task ToggleRoundAsync(int countdownMs, CancellationToken stop) => SendAsync(new ToggleRound(countdownMs), stop);

    public async Task LeaveRoomAsync(CancellationToken stop)
    {
        await SendAsync(new LeaveRoom(), stop);
        LeftRoom();
    }

    /// <summary>Host only: closes the room for every member.</summary>
    public async Task CloseRoomAsync(CancellationToken stop)
    {
        await SendAsync(new CloseRoom(), stop);
        LeftRoom();
    }

    private void LeftRoom()
    {
        _roomCode = null;
        Room = null;
        IsReady = false;
        LeaveLan();
        RoomChanged?.Invoke(null);
    }

    /// <summary>
    /// Joins the room's ZeroTier network through this machine's ZeroTier service, has the orchestrator authorize this node
    /// and waits for an address. Called again after ZeroTier has been installed.
    /// </summary>
    public async Task JoinLanAsync(CancellationToken stop)
    {
        if (Room?.LanNetworkId is not { } network) return;
        _lanNetwork = network;
        if (ZeroTierApi.LocalToken() is not { } token)
        {
            LanChanged?.Invoke(new LanState(network, "zerotier_missing"));
            return;
        }
        LanChanged?.Invoke(new LanState(network, "joining"));
        using var zeroTier = new ZeroTierApi(ZeroTierApi.Local, token);
        try
        {
            string node = await zeroTier.NodeIdAsync(stop);
            await zeroTier.JoinAsync(network, stop);
            await SendAsync(new LanMember(node), stop);
            for (int i = 0; i < 60 && _lanNetwork == network; i++)
            {
                var (status, addresses) = await zeroTier.NetworkAsync(network, stop);
                if (status == "OK" && addresses.Count > 0)
                {
                    LanChanged?.Invoke(new LanState(network, "ok", addresses[0]));
                    Log?.Invoke($"LAN {addresses[0]} (ZeroTier {network})");
                    return;
                }
                await Task.Delay(1000, stop);
            }
            if (_lanNetwork == network) LanChanged?.Invoke(new LanState(network, "error", "no address from the room's LAN after 60 s"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            LanChanged?.Invoke(new LanState(network, e is HttpRequestException ? "zerotier_missing" : "error", e.Message));
        }
    }

    private void LeaveLan()
    {
        if (_lanNetwork is not { } network) return;
        _lanNetwork = null;
        if (ZeroTierApi.LocalToken() is { } token)
            _ = Task.Run(async () =>
            {
                using var zeroTier = new ZeroTierApi(ZeroTierApi.Local, token);
                try
                {
                    await zeroTier.LeaveAsync(network, default);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                }
            });
        LanChanged?.Invoke(null);
    }

    private static string MachineId =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16]
            .ToLowerInvariant();

    /// <summary>
    /// The Steam account signed in on this machine as a SteamID64, from the Steam client's registry entry (the account id
    /// of an individual account plus 76561197960265728); null when Steam is not running or not on Windows.
    /// </summary>
    private static string? SignedInSteamId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        return key?.GetValue("ActiveUser") is int accountId and not 0
            ? (76561197960265728UL + (uint)accountId).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private async Task HandleAsync(AgentMessage message, CancellationToken stop)
    {
        switch (message)
        {
            case Welcome w:
                _timeServer = $"{options.Orchestrator.Host}:{w.TimePort}";
                MeasureClock();
                if (w.GameProfileJson is { } welcomeProfile &&
                    GameProfile.Parse(JsonNode.Parse(welcomeProfile)!.AsObject(), "orchestrator").Network is { } network)
                    PacketCaptureWanted?.Invoke(network);
                break;
            case RoomUpdate r:
                bool joined = Room?.Code != r.Code;
                Room = r;
                _roomCode = r.Code;
                var me = r.Members.FirstOrDefault(m => m.PlayerId == options.PlayerId);
                IsReady = me?.Ready == true;
                if (joined) Log?.Invoke($"in room {r.Code}{(IsHost ? " (host)" : "")}");
                RoomChanged?.Invoke(r);
                if (r.LanNetworkId is not null && r.LanNetworkId != _lanNetwork)
                {
                    _lanNetwork = r.LanNetworkId;  // before the next update arrives, so it is joined once
                    _ = Task.Run(() => JoinLanAsync(stop), stop);
                }
                if (options.AutoReady && !IsHost && me is { Ready: false, State: "idle" } && r.CurrentSession is null)
                    await SetReadyAsync(true, stop);
                break;
            case SessionResult result:
                Log?.Invoke($"{result.SessionId}: {(result.Pass ? "PASS" : "FAIL")}");
                SessionResultReceived?.Invoke(result);
                break;
            case RoomError e:
                Log?.Invoke(e.Message);
                RoomError?.Invoke(e.Message);
                break;
            case RoomClosed c:
                Log?.Invoke($"room {c.Code} was closed by its host");
                LeftRoom();
                break;
            case Arm a:
                _armed = a;
                var armedProfile = GameProfile.Parse(JsonNode.Parse(a.GameProfileJson)!.AsObject(), "orchestrator");
                _profileWindow = armedProfile.WindowProcess;
                _armedNetwork = armedProfile.Network;
                _profilePath = Path.Combine(Path.GetTempPath(), $"odx-agent-{options.PlayerId}-{a.SessionId}.json");
                await File.WriteAllTextAsync(_profilePath, a.GameProfileJson, stop);
                await SendAsync(new AgentState("armed", a.SessionId), stop);
                Log?.Invoke($"armed for {a.SessionId}");
                break;
            case Start s when _armed?.SessionId == s.SessionId:
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
    /// The first open window of: the player's choice, the game's window from the profile; else the primary monitor (the
    /// session's capture.window check then flags it). A round never fails just because a game is not open.
    /// </summary>
    private string? WindowToRecord()
    {
        foreach (string? process in new[] { options.WindowProcess, _profileWindow }.Distinct())
        {
            if (process is null) continue;
            if (HasWindow(process)) return process;
            Log?.Invoke($"{process} is not running");
        }
        if (options.WindowProcess is not null || _profileWindow is not null) Log?.Invoke("recording the primary monitor");
        return null;
    }

    private static bool HasWindow(string process)
    {
        var running = System.Diagnostics.Process.GetProcessesByName(process);
        bool open = running.Any(p => p.MainWindowHandle != IntPtr.Zero);
        foreach (var p in running) p.Dispose();
        return open;
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
        string? etl = await StartPacketsAsync(s.SessionId, stop);
        ICaptureRun run;
        try
        {
            run = startCapture(new CaptureOptions(options.OutputRoot, WindowToRecord(), _armed!.Fps, _profilePath, _timeServer,
                NtpIntervalS: 1));
        }
        catch (Exception e)
        {
            if (etl is not null) await StopPacketsAsync(etl, null, stop);
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
        if (etl is not null) await StopPacketsAsync(etl, result.SessionDir, stop);
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
        RoundUploaded?.Invoke(s.SessionId, ok, ok ? null : error ?? "capture failed");
        lock (_gate)
        {
            _run = null;
            _runSessionId = null;
        }
    }

    /// <summary>The .etl pktmon writes to while recording, or null when this round captures no packets.</summary>
    private async Task<string?> StartPacketsAsync(string sessionId, CancellationToken stop)
    {
        if (Packets is not { } packets || _armedNetwork is not { } network) return null;
        string etl = Path.Combine(Path.GetTempPath(), $"odx-packets-{options.PlayerId}-{sessionId}.etl");
        try
        {
            await packets.StartAsync(network.Ports, etl, stop);
            return etl;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            Log?.Invoke($"packet capture not started: {e.Message}");
            return null;
        }
    }

    /// <param name="sessionDir">Where packets.pcapng goes (uploaded with the capture); null discards the capture.</param>
    private async Task StopPacketsAsync(string etl, string? sessionDir, CancellationToken stop)
    {
        string pcapng = Path.Combine(sessionDir ?? Path.GetTempPath(), sessionDir is null ? Path.GetFileNameWithoutExtension(etl) + ".pcapng"
            : PacketCaptureFiles.Pcapng);
        try
        {
            await Packets!.StopAsync(etl, pcapng, stop);
            if (sessionDir is null) File.Delete(pcapng);
            else PacketCaptureFiles.WriteLocalAddresses(sessionDir);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            Log?.Invoke($"packet capture: {e.Message}");
        }
    }

    private async Task EndWithoutCaptureAsync(string sessionId, string reason, CancellationToken stop)
    {
        lock (_gate) _startingSessionId = null;
        await SendAsync(new Done(sessionId, false, reason), stop);
        await SendAsync(new AgentState("idle"), stop);
        Log?.Invoke($"{sessionId}: {reason}");
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
