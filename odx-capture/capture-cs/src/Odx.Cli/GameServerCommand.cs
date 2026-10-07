// odx game-server --orchestrator http://<host>:5180 --cs2-game-dir <cs2>/game/csgo --cs2-rcon-password-file <file>
//                 [--cs2-rcon <address>:27015] [--work <dir>] [--id <id>] [--name <name>] [--access-key K]
// The CS2 dedicated server's agent (GameServerAgent), on the server's machine: records the server's side of every round the
// orchestrator starts for it (SourceTV demo + odx_netlog log) and keeps it in --work. --id: this server's name for the
// orchestrator when several run (rooms are opened for one with POST /rooms?server=<id>; default game-server).
// --cs2-rcon: where RCON listens (default: this machine's address, port 27015; CS2 binds RCON to the network address, not
// to 127.0.0.1). The RCON password
// may come from ODX_CS2_RCON_PASSWORD, the access key from ODX_ACCESS_KEY or access_key of odx-capture.json next to odx.
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Odx.Games.Cs2;
using Odx.Session;

namespace Odx.Cli;

public static class GameServerCommand
{
    public static int Run(string[] args)
    {
        string? orchestrator = null, gameDir = null, rcon = null, password = Environment.GetEnvironmentVariable("ODX_CS2_RCON_PASSWORD");
        string? work = null, name = null, id = null, accessKey = Environment.GetEnvironmentVariable("ODX_ACCESS_KEY") is { Length: > 0 } k ? k : null;
        bool usage = false;
        for (int i = 0; i < args.Length && !usage; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--orchestrator": orchestrator = Next(); break;
                case "--cs2-game-dir": gameDir = Next(); break;
                case "--cs2-rcon": rcon = Next(); break;
                case "--cs2-rcon-password-file": password = Next() is { } file ? File.ReadAllText(file).Trim() : null; break;
                case "--work": work = Next(); break;
                case "--id": id = Next(); break;
                case "--name": name = Next(); break;
                case "--access-key": accessKey = Next(); break;
                default: usage = true; break;
            }
        }
        if (usage || orchestrator is null || gameDir is null || password is null ||
            !Uri.TryCreate(orchestrator, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("usage: odx game-server --orchestrator http://<host>:5180 --cs2-game-dir DIR --cs2-rcon-password-file PATH " +
                                    "[--cs2-rcon ADDRESS:27015] [--work DIR] [--id ID] [--name NAME] [--access-key K]");
            return 2;
        }
        rcon ??= $"{LocalAddress()}:27015";
        int colon = rcon.LastIndexOf(':');
        var cs2 = new Cs2ServerOptions(rcon[..colon], int.Parse(rcon[(colon + 1)..], CultureInfo.InvariantCulture), password, gameDir);

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss "));
        var log = logging.CreateLogger("game-server");
        var agent = new GameServerAgent(new GameServerAgentOptions(uri,
            work ?? Path.Combine(Path.GetTempPath(), "odx-game-server"), accessKey ?? Odx.Core.CaptureConfig.Load().AccessKey,
            name ?? "CS2 server", id ?? "game-server"), new Cs2Server(cs2, log), log);
        agent.RunUntilStoppedAsync(stop.Token).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>The IPv4 address of the first network interface that is up and not loopback.</summary>
    private static IPAddress LocalAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
}
