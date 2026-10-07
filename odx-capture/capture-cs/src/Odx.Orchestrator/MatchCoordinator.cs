using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Session;

namespace Odx.Orchestrator;

public sealed record OrchestratorOptions(string GameProfilePath, int HttpPort = 5180, int TimePort = 12300, int Fps = 30)
{
    /// <summary>How long after a round ends to wait for every participant's Done before sending the round's files anyway.</summary>
    public TimeSpan DoneTimeout { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>
    /// How long an agent that lost its connection keeps its place in a running round (recording on, on its own machine)
    /// before it is stopped there; the agent stops by itself after as long.
    /// </summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The SQLite file keeping rooms and rounds across restarts (<see cref="OrchestratorStore"/>); null keeps none.</summary>
    public string? StatePath { get; init; }
    /// <summary>A room nobody is in is closed after this long (the host closes it otherwise).</summary>
    public TimeSpan EmptyRoomTimeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Every HTTP and WebSocket request must carry "Authorization: Bearer &lt;key&gt;"; null lets anyone in.</summary>
    public string? AccessKey { get; init; }
    /// <summary>ZeroTier controller API (and its token) for rooms with a virtual LAN; null: rooms have no LAN.</summary>
    public Uri? ZeroTierApi { get; init; }
    public string? ZeroTierToken { get; init; }
}

/// <summary>
/// Rooms and their match state machine (spec v3 §2). A host opens a room and shares its invite code; only the room's members
/// are recorded. Rounds start and end only on request: the host's (once every other member is ready), or a game server
/// plugin's round_start / round_end (/rooms/{code}/events). Ready is a signal for the host, never a start by itself. With a countdown, members record from round_countdown and play starts at round_start; a
/// round_end, an unready or a member leaving during the countdown cancels the round (nothing is kept). A permanent
/// player_death stops that player, round_end stops the rest. A connected game server agent records every round's server side
/// (spec v3 §3) along with the players. Everyone keeps its recording; when all are done (or
/// <see cref="OrchestratorOptions.DoneTimeout"/> passes) each participant gets the round's files (<see cref="RoundFiles"/>) to keep
/// with it, and odx merge builds the v3 session from the copied folders.
/// </summary>
public sealed class MatchCoordinator(OrchestratorOptions options, int timePort, ILogger<MatchCoordinator> log) : IDisposable
{
    private sealed class AgentLink(Hello hello, WebSocket socket)
    {
        public Hello Hello { get; } = hello;
        public string Id => Hello.PlayerId;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public string State { get; set; } = "idle";
        public bool Ready { get; set; }
        public Room? Room { get; set; }
        /// <summary>ZeroTier node authorized on the room's LAN.</summary>
        public string? LanNode { get; set; }
    }

    private sealed class Room(string code, string? hostPlayerId, string? server = null)
    {
        public string Code { get; } = code;
        /// <summary>The game server agent recording this room's rounds; null: the only one connected, if just one is.</summary>
        public string? Server { get; } = server;
        /// <summary>Null for a room opened over HTTP (game server plugin, simulator): its round_start/round_end drive it.</summary>
        public string? HostPlayerId { get; } = hostPlayerId;
        public List<AgentLink> Members { get; } = [];
        public string? MatchId { get; set; }
        public string? Map { get; set; }
        public string Game { get; set; } = "";
        public int RoundNumber { get; set; }
        public OpenRound? Current { get; set; }
        /// <summary>Events between rounds (match_start, player_ready…); they open the next round's events.</summary>
        public List<JsonObject> BetweenRounds { get; } = [];
        public List<RoundRecord> Recorded { get; } = [];
        public DateTime EmptySinceUtc { get; set; } = DateTime.UtcNow;
        /// <summary>The room's ZeroTier network (virtual LAN); null without one.</summary>
        public string? LanNetworkId { get; init; }

        public bool IsHost(AgentLink a) => a.Id == HostPlayerId;
    }

    private sealed class OpenRound(Room room, RoundRecord record)
    {
        public Room Room { get; } = room;
        public RoundRecord Record { get; set; } = record;
        public List<JsonObject> Events { get; } = [];
        public bool Counting { get; set; }
        public bool Ended { get; set; }
        public int Finalized;
        /// <summary>The game server agent recording this round; it is done once it kept its side (or failed to).</summary>
        public string? Server { get; init; }
        public bool ServerDone { get; set; }
        /// <summary>Players who lost their connection while recording it, kept until <see cref="OrchestratorOptions.ReconnectGrace"/>.</summary>
        public HashSet<string> Away { get; } = [];

        public int IndexOf(string playerId) => Record.Players.FindIndex(p => p.PlayerId == playerId);
    }

