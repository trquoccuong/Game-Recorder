using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Odx.Core;
using Odx.Orchestrator;
using Odx.Session;
using Xunit;

namespace Odx.Tests;

/// <summary>Orchestrator + real AgentClients over loopback; the capture is faked with a copy of the fixture session.</summary>
public sealed class OrchestratorTests : IAsyncLifetime
{
    private sealed class FakeCapture : ICaptureRun
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeCapture(CaptureOptions options, string? template)
        {
            SessionDir = Path.Combine(options.OutputRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SessionDir);
            if (template is not null)
            {
                SessionFixtures.CopySession(template, SessionDir);
                double now = NtpResponder.NowUnixMs();
                File.WriteAllLines(Path.Combine(SessionDir, "video_frames.csv"),
                    ["frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate",
                     .. Enumerable.Range(0, SessionFixtures.NFrames).Select(i =>
                         string.Create(CultureInfo.InvariantCulture, $"{i},{1000 + i * 1000.0 / SessionFixtures.Fps},{1000 + i * 1000.0 / SessionFixtures.Fps},0"))]);
                File.WriteAllLines(Path.Combine(SessionDir, "ntp_samples.csv"),
                    [NtpSampleWriter.Header,
                     string.Create(CultureInfo.InvariantCulture, $"1000,{now},1,0,{options.NtpServer}"),
                     string.Create(CultureInfo.InvariantCulture, $"2000,{now + 1000},1,0,{options.NtpServer}")]);
            }
            Completion = _stopped.Task.ContinueWith(_ => new RunResult(SessionDir, template is not null, null, 0, [], null));
        }

