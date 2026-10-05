// odx agent --orchestrator http://<host>:5180 --room <invite code> --player <id> --out <dir> [--name <name>] [--window <process>]
//           [--auto-ready]
// One player's capture machine in a room of odx-orchestrator (spec v3): records each round when told to, uploads it, repeats.
// Reconnects every 5 s while the orchestrator is unreachable; Ctrl+C quits. --auto-ready: says ready on connect and after
// every round, so a headless machine does not keep the host waiting. The access key is access_key of odx-capture.json
// next to odx, or --access-key.
#if WINDOWS
using Odx.Session;

namespace Odx.Cli;

public static class AgentCommand
{
    public static int Run(string[] args)
    {
        string? orchestrator = null, room = null, player = null, outRoot = null, name = null, window = null;
        bool autoReady = false;
        string? accessKey = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--orchestrator" when i + 1 < args.Length: orchestrator = args[++i]; break;
                case "--room" when i + 1 < args.Length: room = args[++i]; break;
                case "--player" when i + 1 < args.Length: player = args[++i]; break;
                case "--out" when i + 1 < args.Length: outRoot = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--window" when i + 1 < args.Length: window = args[++i]; break;
                case "--auto-ready": autoReady = true; break;
                case "--access-key" when i + 1 < args.Length: accessKey = args[++i]; break;
                default: orchestrator = null; i = args.Length; break;
            }
        }
        if (orchestrator is null || room is null || player is null || outRoot is null ||
            !Uri.TryCreate(orchestrator, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("usage: odx agent --orchestrator http://<host>:5180 --room <invite code> --player <id> --out <dir> " +
                                    "[--name <name>] [--window <process>] [--auto-ready] [--access-key K]");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var agent = new AgentClient(new AgentOptions(uri, player, name ?? player, outRoot, window, RoomCode: room,
            AutoReady: autoReady, AccessKey: accessKey ?? Odx.Core.CaptureConfig.Load().AccessKey), CaptureRun.Start);
        agent.Log += line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        agent.PacketCaptureWanted += _ =>
        {
            if (PktmonCapture.IsElevated) agent.Packets ??= new PktmonCapture();
            else Console.WriteLine($"{DateTime.Now:HH:mm:ss} this game's packets are recorded only when odx agent runs as administrator");
        };
        agent.RunUntilStoppedAsync(stop.Token).GetAwaiter().GetResult();
        return 0;
    }
}
#endif
