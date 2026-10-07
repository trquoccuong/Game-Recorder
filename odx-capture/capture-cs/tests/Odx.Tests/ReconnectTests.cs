using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Odx.Orchestrator;
using Odx.Session;
using Xunit;

namespace Odx.Tests;

/// <summary>
/// A player whose connection drops (a <see cref="TcpProxy"/> between it and the orchestrator cuts it, as lost Wi-Fi would)
/// keeps recording and its place in the round for the orchestrator's grace.
/// </summary>
public sealed class ReconnectTests : IAsyncLifetime
{
    /// <summary>Forwards TCP to a target until <see cref="Cut"/>; refuses connections while <see cref="Down"/>.</summary>
    private sealed class TcpProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _open = [];
        private readonly int _target;

        public TcpProxy(int target)
        {
            _target = target;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool Down { get; set; }

        public void Cut()
        {
            lock (_open)
            {
                foreach (var c in _open) c.Close();
                _open.Clear();
            }
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (Down)
                {
                    client.Close();
                    continue;
                }
                var server = new TcpClient();
                await server.ConnectAsync(IPAddress.Loopback, _target);
                lock (_open) _open.AddRange([client, server]);
                _ = Pump(client, server);
                _ = Pump(server, client);
            }
        }

        private async Task Pump(TcpClient from, TcpClient to)
        {
            try
            {
                await from.GetStream().CopyToAsync(to.GetStream());
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            from.Close();
            to.Close();
        }

        public void Dispose()
        {
            _listener.Stop();
            Cut();
        }
    }

    private readonly string _root = Cs2Fixtures.TempDir();
    private readonly CancellationTokenSource _stop = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private TcpProxy _proxy = null!;
    private string _template = null!;
    private string _room = null!;

