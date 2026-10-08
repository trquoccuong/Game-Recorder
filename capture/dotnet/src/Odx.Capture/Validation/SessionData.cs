using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Odx.Core;

namespace Odx.Capture;

public sealed record VideoInfo(int Width, int Height, long Frames, double Duration);

/// <summary>A session parsed once for all rules. Frame columns are parsed eagerly so a malformed value fails up front.</summary>
internal sealed partial class SessionData
{
    public static readonly string[] CameraColumns = [.. Frames.C2wColumns, "camera_model", .. Frames.IntrinsicColumns];

    public JsonObject Session { get; }
    public ValidationTolerances Tol { get; }

    public bool TimestampsParsed { get; }
    public bool TimezoneAware { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public double? DurationMs => Number("duration_ms");
    public double? DurationSeconds => Number("duration_seconds");
    public double? Fps => Number("fps");
    public double? FrameCount => Number("frame_count") is double n ? Math.Truncate(n) : null;
    public double? RecordWidth => Number("record_width_px");
    public double? RecordHeight => Number("record_height_px");

    public List<string> Header { get; private set; } = [];
    public List<Dictionary<string, string>> Rows { get; private set; } = [];
    public bool HeaderComplete { get; private set; }
    public long[] FrameIds { get; private set; } = [];
    public double[] Timestamps { get; private set; } = [];
    /// <summary>Per row: the C2W matrix, or null when any c2w column is null.</summary>
    public double[,]?[] C2w { get; private set; } = [];
    /// <summary>Per row: intrinsics when the C2W and all four intrinsic columns are present.</summary>
    public Intrinsics?[] Intrinsics { get; private set; } = [];
    public bool HasCamera { get; private set; }
    public double Interval => 1000.0 / (Fps is double fps && fps != 0 ? fps : 1);

    public VideoInfo? Video { get; set; }

    public SessionData(JsonObject session, ValidationTolerances tol)
    {
        Session = session;
        Tol = tol;
        if (TryTimestamp("created_at_utc", out var start, out bool startAware) &&
            TryTimestamp("ended_at_utc", out var end, out bool endAware))
        {
            TimestampsParsed = true;
            TimezoneAware = startAware && endAware;
            (Start, End) = (start, end);
        }
    }

    /// <exception cref="FormatException">A frame_id, timestamp, C2W or intrinsic value is not a number.</exception>
    public void LoadFrames(List<string> header, List<Dictionary<string, string>> rows)
    {
        (Header, Rows) = (header, rows);
        HeaderComplete = Frames.Columns.All(header.Contains);
        if (!HeaderComplete) return;
        FrameIds = rows.Select(r => long.Parse(r["frame_id"], CultureInfo.InvariantCulture)).ToArray();
        Timestamps = rows.Select(r => Frames.ParseDouble(r["timestamp_ms"])).ToArray();
        HasCamera = rows.Any(r => CameraColumns.Any(c => r[c] != Frames.Null));
        if (!HasCamera) return;
        C2w = rows.Select(ParseC2w).ToArray();
        Intrinsics = rows.Select((r, i) => C2w[i] is not null && Frames.IntrinsicColumns.All(c => r[c] != Frames.Null)
            ? new Intrinsics(Frames.ParseDouble(r["camera_fx"]), Frames.ParseDouble(r["camera_fy"]),
                Frames.ParseDouble(r["camera_cx"]), Frames.ParseDouble(r["camera_cy"]))
            : (Intrinsics?)null).ToArray();
    }

    public string Raw(string key) => Session[key]?.ToJsonString() ?? "None";

    private static double[,]? ParseC2w(Dictionary<string, string> row)
    {
        if (Frames.C2wColumns.Any(c => row[c] == Frames.Null)) return null;
        var m = new double[4, 4];
        for (int k = 0; k < 16; k++) m[k / 4, k % 4] = Frames.ParseDouble(row[Frames.C2wColumns[k]]);
        return m;
    }

    private double? Number(string key) =>
        Session[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private bool TryTimestamp(string key, out DateTimeOffset value, out bool aware)
    {
        value = default;
        aware = false;
        if (Session[key] is not JsonValue v || !v.TryGetValue(out string? text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
            return false;
        aware = ZoneSuffix().IsMatch(text);
        return true;
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex ZoneSuffix();
}
