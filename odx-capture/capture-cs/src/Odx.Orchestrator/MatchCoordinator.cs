using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Session;

namespace Odx.Orchestrator;

/// <param name="OutRoot">Sessions go to &lt;OutRoot&gt;/&lt;vendor&gt;/…; uploads are staged in &lt;OutRoot&gt;/.staging/.</param>
/// <param name="DoneTimeout">How long after a round ends to wait for uploads before assembling with what arrived.</param>
public sealed record OrchestratorOptions(string OutRoot, string GameProfilePath, string Vendor = "odx", int HttpPort = 5180,
    int TimePort = 12300, int Fps = 30)
{
    public GateTolerances Tolerances { get; init; } = GateTolerances.Default;
    public TimeSpan DoneTimeout { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Countdown of a round started because every player is ready (as in the client's sample delivery).</summary>
    public int ReadyCountdownMs { get; init; } = 5000;
    /// <summary>Players (host included) needed before a ready check can start a round.</summary>
    public int MinReadyPlayers { get; init; } = 2;
}

/// <summary>
/// The match state machine (spec v3 §2). A round starts from round_start (game server plugin or host) or when every
/// non-host player is ready. With a countdown, players record from round_countdown and play starts at round_start; a
/// round_end, an unready or a player leaving during the countdown cancels the round (nothing is kept). A permanent
/// player_death stops that player, round_end stops the rest; when all players have uploaded (or
/// <see cref="OrchestratorOptions.DoneTimeout"/> passes) the round is assembled into a v3 session.
/// </summary>
public sealed class MatchCoordinator(OrchestratorOptions options, int timePort, ILogger<MatchCoordinator> log)
{
    private sealed class AgentLink(Hello hello, WebSocket socket)
    {
        public Hello Hello { get; } = hello;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public string State { get; set; } = "idle";
        public bool Ready { get; set; }
    }

    private sealed class OpenRound(RoundRecord record, string stagingDir)
    {
        public RoundRecord Record { get; set; } = record;
        public string StagingDir { get; } = stagingDir;
        public List<JsonObject> Events { get; } = [];
        public bool Counting { get; set; }
        public bool Ended { get; set; }
        public int Finalized;

        public int IndexOf(string playerId) => Record.Players.FindIndex(p => p.PlayerId == playerId);
    }

    /// <summary>What a state change asks for once the lock is released.</summary>
    private sealed class Effects
    {
        public List<(AgentLink, AgentMessage)> Sends { get; } = [];
        public List<OpenRound> Ended { get; } = [];
        public List<OpenRound> Cancelled { get; } = [];
        public OpenRound? Countdown { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentLink> _agents = [];
    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly List<SessionReport> _reports = [];
    /// <summary>Events between rounds (match_start, player_ready…); they open the next round's events.</summary>
    private readonly List<JsonObject> _betweenRounds = [];
    private readonly string _profileJson = File.ReadAllText(options.GameProfilePath);
    private readonly string _profileGame = GameProfile.Load(options.GameProfilePath).GameTitle;
    private string? _matchId, _map;
    private string _game = "";
    private int _roundNumber;
    private long _eventId;
    private OpenRound? _current;
    private ServerClock? _serverClock;

    public int TimePort => timePort;

    /// <summary>Raised with the session folder and its report each time a round has been assembled.</summary>
    public event Action<string, SessionReport>? SessionAssembled;

    /// <summary>Raised with the session id when a round is cancelled during its countdown.</summary>
    public event Action<string>? RoundCancelled;

    public async Task HandleAgentAsync(WebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[64 * 1024];
        if (await ReceiveAsync(socket, buffer, stop) is not Hello hello)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "expected hello", stop);
            return;
        }
        var link = new AgentLink(hello, socket);
        lock (_lock) _agents[hello.PlayerId] = link;
        log.LogInformation("agent {Player} ({Name}, {Version}{Host}) connected", hello.PlayerId, hello.Name, hello.ToolVersion,
            hello.IsHost ? ", host" : "");
        await SendAsync(link, new Welcome(timePort));

        try
        {
            while (!stop.IsCancellationRequested && await ReceiveAsync(socket, buffer, stop) is { } message)
            {
                switch (message)
                {
                    case AgentState s:
                        link.State = s.State;
                        log.LogInformation("agent {Player}: {State} {Detail}", hello.PlayerId, s.State, s.Detail);
                        break;
                    case Done d:
                        MarkDone(d.SessionId, hello.PlayerId, d.Ok, d.Error);
                        break;
                    case Ready r:
                        await SetReadyAsync(link, r.IsReady);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            var effects = new Effects();
            lock (_lock)
            {
                if (_agents.GetValueOrDefault(hello.PlayerId) == link) _agents.Remove(hello.PlayerId);
                if (_current is { Counting: true } counting && counting.IndexOf(hello.PlayerId) >= 0)
                    Cancel(counting, $"player_left:{hello.PlayerId}", effects);
            }
            log.LogWarning("agent {Player} disconnected", hello.PlayerId);
            await RunAsync(effects);
            foreach (string sid in RoundsWaitingFor(hello.PlayerId))
                MarkDone(sid, hello.PlayerId, false, "agent disconnected");
        }
    }

    /// <summary>Applies one game server (or host) event and returns it stamped with its server time and id.</summary>
    /// <exception cref="FormatException">The event has no "type".</exception>
    public async Task<JsonObject> PostEventAsync(JsonObject e)
    {
        var effects = new Effects();
        lock (_lock) Apply(e, effects);
        await RunAsync(effects);
        return e;
    }

    /// <summary>
    /// Measures this machine's clock against <paramref name="server"/> (time_server of odx-capture.json) every minute;
    /// each round records the latest measurement. It never changes the Windows clock.
    /// </summary>
    public async Task MeasureClockAsync(string server, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var s = SntpClient.Query(server, TimeSpan.FromSeconds(2));
                Volatile.Write(ref _serverClock, new ServerClock(server, s.SystemOffsetMs, s.RttMs, NtpResponder.NowUnixMs()));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
            {
                log.LogWarning("time server {Server}: {Error}", server, e.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Where an upload goes, or null when the session/player is unknown or the name is not a plain file name.</summary>
    public string? UploadPath(string sessionId, string playerId, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..") return null;
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out var round) || round.Finalized != 0 || round.IndexOf(playerId) < 0) return null;
            string dir = Path.Combine(round.StagingDir, "players", playerId);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
    }

    public JsonObject Status()
    {
        lock (_lock)
        {
            var agents = new JsonArray(_agents.Values.Select(a => (JsonNode)new JsonObject
            {
                ["player_id"] = a.Hello.PlayerId,
                ["name"] = a.Hello.Name,
                ["version"] = a.Hello.ToolVersion,
                ["state"] = a.State,
                ["host"] = a.Hello.IsHost,
                ["ready"] = a.Ready,
            }).ToArray());
            return new JsonObject
            {
                ["match_id"] = _matchId,
                ["game"] = _game,
                ["current_session"] = _current?.Record.SessionId,
                ["countdown"] = _current?.Counting ?? false,
                ["pending_sessions"] = new JsonArray(_rounds.Keys.Select(k => (JsonNode)k).ToArray()),
                ["agents"] = agents,
                ["sessions"] = new JsonArray(_reports.Select(r => (JsonNode)new JsonObject
                {
                    ["session_id"] = r.SessionId, ["pass"] = r.Pass, ["valid_players"] = r.ValidPlayers,
                    ["required_players"] = r.RequiredPlayers,
                }).ToArray()),
            };
        }
    }

    private async Task SetReadyAsync(AgentLink link, bool ready)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (link.Ready == ready) return;
            link.Ready = ready;
            string id = link.Hello.PlayerId;
            Apply(new JsonObject { ["type"] = ready ? "player_ready" : "player_unready", ["player"] = id, ["name"] = link.Hello.Name },
                effects);
            if (!ready && _current is { Counting: true } counting && counting.IndexOf(id) >= 0)
                Cancel(counting, $"player_unready:{id}", effects);
            else if (ready && EveryoneReady())
                Apply(new JsonObject { ["type"] = GameEvents.RoundStart, ["countdown_ms"] = options.ReadyCountdownMs, ["by"] = "ready_check" },
                    effects);
        }
        await RunAsync(effects);
    }

    /// <summary>No round running, enough players, and every one that is not a host is ready and idle.</summary>
    private bool EveryoneReady()
    {
        var players = _agents.Values.Where(a => !a.Hello.IsHost).ToList();
        return _current is null && _agents.Count >= options.MinReadyPlayers && players.Count > 0 &&
               players.All(a => a.Ready && a.State is "idle" or "armed");
    }

    /// <summary>Called with the lock held.</summary>
    private void Apply(JsonObject e, Effects effects)
    {
        e["utc_ms"] ??= NtpResponder.NowUnixMs();
        double utc = GameEvents.UtcMs(e);
        string type = GameEvents.Type(e);
        Stamp(e, utc);

        if (_current is { Counting: true } counting && type is GameEvents.RoundStart or GameEvents.RoundEnd or GameEvents.MatchStart or GameEvents.MatchEnd)
        {
            Cancel(counting, EndReason(e), effects);
            if (type == GameEvents.RoundEnd) return;  // the cancel is the whole story of this round
        }
        switch (type)
        {
            case GameEvents.MatchStart:
                if (_current is not null) EndRound(utc, "abort", "abort", effects);
                StartMatch(e);
                break;
            case GameEvents.RoundStart:
                if (_current is not null) EndRound(utc, "abort", "abort", effects);
                if (_matchId is null) StartMatch(e);
                StartRound(e, utc, effects);
                break;
            case GameEvents.PlayerDeath when _current is not null && e["permanent"]?.GetValue<bool>() == true &&
                                             GameEvents.Text(e, "player") is { } player:
                StopPlayer(_current, player, utc, "permanent_death", effects);
                break;
        }
        AddToRound(e);
        if (type is GameEvents.RoundEnd or GameEvents.MatchEnd && _current is not null)
            EndRound(utc, "round_end", EndReason(e), effects);
        if (type == GameEvents.MatchEnd) _matchId = null;
    }

    private void Stamp(JsonObject e, double utc)
    {
        e["event_id"] = ++_eventId;
        e["utc"] = SessionBuilder.IsoUtc(utc);
        e["server_time_us"] = (long)Math.Round(utc * 1000);
    }

    private void AddToRound(JsonObject e)
    {
        if (_current is not { } round)
        {
            _betweenRounds.Add(e);
            return;
        }
        e["session_id"] = round.Record.SessionId;
        e["round_index"] = round.Record.Round;
        round.Events.Add(e);
    }

    private void StartMatch(JsonObject e)
    {
        _matchId = GameEvents.Text(e, "match_id") ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _game = GameEvents.Text(e, "game") ?? _profileGame;
        _map = GameEvents.Text(e, "map");
        _roundNumber = 0;
        log.LogInformation("match {Match}: {Game} {Map}", _matchId, _game, _map);
    }

    /// <summary>With a countdown, <paramref name="e"/> becomes round_countdown and round_start follows when play starts.</summary>
    private void StartRound(JsonObject e, double utc, Effects effects)
    {
        _roundNumber = e["round"]?.GetValue<int>() ?? _roundNumber + 1;
        string sid = $"{_matchId}-r{_roundNumber:D2}";
        var players = _agents.Values.Where(a => a.State is "idle" or "armed").ToList();
        foreach (var busy in _agents.Values.Except(players))
            log.LogWarning("{Player} is {State}, left out of {Session}", busy.Hello.PlayerId, busy.State, sid);
        int countdownMs = e["countdown_ms"]?.GetValue<int>() ?? 0;
        var record = new RoundRecord(sid, _matchId!, _game, _map, _roundNumber, utc + countdownMs, null, timePort,
            players.Select(a => new RoundPlayer(a.Hello.PlayerId, a.Hello.Name, ToolVersion: a.Hello.ToolVersion,
                MachineId: a.Hello.MachineId)).ToList(), countdownMs,
            ServerClock: Volatile.Read(ref _serverClock));
        // Unique per attempt: a cancelled round's number is used again while its staging may still be deleted.
        string staging = Path.Combine(options.OutRoot, ".staging", $"{sid}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, RoundRecord.ProfileFileName), _profileJson);
        _current = new OpenRound(record, staging) { Counting = countdownMs > 0 };
        _current.Events.AddRange(_betweenRounds);
        _betweenRounds.Clear();
        _rounds[sid] = _current;
        if (countdownMs == 0) ClearReady();
        foreach (var a in players)
        {
            a.State = "starting";
            effects.Sends.Add((a, new Arm(sid, _profileJson, options.Fps)));
            effects.Sends.Add((a, new Start(sid, countdownMs)));
        }
        if (countdownMs > 0)
        {
            e["type"] = "round_countdown";
            e["starts_in_ms"] = countdownMs;
            effects.Countdown = _current;
        }
        log.LogInformation("round {Session}: {Count} player(s){Countdown}", sid, players.Count,
            countdownMs > 0 ? $", play starts in {countdownMs / 1000.0:F0} s" : "");
    }

    private void CountdownEnded(OpenRound round)
    {
        lock (_lock)
        {
            if (_current != round || !round.Counting) return;
            round.Counting = false;
            ClearReady();
            var e = new JsonObject
            {
                ["type"] = GameEvents.RoundStart,
                ["utc_ms"] = round.Record.StartUtcMs,
                ["players"] = new JsonArray(round.Record.Players.Select(p => (JsonNode)p.PlayerId).ToArray()),
            };
            Stamp(e, round.Record.StartUtcMs);
            AddToRound(e);
        }
        log.LogInformation("round {Session}: play started", round.Record.SessionId);
    }

    /// <summary>Ready is for the next round once play starts; until then an unready cancels the countdown.</summary>
    private void ClearReady()
    {
        foreach (var a in _agents.Values) a.Ready = false;
    }

    /// <summary>Drops a round during its countdown: everyone stops, nothing is uploaded or kept, its number is used again.</summary>
    private void Cancel(OpenRound round, string reason, Effects effects)
    {
        double utc = NtpResponder.NowUnixMs();
        round.Counting = false;
        round.Ended = true;
        _current = null;
        _rounds.Remove(round.Record.SessionId);
        _roundNumber = round.Record.Round - 1;
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, "cancelled", effects);
        var e = new JsonObject { ["type"] = "round_cancelled", ["utc_ms"] = utc, ["cancelled_session_id"] = round.Record.SessionId,
            ["reason"] = reason };
        Stamp(e, utc);
        _betweenRounds.Add(e);
        effects.Cancelled.Add(round);
        log.LogInformation("round {Session} cancelled: {Reason}", round.Record.SessionId, reason);
    }

    private void StopPlayer(OpenRound round, string playerId, double utc, string reason, Effects effects)
    {
        int i = round.IndexOf(playerId);
        if (i < 0 || round.Record.Players[i].StopReason is not null) return;
        round.Record.Players[i] = round.Record.Players[i] with { StopReason = reason, StopUtcMs = utc };
        if (_agents.TryGetValue(playerId, out var link)) effects.Sends.Add((link, new Stop(round.Record.SessionId, reason)));
        log.LogInformation("stop {Player} in {Session}: {Reason}", playerId, round.Record.SessionId, reason);
    }

    /// <param name="playerReason">Stop reason of every player still recording.</param>
    /// <param name="endReason">Why the round ended, for session.json.</param>
    private void EndRound(double utc, string playerReason, string endReason, Effects effects)
    {
        var round = _current!;
        _current = null;
        round.Ended = true;
        round.Record = round.Record with { EndUtcMs = utc, EndReason = endReason };
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, playerReason, effects);
        effects.Ended.Add(round);
    }

    /// <summary>The game's own "reason", else "player_stop:&lt;id&gt;" when a host ended it, else the event type.</summary>
    private static string EndReason(JsonObject e) =>
        GameEvents.Text(e, "reason") ?? (GameEvents.Text(e, "by") is { } by ? $"player_stop:{by}" : GameEvents.Type(e));

    private async Task RunAsync(Effects effects)
    {
        foreach (var (link, message) in effects.Sends) await SendAsync(link, message);
        foreach (var round in effects.Ended)
        {
            if (AllDone(round)) _ = Task.Run(() => Assemble(round));
            else _ = Task.Delay(options.DoneTimeout).ContinueWith(_ => Assemble(round), TaskScheduler.Default);
        }
        foreach (var round in effects.Cancelled)
        {
            Interlocked.Exchange(ref round.Finalized, 1);
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.Delete(round.StagingDir, true);
                }
                catch (IOException e)
                {
                    log.LogWarning("cannot delete {Dir}: {Error}", round.StagingDir, e.Message);
                }
            });
            RoundCancelled?.Invoke(round.Record.SessionId);
        }
        if (effects.Countdown is { } counting)
            _ = Task.Delay(counting.Record.CountdownMs).ContinueWith(_ => CountdownEnded(counting), TaskScheduler.Default);
    }

    private void MarkDone(string sessionId, string playerId, bool ok, string? error)
    {
        OpenRound? round;
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out round) || round.IndexOf(playerId) is var i && i < 0) return;
            round.Record.Players[i] = round.Record.Players[i] with { Done = true, UploadOk = ok, Error = error };
            if (!round.Ended || !AllDone(round)) return;
        }
        log.LogInformation("{Player} done with {Session}: {Result}", playerId, sessionId, ok ? "uploaded" : error);
        _ = Task.Run(() => Assemble(round));
    }

    private static bool AllDone(OpenRound round) => round.Record.Players.All(p => p.Done);

    private List<string> RoundsWaitingFor(string playerId)
    {
        lock (_lock)
            return _rounds.Values.Where(r => r.IndexOf(playerId) is var i && i >= 0 && !r.Record.Players[i].Done)
                .Select(r => r.Record.SessionId).ToList();
    }

    private void Assemble(OpenRound round)
    {
        if (Interlocked.Exchange(ref round.Finalized, 1) == 1) return;
        // Stays in _rounds (pending in /status) until its report is recorded; uploads are refused from here on.
        lock (_lock)
        {
            round.Record.Save(round.StagingDir);
            MultiplayerSession.WriteEvents(Path.Combine(round.StagingDir, RoundRecord.EventsFileName), round.Events);
        }
        try
        {
            var (dir, report) = MultiplayerSession.Assemble(round.StagingDir, options.OutRoot, options.Vendor, options.Tolerances);
            lock (_lock)
            {
                _reports.Add(report);
                _rounds.Remove(round.Record.SessionId);
            }
            log.LogInformation("session {Session}: {Result}, {Valid}/{Count} players valid -> {Dir}", report.SessionId,
                report.Pass ? "PASS" : "FAIL", report.ValidPlayers, report.Players.Count, dir);
            foreach (var issue in report.Issues.Concat(report.Players.SelectMany(p => p.Issues)))
                log.LogWarning("{Session}: [{Rule}] {Message}", report.SessionId, issue.Rule, issue.Message);
            SessionAssembled?.Invoke(dir, report);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            log.LogError("cannot assemble {Session}: {Error} (staged in {Dir})", round.Record.SessionId, e.Message, round.StagingDir);
            lock (_lock) _rounds.Remove(round.Record.SessionId);
        }
    }

    private async Task SendAsync(AgentLink link, AgentMessage message)
    {
        await link.SendLock.WaitAsync();
        try
        {
            if (link.Socket.State == WebSocketState.Open)
                await link.Socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, default);
        }
        catch (WebSocketException e)
        {
            log.LogWarning("send to {Player} failed: {Error}", link.Hello.PlayerId, e.Message);
        }
        finally
        {
            link.SendLock.Release();
        }
    }

    private async Task<AgentMessage?> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken stop)
    {
        while (await AgentClient.ReceiveTextAsync(socket, buffer, stop) is { } text)
        {
            try
            {
                return AgentMessage.Parse(text);
            }
            catch (System.Text.Json.JsonException e)
            {
                log.LogWarning("bad agent message: {Error}", e.Message);
            }
        }
        return null;
    }
}
