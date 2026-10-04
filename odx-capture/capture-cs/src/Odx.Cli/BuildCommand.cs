// odx build <capture_dir> --profile <game.json> [--vendor <name>] [--camera <dir>]
// Turns an `odx input` + `odx record` capture folder into a spec v2 session (frames.csv + session.json next to video.mp4),
// ready for `odx validate`. --camera: folder with the extractor DLL's camera_samples.csv + extractor.json (default: the
// capture folder; without them the camera columns are null). Exit code 1 if the folder cannot be built.
using Odx.Session;

namespace Odx.Cli;

public static class BuildCommand
{
    public static int Run(string[] args)
    {
        string? dir = null, profilePath = null, vendor = "odx", cameraDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length: profilePath = args[++i]; break;
                case "--vendor" when i + 1 < args.Length: vendor = args[++i]; break;
                case "--camera" when i + 1 < args.Length: cameraDir = args[++i]; break;
                case var a when !a.StartsWith("--", StringComparison.Ordinal) && dir is null: dir = a; break;
                default: dir = null; i = args.Length; break;
            }
        }
        if (dir is null || profilePath is null)
        {
            Console.Error.WriteLine("usage: odx build <capture_dir> --profile <game.json> [--vendor <name>] [--camera <dir>]");
            return 2;
        }

        try
        {
            var report = SessionBuilder.Build(dir, GameProfile.Load(profilePath), vendor, cameraDir);
            Console.WriteLine($"built {report.SessionId}: {report.FrameCount} frames, {report.DurationMs / 1000:F1} s, " +
                              $"camera on {report.CameraFrames} frames");
            var c = report.Clock;
            Console.WriteLine(c.Source == "ntp"
                ? $"clock: NTP {c.Server}, RTT {c.RttMs:F1} ms, Windows clock off by {c.SystemOffsetMs:F1} ms"
                : $"clock: {c.Source} (Windows clock, not checked against NTP; capture with odx input --ntp <server>)");
            if (report.UnboundKeys.Count > 0)
                Console.WriteLine("unbound keys left out of input_keys: " +
                                  string.Join(", ", report.UnboundKeys.Select(kv => $"{kv.Key} ×{kv.Value}")));
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"cannot build {dir}: {e.Message}");
            return 1;
        }
    }
}
