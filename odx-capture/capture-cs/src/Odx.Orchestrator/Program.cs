// odx-orchestrator --out <dir> --game-profile <game.json> [--vendor odx] [--http-port 5180] [--time-port 12300]
//                  [--fps 30] [--min-valid K] [--max-rtt-ms 100] [--access-key K]
//                  [--zerotier-api http://zerotier:9993 --zerotier-token-file <authtoken.secret>]
// --access-key (or the ODX_ACCESS_KEY environment variable): clients must send it (access_key in odx-capture.json).
// --zerotier-api: a ZeroTier controller giving rooms created "with LAN" their own virtual LAN; its token comes from
// --zerotier-token-file or the ODX_ZT_TOKEN environment variable.
// Runs next to the game server; agents connect to ws://<host>:<http-port>/agents, the game server plugin posts events.
using System.Globalization;
using Odx.Orchestrator;
using Odx.Session;

string? outRoot = null, profile = null;
var options = new OrchestratorOptions("", "")
{
    AccessKey = Environment.GetEnvironmentVariable("ODX_ACCESS_KEY"),
    ZeroTierToken = Environment.GetEnvironmentVariable("ODX_ZT_TOKEN") is { Length: > 0 } zt ? zt : null,
};
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
        case "--access-key": options = options with { AccessKey = Next() }; break;
        case "--zerotier-api": options = options with { ZeroTierApi = new Uri(Next()) }; break;
        case "--zerotier-token-file": options = options with { ZeroTierToken = File.ReadAllText(Next()).Trim() }; break;
        default: outRoot = null; i = args.Length; break;
    }
}
if (outRoot is null || profile is null)
{
    Console.Error.WriteLine("usage: odx-orchestrator --out <dir> --game-profile <game.json> [--vendor odx] [--http-port 5180] " +
                            "[--time-port 12300] [--fps 30] [--min-valid K] [--max-rtt-ms 100] " +
                            "[--access-key K] [--zerotier-api URL --zerotier-token-file PATH]");
    return 2;
}

var app = OrchestratorHost.Build(options with { OutRoot = outRoot, GameProfilePath = profile, Tolerances = tolerances });
await app.RunAsync();
return 0;
