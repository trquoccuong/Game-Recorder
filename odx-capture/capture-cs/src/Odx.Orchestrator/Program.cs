// odx-orchestrator --game-profile <game.json> [--merge http://<host>:5190 [--merge-public <url>]] [--http-port 5180]
//                  [--time-port 12300] [--fps 30] [--access-key K]
//                  [--zerotier-api http://zerotier:9993 --zerotier-token-file <authtoken.secret>]
// Rooms, rounds, the time service and room LANs: it starts and stops every capture (players' and the game server agent's)
// and has odx-merge assemble each round. --merge: odx-merge as this machine reaches it; --merge-public: as players and the
// game server agent reach it (default: the same).
// --access-key (or the ODX_ACCESS_KEY environment variable): clients must send it (access_key in odx-capture.json);
// the same key is sent to odx-merge.
// --zerotier-api: a ZeroTier controller giving rooms created "with LAN" their own virtual LAN; its token comes from
// --zerotier-token-file or the ODX_ZT_TOKEN environment variable.
using System.Globalization;
using Odx.Orchestrator;

string? profile = null;
var options = new OrchestratorOptions("")
{
    AccessKey = Environment.GetEnvironmentVariable("ODX_ACCESS_KEY"),
    ZeroTierToken = Environment.GetEnvironmentVariable("ODX_ZT_TOKEN") is { Length: > 0 } zt ? zt : null,
};
bool usage = false;
for (int i = 0; i < args.Length && !usage; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);
    switch (args[i])
    {
        case "--game-profile": profile = Next(); break;
        case "--merge": options = options with { MergeUrl = new Uri(Next()) }; break;
        case "--merge-public": options = options with { MergePublicUrl = new Uri(Next()) }; break;
        case "--http-port": options = options with { HttpPort = NextInt() }; break;
        case "--time-port": options = options with { TimePort = NextInt() }; break;
        case "--fps": options = options with { Fps = NextInt() }; break;
        case "--access-key": options = options with { AccessKey = Next() }; break;
        case "--zerotier-api": options = options with { ZeroTierApi = new Uri(Next()) }; break;
        case "--zerotier-token-file": options = options with { ZeroTierToken = File.ReadAllText(Next()).Trim() }; break;
        default: usage = true; break;
    }
}
if (usage || profile is null)
{
    Console.Error.WriteLine("usage: odx-orchestrator --game-profile <game.json> [--merge URL [--merge-public URL]] [--http-port 5180] " +
                            "[--time-port 12300] [--fps 30] [--access-key K] [--zerotier-api URL --zerotier-token-file PATH]");
    return 2;
}
var app = OrchestratorHost.Build(options with { GameProfilePath = profile });
if (options.MergeUrl is null) app.Logger.LogWarning("no --merge: rounds are recorded but not assembled into sessions");
await app.RunAsync();
return 0;
