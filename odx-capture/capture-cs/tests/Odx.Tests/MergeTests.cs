using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Odx.Merge;
using Odx.Orchestrator;
using Odx.Session;
using Xunit;

namespace Odx.Tests;

public sealed class MergeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "odx-merge-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = MergeHost.Build(new MergeOptions(Path.Combine(_root, "out"), HttpPort: 0) { AccessKey = "k" }, "127.0.0.1");
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), DefaultRequestHeaders = { Authorization = new("Bearer", "k") } };
    }

    public async Task DisposeAsync()
    {
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

    private async Task<HttpStatusCode> Put(string path, string text = "x")
    {
        using var response = await _http.PutAsync(path, new StringContent(text));
        return response.StatusCode;
    }

    private Task<HttpStatusCode> Register(string sessionId, params string[] players) =>
        Put($"/sessions/{sessionId}/round/round.json",
            new RoundRecord(sessionId, "m", "Synthetic", null, 1, 0, null, 0, players.Select(p => new RoundPlayer(p, p)).ToList()).ToJson());

    private string Staging(string sessionId) => Path.Combine(_root, "out", ".staging", sessionId);

    [Fact]
    public async Task Uploads_go_only_to_registered_rounds_and_their_players_with_plain_names()
    {
        Assert.Equal(HttpStatusCode.NotFound, await Put("/sessions/m9-r01/players/p1/files/video.mp4"));
        Assert.Equal(HttpStatusCode.OK, await Register("m9-r01", "p1"));

        Assert.Equal(HttpStatusCode.OK, await Put("/sessions/m9-r01/players/p1/files/video.mp4", "video"));
        Assert.Equal(HttpStatusCode.OK, await Put("/sessions/m9-r01/server/files/demo.dem", "demo"));
        Assert.Equal(HttpStatusCode.NotFound, await Put("/sessions/m9-r01/players/p7/files/video.mp4"));
        Assert.Equal(HttpStatusCode.NotFound, await Put($"/sessions/m9-r01/players/p1/files/{Uri.EscapeDataString("..\\x")}"));
        Assert.Equal(HttpStatusCode.NotFound, await Put("/sessions/m9-r01/round/other.json"));
        Assert.Equal("video", File.ReadAllText(Path.Combine(Staging("m9-r01"), "players", "p1", "video.mp4")));
        Assert.Equal("demo", File.ReadAllText(Path.Combine(Staging("m9-r01"), "server", "demo.dem")));
    }

    [Fact]
    public async Task A_cancelled_round_is_dropped_and_an_assembled_one_takes_no_more_uploads()
    {
        await Register("m8-r01", "p1");
        Assert.True((await _http.DeleteAsync("/sessions/m8-r01")).IsSuccessStatusCode);
        Assert.False(Directory.Exists(Staging("m8-r01")));
        Assert.Equal(HttpStatusCode.NotFound, await Put("/sessions/m8-r01/players/p1/files/video.mp4"));

        await Register("m7-r01", "p1");
        await Put("/sessions/m7-r01/round/game_profile.json", """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        using var response = await _http.PostAsync("/sessions/m7-r01/assemble", null);
        var assembled = await response.Content.ReadFromJsonAsync<AssembledSession>(AgentMessage.Json);
        Assert.False(assembled!.Report.Pass);
        Assert.Contains(assembled.Report.Players.Single().Issues, i => i.Rule == "player.missing");
        Assert.True(File.Exists(Path.Combine(assembled.Dir, "session.json")));
        Assert.Equal(HttpStatusCode.NotFound, await Put("/sessions/m7-r01/players/p1/files/video.mp4"));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/sessions/m0-r01/assemble", null)).StatusCode);
    }

    [Fact]
    public async Task Requests_without_the_key_are_refused()
    {
        using var anonymous = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/sessions/m1-r01/round/round.json", new StringContent("{}"))).StatusCode);
    }
}

/// <summary>
/// The game server agent between a real orchestrator and odx-merge, against a fake RCON and a game directory written by
/// hand (no demo: SourceTV "does not record").
/// </summary>
public sealed class GameServerAgentTests
{
    [Fact]
    public async Task Every_round_records_the_servers_side_and_its_clock_and_uploads_them_to_merge()
    {
        string root = Cs2Fixtures.TempDir(), game = Path.Combine(root, "csgo");
        string logs = Path.Combine(game, "odx-netlog");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        var lines = File.ReadAllLines(Cs2Fixtures.NetLog);
        File.WriteAllLines(Path.Combine(logs, "a.jsonl"), lines[..10]);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var merge = MergeHost.Build(new MergeOptions(Path.Combine(root, "out"), HttpPort: 0), "127.0.0.1");
        await merge.StartAsync();
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0)
            { MergeUrl = new Uri(merge.Urls.First()) }, "127.0.0.1");
        await app.StartAsync();
        var coordinator = app.Services.GetRequiredService<MatchCoordinator>();
        var assembled = new TaskCompletionSource<(string Dir, SessionReport Report)>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SessionAssembled += (dir, report) => assembled.TrySetResult((dir, report));
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var rcon = new FakeRcon("pw");
        using var stop = new CancellationTokenSource();
        var agent = new GameServerAgent(new GameServerAgentOptions(new Uri(app.Urls.First()),
            new Cs2ServerOptions("127.0.0.1", rcon.Port, "pw", game), Path.Combine(root, "work")), NullLogger.Instance);
        _ = agent.RunUntilStoppedAsync(stop.Token);
        for (int i = 0; i < 100 && (await http.GetFromJsonAsync<JsonObject>("/status"))!["game_server"] is null; i++) await Task.Delay(50);

        string code = (string)(await (await http.PostAsync("/rooms", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_start", match_id = "g1" })).EnsureSuccessStatusCode();
        for (int i = 0; i < 100 && !rcon.Commands.Contains("tv_record replays/g1-r01"); i++) await Task.Delay(50);
        File.AppendAllLines(Path.Combine(logs, "a.jsonl"), lines[10..20]);
        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_end" })).EnsureSuccessStatusCode();

        var (dir, report) = await assembled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        Assert.Equal(["tv_record replays/g1-r01", "tv_stoprecord"], rcon.Commands);
        Assert.Equal(lines[10..20], File.ReadAllLines(Path.Combine(dir, "server", "capture", Cs2ServerCapture.NetLogFile)));
        Assert.True(File.Exists(Path.Combine(dir, "server", PacketLog.BinFile)));
        var server = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!["server"]!;
        Assert.True(server["clock_sync"]!["sample_count"]!.GetValue<int>() >= 2);  // at least the round's start and end
        Assert.InRange(server["clock_sync"]!["offset_ms"]!.GetValue<double>(), -50, 50);  // the same machine
        Assert.Contains(report.Issues, i => i.Rule == "server.capture" && i.Message.StartsWith("SourceTV is not recording", StringComparison.Ordinal));
        string kept = Path.Combine(root, "work", "g1-r01");
        Assert.True(await LocalRoundTests.WaitForFile(Path.Combine(kept, RoundRecord.FileName)));
        Assert.Equal(lines[10..20], File.ReadAllLines(Path.Combine(kept, "server", Cs2ServerCapture.NetLogFile)));
    }
}

public sealed class LocalRoundTests
{
    internal static async Task<bool> WaitForFile(string path)
    {
        for (int i = 0; i < 200 && !File.Exists(path); i++) await Task.Delay(50);
        return File.Exists(path);
    }

    [Fact]
    public async Task Two_players_and_the_game_server_each_keep_a_folder_of_the_staging_layout()
    {
        string root = Cs2Fixtures.TempDir(), game = Path.Combine(root, "csgo");
        Directory.CreateDirectory(Path.Combine(game, "odx-netlog"));
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        string template = SessionFixtures.MakeValidSession(Path.Combine(root, "template"), video: false);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0), "127.0.0.1");
        await app.StartAsync();
        var orchestrator = new Uri(app.Urls.First());
        using var http = new HttpClient { BaseAddress = orchestrator };
        using var rcon = new FakeRcon("pw");
        using var stop = new CancellationTokenSource();
        var server = new GameServerAgent(new GameServerAgentOptions(orchestrator,
            new Cs2ServerOptions("127.0.0.1", rcon.Port, "pw", game), Path.Combine(root, "server")), NullLogger.Instance);
        _ = server.RunUntilStoppedAsync(stop.Token);
        for (int i = 0; i < 100 && (await http.GetFromJsonAsync<JsonObject>("/status"))!["game_server"] is null; i++) await Task.Delay(50);

        string code = (string)(await (await http.PostAsync("/rooms", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
        string[] players = ["p1", "p2"];
        foreach (string p in players)
            _ = new AgentClient(new AgentOptions(orchestrator, p, p, Path.Combine(root, "captures-" + p), null, RoomCode: code),
                options => new OrchestratorTests.FakeCapture(options, template)).RunAsync(stop.Token);
        for (int i = 0; i < 100 && (await http.GetFromJsonAsync<JsonObject>($"/rooms/{code}"))!["members"]!.AsArray()
                 .Count(m => (string?)m!["state"] == "idle") < players.Length; i++)
            await Task.Delay(50);

        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_start", match_id = "g1" })).EnsureSuccessStatusCode();
        for (int i = 0; i < 100 && !rcon.Commands.Contains("tv_record replays/g1-r01"); i++) await Task.Delay(50);
        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_end" })).EnsureSuccessStatusCode();

        string serverKept = Path.Combine(root, "server", "g1-r01");
        var kept = players.Select(p => Path.Combine(root, "captures-" + p, "g1-r01")).Append(serverKept).ToList();
        foreach (string dir in kept) Assert.True(await WaitForFile(Path.Combine(dir, RoundRecord.FileName)), dir);
        stop.Cancel();

        foreach (string dir in kept)
        {
            Assert.True(File.Exists(Path.Combine(dir, RoundRecord.EventsFileName)));
            Assert.True(File.Exists(Path.Combine(dir, RoundRecord.ProfileFileName)));
            Assert.Equal(File.ReadAllText(Path.Combine(serverKept, RoundRecord.FileName)), File.ReadAllText(Path.Combine(dir, RoundRecord.FileName)));
        }
        foreach (string dir in kept.SkipLast(1))
            foreach (string f in new[] { "session.json", "frames.csv", "video_frames.csv" })
                Assert.True(File.Exists(Path.Combine(dir, f)), Path.Combine(dir, f));
        Assert.True(File.Exists(Path.Combine(serverKept, "server", Cs2ServerCapture.ClockSamplesFile)));
        var round = RoundRecord.Load(serverKept);
        Assert.Equal(players, round.Players.Select(p => p.PlayerId).Order());
        Assert.All(round.Players, p => Assert.True(p.Done));
        Assert.Contains("round_end", File.ReadAllText(Path.Combine(serverKept, RoundRecord.EventsFileName)));
    }
}
