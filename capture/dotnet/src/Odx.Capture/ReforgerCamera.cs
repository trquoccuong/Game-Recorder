using System.Globalization;
using Odx.Core;

namespace Odx.Capture;

/// <summary>One frame of the OdxCapture mod's camera log, in Enfusion axes (rows of GetWorldCameraTransform).</summary>
public sealed record ReforgerCameraRow(long TickMs, long UnixS, int ThirdPerson, Vec3 Right, Vec3 Up, Vec3 Forward, Vec3 Position,
    double VfovDeg);

/// <summary>
/// Arma Reforger's camera (document/games/arma-reforger.md §3, §5). While a round records, control.txt in the game
/// profile's odx folder names the round; the OdxCapture script mod then logs the render camera every frame to
/// odx/&lt;round&gt;.csv and ends it with "end" once control.txt is gone. The mod only has game time
/// (System.GetTickCount) and whole UTC seconds of the Windows clock (System.GetUnixTime), so each row bounds
/// UTC − tick to a 1 s interval and their intersection pins it; clock_samples.csv then maps the Windows clock onto QPC.
/// The result is camera_samples.csv + extractor.json, which <see cref="SessionBuilder"/> reads like the extractor DLL's.
/// The raw log format is pinned by dotnet/fixtures/reforger_camera.csv.
/// </summary>
public static class ReforgerCamera
{
    /// <summary>The game profile's camera_source for this route.</summary>
    public const string Source = "reforger-mod";
    public const string RawLog = "reforger_camera.csv";
    public const string Control = "control.txt";
    public const string EndLine = "end";

    /// <summary>UTC − tick is fitted per window this long, so the two clocks may drift apart over a long round.</summary>
    public const double WindowMs = 60_000;
    /// <summary>
    /// A window whose rows disagree by more than this (Windows clock step) is left out; it allows for a GetTickCount
    /// that only advances every 15.6 ms timer interrupt.
    /// </summary>
    public const double MaxConflictMs = 20;

    private static readonly string[] Header =
    [
        "tick_ms", "unix_s", "third_person", "right_x", "right_y", "right_z", "up_x", "up_y", "up_z", "fwd_x", "fwd_y", "fwd_z",
        "pos_x", "pos_y", "pos_z", "vfov_deg",
    ];

