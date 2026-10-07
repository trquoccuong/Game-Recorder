using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Odx.Orchestrator;
using Odx.Session;
using Xunit;

namespace Odx.Tests;

/// <summary>
/// The game server agent under a real orchestrator, against a fake RCON and a game directory written by hand (no demo:
/// SourceTV "does not record").
/// </summary>
public sealed class GameServerAgentTests
{
    [Fact]
    public async Task Every_round_records_the_servers_side_and_its_clock_and_keeps_them_for_merge()
    {
        string root = Cs2Fixtures.TempDir(), game = Path.Combine(root, "csgo");
        string logs = Path.Combine(game, "odx-netlog");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        var lines = File.ReadAllLines(Cs2Fixtures.NetLog);
        File.WriteAllLines(Path.Combine(logs, "a.jsonl"), lines[..10]);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Counter-Strike 2", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0), "127.0.0.1");
        await app.StartAsync();
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

        string kept = Path.Combine(root, "work", "g1-r01");
        Assert.True(await LocalRoundTests.WaitForFile(Path.Combine(kept, RoundRecord.FileName)));
        stop.Cancel();
        var (dir, report) = MultiplayerSession.Assemble(kept, Path.Combine(root, "out"), Odx.Cli.MergeCommand.Vendor);
        Assert.Equal(["tv_record replays/g1-r01", "tv_stoprecord"], rcon.Commands);
        Assert.Equal(lines[10..20], File.ReadAllLines(Path.Combine(dir, "server", "capture", Cs2ServerCapture.NetLogFile)));
        Assert.True(File.Exists(Path.Combine(dir, "server", PacketLog.BinFile)));
        var server = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!["server"]!;
        Assert.True(server["clock_sync"]!["sample_count"]!.GetValue<int>() >= 2);  // at least the round's start and end
        Assert.InRange(server["clock_sync"]!["offset_ms"]!.GetValue<double>(), -50, 50);  // the same machine
        Assert.Contains(report.Issues, i => i.Rule == "server.capture" && i.Message.StartsWith("SourceTV is not recording", StringComparison.Ordinal));
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
    public void Merge_refuses_a_folder_without_sessions()
    {
        string root = Cs2Fixtures.TempDir();
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([]));
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([Path.Combine(root, "missing")]));
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([root]));
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([root, "--out"]));
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

public sealed class GameMergerTests
{
    [Fact]
    public void The_game_profile_picks_the_module_and_a_game_without_one_has_no_server_side()
    {
        Assert.IsType<Cs2Merger>(GameMergers.For("Counter-Strike 2"));
        Assert.Null(GameMergers.For("Synthetic"));
    }
}