    /// <summary>What a state change asks for once the lock is released.</summary>
    private sealed class Effects
    {
        public List<(AgentLink, AgentMessage)> Sends { get; } = [];
        public List<OpenRound> Ended { get; } = [];
        public List<OpenRound> Cancelled { get; } = [];
        public OpenRound? Countdown { get; set; }
        public HashSet<Room> Changed { get; } = [];
        public List<(string Network, string Node)> LanRevoke { get; } = [];
        public List<string> LanDelete { get; } = [];
    }

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";  // no 0/O, 1/I: read out and typed by people

    private readonly object _lock = new();
    private readonly Dictionary<string, AgentLink> _agents = [];
    private readonly Dictionary<string, Room> _rooms = [];
    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly Dictionary<string, AgentLink> _gameServers = [];
    /// <summary>Agents (players and game servers) whose connection was lost, by id: which loss, for its grace timer.</summary>
    private readonly Dictionary<string, long> _absent = [];
    private long _absences;
    private readonly string _profileJson = File.ReadAllText(options.GameProfilePath);
    private readonly string _profileGame = GameProfile.Load(options.GameProfilePath).GameTitle;
    private long _eventId;
    private ServerClock? _serverClock;
    private readonly ZeroTierApi? _zeroTier = options is { ZeroTierApi: { } api, ZeroTierToken: { } token } ? new ZeroTierApi(api, token) : null;
    private readonly OrchestratorStore? _store = options.StatePath is { } path ? new OrchestratorStore(path) : null;
    /// <summary>Set once the orchestrator stops: timers still running then must not write to the store.</summary>
    private bool _disposed;

    /// <summary>Called with the lock held.</summary>
    private OrchestratorStore? Store => _disposed ? null : _store;

    public int TimePort => timePort;

    /// <summary>Raised with a round's files once they have been sent to its participants.</summary>
    public event Action<RoundFiles>? RoundRecorded;

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
        if (hello.Role == Hello.GameServerRole)
        {
            await HandleGameServerAsync(link, buffer, stop);
            return;
        }
        var resumed = new Effects();
        lock (_lock)
        {
            _agents[hello.PlayerId] = link;
            Resume(link, resumed);
        }
        log.LogInformation("agent {Player} ({Name}, {Version}) connected", hello.PlayerId, hello.Name, hello.ToolVersion);
        await SendAsync(link, new Welcome(timePort, _profileJson, (int)options.ReconnectGrace.TotalMilliseconds));
        await RunAsync(resumed);

