// odx-orchestrator --out <dir> --game-profile <game.json> [--vendor odx] [--http-port 5180] [--time-port 12300]
//                  [--fps 30] [--min-valid K] [--max-rtt-ms 100] [--access-key K]
//                  [--zerotier-api http://zerotier:9993 --zerotier-token-file <authtoken.secret>]
//                  [--cs2-game-dir <cs2>/game/csgo --cs2-rcon 127.0.0.1:27015 --cs2-rcon-password-file <file>]
// --access-key (or the ODX_ACCESS_KEY environment variable): clients must send it (access_key in odx-capture.json).
// --zerotier-api: a ZeroTier controller giving rooms created "with LAN" their own virtual LAN; its token comes from
// --zerotier-token-file or the ODX_ZT_TOKEN environment variable.
// --cs2-game-dir: a CS2 dedicated server on this machine (odx_netlog plugin, tv_enable 1, tv_delay 0) whose demo and
// net message log become each session's server/ files; its RCON password comes from --cs2-rcon-password-file or the
// ODX_CS2_RCON_PASSWORD environment variable.
// Runs next to the game server; agents connect to ws://<host>:<http-port>/agents, the game server plugin posts events.
using System.Globalization;
using Odx.Orchestrator;
using Odx.Session;

string? outRoot = null, profile = null, cs2Dir = null, cs2Rcon = "127.0.0.1:27015";
string? cs2Password = Environment.GetEnvironmentVariable("ODX_CS2_RCON_PASSWORD") is { Length: > 0 } pw ? pw : null;
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
        case "--cs2-game-dir": cs2Dir = Next(); break;
        case "--cs2-rcon": cs2Rcon = Next(); break;
        case "--cs2-rcon-password-file": cs2Password = File.ReadAllText(Next()).Trim(); break;
        default: outRoot = null; i = args.Length; break;
    }
}
if (outRoot is null || profile is null)
{
    Console.Error.WriteLine("usage: odx-orchestrator --out <dir> --game-profile <game.json> [--vendor odx] [--http-port 5180] " +
                            "[--time-port 12300] [--fps 30] [--min-valid K] [--max-rtt-ms 100] " +
                            "[--access-key K] [--zerotier-api URL --zerotier-token-file PATH] " +
                            "[--cs2-game-dir DIR --cs2-rcon HOST:PORT --cs2-rcon-password-file PATH]");
    return 2;
}
if (cs2Dir is not null)
{
    if (cs2Password is null)
    {
        Console.Error.WriteLine("--cs2-game-dir needs the server's RCON password (--cs2-rcon-password-file or ODX_CS2_RCON_PASSWORD)");
        return 2;
    }
    int colon = cs2Rcon.LastIndexOf(':');
    options = options with
    {
        Cs2Server = new Cs2ServerOptions(cs2Rcon[..colon], int.Parse(cs2Rcon[(colon + 1)..], CultureInfo.InvariantCulture), cs2Password,
            cs2Dir),
    };
}

var app = OrchestratorHost.Build(options with { OutRoot = outRoot, GameProfilePath = profile, Tolerances = tolerances });
await app.RunAsync();
return 0;
