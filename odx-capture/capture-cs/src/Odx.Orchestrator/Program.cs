// odx-orchestrator --out <dir> --game-profile <game.json> [--vendor odx] [--http-port 5180] [--time-port 12300]
//                  [--fps 30] [--min-valid K] [--max-rtt-ms 100]
// Runs next to the game server; agents connect to ws://<host>:<http-port>/agents, the game server plugin posts events.
using System.Globalization;
using Odx.Orchestrator;
using Odx.Session;

string? outRoot = null, profile = null;
var options = new OrchestratorOptions("", "");
var tolerances = GateTolerances.Default;
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);
    switch (args[i])
    {
        case "--out": outRoot = Next(); break;
        case "--game-profile": profile = Next(); break;
        case "--vendor": options = options with { Vendor = Next() }; break;
        case "--http-port": options = options with { HttpPort = NextInt() }; break;
        case "--time-port": options = options with { TimePort = NextInt() }; break;
        case "--fps": options = options with { Fps = NextInt() }; break;
        case "--min-valid": tolerances = tolerances with { MinValidPlayers = NextInt() }; break;
        case "--max-rtt-ms": tolerances = tolerances with { MaxRttMs = NextInt() }; break;
        default: outRoot = null; i = args.Length; break;
    }
}
if (outRoot is null || profile is null)
{
    Console.Error.WriteLine("usage: odx-orchestrator --out <dir> --game-profile <game.json> [--vendor odx] [--http-port 5180] " +
                            "[--time-port 12300] [--fps 30] [--min-valid K] [--max-rtt-ms 100]");
    return 2;
}

var app = OrchestratorHost.Build(options with { OutRoot = outRoot, GameProfilePath = profile, Tolerances = tolerances });
await app.RunAsync();
return 0;
