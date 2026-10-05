// odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--rounds 2] [--round-s 20] [--match sim]
// Stands in for a game server plugin: posts a scripted match to /game/events. Each round: round_start, a kill and a
// permanent death of the last player halfway through, round_end. Before each round it waits until every agent is idle
// and the previous session is assembled; at the end it prints each session's gate result.
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Odx.Cli;

public static class SimulateGameCommand
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(10);

    public static int Run(string[] args)
    {
        string? orchestrator = null, match = "sim";
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
                default: orchestrator = null; i = args.Length; break;
            }
        }
        if (orchestrator is null || players.Length == 0 || !Uri.TryCreate(orchestrator, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("usage: odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--rounds 2] [--round-s 20] [--match sim]");
            return 2;
        }
        try
        {
            return RunAsync(uri, players, rounds, TimeSpan.FromSeconds(roundSeconds), match).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException)
        {
            Console.Error.WriteLine($"simulate-game: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(Uri orchestrator, string[] players, int rounds, TimeSpan roundLength, string match)
    {
        using var http = new HttpClient { BaseAddress = orchestrator };
        await Post(http, new { type = "match_start", match_id = match, map = "sim_map" });
        for (int round = 1; round <= rounds; round++)
        {
            await WaitUntilSettled(http);
            await Post(http, new { type = "round_start", round });
            await Task.Delay(roundLength / 2);
            if (players.Length > 1)
            {
                await Post(http, new { type = "kill", attacker = players[0], victim = players[^1], weapon = "sim_rifle" });
                await Post(http, new { type = "player_death", player = players[^1], permanent = true });
            }
            await Task.Delay(roundLength / 2);
            await Post(http, new { type = "round_end", round, winner = players[0] });
        }
        await Post(http, new { type = "match_end" });
        var status = await WaitUntilSettled(http);
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

    private static async Task Post(HttpClient http, object e)
    {
        using var response = await http.PostAsJsonAsync("/game/events", e);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Waits until no session is waiting for uploads and every agent is idle (agents still busy would sit out the next round).</summary>
    private static async Task<JsonObject> WaitUntilSettled(HttpClient http)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;
        while (true)
        {
            var status = (await http.GetFromJsonAsync<JsonObject>("/status"))!;
            if (status["pending_sessions"]!.AsArray().Count == 0 &&
                status["agents"]!.AsArray().All(a => (string?)a!["state"] == "idle"))
                return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("agents did not finish uploading in time");
            await Task.Delay(500);
        }
    }
}
