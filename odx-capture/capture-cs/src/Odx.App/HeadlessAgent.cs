// odx-app --headless --room <invite code> --player <id> [--auto-ready] [--name <name>] [--window <process>] [--out <dir>]
//         [--orchestrator http://<host>:5180] [--access-key K]
// One player's capture machine in a room, without a window: records each round when told to into --out (default: the
// package's data\captures\), keeps it, repeats. Reconnects every 5 s while the orchestrator is unreachable; Ctrl+C quits.
// --auto-ready: says ready on connect and after every round, so a headless machine does not keep the host waiting. The
// orchestrator and the access key default to odx-capture.json next to odx-app.
using System.IO;
using System.Runtime.InteropServices;
using Odx.Core;
using Odx.Session;

namespace Odx.App;

public static class HeadlessAgent
{
    public static bool Wanted(string[] args) => args.Contains("--headless");

    public static int Run(string[] args)
    {
        AttachConsole(AttachParentProcess);
        var config = CaptureConfig.Load();
        string? room = null, player = null, name = null, window = null, accessKey = config.AccessKey;
        string outRoot = PackageFolders.Captures;
        Uri? orchestrator = config.Orchestrator;
        bool autoReady = false, usage = false;
        for (int i = 0; i < args.Length && !usage; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--headless": break;
                case "--room": room = Next(); break;
                case "--player": player = Next(); break;
                case "--name": name = Next(); break;
                case "--window": window = Next(); break;
                case "--out": outRoot = Next() ?? outRoot; break;
                case "--orchestrator": orchestrator = Uri.TryCreate(Next(), UriKind.Absolute, out var uri) ? uri : null; break;
                case "--access-key": accessKey = Next(); break;
                case "--auto-ready": autoReady = true; break;
                default: usage = true; break;
            }
        }
        if (usage || room is null || player is null || orchestrator is null)
        {
            Console.Error.WriteLine("usage: odx-app --headless --room <invite code> --player <id> [--auto-ready] [--name <name>] " +
                                    "[--window <process>] [--out <dir>] [--orchestrator URL] [--access-key K]");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var agent = new AgentClient(new AgentOptions(orchestrator, player, name ?? player, Path.GetFullPath(outRoot), window,
            RoomCode: room, AutoReady: autoReady, AccessKey: accessKey), CaptureRun.Start);
        agent.Log += line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {player}: {line}");
        agent.PacketCaptureWanted += _ =>
        {
            if (PktmonCapture.IsElevated) agent.Packets ??= new PktmonCapture();
            else Console.WriteLine($"{DateTime.Now:HH:mm:ss} {player}: this game's packets are recorded only when run as administrator");
        };
        // Off WPF's thread: its continuations would wait for the dispatcher this call blocks.
        Task.Run(() => agent.RunUntilStoppedAsync(stop.Token)).GetAwaiter().GetResult();
        return 0;
    }

    private const int AttachParentProcess = -1;

    // odx-app is a Windows application: without this its output goes nowhere when started from a console.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
