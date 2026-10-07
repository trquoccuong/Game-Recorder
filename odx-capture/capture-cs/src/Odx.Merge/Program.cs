// odx-merge [--out <dir>] [--staging <dir>] [--vendor odx] [--http-port 5190] [--min-valid K] [--max-rtt-ms 100]
//           [--access-key K]
// --out: where sessions go; by default the package's data\delivery\, with rounds staged in data\staging\ (PackageFolders).
// --access-key (or the ODX_ACCESS_KEY environment variable): the orchestrator, the players and the game server agent must
// send it.
// Receives each round's uploads and assembles it into a v3 session when the orchestrator asks (MergeHost).
using System.Globalization;
using Odx.Core;
using Odx.Merge;
using Odx.Session;

string? outRoot = null, staging = null;
var options = new MergeOptions("") { AccessKey = Environment.GetEnvironmentVariable("ODX_ACCESS_KEY") };
var tolerances = GateTolerances.Default;
bool usage = false;
for (int i = 0; i < args.Length && !usage; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);
    switch (args[i])
    {
        case "--out": outRoot = Next(); break;
        case "--staging": staging = Next(); break;
        case "--vendor": options = options with { Vendor = Next() }; break;
        case "--http-port": options = options with { HttpPort = NextInt() }; break;
        case "--min-valid": tolerances = tolerances with { MinValidPlayers = NextInt() }; break;
        case "--max-rtt-ms": tolerances = tolerances with { MaxRttMs = NextInt() }; break;
        case "--access-key": options = options with { AccessKey = Next() }; break;
        default: usage = true; break;
    }
}
if (usage)
{
    Console.Error.WriteLine("usage: odx-merge [--out DIR] [--staging DIR] [--vendor odx] [--http-port 5190] [--min-valid K] " +
                            "[--max-rtt-ms 100] [--access-key K]");
    return 2;
}
options = options with
{
    OutRoot = outRoot ?? PackageFolders.Delivery,
    StagingRoot = staging ?? (outRoot is null ? PackageFolders.Staging : null),
    Tolerances = tolerances,
};

var app = MergeHost.Build(options);
app.Logger.LogInformation("odx-merge {Version} on port {Port}, sessions to {Out}{Key}", BuildInfo.Version, options.HttpPort,
    options.OutRoot, options.AccessKey is null ? ", no access key" : "");
await app.RunAsync();
return 0;