        public string SessionDir { get; }
        public Task Started => Task.CompletedTask;
        public Task CaptureStopped => _stopped.Task;
        public Task<RunResult> Completion { get; }
        public void Stop() => _stopped.TrySetResult();
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "odx-orch-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private MatchCoordinator _coordinator = null!;
    private readonly List<AgentClient> _agents = [];
    private const int ReadyCountdownMs = 1500;
    /// <summary>A room without a host (as a game server plugin opens one); most tests use it.</summary>
    private string _room = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        string profile = Path.Combine(_root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {"W": "Forward"}}
            """);
        _app = OrchestratorHost.Build(new OrchestratorOptions(Path.Combine(_root, "out"), profile, HttpPort: 0, TimePort: 0,
            Fps: SessionFixtures.Fps)
        {
            // The fixture session is 320x180 and 2 s long.
            Tolerances = GateTolerances.Default with { MinWidth = SessionFixtures.Width, MinHeight = SessionFixtures.Height, MinDurationMs = 1000 },
            ReadyCountdownMs = ReadyCountdownMs,
        }, "127.0.0.1");
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _coordinator = _app.Services.GetRequiredService<MatchCoordinator>();
        using var opened = await _http.PostAsync("/rooms", null);
        _room = (string)(await opened.Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private Task<(string Dir, SessionReport Report)> NextSession()
    {
        var tcs = new TaskCompletionSource<(string, SessionReport)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.SessionAssembled += (dir, report) => tcs.TrySetResult((dir, report));
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Agents joining the hostless test room.</summary>
    private List<TaskCompletionSource> ConnectAgents(string? template, params string[] players) =>
        players.Select(p => Connect(template, p, _room).Started).ToList();

    /// <param name="room">Invite code to join; null opens a new room with this player as host.</param>
    private (AgentClient Agent, TaskCompletionSource Started) Connect(string? template, string player, string? room)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentClient(new AgentOptions(_http.BaseAddress!, player, player.ToUpperInvariant(),
            Path.Combine(_root, "agent-" + player), null, RoomCode: room, CreateRoom: room is null), options =>
        {
            try
            {
                return template is null ? throw new InvalidOperationException("no game window") : new FakeCapture(options, template);
            }
            finally
            {
                started.TrySetResult();
            }
        });
        _agents.Add(agent);
        _ = agent.RunAsync(_stop.Token);
        return (agent, started);
    }

    /// <summary>Opens a room hosted by <paramref name="host"/> and returns its invite code.</summary>
    private async Task<(AgentClient Host, TaskCompletionSource Started, string Code)> HostRoom(string host)
    {
        var (agent, started) = Connect(null, host, null);
        for (int i = 0; i < 100 && agent.Room is null; i++) await Task.Delay(50);
        return (agent, started, agent.Room?.Code ?? throw new TimeoutException("no room opened"));
    }

    private async Task WaitForAgents(int count, string? room = null)
    {
        for (int i = 0; i < 100; i++)
        {
            var status = await _http.GetFromJsonAsync<JsonObject>($"/rooms/{room ?? _room}");
            if (status!["members"]!.AsArray().Count(a => (string?)a!["state"] == "idle") == count) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("agents did not join");
    }

    private Task<JsonObject> RoomStatus(string? room = null) => _http.GetFromJsonAsync<JsonObject>($"/rooms/{room ?? _room}")!;

    private Task Post(object e) =>
        _http.PostAsJsonAsync($"/rooms/{_room}/events", e).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    [SkippableFact]
    public async Task RoundBecomesV3SessionWithPerPlayerStopsAndFrameIndexedEvents()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "needs ffmpeg + ffprobe");
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        var started = ConnectAgents(template, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();

        await Post(new { type = "match_start", match_id = "m1", map = "de_dust2" });
        await Post(new { type = "round_start", countdown_ms = 1000 });
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        await Post(new { type = "kill", attacker = "p1", victim = "p2", weapon = "ak47" });
        await Post(new { type = "player_death", player = "p2", permanent = true });
        await Task.Delay(1200);  // past the 1 s countdown: a round_end during it would cancel the round
        await Post(new { type = "round_end", winner = "t" });

        var (dir, report) = await session;
        Assert.True(report.Pass, string.Join("; ", report.Players.SelectMany(p => p.Issues).Select(i => i.Message)));
        Assert.Equal("m1-r01", report.SessionId);
        Assert.Equal(2, report.ValidPlayers);
        Assert.EndsWith(Path.Combine("odx", DateTime.UtcNow.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture), "synthetic", "m1-r01"), dir);

        var serverEvents = MultiplayerSession.ReadEvents(Path.Combine(dir, "server", "events.jsonl")).Select(GameEvents.Type);
        Assert.Equal(["match_start", "round_countdown", "kill", "player_death", "round_start", "round_end"], serverEvents);
        Assert.Equal("permanent_death", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p2", "player.json")))!["stop_reason"]);
        var p1 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p1", "player.json")))!;
        Assert.Equal("round_end", (string?)p1["stop_reason"]);
        // The fake's 2 s of frames start when it is created, right after the round_start with a 1 s countdown.
        Assert.InRange((int)p1["round_start_frame"]!, 40, 61);
        Assert.InRange((double)p1["round_start_video_time_ms"]!, 600, 1000);
        Assert.Equal(2, (int)p1["clock_sync"]!["sample_count"]!);
        Assert.Equal("server_clock - player_clock", (string?)p1["clock_sync"]!["offset_definition"]);
        foreach (string p in new[] { "p1", "p2" })
        {
            Assert.True(File.Exists(Path.Combine(dir, "players", p, "video.mp4")));
            Assert.True(File.Exists(Path.Combine(dir, "players", p, "capture", "session.json")));
            var events = MultiplayerSession.ReadEvents(Path.Combine(dir, "players", p, "events.jsonl"));
            var kill = events.Single(e => GameEvents.Type(e) == "kill");
            Assert.InRange(kill["frame_index"]!.GetValue<int>(), 0, SessionFixtures.NFrames - 1);
            Assert.Equal("m1-r01", (string?)kill["session_id"]);
            Assert.NotNull(kill["event_id"]);
            var countdown = events.Single(e => GameEvents.Type(e) == "round_countdown");
            Assert.Null(countdown["frame_index"]);  // before the first frame: kept, with a negative video time
            Assert.True((double)countdown["video_time_ms"]! < 0);
            var player = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", p, "player.json")))!;
            var roundStart = events.Single(e => GameEvents.Type(e) == "round_start");
            Assert.Equal((int)player["round_start_frame"]!, (int)roundStart["frame_index"]! + 1);

            var (header, frames) = FramesBin.Read(Path.Combine(dir, "players", p, FramesBin.FileName));
            Assert.Equal("odx.capture.v1.FrameRecord", header.RecordType);
            Assert.Equal(SessionFixtures.NFrames, frames.Count);
            var (_, rows) = Csv.ReadFile(Path.Combine(dir, "players", p, "frames.csv"));
            Assert.Equal(Frames.ParseDouble(rows[40]["C2W_M03"]), frames[40].C2W[3]);
            Assert.True(frames[1].ServerTimeUs > frames[0].ServerTimeUs);

            Assert.True((bool)player["qa"]!["stream_valid"]!);
            Assert.Contains(player["qa"]!["checks"]!.AsArray(), c => (string?)c!["id"] == "floor.fps" && (bool)c["pass"]!);
            Assert.Equal("Forward", (string?)player["key_bindings"]!["W"]);
        }
        using (var gzip = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(dir, FramesBin.SchemaFileName)),
                   System.IO.Compression.CompressionMode.Decompress))
        {
            var schema = Google.Protobuf.Reflection.FileDescriptorSet.Parser.ParseFrom(gzip);
            Assert.Equal("odx.capture.v1", schema.File.Single().Package);
            Assert.Contains(schema.File.Single().MessageType, m => m.Name == "FrameRecord");
        }
    }

    [Fact]
    public async Task FailedCapturesFailTheSessionGate()
    {
        ConnectAgents(null, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();

        await Post(new { type = "round_start", round = 3 });
        await Post(new { type = "round_end" });

        var (dir, report) = await session;
        Assert.False(report.Pass);
        Assert.Equal(0, report.ValidPlayers);
        Assert.Contains(report.Issues, i => i.Rule == "session.min_players");
        Assert.All(report.Players, p => Assert.Contains(p.Issues, i => i.Rule == "player.missing"));
        Assert.EndsWith("-r03", report.SessionId);
        Assert.True(File.Exists(Path.Combine(dir, "session.json")));
        Assert.True(File.Exists(Path.Combine(dir, "validation.json")));
    }

    [Fact]
    public async Task HostStartsRoundWithCountdownOnEveryPlayerAndEndsIt()
    {
        var (_, hostStarted, code) = await HostRoom("p1");
        var started = new List<TaskCompletionSource> { hostStarted, Connect(null, "p2", code).Started };
        await WaitForAgents(2, code);
        var countdowns = _agents.Select(a =>
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Countdown += (ms, _) => tcs.TrySetResult(ms);
            return tcs.Task;
        }).ToList();
        var session = NextSession();

        await _agents[0].ToggleRoundAsync(300, default);
        int[] countdownMs = await Task.WhenAll(countdowns).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([300, 300], countdownMs);
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull((await RoomStatus(code))["current_session"]);
        await Task.Delay(600);  // past the countdown: ending during it would cancel the round
        await _agents[0].ToggleRoundAsync(300, default);

        var (_, report) = await session;
        Assert.Equal(2, report.Players.Count);
        Assert.All(report.Players, p => Assert.Contains(p.Issues, i => i.Message == "no game window"));
    }

    [Fact]
    public async Task StoppingDuringTheCountdownCancelsTheRoundAndItsNumberIsUsedAgain()
    {
        var (_, hostStarted, code) = await HostRoom("p1");
        var started = new List<TaskCompletionSource> { hostStarted };
        await WaitForAgents(1, code);
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.RoundCancelled += sid => cancelled.TrySetResult(sid);

        await _agents[0].ToggleRoundAsync(10_000, default);
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _agents[0].ToggleRoundAsync(10_000, default);
        string cancelledId = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAgents(1, code);
        var status = await RoomStatus(code);
        Assert.Null(status["current_session"]);
        Assert.Empty(status["pending_sessions"]!.AsArray());

        var session = NextSession();
        await _agents[0].ToggleRoundAsync(200, default);
        await Task.Delay(600);
        await _agents[0].ToggleRoundAsync(200, default);
        var (dir, report) = await session;
        Assert.Equal(cancelledId, report.SessionId);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!;
        Assert.Equal("3.1", (string?)json["spec_version"]);
        Assert.Equal(200, (int)json["countdown_ms"]!);
        Assert.Equal("player_stop:p1", (string?)json["end_reason"]);
        var types = MultiplayerSession.ReadEvents(Path.Combine(dir, "server", "events.jsonl")).Select(GameEvents.Type).ToList();
        Assert.Equal(["round_cancelled", "round_countdown", "round_start", "round_end"], types);
    }

    [Fact]
    public async Task EveryPlayerReadyStartsARoundAfterTheCountdown()
    {
        ConnectAgents(null, "p1", "p2");
        await WaitForAgents(2);
        var countdown = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _agents[0].Countdown += (ms, _) => countdown.TrySetResult(ms);
        var session = NextSession();

        await _agents[0].SetReadyAsync(true, default);
        await Task.Delay(300);
        Assert.Null((await RoomStatus())["current_session"]);
        await _agents[1].SetReadyAsync(true, default);
        Assert.Equal(ReadyCountdownMs, await countdown.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(ReadyCountdownMs + 300);
        await Post(new { type = "round_end" });

        var (dir, _) = await session;
        var types = MultiplayerSession.ReadEvents(Path.Combine(dir, "server", "events.jsonl")).Select(GameEvents.Type).ToList();
        Assert.Equal(["player_ready", "player_ready", "round_countdown", "round_start", "round_end"], types);
        Assert.False(_agents[0].IsReady);
    }

    [Fact]
    public async Task UnreadyDuringTheCountdownCancelsTheRound()
    {
        ConnectAgents(null, "p1", "p2");
        await WaitForAgents(2);
        var countdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agents[0].Countdown += (_, _) => countdown.TrySetResult();
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.RoundCancelled += sid => cancelled.TrySetResult(sid);

        await _agents[0].SetReadyAsync(true, default);
        await _agents[1].SetReadyAsync(true, default);
        await countdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _agents[1].SetReadyAsync(false, default);

        Assert.EndsWith("-r01", await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null((await RoomStatus())["current_session"]);
    }

    [Fact]
    public async Task OnlyTheHostsRoomIsRecorded()
    {
        var (_, aStarted, a) = await HostRoom("p1");
        var memberStarted = Connect(null, "p2", a).Started;
        var (otherHost, bStarted, b) = await HostRoom("p3");
        await WaitForAgents(2, a);
        Assert.NotEqual(a, b);

        await _agents[0].ToggleRoundAsync(300, default);
        await Task.WhenAll(aStarted.Task, memberStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        Assert.False(bStarted.Task.IsCompleted);
        Assert.Null((await RoomStatus(b))["current_session"]);
        Assert.Equal(["p1", "p2"], (await RoomStatus(a))["members"]!.AsArray().Select(m => (string)m!["player_id"]!).Order());
        Assert.True(otherHost.IsHost);
        Assert.False(_agents[1].IsHost);
    }

    [Fact]
    public async Task UnknownInviteCodesAreRefusedAndTheHostClosesTheRoom()
    {
        var (stranger, _) = Connect(null, "p9", "ZZZZZZ");
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        stranger.RoomError += m => refused.TrySetResult(m);
        Assert.Contains("ZZZZZZ", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var (host, _, code) = await HostRoom("p1");
        var (member, _) = Connect(null, "p2", code.ToLowerInvariant());
        await WaitForAgents(2, code);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        member.RoomChanged += r => { if (r is null) closed.TrySetResult(); };
        await host.CloseRoomAsync(default);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(member.Room);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _http.GetAsync($"/rooms/{code}")).StatusCode);
    }

    [Fact]
    public async Task UploadsOnlyGoToKnownPlayersWithPlainFileNames()
    {
        ConnectAgents(null, "p1");
        await WaitForAgents(1);
        await Post(new { type = "round_start", match_id = "m9" });
        Assert.NotNull(_coordinator.UploadPath("m9-r01", "p1", "video.mp4"));
        Assert.Null(_coordinator.UploadPath("m9-r01", "p1", "..\\x"));
        Assert.Null(_coordinator.UploadPath("m9-r01", "p7", "video.mp4"));
        Assert.Null(_coordinator.UploadPath("m0-r01", "p1", "video.mp4"));
    }
}
