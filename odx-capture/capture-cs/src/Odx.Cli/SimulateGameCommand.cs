// odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--room <code>] [--rounds 2] [--round-s 20] [--match sim]
//                   [--access-key K]   (default: access_key of odx-capture.json next to odx)
// Stands in for a game server plugin: opens a room without a host (or uses --room), prints its invite code, waits until
// every --players member has joined, then posts a scripted match to /rooms/<code>/events. Each round: round_start, a kill
// and a permanent death of the last player halfway through, round_end. Before each round it waits until every member is
// idle and the previous session is assembled; at the end it prints each session's gate result.
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Odx.Cli;

public static class SimulateGameCommand
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(10);

    public static int Run(string[] args)
    {
        string? orchestrator = null, match = "sim", room = null, accessKey = null;
        string[] players = [];
        int rounds = 2;
        double roundSeconds = 20;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--orchestrator" when i + 1 < args.Length: orchestrator = args[++i]; break;
                case "--players" when i + 1 < args.Length: players = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--rounds" when i + 1 < args.Length: rounds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--round-s" when i + 1 < args.Length: roundSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--match" when i + 1 < args.Length: match = args[++i]; break;
                case "--room" when i + 1 < args.Length: room = args[++i]; break;
                case "--access-key" when i + 1 < args.Length: accessKey = args[++i]; break;
                default: orchestrator = null; i = args.Length; break;
            }
        }
        if (orchestrator is null || players.Length == 0 || !Uri.TryCreate(orchestrator, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("usage: odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--room <code>] " +
                                    "[--rounds 2] [--round-s 20] [--match sim] [--access-key K]");
            return 2;
        }
        try
        {
            return RunAsync(uri, accessKey ?? Odx.Core.CaptureConfig.Load().AccessKey, room, players, rounds,
                TimeSpan.FromSeconds(roundSeconds), match).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException)
        {
            Console.Error.WriteLine($"simulate-game: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(Uri orchestrator, string? accessKey, string? room, string[] players, int rounds,
        TimeSpan roundLength, string match)
    {
        using var http = new HttpClient { BaseAddress = orchestrator };
        if (accessKey is not null) http.DefaultRequestHeaders.Authorization = new("Bearer", accessKey);
        if (room is null)
        {
            using var opened = await http.PostAsync("/rooms", null);
            opened.EnsureSuccessStatusCode();
            room = (string)(await opened.Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
        }
        Console.WriteLine($"room {room}: waiting for {string.Join(", ", players)} to join");
        await WaitUntil(http, room, s => players.All(p => s["members"]!.AsArray().Any(m => (string?)m!["player_id"] == p)),
            "players did not join");
        await Post(http, room, new { type = "match_start", match_id = match, map = "sim_map" });
        for (int round = 1; round <= rounds; round++)
        {
            await WaitUntilSettled(http, room);
            await Post(http, room, new { type = "round_start", round });
            await Task.Delay(roundLength / 2);
            if (players.Length > 1)
            {
                await Post(http, room, new { type = "kill", attacker = players[0], victim = players[^1], weapon = "sim_rifle" });
                await Post(http, room, new { type = "player_death", player = players[^1], permanent = true });
            }
            await Task.Delay(roundLength / 2);
            await Post(http, room, new { type = "round_end", round, winner = players[0] });
        }
        await Post(http, room, new { type = "match_end" });
        var status = await WaitUntilSettled(http, room);
        bool pass = true;
        foreach (var s in status["sessions"]!.AsArray().Select(n => n!.AsObject())
                     .Where(s => ((string)s["session_id"]!).StartsWith(match + "-r", StringComparison.Ordinal)))
        {
            bool ok = (bool)s["pass"]!;
            pass &= ok;
            Console.WriteLine($"{s["session_id"]}: {(ok ? "PASS" : "FAIL")} ({s["valid_players"]}/{s["required_players"]} players required)");
        }
        return pass ? 0 : 1;
    }

    private static async Task Post(HttpClient http, string room, object e)
    {
        using var response = await http.PostAsJsonAsync($"/rooms/{room}/events", e);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Waits until no session is waiting for uploads and every member is idle (members still busy would sit out the next round).</summary>
    private static Task<JsonObject> WaitUntilSettled(HttpClient http, string room) =>
        WaitUntil(http, room, s => s["pending_sessions"]!.AsArray().Count == 0 &&
                                   s["members"]!.AsArray().All(m => (string?)m!["state"] == "idle"),
            "agents did not finish uploading in time");

    private static async Task<JsonObject> WaitUntil(HttpClient http, string room, Func<JsonObject, bool> done, string timeout)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;
        while (true)
        {
            var status = (await http.GetFromJsonAsync<JsonObject>($"/rooms/{room}"))!;
            if (done(status)) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(timeout);
            await Task.Delay(500);
        }
    }
}
