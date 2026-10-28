// odx merge <folder> [--out DIR]
// <folder> holds one session folder per round in the staging layout (round.json, server_events.jsonl, game_profile.json,
// players/<id>/, server/), copied by hand from the participants; a session folder itself works too. Each is assembled into
// <out>/<vendor>/<date>/<game>/<session>/ (default out: the package's data\delivery). Exit code 1 if any session fails the gate.
using Odx.Core;
using Odx.Games.Cs2;
using Odx.Games.Csgo;
using Odx.Games.Wc3;
using Odx.Merge;
using Odx.Protocol;

namespace Odx.Cli;

public static class MergeCommand
{
    public static string Vendor => CaptureConfig.Load().VendorName.ToLowerInvariant();

    /// <summary>What odx merge knows of each game: its server side (IGameMerger) and its players' traffic (IPacketDecoder).</summary>
    public static readonly GameModules Games = new([new Cs2Merger()], [new CsgoPacketDecoder(), new W3gsPacketDecoder()]);

    public static int Run(string[] args)
    {
        string? folder = null, outRoot = PackageFolders.Delivery;
        bool usage = false;
        for (int i = 0; i < args.Length && !usage; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outRoot = args[++i]; break;
                case var a when !a.StartsWith("--", StringComparison.Ordinal) && folder is null: folder = a; break;
                default: usage = true; break;
            }
        }
        if (usage || folder is null || !Directory.Exists(folder))
        {
            Console.Error.WriteLine("usage: odx merge <folder> [--out DIR]");
            return 2;
        }

        var sessions = Sessions(folder);
        if (sessions.Count == 0)
        {
            Console.Error.WriteLine($"no session folder (with {RoundRecord.FileName}) in {folder}");
            return 2;
        }
        bool allPass = true;
        foreach (string staging in sessions)
        {
            var (dir, report) = MultiplayerSession.Assemble(staging, outRoot, Vendor, games: Games);
            allPass &= report.Pass;
            Console.WriteLine($"{(report.Pass ? "PASS" : "FAIL")}  {report.SessionId}  {report.ValidPlayers}/{report.Players.Count} players " +
                              $"valid, {report.RequiredPlayers} required -> {dir}");
            foreach (var i in report.Issues) Console.WriteLine($"  [{i.Severity}] {i.Rule}: {i.Message}");
            foreach (var p in report.Players)
                foreach (var i in p.Issues) Console.WriteLine($"  {p.PlayerId} [{i.Severity}] {i.Rule}: {i.Message}");
        }
        return allPass ? 0 : 1;
    }

    public static List<string> Sessions(string folder) =>
        File.Exists(Path.Combine(folder, RoundRecord.FileName))
            ? [folder]
            : Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, RoundRecord.FileName))).Order().ToList();
}
