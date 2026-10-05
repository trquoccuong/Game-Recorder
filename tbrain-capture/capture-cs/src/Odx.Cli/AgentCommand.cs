// odx agent --orchestrator http://<host>:5180 --player <id> --out <dir> [--name <name>] [--window <process>]
// One player's capture machine under odx-orchestrator (spec v3): records each round when told to, uploads it, repeats.
// Reconnects every 5 s while the orchestrator is unreachable; Ctrl+C quits.
#if WINDOWS
using Odx.Session;

namespace Odx.Cli;

public static class AgentCommand
{
    public static int Run(string[] args)
    {
        string? orchestrator = null, player = null, outRoot = null, name = null, window = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--orchestrator" when i + 1 < args.Length: orchestrator = args[++i]; break;
                case "--player" when i + 1 < args.Length: player = args[++i]; break;
                case "--out" when i + 1 < args.Length: outRoot = args[++i]; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--window" when i + 1 < args.Length: window = args[++i]; break;
                default: orchestrator = null; i = args.Length; break;
            }
        }
        if (orchestrator is null || player is null || outRoot is null ||
            !Uri.TryCreate(orchestrator, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("usage: odx agent --orchestrator http://<host>:5180 --player <id> --out <dir> [--name <name>] [--window <process>]");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var agent = new AgentClient(new AgentOptions(uri, player, name ?? player, outRoot, window), CaptureRun.Start);
        agent.Log += line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
        agent.RunUntilStoppedAsync(stop.Token).GetAwaiter().GetResult();
        return 0;
    }
}
#endif
