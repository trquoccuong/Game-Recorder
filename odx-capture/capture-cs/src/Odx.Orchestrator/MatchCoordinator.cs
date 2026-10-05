using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Session;

namespace Odx.Orchestrator;

/// <param name="OutRoot">Sessions go to &lt;OutRoot&gt;/&lt;vendor&gt;/…; uploads are staged in &lt;OutRoot&gt;/.staging/&lt;session&gt;/.</param>
/// <param name="DoneTimeout">How long after a round ends to wait for uploads before assembling with what arrived.</param>
public sealed record OrchestratorOptions(string OutRoot, string GameProfilePath, string Vendor = "odx", int HttpPort = 5180,
    int TimePort = 12300, int Fps = 30)
{
    public SyncTolerances Tolerances { get; init; } = SyncTolerances.Default;
    public TimeSpan DoneTimeout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The match state machine (spec v3 §2): round_start arms and starts every idle agent, a permanent player_death stops
/// that player, round_end stops the rest; when all players have uploaded (or <see cref="OrchestratorOptions.DoneTimeout"/>
/// passes) the round is assembled into a v3 session.
/// </summary>
public sealed class MatchCoordinator(OrchestratorOptions options, int timePort, ILogger<MatchCoordinator> log)
{
    private sealed class AgentLink(Hello hello, WebSocket socket)
    {
        public Hello Hello { get; } = hello;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public string State { get; set; } = "idle";
    }

    private sealed class OpenRound(RoundRecord record, string stagingDir)
    {
        public RoundRecord Record { get; set; } = record;
        public string StagingDir { get; } = stagingDir;
        public List<JsonObject> Events { get; } = [];
        public bool Ended { get; set; }
        public int Finalized;

        public int IndexOf(string playerId) => Record.Players.FindIndex(p => p.PlayerId == playerId);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentLink> _agents = [];
    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly List<SessionReport> _reports = [];
    private readonly string _profileJson = File.ReadAllText(options.GameProfilePath);
    private readonly string _profileGame = GameProfile.Load(options.GameProfilePath).GameTitle;
    private string? _matchId, _map;
    private string _game = "";
    private int _roundNumber;
    private OpenRound? _current;

    public int TimePort => timePort;

    /// <summary>Raised with the session folder and its report each time a round has been assembled.</summary>
    public event Action<string, SessionReport>? SessionAssembled;

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
        log.LogInformation("agent {Player} ({Name}, {Version}) connected", hello.PlayerId, hello.Name, hello.ToolVersion);
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
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
                if (_agents.GetValueOrDefault(hello.PlayerId) == link) _agents.Remove(hello.PlayerId);
            log.LogWarning("agent {Player} disconnected", hello.PlayerId);
            foreach (string sid in RoundsWaitingFor(hello.PlayerId))
                MarkDone(sid, hello.PlayerId, false, "agent disconnected");
        }
    }

    /// <summary>Applies one game server event and returns it with its server UTC (stamped now when it had none).</summary>
    /// <exception cref="FormatException">The event has no "type".</exception>
    public async Task<JsonObject> PostEventAsync(JsonObject e)
    {
        e["utc_ms"] ??= NtpResponder.NowUnixMs();
        double utc = GameEvents.UtcMs(e);
        string type = GameEvents.Type(e);
        var sends = new List<(AgentLink, AgentMessage)>();
        OpenRound? ended = null;
        lock (_lock)
        {
            switch (type)
            {
                case GameEvents.MatchStart:
                    if (_current is not null) ended = EndRound(utc, "abort", sends);
                    StartMatch(e);
                    break;
                case GameEvents.RoundStart:
                    if (_current is not null) ended = EndRound(utc, "abort", sends);
                    if (_matchId is null) StartMatch(e);
                    StartRound(e, utc, sends);
                    break;
                case GameEvents.PlayerDeath when _current is not null && e["permanent"]?.GetValue<bool>() == true &&
                                                 GameEvents.Text(e, "player") is { } player:
                    StopPlayer(_current, player, utc, "permanent_death", sends);
                    break;
            }
            _current?.Events.Add(e);
            if (type is GameEvents.RoundEnd or GameEvents.MatchEnd && _current is not null) ended = EndRound(utc, "round_end", sends);
            if (type == GameEvents.MatchEnd) _matchId = null;
        }
        foreach (var (link, message) in sends) await SendAsync(link, message);
        if (ended is not null) AfterRoundEnded(ended);
        return e;
    }

    /// <summary>Where an upload goes, or null when the session/player is unknown or the name is not a plain file name.</summary>
    public string? UploadPath(string sessionId, string playerId, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..") return null;
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out var round) || round.IndexOf(playerId) < 0) return null;
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
                ["player_id"] = a.Hello.PlayerId, ["name"] = a.Hello.Name, ["version"] = a.Hello.ToolVersion, ["state"] = a.State,
            }).ToArray());
            return new JsonObject
            {
                ["match_id"] = _matchId,
                ["game"] = _game,
                ["current_session"] = _current?.Record.SessionId,
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

    private void StartMatch(JsonObject e)
    {
        _matchId = GameEvents.Text(e, "match_id") ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _game = GameEvents.Text(e, "game") ?? _profileGame;
        _map = GameEvents.Text(e, "map");
        _roundNumber = 0;
        log.LogInformation("match {Match}: {Game} {Map}", _matchId, _game, _map);
    }

    private void StartRound(JsonObject e, double utc, List<(AgentLink, AgentMessage)> sends)
    {
        _roundNumber = e["round"]?.GetValue<int>() ?? _roundNumber + 1;
        string sid = $"{_matchId}-r{_roundNumber:D2}";
        var ready = _agents.Values.Where(a => a.State is "idle" or "armed").ToList();
        foreach (var busy in _agents.Values.Except(ready))
            log.LogWarning("{Player} is {State}, left out of {Session}", busy.Hello.PlayerId, busy.State, sid);
        var record = new RoundRecord(sid, _matchId!, _game, _map, _roundNumber, utc, null, timePort,
            ready.Select(a => new RoundPlayer(a.Hello.PlayerId, a.Hello.Name)).ToList());
        string staging = Path.Combine(options.OutRoot, ".staging", sid);
        Directory.CreateDirectory(staging);
        _current = new OpenRound(record, staging);
        _rounds[sid] = _current;
        foreach (var a in ready)
        {
            a.State = "starting";
            sends.Add((a, new Arm(sid, _profileJson, options.Fps)));
            sends.Add((a, new Start(sid)));
        }
        log.LogInformation("round {Session}: {Count} player(s)", sid, ready.Count);
    }

    private void StopPlayer(OpenRound round, string playerId, double utc, string reason, List<(AgentLink, AgentMessage)> sends)
    {
        int i = round.IndexOf(playerId);
        if (i < 0 || round.Record.Players[i].StopReason is not null) return;
        round.Record.Players[i] = round.Record.Players[i] with { StopReason = reason, StopUtcMs = utc };
        if (_agents.TryGetValue(playerId, out var link)) sends.Add((link, new Stop(round.Record.SessionId, reason)));
        log.LogInformation("stop {Player} in {Session}: {Reason}", playerId, round.Record.SessionId, reason);
    }

    private OpenRound EndRound(double utc, string reason, List<(AgentLink, AgentMessage)> sends)
    {
        var round = _current!;
        _current = null;
        round.Ended = true;
        round.Record = round.Record with { EndUtcMs = utc };
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, reason, sends);
        return round;
    }

    private void AfterRoundEnded(OpenRound round)
    {
        if (AllDone(round)) _ = Task.Run(() => Assemble(round));
        else _ = Task.Delay(options.DoneTimeout).ContinueWith(_ => Assemble(round), TaskScheduler.Default);
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
        lock (_lock)
        {
            _rounds.Remove(round.Record.SessionId);
            round.Record.Save(round.StagingDir);
            MultiplayerSession.WriteEvents(Path.Combine(round.StagingDir, RoundRecord.EventsFileName), round.Events);
        }
        try
        {
            var (dir, report) = MultiplayerSession.Assemble(round.StagingDir, options.OutRoot, options.Vendor, options.Tolerances);
            lock (_lock) _reports.Add(report);
            log.LogInformation("session {Session}: {Result}, {Valid}/{Count} players valid -> {Dir}", report.SessionId,
                report.Pass ? "PASS" : "FAIL", report.ValidPlayers, report.Players.Count, dir);
            foreach (var issue in report.Issues.Concat(report.Players.SelectMany(p => p.Issues)))
                log.LogWarning("{Session}: [{Rule}] {Message}", report.SessionId, issue.Rule, issue.Message);
            SessionAssembled?.Invoke(dir, report);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            log.LogError("cannot assemble {Session}: {Error} (staged in {Dir})", round.Record.SessionId, e.Message, round.StagingDir);
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
