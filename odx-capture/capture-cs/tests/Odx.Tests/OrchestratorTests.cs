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

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        string profile = Path.Combine(_root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {"W": "Forward"}}
            """);
        _app = OrchestratorHost.Build(new OrchestratorOptions(Path.Combine(_root, "out"), profile, HttpPort: 0, TimePort: 0,
            LeadMs: 200, Fps: SessionFixtures.Fps), "127.0.0.1");
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _coordinator = _app.Services.GetRequiredService<MatchCoordinator>();
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

    private List<TaskCompletionSource> ConnectAgents(string? template, params string[] players)
    {
        var started = new List<TaskCompletionSource>();
        foreach (string player in players)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            started.Add(tcs);
            var agent = new AgentClient(new AgentOptions(_http.BaseAddress!, player, player.ToUpperInvariant(),
                Path.Combine(_root, "agent-" + player), null), options =>
            {
                try
                {
                    return template is null ? throw new InvalidOperationException("no game window") : new FakeCapture(options, template);
                }
                finally
                {
                    tcs.TrySetResult();
                }
            });
            _ = agent.RunAsync(_stop.Token);
        }
        return started;
    }

    private async Task WaitForAgents(int count)
    {
        for (int i = 0; i < 100; i++)
        {
            var status = await _http.GetFromJsonAsync<JsonObject>("/status");
            if (status!["agents"]!.AsArray().Count(a => (string?)a!["state"] == "idle") == count) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("agents did not connect");
    }

    private Task Post(object e) => _http.PostAsJsonAsync("/game/events", e).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    [SkippableFact]
    public async Task RoundBecomesV3SessionWithPerPlayerStopsAndFrameIndexedEvents()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "needs ffmpeg + ffprobe");
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        var started = ConnectAgents(template, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();

        await Post(new { type = "match_start", match_id = "m1", map = "de_dust2" });
        await Post(new { type = "round_start" });
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        await Post(new { type = "kill", attacker = "p1", victim = "p2", weapon = "ak47" });
        await Post(new { type = "player_death", player = "p2", permanent = true });
        await Post(new { type = "round_end", winner = "t" });

        var (dir, report) = await session;
        Assert.True(report.Pass, string.Join("; ", report.Players.SelectMany(p => p.Issues).Select(i => i.Message)));
        Assert.Equal("m1-r01", report.SessionId);
        Assert.Equal(2, report.ValidPlayers);
        Assert.EndsWith(Path.Combine("odx", DateTime.UtcNow.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture), "synthetic", "m1-r01"), dir);

        var serverEvents = MultiplayerSession.ReadEvents(Path.Combine(dir, "server", "events.jsonl")).Select(GameEvents.Type);
        Assert.Equal(["round_start", "kill", "player_death", "round_end"], serverEvents);
        Assert.Equal("permanent_death", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p2", "player.json")))!["stop_reason"]);
        Assert.Equal("round_end", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p1", "player.json")))!["stop_reason"]);
        foreach (string p in new[] { "p1", "p2" })
        {
            Assert.True(File.Exists(Path.Combine(dir, "players", p, "video.mp4")));
            Assert.True(File.Exists(Path.Combine(dir, "players", p, "capture", "session.json")));
            var kill = MultiplayerSession.ReadEvents(Path.Combine(dir, "players", p, "events.jsonl")).Single(e => GameEvents.Type(e) == "kill");
            Assert.InRange(kill["frame_index"]!.GetValue<int>(), 0, SessionFixtures.NFrames - 1);
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
