using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Odx.Core;

namespace Odx.Session;

public sealed record Issue(string Rule, string Severity, string Message)
{
    public static Issue Error(string rule, string message) => new(rule, "error", message);
    public static Issue Warning(string rule, string message) => new(rule, "warning", message);
}

/// <summary>Vendor validation checks for one recording (Capture Spec v2 §1.5).</summary>
public static partial class SessionValidator
{
    // Not fixed by the spec; listed under "Decisions pending with Client" in the README.
    public const double RotationTol = 1e-3;
    public const double PrincipalPointTolPx = 4.0;
    public const double LevelRollTol = 0.05;

    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        using var stream = typeof(SessionValidator).Assembly.GetManifestResourceStream("session_schema.json")!;
        return JsonSchema.FromText(new StreamReader(stream).ReadToEnd());
    });

    public static List<Issue> ValidateSession(string sessionDir, bool video = true)
    {
        JsonObject session;
        try
        {
            session = JsonNode.Parse(File.ReadAllText(Path.Combine(sessionDir, "session.json"))) as JsonObject
                      ?? throw new JsonException("session.json is not a JSON object");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [Issue.Error("session.unreadable", e.Message)];
        }
        var issues = CheckSession(session);

        List<string> header;
        List<Dictionary<string, string>> rows;
        try
        {
            (header, rows) = Csv.ReadFile(Path.Combine(sessionDir, "frames.csv"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(Issue.Error("frames.unreadable", e.Message));
            return issues;
        }
        try
        {
            issues.AddRange(CheckFrames(session, header, rows));
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            issues.Add(Issue.Error("frames.unparseable", $"could not parse frames.csv: {e.Message}"));
        }
        if (video) issues.AddRange(CheckVideo(session, rows.Count, Path.Combine(sessionDir, "video.mp4")));
        return issues;
    }

    public static List<Issue> CheckSession(JsonObject session)
    {
        var result = Schema.Value.Evaluate(session, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        var issues = FailedNodes(result)
            .SelectMany(d => d.Errors!.Values.Select(msg => Issue.Error("session.schema",
                $"{Location(d.InstanceLocation.ToString())}: {msg.Replace("\\u0022", "")}")))
            .Distinct()
            .ToList();

        if (!TryTimestamp(session, "created_at_utc", out var start, out bool startAware) ||
            !TryTimestamp(session, "ended_at_utc", out var end, out bool endAware))
            return issues;
        if (!startAware || !endAware)
        {
            issues.Add(Issue.Error("session.timezone", "timestamps must be timezone-aware (Z or ±HH:MM)"));
            return issues;
        }
        if (end <= start)
            issues.Add(Issue.Error("session.end_after_start", "ended_at_utc must be after created_at_utc"));

        if (Number(session, "duration_ms") is not double durationMs || Number(session, "duration_seconds") is not double durationS ||
            Number(session, "fps") is not double fps || Number(session, "frame_count") is not double frameCount)
            return issues;
        frameCount = Math.Truncate(frameCount);
        if (Math.Abs(durationMs / 1000 - durationS) > 1)
            issues.Add(Issue.Error("session.duration_units",
                $"duration_ms/1000={Fmt(durationMs / 1000)} vs duration_seconds={Fmt(durationS)}"));
        double wallMs = (end - start).TotalMilliseconds;
        if (Math.Abs(durationMs - wallMs) > 1000)
            issues.Add(Issue.Error("session.duration_timestamps", $"duration_ms={Fmt(durationMs)} vs ended-created={Fmt(wallMs)}"));
        if (Math.Abs(frameCount - fps * durationS) > 2)
            issues.Add(Issue.Error("session.frame_count_fps",
                $"frame_count={frameCount} vs fps*duration={(fps * durationS).ToString("F1", CultureInfo.InvariantCulture)}"));
        return issues;
    }

    public static List<Issue> CheckFrames(JsonObject session, List<string> header, List<Dictionary<string, string>> rows)
    {
        var issues = new List<Issue>();
        var missing = Frames.Columns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            issues.Add(Issue.Error("frames.headers", $"missing columns: {string.Join(", ", missing)}"));
            return issues;
        }
        if (Number(session, "frame_count") is not double frameCount || rows.Count != frameCount)
            issues.Add(Issue.Error("frames.row_count", $"{rows.Count} rows vs frame_count={Raw(session, "frame_count")}"));
        if (rows.Count == 0) return issues;

        var badIds = rows.Select((r, i) => (Id: long.Parse(r["frame_id"], CultureInfo.InvariantCulture), i))
            .Where(x => x.Id != x.i).Select(x => x.i).ToList();
        if (badIds.Count > 0)
            issues.Add(Issue.Error("frames.frame_id_sequence", $"frame_id not 0..N-1: {Summary(badIds)}"));

        var ts = rows.Select(r => Frames.ParseDouble(r["timestamp_ms"])).ToList();
        double interval = 1000.0 / (Number(session, "fps") is double fps && fps != 0 ? fps : 1);
        var nonIncreasing = new List<int>();
        var irregular = new List<int>();
        for (int i = 1; i < ts.Count; i++)
        {
            double dt = ts[i] - ts[i - 1];
            if (dt <= 0) nonIncreasing.Add(i);
            else if (Math.Abs(dt - interval) > Math.Max(1.0, 0.25 * interval)) irregular.Add(i);
        }
        if (nonIncreasing.Count > 0)
            issues.Add(Issue.Error("frames.timestamp_monotonic", $"timestamp_ms not increasing: {Summary(nonIncreasing)}"));
        if (Math.Abs(ts[0]) > interval)
            issues.Add(Issue.Error("frames.timestamp_start", $"timestamp_ms[0]={Fmt(ts[0])} should be ≈ 0"));
        if (Number(session, "duration_ms") is double durationMs && Math.Abs(ts[^1] - durationMs) > interval + 1)
            issues.Add(Issue.Error("frames.timestamp_end", $"timestamp_ms[-1]={Fmt(ts[^1])} vs duration_ms={Fmt(durationMs)}"));
        if (irregular.Count > 0)
            issues.Add(Issue.Error("frames.frame_interval",
                $"spacing deviates from {interval.ToString("F2", CultureInfo.InvariantCulture)} ms: {Summary(irregular)}"));

        issues.AddRange(CheckCamera(session, rows));
        issues.AddRange(CheckInput(rows));
        return issues;
    }

    private static List<Issue> CheckCamera(JsonObject session, List<Dictionary<string, string>> rows)
    {
        var issues = new List<Issue>();
        string[] cameraCols = [.. Frames.C2wColumns, "camera_model", .. Frames.IntrinsicColumns];
        if (rows.All(r => cameraCols.All(c => r[c] == Frames.Null)))
            return issues;  // session without camera data: all camera columns null
        var sparse = rows.Select((r, i) => (r, i)).Where(x => cameraCols.Any(c => x.r[c] == Frames.Null)).Select(x => x.i).ToList();
        if (sparse.Count > 0)
            issues.Add(Issue.Error("camera.sparse_nulls", $"camera columns null mid-session: {Summary(sparse)}"));

        var bad = new Dictionary<string, List<int>>();
        void Flag(string key, int frame) => (bad.TryGetValue(key, out var l) ? l : bad[key] = []).Add(frame);
        double? width = Number(session, "record_width_px"), height = Number(session, "record_height_px");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r["camera_model"] != Frames.Null && !Frames.CameraModels.Contains(r["camera_model"])) Flag("model", i);
            if (Frames.C2wColumns.Any(c => r[c] == Frames.Null)) continue;
            var m = new double[4, 4];
            for (int k = 0; k < 16; k++) m[k / 4, k % 4] = Frames.ParseDouble(r[Frames.C2wColumns[k]]);
            if (!(m[3, 0] == 0 && m[3, 1] == 0 && m[3, 2] == 0 && m[3, 3] == 1)) Flag("bottom_row", i);
            var (detErr, orthoErr) = Geometry.RotationErrors(m);
            if (detErr > RotationTol) Flag("det", i);
            if (orthoErr > RotationTol) Flag("orthonormal", i);
            if (Math.Abs(m[2, 0]) > LevelRollTol) Flag("roll", i);
            if (m[2, 1] <= 0) Flag("upright", i);
            if (Frames.IntrinsicColumns.Any(c => r[c] == Frames.Null)) continue;
            double fx = Frames.ParseDouble(r["camera_fx"]), fy = Frames.ParseDouble(r["camera_fy"]);
            double cx = Frames.ParseDouble(r["camera_cx"]), cy = Frames.ParseDouble(r["camera_cy"]);
            if ((width is > 0 and var w && Math.Abs(cx - w / 2) > PrincipalPointTolPx) ||
                (height is > 0 and var h && Math.Abs(cy - h / 2) > PrincipalPointTolPx))
                Flag("principal_point", i);
            if (Math.Abs(fx - fy) > 1e-3 * Math.Abs(fy)) Flag("fx_fy", i);
        }

        var rules = new (string Key, string Rule, string Severity, string Message)[]
        {
            ("model", "camera.model", "error", $"camera_model must be one of {string.Join(", ", Frames.CameraModels)}"),
            ("bottom_row", "c2w.bottom_row", "error", "bottom row must be exactly (0, 0, 0, 1)"),
            ("det", "c2w.det", "error", "det(R) must be +1 (right-handed)"),
            ("orthonormal", "c2w.orthonormal", "error", "R·Rᵀ must be identity"),
            ("principal_point", "camera.principal_point", "error", "principal point must be near the image centre"),
            ("fx_fy", "camera.fx_fy", "error", "fx must equal fy for square pixels"),
            ("roll", "c2w.roll", "warning", "c2w_m20 not ≈ 0 (camera roll)"),
            ("upright", "c2w.upright", "warning", "c2w_m21 <= 0 (camera not upright)"),
        };
        foreach (var (key, rule, severity, message) in rules)
            if (bad.TryGetValue(key, out var frames))
                issues.Add(new Issue(rule, severity, $"{message}: {Summary(frames)}"));
        return issues;
    }

    private static List<Issue> CheckInput(List<Dictionary<string, string>> rows)
    {
        var bad = new Dictionary<string, List<int>>();
        void Flag(string key, int frame) => (bad.TryGetValue(key, out var l) ? l : bad[key] = []).Add(frame);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double? dx = NullableDouble(r["input_mouse_dx"]), dy = NullableDouble(r["input_mouse_dy"]);
            if (dx is null || dy is null) Flag("delta", i);
            string keys = r["input_keys"], actions = r["input_actions"], buttons = r["input_mouse_buttons"];
            if (keys != Frames.Null && actions == Frames.Null) Flag("keys_without_actions", i);
            if (keys == Frames.Null && buttons == Frames.Null && dx == 0 && dy == 0 && actions != Frames.Null)
                Flag("no_input_null", i);
            if (new[] { keys, actions, buttons }.Any(v =>
                    v != Frames.Null && (v.Contains(',') || v.Split('|').Any(t => t.Trim().Length == 0))))
                Flag("delimiter", i);
        }

        var rules = new (string Key, string Rule, string Message)[]
        {
            ("delta", "input.mouse_delta", "input_mouse_dx/dy must be numeric, \"0.0\" when no movement"),
            ("keys_without_actions", "input.keys_without_actions", "active input_keys need non-null input_actions"),
            ("no_input_null", "input.no_input_null", "frames with no input must have null input_actions"),
            ("delimiter", "input.delimiter", "multi-value fields must be pipe-delimited with no empty tokens"),
        };
        return rules.Where(x => bad.ContainsKey(x.Key))
            .Select(x => Issue.Error(x.Rule, $"{x.Message}: {Summary(bad[x.Key])}")).ToList();
    }

    public static List<Issue> CheckVideo(JsonObject session, int rowCount, string videoPath)
    {
        if (!File.Exists(videoPath)) return [Issue.Error("video.missing", $"{Path.GetFileName(videoPath)} not found")];
        VideoInfo v;
        try
        {
            v = ProbeVideo(videoPath);
        }
        catch (Win32Exception)
        {
            return [Issue.Warning("video.ffprobe_unavailable", "ffprobe not installed; video checks skipped")];
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or FormatException or KeyNotFoundException
                                       or NullReferenceException)
        {
            return [Issue.Error("video.unreadable", $"ffprobe failed: {e.Message}")];
        }
        var issues = new List<Issue>();
        double? width = Number(session, "record_width_px"), height = Number(session, "record_height_px");
        if (v.Width != width || v.Height != height)
            issues.Add(Issue.Error("video.resolution",
                $"video {v.Width}x{v.Height} vs session {Raw(session, "record_width_px")}x{Raw(session, "record_height_px")}"));
        if (v.Frames != rowCount)
            issues.Add(Issue.Error("video.frame_count", $"video has {v.Frames} frames vs {rowCount} frames.csv rows"));
        if (Number(session, "duration_seconds") is double durationS && Math.Abs(v.Duration - durationS) > 1)
            issues.Add(Issue.Error("video.duration",
                $"video duration {v.Duration.ToString("F2", CultureInfo.InvariantCulture)}s vs duration_seconds={Fmt(durationS)}"));
        return issues;
    }

    private sealed record VideoInfo(int Width, int Height, long Frames, double Duration);

    private static VideoInfo ProbeVideo(string path)
    {
        var psi = new ProcessStartInfo(ExternalTools.Ffmpeg("ffprobe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries",
                     "stream=width,height,nb_read_packets:format=duration", "-of", "json", path })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        var data = JsonNode.Parse(stdout)!;
        var stream = data["streams"]![0]!;
        return new VideoInfo(stream["width"]!.GetValue<int>(), stream["height"]!.GetValue<int>(),
            long.Parse(stream["nb_read_packets"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            Frames.ParseDouble(data["format"]!["duration"]!.GetValue<string>()));
    }

    // Descends only into failing nodes: the branches a passing oneOf rejected are not errors.
    private static IEnumerable<EvaluationResults> FailedNodes(EvaluationResults node)
    {
        if (node.IsValid) yield break;
        if (node.Errors is { Count: > 0 }) yield return node;
        if (!node.HasDetails) yield break;
        foreach (var child in node.Details)
            foreach (var failed in FailedNodes(child))
                yield return failed;
    }

    private static bool TryTimestamp(JsonObject session, string key, out DateTimeOffset value, out bool aware)
    {
        value = default;
        aware = false;
        if (session[key] is not JsonValue v || !v.TryGetValue(out string? text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
            return false;
        aware = ZoneSuffix().IsMatch(text);
        return true;
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ZoneSuffix();

    private static double? Number(JsonObject session, string key) =>
        session[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static string Raw(JsonObject session, string key) => session[key]?.ToJsonString() ?? "None";

    private static double? NullableDouble(string s) =>
        s != Frames.Null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string Fmt(double v) => Frames.FormatDouble(v);

    private static string Location(string pointer) => pointer.TrimStart('/') is { Length: > 0 } p ? p : "<root>";

    private static string Summary(List<int> frames) => $"{frames.Count} frame(s), first at frame {frames[0]}";
}