        try
        {
            while (!stop.IsCancellationRequested && await ReceiveAsync(socket, buffer, stop) is { } message)
            {
                if (message is Done d)
                {
                    MarkDone(d.SessionId, hello.PlayerId, d.Ok, d.Error);
                    continue;
                }
                if (message is LanMember lan)
                {
                    await AuthorizeLanAsync(link, lan.NodeId, stop);
                    continue;
                }
                string? lanNetwork = null;
                if (message is CreateRoom { Lan: true })
                {
                    lanNetwork = await CreateLanAsync(link, stop);
                    if (lanNetwork is null) continue;
                }
                var effects = new Effects();
                lock (_lock) Handle(link, message, effects, lanNetwork);
                await RunAsync(effects);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            var effects = new Effects();
            long? absence = null;
            lock (_lock)
            {
                if (_agents.GetValueOrDefault(hello.PlayerId) == link)
                {
                    _agents.Remove(hello.PlayerId);
                    _absent[hello.PlayerId] = (long)(absence = ++_absences);
                }
                Leave(link, absence is null ? "reconnected" : "disconnected", effects);
            }
            log.LogWarning("agent {Player} disconnected", hello.PlayerId);
            await RunAsync(effects);
            if (absence is { } a)
                _ = Task.Delay(options.ReconnectGrace).ContinueWith(_ => PlayerGoneAsync(hello.PlayerId, a), TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// After a lost connection, with the lock held: the rounds this agent still records go on (it is no longer away) or get
    /// the Stop it missed; a round the orchestrator no longer knows is stopped too.
    /// </summary>
    private void Resume(AgentLink link, Effects effects)
    {
        _absent.Remove(link.Id);
        foreach (string sid in link.Hello.Recording ?? [])
        {
            link.State = "recording";
            var round = _rounds.GetValueOrDefault(sid);
            bool server = round?.Server == link.Id;
            int player = round?.IndexOf(link.Id) ?? -1;
            if (round is null || !server && player < 0)
            {
                effects.Sends.Add((link, new Stop(sid, "disconnected")));
                continue;
            }
            if (round.Away.Remove(link.Id)) log.LogInformation("{Agent} back in {Session}", link.Id, sid);
            string? missed = server
                ? round.Ended ? round.Record.EndReason ?? GameEvents.RoundEnd : null
                : round.Record.Players[player].StopReason;
            if (missed is not null) effects.Sends.Add((link, new Stop(sid, missed)));
        }
    }

    /// <summary>A player still gone <see cref="OrchestratorOptions.ReconnectGrace"/> after losing the connection.</summary>
    private async Task PlayerGoneAsync(string playerId, long absence)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (_absent.GetValueOrDefault(playerId) != absence) return;
            _absent.Remove(playerId);
            double utc = NtpResponder.NowUnixMs();
            foreach (var round in _rounds.Values.Where(r => r.Away.Remove(playerId)))
            {
                if (!round.Ended) StopPlayer(round, playerId, utc, "disconnected", effects);
                effects.Changed.Add(round.Room);
            }
        }
        await RunAsync(effects);
        foreach (string sid in RoundsWaitingFor(playerId)) MarkDone(sid, playerId, false, "agent disconnected");
    }

    /// <summary>The game server agent: told to start and stop with every round from now on, it answers with Done.</summary>
    private async Task HandleGameServerAsync(AgentLink link, byte[] buffer, CancellationToken stop)
    {
        var resumed = new Effects();
        lock (_lock)
        {
            _gameServers[link.Id] = link;
            Resume(link, resumed);
        }
        log.LogInformation("game server agent {Id} ({Name}, {Version}) connected", link.Id, link.Hello.Name, link.Hello.ToolVersion);
        await SendAsync(link, new Welcome(timePort, _profileJson, (int)options.ReconnectGrace.TotalMilliseconds));
        await RunAsync(resumed);
        try
        {
            while (!stop.IsCancellationRequested && await ReceiveAsync(link.Socket, buffer, stop) is { } message)
                if (message is Done d) MarkServerDone(link.Id, d.SessionId, d.Ok, d.Error);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            long? absence = null;
            lock (_lock)
                if (_gameServers.GetValueOrDefault(link.Id) == link)
                {
                    _gameServers.Remove(link.Id);
                    _absent[link.Id] = (long)(absence = ++_absences);
                }
            log.LogWarning("game server agent {Id} disconnected", link.Id);
            if (absence is { } a)
                _ = Task.Delay(options.ReconnectGrace).ContinueWith(_ => GameServerGone(link.Id, a), TaskScheduler.Default);
        }
    }

    /// <summary>A game server agent still gone <see cref="OrchestratorOptions.ReconnectGrace"/> after losing the connection.</summary>
    private void GameServerGone(string serverId, long absence)
    {
        List<string> waiting;
        lock (_lock)
        {
            if (_absent.GetValueOrDefault(serverId) != absence) return;
            _absent.Remove(serverId);
            waiting = _rounds.Values.Where(r => r.Server == serverId && !r.ServerDone).Select(r => r.Record.SessionId).ToList();
        }
        foreach (string sid in waiting) MarkServerDone(serverId, sid, false, "game server agent disconnected");
    }

    /// <summary>A ZeroTier network for a new room; null (and the host told why) when this server cannot make one.</summary>
    private async Task<string?> CreateLanAsync(AgentLink link, CancellationToken stop)
    {
        if (_zeroTier is null)
        {
            await SendAsync(link, new RoomError("this server has no virtual LAN (ZeroTier) set up"));
            return null;
        }
        try
        {
            return await _zeroTier.CreateNetworkAsync("odx-room", stop);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogError("ZeroTier controller: {Error}", e.Message);
            await SendAsync(link, new RoomError($"cannot create the room's LAN: {e.Message}"));
            return null;
        }
    }

    private async Task AuthorizeLanAsync(AgentLink link, string nodeId, CancellationToken stop)
    {
        string? network;
        lock (_lock)
        {
            network = link.Room?.LanNetworkId;
            if (network is null || !ZeroTierApi.IsNodeId(nodeId)) return;
            link.LanNode = nodeId;
        }
        try
        {
            await _zeroTier!.AuthorizeAsync(network, nodeId, true, stop);
            log.LogInformation("{Player} ({Node}) on the LAN of room {Room}", link.Id, nodeId, link.Room?.Code);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogError("ZeroTier controller: {Error}", e.Message);
            await SendAsync(link, new RoomError($"cannot add you to the room's LAN: {e.Message}"));
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void Handle(AgentLink link, AgentMessage message, Effects effects, string? lanNetwork = null)
    {
        var room = link.Room;
        switch (message)
        {
            case AgentState s:
                link.State = s.State;
                log.LogInformation("agent {Player}: {State} {Detail}", link.Id, s.State, s.Detail);
                if (room is not null) effects.Changed.Add(room);
                break;
            case CreateRoom:
                Leave(link, "left_room", effects);
                var created = new Room(NewCode(), link.Id) { LanNetworkId = lanNetwork };
                _rooms[created.Code] = created;
                log.LogInformation("room {Room} opened by {Player}{Lan}", created.Code, link.Id,
                    lanNetwork is null ? "" : $", LAN {lanNetwork}");
                Join(link, created, effects);
                break;
            case JoinRoom j:
                string code = j.Code.Trim().ToUpperInvariant();
                if (!_rooms.TryGetValue(code, out var target))
                    effects.Sends.Add((link, new RoomError($"no room with invite code {code}")));
                else if (target != room)
                {
                    Leave(link, "left_room", effects);
                    Join(link, target, effects);
                }
                else effects.Changed.Add(target);
                break;
            case LeaveRoom:
                Leave(link, "left_room", effects);
                break;
            case CloseRoom when room is not null && room.IsHost(link):
                Close(room, effects);
                break;
            case ToggleRound when room is not null && room.IsHost(link) && room.Current is null && NotReady(room) is { Count: > 0 } waiting:
                effects.Sends.Add((link, new RoomError($"not ready yet: {string.Join(", ", waiting.Select(a => a.Hello.Name))}")));
                break;
            case ToggleRound t when room is not null && room.IsHost(link):
                Apply(room, room.Current is null
                    ? new JsonObject { ["type"] = GameEvents.RoundStart, ["countdown_ms"] = t.CountdownMs, ["by"] = link.Id }
                    : new JsonObject { ["type"] = GameEvents.RoundEnd, ["by"] = link.Id }, effects);
                break;
            case CloseRoom or ToggleRound:
                effects.Sends.Add((link, new RoomError("only the room's host can do that")));
                break;
            case Ready r when room is not null && link.Ready != r.IsReady:
                SetReady(room, link, r.IsReady, effects);
                break;
        }
    }

    private void Join(AgentLink link, Room room, Effects effects)
    {
        room.Members.Add(link);
        link.Room = room;
        link.Ready = false;
        effects.Changed.Add(room);
        log.LogInformation("{Player} joined room {Room}", link.Id, room.Code);
    }

    /// <summary>Out of the room: a countdown they are in is cancelled, a recording of theirs is stopped.</summary>
    private void Leave(AgentLink link, string reason, Effects effects)
    {
        if (link.Room is not { } room) return;
        room.Members.Remove(link);
        link.Room = null;
        link.Ready = false;
        // "reconnected": this connection died after the agent's new one came in; the new one holds its LAN node and rounds.
        bool replaced = reason == "reconnected";
        if (!replaced && room.LanNetworkId is { } network && link.LanNode is { } node) effects.LanRevoke.Add((network, node));
        link.LanNode = null;
        if (!replaced && room.Current is { } round && round.IndexOf(link.Id) is var i && i >= 0)
        {
            if (round.Counting) Cancel(round, $"player_left:{link.Id}", effects);
            else if (reason == "disconnected" && round.Record.Players[i].StopReason is null)
            {
                round.Away.Add(link.Id);
                log.LogWarning("{Player} lost the connection in {Session}; kept for {Grace} s", link.Id, round.Record.SessionId,
                    options.ReconnectGrace.TotalSeconds);
            }
            else StopPlayer(round, link.Id, NtpResponder.NowUnixMs(), reason, effects);
        }
        if (room.Members.Count == 0) room.EmptySinceUtc = DateTime.UtcNow;
        effects.Changed.Add(room);
        log.LogInformation("{Player} left room {Room} ({Reason})", link.Id, room.Code, reason);
    }

    private void Close(Room room, Effects effects)
    {
        if (room.Current is { Counting: true } counting) Cancel(counting, "room_closed", effects);
        else if (room.Current is not null) EndRound(room, NtpResponder.NowUnixMs(), "round_end", "room_closed", effects);
        foreach (var member in room.Members)
        {
            member.Room = null;
            member.Ready = false;
            member.LanNode = null;
            effects.Sends.Add((member, new RoomClosed(room.Code)));
        }
        room.Members.Clear();
        _rooms.Remove(room.Code);
        if (room.LanNetworkId is { } network) effects.LanDelete.Add(network);
        log.LogInformation("room {Room} closed", room.Code);
    }

    private string NewCode()
    {
        string code;
        do code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 6));
        while (_rooms.ContainsKey(code));
        return code;
    }

    /// <summary>A room without a host, for a game server plugin or the simulator; returns its invite code.</summary>
    /// <param name="server">The game server agent that records its rounds; null: the only one connected.</param>
    public string OpenRoom(string? server = null)
    {
        lock (_lock)
        {
            var room = new Room(NewCode(), null, server);
            _rooms[room.Code] = room;
            var opened = new Effects();
            opened.Changed.Add(room);
            Persist(opened);
            log.LogInformation("room {Room} opened (no host{Server})", room.Code, server is null ? "" : $", game server {server}");
            return room.Code;
        }
    }

    /// <summary>Called with the lock held.</summary>
    private AgentLink? ServerFor(Room room) =>
        room.Server is { } id ? _gameServers.GetValueOrDefault(id) : _gameServers.Count == 1 ? _gameServers.Values.Single() : null;

    /// <summary>Applies one game server event to a room and returns it stamped; null when there is no such room.</summary>
    /// <exception cref="FormatException">The event has no "type".</exception>
    public async Task<JsonObject?> PostEventAsync(string code, JsonObject e)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (!_rooms.TryGetValue(code.ToUpperInvariant(), out var room)) return null;
            Apply(room, e, effects);
        }
        await RunAsync(effects);
        return e;
    }