    /// <summary>
    /// The odx folder in the game profile that OdxReforger.exe starts the game with: its saved "profile" setting,
    /// else its default Documents\OdxReforger\profile; $profile: is the profile\ folder inside it.
    /// </summary>
    public static string OdxDir()
    {
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OdxReforger",
            "settings.txt");
        string? profile = File.Exists(settings)
            ? File.ReadLines(settings).Where(l => l.StartsWith("profile=", StringComparison.Ordinal)).Select(l => l[8..]).LastOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(profile))
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OdxReforger", "profile");
        return Path.Combine(profile, "profile", "odx");
    }

    public static void Begin(string odxDir, string round)
    {
        Directory.CreateDirectory(odxDir);
        File.WriteAllText(Path.Combine(odxDir, Control), round + "\n");
    }

    /// <summary>
    /// Ends the round for the mod, waits for it to close the log, moves the log into the capture folder and converts it.
    /// Returns a warning when there is no camera for this round; the capture itself is kept either way.
    /// </summary>
    public static string? Collect(string odxDir, string round, string captureDir, TimeSpan wait)
    {
        File.Delete(Path.Combine(odxDir, Control));
        string log = Path.Combine(odxDir, round + ".csv");
        var deadline = DateTime.UtcNow + wait;
        while (!Ended(log) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (!File.Exists(log)) return $"no Arma Reforger camera log in {odxDir} (game not started with OdxReforger.exe?)";
        string raw = Path.Combine(captureDir, RawLog);
        try
        {
            File.Copy(log, raw, overwrite: true);
            File.Delete(log);
        }
        catch (IOException e)
        {
            return $"cannot copy the Arma Reforger camera log: {e.Message}";
        }
        try
        {
            return Convert(captureDir);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
        {
            return $"Arma Reforger camera log not converted: {e.Message}";
        }
    }

    private static bool Ended(string log)
    {
        try
        {
            return File.Exists(log) && File.ReadLines(log).LastOrDefault()?.Trim() == EndLine;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>reforger_camera.csv + clock_samples.csv → camera_samples.csv + extractor.json; a warning or null.</summary>
    public static string? Convert(string captureDir)
    {
        var rows = Read(Path.Combine(captureDir, RawLog));
        if (rows.Count == 0) return "the Arma Reforger camera log is empty";
        string clockPath = Path.Combine(captureDir, "clock_samples.csv");
        if (!File.Exists(clockPath)) return "no clock_samples.csv to place the Arma Reforger camera on QPC";
        var clock = ClockMap.Fit(Csv.ReadFile(clockPath).Rows
            .Select(r => (Frames.ParseDouble(r["t_qpc_ms"]), Frames.ParseDouble(r["utc_unix_ms"]))).ToList());
        var utcMinusTick = FitUtcMinusTick(rows);
        if (utcMinusTick.Count == 0) return "the Arma Reforger camera log's clock rows disagree; camera left out";

        using (var w = new StreamWriter(Path.Combine(captureDir, "camera_samples.csv")))
        {
            w.Write(Csv.Line(["t_qpc_ms", "pos_x", "pos_y", "pos_z", "fwd_x", "fwd_y", "fwd_z", "right_x", "right_y", "right_z",
                "up_x", "up_y", "up_z", "fov_deg"]));
            foreach (var r in rows)
            {
                double utc = r.TickMs + Interpolate(utcMinusTick, r.TickMs);
                double qpc = clock.QpcRefMs + (utc - clock.UtcAtRefMs) / clock.Slope;
                w.Write(Csv.Line(new[] { qpc }
                    .Concat(new[] { r.Position, r.Forward, r.Right, r.Up }.SelectMany(v => new[] { v.X, v.Y, v.Z }))
                    .Append(r.VfovDeg).Select(Frames.FormatDouble)));
            }
        }
        File.WriteAllText(Path.Combine(captureDir, "extractor.json"), """
            {
              "game": "Arma Reforger",
              "engine": "Enfusion",
              "convention": "unity",
              "units_to_world": 1.0,
              "fov_axis": "vertical",
              "camera_model": "PINHOLE"
            }

            """);
        return null;
    }

    public static List<ReforgerCameraRow> Read(string path)
    {
        var lines = File.ReadLines(path).Where(l => l.Length > 0 && l.Trim() != EndLine).ToList();
        if (lines.Count == 0 || lines[0].Trim() != string.Join(",", Header))
            throw new InvalidDataException($"{path}: unexpected header");
        var rows = new List<ReforgerCameraRow>(lines.Count - 1);
        for (int k = 1; k < lines.Count; k++)
        {
            var f = lines[k].Split(',');
            if (f.Length != Header.Length)
            {
                if (k == lines.Count - 1) break;  // cut short: the game was closed mid-round
                throw new InvalidDataException($"{path}: row with {f.Length} fields: {lines[k]}");
            }
            double D(int i) => Frames.ParseDouble(f[i]);
            Vec3 V(int i) => new(D(i), D(i + 1), D(i + 2));
            rows.Add(new ReforgerCameraRow(long.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture),
                int.Parse(f[2], CultureInfo.InvariantCulture), V(3), V(6), V(9), V(12), D(15)));
        }
        return rows;
    }

    /// <summary>
    /// (tick, UTC − tick) at the middle of each window. A row read at tick t during UTC second s says
    /// s·1000 − t ≤ UTC − tick &lt; s·1000 + 1000 − t; the rows next to a second rollover bound it tightly.
    /// </summary>
    public static List<(double TickMs, double OffsetMs)> FitUtcMinusTick(IReadOnlyList<ReforgerCameraRow> rows)
    {
        var points = new List<(double, double)>();
        foreach (var window in rows.GroupBy(r => Math.Floor((r.TickMs - rows[0].TickMs) / WindowMs)))
        {
            double low = window.Max(r => r.UnixS * 1000.0 - r.TickMs), high = window.Min(r => r.UnixS * 1000.0 + 1000 - r.TickMs);
            if (low > high + MaxConflictMs) continue;
            points.Add((window.Average(r => (double)r.TickMs), (low + high) / 2));
        }
        return points;
    }

    private static double Interpolate(List<(double TickMs, double OffsetMs)> points, double tick)
    {
        if (points.Count == 1 || tick <= points[0].TickMs) return points[0].OffsetMs;
        for (int i = 1; i < points.Count; i++)
        {
            var (t0, o0) = points[i - 1];
            var (t1, o1) = points[i];
            if (tick <= t1) return o0 + (o1 - o0) * (tick - t0) / (t1 - t0);
        }
        return points[^1].OffsetMs;
    }
}