    public async Task InitializeAsync()
    {
        string profile = Path.Combine(_root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        _app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0)
            { ReconnectGrace = TimeSpan.FromSeconds(2) }, "127.0.0.1");
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _proxy = new TcpProxy(_http.BaseAddress.Port);
        _template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"), video: false);
        _room = (string)(await (await _http.PostAsync("/rooms", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        _proxy.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>A player through the proxy, reconnecting at once (not after 5 s) whenever its connection ends.</summary>
    private (AgentClient Agent, List<FakeRun> Runs) Player(string id, TimeSpan building = default)
    {
        var runs = new List<FakeRun>();
        var agent = new AgentClient(new AgentOptions(new Uri($"http://127.0.0.1:{_proxy.Port}"), id, id, Path.Combine(_root, id), null,
            RoomCode: _room), options =>
        {
            var run = new FakeRun(new OrchestratorTests.FakeCapture(options, _template, building));
            lock (runs) runs.Add(run);
            return run;
        });
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await agent.RunAsync(_stop.Token);
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or OperationCanceledException)
                {
                }
                await Task.Delay(100);
            }
        });
        return (agent, runs);
    }

    /// <summary>Notes when the capture was told to stop.</summary>
    private sealed class FakeRun(ICaptureRun inner) : ICaptureRun
    {
        public DateTime? StoppedAt { get; private set; }
        public string SessionDir => inner.SessionDir;
        public Task Started => inner.Started;
        public Task CaptureStopped => inner.CaptureStopped;
        public Task<RunResult> Completion => inner.Completion;

        public void Stop()
        {
            StoppedAt ??= DateTime.UtcNow;
            inner.Stop();
        }
    }

    private async Task<JsonObject> Status() => (await _http.GetFromJsonAsync<JsonObject>($"/rooms/{_room}"))!;

    private async Task Until(Func<JsonObject, bool> done, string what)
    {
        for (int i = 0; i < 200; i++)
        {
            if (done(await Status())) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(what);
    }

    private Task Post(object e) => _http.PostAsJsonAsync($"/rooms/{_room}/events", e).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private async Task<RoundRecord> Kept(string player, string session)
    {
        string round = Path.Combine(_root, player, session, RoundRecord.FileName);
        Assert.True(await LocalRoundTests.WaitForFile(round), round);
        return RoundRecord.Load(Path.GetDirectoryName(round)!);
    }

    private static bool Members(JsonObject status, int count) =>
        status["members"]!.AsArray().Count(m => (string?)m!["state"] == "idle") == count;

    [Fact]
    public async Task A_player_back_within_the_grace_records_on_until_the_round_ends()
    {
        var (_, runs) = Player("p1");
        await Until(s => Members(s, 1), "p1 did not join");
        await Post(new { type = "round_start", match_id = "m1" });
        await Until(s => s["members"]!.AsArray().Any(m => (string?)m!["state"] == "recording"), "p1 did not record");

        _proxy.Cut();
        await Until(s => s["members"]!.AsArray().Count == 1, "p1 did not come back");
        await Task.Delay(2500);  // past the grace: p1 is back, so nothing stops it
        Assert.Null(runs.Single().StoppedAt);
        await Post(new { type = "round_end" });

        var round = await Kept("p1", "m1-r01");
        Assert.Single(runs);
        Assert.Equal("round_end", round.Players.Single().StopReason);
        Assert.True(round.Players.Single().Kept);
    }

    [Fact]
    public async Task A_player_away_when_the_round_ends_gets_the_missed_stop_and_its_done_gets_through()
    {
        var (_, runs) = Player("p1", building: TimeSpan.FromMilliseconds(300));
        await Until(s => Members(s, 1), "p1 did not join");
        await Post(new { type = "round_start", match_id = "m2" });
        await Until(s => s["members"]!.AsArray().Any(m => (string?)m!["state"] == "recording"), "p1 did not record");

        _proxy.Down = true;
        _proxy.Cut();
        await Post(new { type = "round_end" });  // its Stop goes nowhere
        await Task.Delay(500);
        Assert.Null(runs.Single().StoppedAt);
        _proxy.Down = false;

        var round = await Kept("p1", "m2-r01");
        Assert.NotNull(runs.Single().StoppedAt);
        Assert.Equal("round_end", round.Players.Single().StopReason);
        Assert.True(round.Players.Single().Kept);
    }

    [Fact]
    public async Task A_done_sent_while_the_connection_was_down_is_sent_again()
    {
        var (_, runs) = Player("p1", building: TimeSpan.FromMilliseconds(400));
        await Until(s => Members(s, 1), "p1 did not join");
        await Post(new { type = "round_start", match_id = "m4" });
        await Until(s => s["members"]!.AsArray().Any(m => (string?)m!["state"] == "recording"), "p1 did not record");

        await Post(new { type = "round_end" });
        for (int i = 0; i < 100 && runs.Single().StoppedAt is null; i++) await Task.Delay(20);
        _proxy.Down = true;
        _proxy.Cut();  // while the capture builds: its Done goes nowhere
        await Task.Delay(800);
        Assert.Equal(["m4-r01"], (await Status())["pending_sessions"]!.AsArray().Select(s => (string)s!));
        _proxy.Down = false;

        var round = await Kept("p1", "m4-r01");
        Assert.True(round.Players.Single().Kept);
    }

    [Fact]
    public async Task A_player_gone_past_the_grace_is_stopped_on_both_sides()
    {
        var recorded = new TaskCompletionSource<RoundRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Services.GetRequiredService<MatchCoordinator>().RoundRecorded += files =>
            recorded.TrySetResult(System.Text.Json.JsonSerializer.Deserialize<RoundRecord>(files.Round, AgentMessage.Json)!);
        var (_, runs) = Player("p1");
        await Until(s => Members(s, 1), "p1 did not join");
        await Post(new { type = "round_start", match_id = "m3" });
        await Until(s => s["members"]!.AsArray().Any(m => (string?)m!["state"] == "recording"), "p1 did not record");

        _proxy.Down = true;
        _proxy.Cut();
        for (int i = 0; i < 100 && runs.Single().StoppedAt is null; i++) await Task.Delay(50);
        Assert.NotNull(runs.Single().StoppedAt);  // the agent's own grace
        await Task.Delay(500);
        await Post(new { type = "round_end" });

        var round = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("disconnected", round.Players.Single().StopReason);
        Assert.False(round.Players.Single().Kept);
        Assert.True(round.EndUtcMs > round.Players.Single().StopUtcMs);
    }
}