    /// <summary>Closes rooms that stayed empty for <see cref="OrchestratorOptions.EmptyRoomTimeout"/>.</summary>
    public async Task SweepRoomsAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_lock)
                foreach (var room in _rooms.Values.Where(r => r.Members.Count == 0 && r.Current is null &&
                                                              DateTime.UtcNow - r.EmptySinceUtc > options.EmptyRoomTimeout).ToList())
                {
                    _rooms.Remove(room.Code);
                    Store?.CloseRoom(room.Code);
                    if (room.LanNetworkId is { } network) _ = DeleteLanAsync(network);
                    log.LogInformation("room {Room} closed after staying empty", room.Code);
                }
        }
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

    public JsonObject Status()
    {
        lock (_lock)
            return new JsonObject
            {
                ["rooms"] = new JsonArray(_rooms.Values.Select(r => (JsonNode)RoomStatus(r)).ToArray()),
                ["agents_outside_rooms"] = new JsonArray(_agents.Values.Where(a => a.Room is null)
                    .Select(a => (JsonNode)a.Id).ToArray()),
                ["game_servers"] = new JsonArray(_gameServers.Values.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id, ["name"] = s.Hello.Name,
                }).ToArray()),
            };
    }

    /// <summary>
    /// Takes back what the store kept from before a restart: the open rooms (empty until their members reconnect) and their
    /// unfinished rounds. Everyone in those rounds counts as away, with the usual grace to come back; a round that was
    /// counting down is cancelled, since its countdown cannot be resumed.
    /// </summary>
    public void Restore()
    {
        if (_store is null) return;
        var gone = new List<(string Id, long Absence, bool Server)>();
        var ended = new List<OpenRound>();
        lock (_lock)
        {
            _eventId = _store.NextEventId;
            foreach (var r in _store.OpenRooms())
            {
                var room = new Room(r.Code, r.Host, r.Server)
                    { LanNetworkId = r.LanNetworkId, MatchId = r.MatchId, Map = r.Map, Game = r.Game, RoundNumber = r.RoundNumber };
                room.BetweenRounds.AddRange(r.BetweenRounds);
                _rooms[room.Code] = room;
            }
            foreach (var s in _store.UnfinishedRounds())
            {
                if (!_rooms.TryGetValue(s.Room, out var room)) continue;
                if (s.Counting)
                {
                    _store.SaveRound(s with { State = "cancelled" });
                    room.RoundNumber = s.Record.Round - 1;
                    continue;
                }
                var round = new OpenRound(room, s.Record) { Server = s.Server, ServerDone = s.ServerDone, Ended = s.State == "ended" };
                round.Events.AddRange(s.Events);
                _rounds[s.SessionId] = round;
                if (round.Ended) ended.Add(round);
                else room.Current = round;
                foreach (var p in round.Record.Players.Where(p => !p.Done))
                {
                    if (p.StopReason is null) round.Away.Add(p.PlayerId);
                    gone.Add((p.PlayerId, _absent[p.PlayerId] = ++_absences, false));
                }
                if (round.Server is { } server && !round.ServerDone) gone.Add((server, _absent[server] = ++_absences, true));
            }
            log.LogInformation("restored {Rooms} room(s) and {Rounds} unfinished round(s) from {Store}", _rooms.Count, _rounds.Count,
                options.StatePath);
        }
        foreach (var (id, absence, server) in gone)
            _ = Task.Delay(options.ReconnectGrace).ContinueWith(_ => server ? Task.Run(() => GameServerGone(id, absence)) : PlayerGoneAsync(id, absence),
                TaskScheduler.Default).Unwrap();
        foreach (var round in ended)
            _ = Task.Delay(options.DoneTimeout).ContinueWith(_ => FinishAsync(round), TaskScheduler.Default).Unwrap();
    }

    /// <summary>Called with the lock held, after every change.</summary>
    private void Persist(Effects effects)
    {
        if (Store is not { } store) return;
        store.NextEventId = _eventId;
        // Cancelled first: a round_start during a countdown cancels the round and starts one with the same session id.
        foreach (var round in effects.Cancelled) Save(round, "cancelled");
        foreach (var room in effects.Changed)
        {
            if (!_rooms.ContainsKey(room.Code))
            {
                store.CloseRoom(room.Code);
                continue;
            }
            store.SaveRoom(new StoredRoom(room.Code, room.HostPlayerId, room.Server, room.LanNetworkId, room.MatchId, room.Map,
                room.Game, room.RoundNumber, room.BetweenRounds));
            foreach (var round in _rounds.Values.Where(r => r.Room == room)) Save(round);
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void Save(OpenRound round, string? state = null) =>
        Store?.SaveRound(new StoredRound(round.Record.SessionId, round.Room.Code, state ?? (round.Ended ? "ended" : "open"),
            round.Counting, round.Server, round.ServerDone, round.Record, round.Events));

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _store?.Dispose();
        }
    }

    /// <summary>Null when there is no room with this code.</summary>
    public JsonObject? RoomStatus(string code)
    {
        lock (_lock) return _rooms.TryGetValue(code.ToUpperInvariant(), out var room) ? RoomStatus(room) : null;
    }

    private JsonObject RoomStatus(Room room) => new()
    {
        ["code"] = room.Code,
        ["host"] = room.HostPlayerId,
        ["server"] = room.Server,
        ["match_id"] = room.MatchId,
        ["game"] = room.Game,
        ["current_session"] = room.Current?.Record.SessionId,
        ["countdown"] = room.Current?.Counting ?? false,
        ["pending_sessions"] = new JsonArray(_rounds.Values.Where(r => r.Room == room)
            .Select(r => (JsonNode)r.Record.SessionId).ToArray()),
        ["members"] = new JsonArray(room.Members.Select(a => (JsonNode)new JsonObject
        {
            ["player_id"] = a.Id,
            ["name"] = a.Hello.Name,
            ["version"] = a.Hello.ToolVersion,
            ["state"] = a.State,
            ["ready"] = a.Ready,
        }).ToArray()),
        ["sessions"] = new JsonArray(room.Recorded.Select(r => (JsonNode)new JsonObject
        {
            ["session_id"] = r.SessionId, ["players"] = r.Players.Count, ["kept"] = r.Players.Count(p => p.Kept),
        }).ToArray()),
    };

    private void SetReady(Room room, AgentLink link, bool ready, Effects effects)
    {
        link.Ready = ready;
        effects.Changed.Add(room);
        Apply(room, new JsonObject { ["type"] = ready ? "player_ready" : "player_unready", ["player"] = link.Id,
            ["name"] = link.Hello.Name }, effects);
        if (!ready && room.Current is { Counting: true } counting && counting.IndexOf(link.Id) >= 0)
            Cancel(counting, $"player_unready:{link.Id}", effects);
    }

    /// <summary>Members other than the host who are not ready (or still busy with the last round).</summary>
    private static List<AgentLink> NotReady(Room room) =>
        room.Members.Where(a => !room.IsHost(a) && !(a.Ready && a.State is "idle" or "armed")).ToList();

    /// <summary>Called with the lock held.</summary>
    private void Apply(Room room, JsonObject e, Effects effects)
    {
        e["utc_ms"] ??= NtpResponder.NowUnixMs();
        double utc = GameEvents.UtcMs(e);
        string type = GameEvents.Type(e);
        Stamp(e, utc);
        effects.Changed.Add(room);

        if (room.Current is { Counting: true } counting &&
            type is GameEvents.RoundStart or GameEvents.RoundEnd or GameEvents.MatchStart or GameEvents.MatchEnd)
        {
            Cancel(counting, EndReason(e), effects);
            if (type == GameEvents.RoundEnd) return;  // the cancel is the whole story of this round
        }
        switch (type)
        {
            case GameEvents.MatchStart:
                if (room.Current is not null) EndRound(room, utc, "abort", "abort", effects);
                StartMatch(room, e);
                break;
            case GameEvents.RoundStart:
                if (room.Current is not null) EndRound(room, utc, "abort", "abort", effects);
                if (room.MatchId is null) StartMatch(room, e);
                StartRound(room, e, utc, effects);
                break;
            case GameEvents.PlayerDeath when room.Current is not null && e["permanent"]?.GetValue<bool>() == true &&
                                             GameEvents.Text(e, "player") is { } player:
                StopPlayer(room.Current, player, utc, "permanent_death", effects);
                break;
        }
        AddToRound(room, e);
        if (type is GameEvents.RoundEnd or GameEvents.MatchEnd && room.Current is not null)
            EndRound(room, utc, "round_end", EndReason(e), effects);
        if (type == GameEvents.MatchEnd) room.MatchId = null;
    }

    private void Stamp(JsonObject e, double utc)
    {
        e["event_id"] = ++_eventId;
        e["utc"] = SessionBuilder.IsoUtc(utc);
        e["server_time_us"] = (long)Math.Round(utc * 1000);
    }

    private static void AddToRound(Room room, JsonObject e)
    {
        if (room.Current is not { } round)
        {
            room.BetweenRounds.Add(e);
            return;
        }
        e["session_id"] = round.Record.SessionId;
        e["round_index"] = round.Record.Round;
        round.Events.Add(e);
    }

    private void StartMatch(Room room, JsonObject e)
    {
        room.MatchId = GameEvents.Text(e, "match_id") ??
                       $"{room.Code}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        room.Game = GameEvents.Text(e, "game") ?? _profileGame;
        room.Map = GameEvents.Text(e, "map");
        room.RoundNumber = 0;
        log.LogInformation("room {Room}: match {Match}, {Game} {Map}", room.Code, room.MatchId, room.Game, room.Map);
    }

    /// <summary>With a countdown, <paramref name="e"/> becomes round_countdown and round_start follows when play starts.</summary>
    private void StartRound(Room room, JsonObject e, double utc, Effects effects)
    {
        room.RoundNumber = e["round"]?.GetValue<int>() ?? room.RoundNumber + 1;
        string sid = $"{room.MatchId}-r{room.RoundNumber:D2}";
        var players = room.Members.Where(a => a.State is "idle" or "armed").ToList();
        foreach (var busy in room.Members.Except(players))
            log.LogWarning("{Player} is {State}, left out of {Session}", busy.Id, busy.State, sid);
        int countdownMs = e["countdown_ms"]?.GetValue<int>() ?? 0;
        var record = new RoundRecord(sid, room.MatchId!, room.Game, room.Map, room.RoundNumber, utc + countdownMs, null, timePort,
            players.Select(a => new RoundPlayer(a.Id, a.Hello.Name, ToolVersion: a.Hello.ToolVersion,
                MachineId: a.Hello.MachineId, SteamId: a.Hello.SteamId)).ToList(), countdownMs,
            ServerClock: Volatile.Read(ref _serverClock));
        var server = ServerFor(room);
        if (server is null && (room.Server is not null || _gameServers.Count > 1))
            log.LogWarning("{Session}: no game server records it (room bound to {Server}; connected: {Connected})", sid,
                room.Server ?? "none", string.Join(", ", _gameServers.Keys));
        var round = new OpenRound(room, record) { Counting = countdownMs > 0, Server = server?.Id };
        round.Events.AddRange(room.BetweenRounds);
        room.BetweenRounds.Clear();
        room.Current = round;
        _rounds[sid] = round;
        if (countdownMs == 0) ClearReady(room);
        foreach (var a in players)
        {
            a.State = "starting";
            effects.Sends.Add((a, new Arm(sid, _profileJson, options.Fps)));
            effects.Sends.Add((a, new Start(sid, countdownMs)));
        }
        if (server is not null) effects.Sends.Add((server, new Start(sid, countdownMs)));
        if (countdownMs > 0)
        {
            e["type"] = "round_countdown";
            e["starts_in_ms"] = countdownMs;
            effects.Countdown = round;
        }
        log.LogInformation("round {Session}: {Count} player(s){Countdown}", sid, players.Count,
            countdownMs > 0 ? $", play starts in {countdownMs / 1000.0:F0} s" : "");
    }

    private async Task CountdownEndedAsync(OpenRound round)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (round.Room.Current != round || !round.Counting) return;
            round.Counting = false;
            ClearReady(round.Room);
            var e = new JsonObject
            {
                ["type"] = GameEvents.RoundStart,
                ["utc_ms"] = round.Record.StartUtcMs,
                ["players"] = new JsonArray(round.Record.Players.Select(p => (JsonNode)p.PlayerId).ToArray()),
            };
            Stamp(e, round.Record.StartUtcMs);
            AddToRound(round.Room, e);
            effects.Changed.Add(round.Room);
        }
        log.LogInformation("round {Session}: play started", round.Record.SessionId);
        await RunAsync(effects);
    }

    /// <summary>Ready is for the next round once play starts; until then an unready cancels the countdown.</summary>
    private static void ClearReady(Room room)
    {
        foreach (var a in room.Members) a.Ready = false;
    }

    /// <summary>Drops a round during its countdown: everyone stops, nothing goes to a merge, its number is used again.</summary>
    private void Cancel(OpenRound round, string reason, Effects effects)
    {
        var room = round.Room;
        double utc = NtpResponder.NowUnixMs();
        round.Counting = false;
        round.Ended = true;
        room.Current = null;
        _rounds.Remove(round.Record.SessionId);
        room.RoundNumber = round.Record.Round - 1;
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, Stop.Cancelled, effects);
        StopServer(round, Stop.Cancelled, effects);
        var e = new JsonObject { ["type"] = "round_cancelled", ["utc_ms"] = utc, ["cancelled_session_id"] = round.Record.SessionId,
            ["reason"] = reason };
        Stamp(e, utc);
        room.BetweenRounds.Add(e);
        effects.Cancelled.Add(round);
        effects.Changed.Add(room);
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

    private void StopServer(OpenRound round, string reason, Effects effects)
    {
        if (round.Server is { } id && _gameServers.TryGetValue(id, out var server))
            effects.Sends.Add((server, new Stop(round.Record.SessionId, reason)));
    }

    /// <param name="playerReason">Stop reason of every player still recording.</param>
    /// <param name="endReason">Why the round ended, for session.json.</param>
    private void EndRound(Room room, double utc, string playerReason, string endReason, Effects effects)
    {
        var round = room.Current!;
        room.Current = null;
        round.Ended = true;
        round.Record = round.Record with { EndUtcMs = utc, EndReason = endReason };
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, playerReason, effects);
        StopServer(round, playerReason, effects);
        effects.Ended.Add(round);
        effects.Changed.Add(room);
    }

    /// <summary>The game's own "reason", else "player_stop:&lt;id&gt;" when a host ended it, else the event type.</summary>
    private static string EndReason(JsonObject e) =>
        GameEvents.Text(e, "reason") ?? (GameEvents.Text(e, "by") is { } by ? $"player_stop:{by}" : GameEvents.Type(e));

    private async Task RunAsync(Effects effects)
    {
        List<(AgentLink, AgentMessage)> updates;
        lock (_lock)
        {
            Persist(effects);
            updates = effects.Changed.Where(r => _rooms.ContainsKey(r.Code)).SelectMany(r =>
            {
                var update = new RoomUpdate(r.Code, r.HostPlayerId,
                    r.Members.Select(a => new RoomMember(a.Id, a.Hello.Name, a.Ready, a.State)).ToList(),
                    r.Current?.Record.SessionId, r.Current?.Counting ?? false, r.LanNetworkId);
                return r.Members.Select(a => (a, (AgentMessage)update));
            }).ToList();
        }
        foreach (var (link, message) in effects.Sends.Concat(updates)) await SendAsync(link, message);
        foreach (var (network, node) in effects.LanRevoke)
        {
            try
            {
                await _zeroTier!.AuthorizeAsync(network, node, false, default);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("ZeroTier controller: {Error}", e.Message);
            }
        }
        foreach (string network in effects.LanDelete) await DeleteLanAsync(network);
        foreach (var round in effects.Ended)
        {
            bool done;
            lock (_lock) done = AllDone(round);
            if (done) _ = FinishAsync(round);
            else _ = Task.Delay(options.DoneTimeout).ContinueWith(_ => FinishAsync(round), TaskScheduler.Default).Unwrap();
        }
        foreach (var round in effects.Cancelled)
        {
            Interlocked.Exchange(ref round.Finalized, 1);
            RoundCancelled?.Invoke(round.Record.SessionId);
        }
        if (effects.Countdown is { } counting)
            _ = Task.Delay(counting.Record.CountdownMs).ContinueWith(_ => CountdownEndedAsync(counting), TaskScheduler.Default).Unwrap();
    }

    private async Task DeleteLanAsync(string network)
    {
        try
        {
            await _zeroTier!.DeleteNetworkAsync(network, default);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("ZeroTier controller: {Error}", e.Message);
        }
    }

    private void MarkDone(string sessionId, string playerId, bool ok, string? error)
    {
        OpenRound? round;
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out round) || round.IndexOf(playerId) is var i && i < 0) return;
            round.Record.Players[i] = round.Record.Players[i] with { Done = true, Kept = ok, Error = error };
            Save(round);
            if (!round.Ended || !AllDone(round)) return;
        }
        log.LogInformation("{Player} done with {Session}: {Result}", playerId, sessionId, ok ? "kept" : error);
        _ = FinishAsync(round);
    }

    private void MarkServerDone(string serverId, string sessionId, bool ok, string? error)
    {
        OpenRound? round;
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out round) || round.Server != serverId || round.ServerDone) return;
            round.ServerDone = true;
            Save(round);
            if (!ok) log.LogWarning("game server agent {Id}, {Session}: {Error}", serverId, sessionId, error);
            if (!round.Ended || !AllDone(round)) return;
        }
        _ = FinishAsync(round);
    }

    /// <summary>Called with the lock held.</summary>
    private static bool AllDone(OpenRound round) => round.Record.Players.All(p => p.Done) && (round.Server is null || round.ServerDone);

    private List<string> RoundsWaitingFor(string playerId)
    {
        lock (_lock)
            return _rounds.Values.Where(r => r.IndexOf(playerId) is var i && i >= 0 && !r.Record.Players[i].Done)
                .Select(r => r.Record.SessionId).ToList();
    }

    private async Task FinishAsync(OpenRound round)
    {
        if (Interlocked.Exchange(ref round.Finalized, 1) == 1) return;
        // Stays in _rounds (pending in the status) until its files are sent.
        RoundRecord record;
        List<JsonObject> events;
        List<AgentLink> links;
        lock (_lock)
        {
            record = round.Record;
            events = round.Events.Select(e => e.DeepClone().AsObject()).ToList();
            links = record.Players.Select(p => _agents.GetValueOrDefault(p.PlayerId)).OfType<AgentLink>().ToList();
            if (round.Server is { } id && _gameServers.TryGetValue(id, out var server)) links.Add(server);
        }
        var files = new RoundFiles(record.SessionId, record.ToJson(), RoundRecord.EventsJsonl(events), _profileJson);
        foreach (var link in links) await SendAsync(link, files);
        lock (_lock)
        {
            round.Room.Recorded.Add(record);
            Save(round, "finished");
            _rounds.Remove(record.SessionId);
        }
        log.LogInformation("round {Session}: {Kept}/{Count} players kept their recording", record.SessionId,
            record.Players.Count(p => p.Kept), record.Players.Count);
        RoundRecorded?.Invoke(files);
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
            log.LogWarning("send to {Player} failed: {Error}", link.Id, e.Message);
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
