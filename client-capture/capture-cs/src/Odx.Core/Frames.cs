using System.Globalization;
using System.Text;

namespace Odx.Core;

/// <summary>Per-frame input columns. Null multi-value fields mean "nothing active".</summary>
public readonly record struct FrameInput(string? Keys, string? Actions, string? MouseButtons, double MouseDx, double MouseDy);

public sealed record FrameRow(long FrameId, double TimestampMs, double[,]? C2w, string? CameraModel, Intrinsics? Intrinsics,
    FrameInput Input);

/// <summary>frames.csv schema, per-frame input aggregation and writing (Capture Spec v2 §1.1.2.6, §1.1.3).</summary>
public static class Frames
{
    public static readonly string[] C2wColumns =
        Enumerable.Range(0, 16).Select(i => $"c2w_m{i / 4}{i % 4}").ToArray();
    public static readonly string[] IntrinsicColumns = ["camera_fx", "camera_fy", "camera_cx", "camera_cy"];
    public static readonly string[] DistortionColumns =
        [.. Enumerable.Range(1, 6).Select(i => $"camera_radial_k{i}"), "camera_tangential_p1", "camera_tangential_p2"];
    public static readonly string[] InputColumns =
        ["input_keys", "input_actions", "input_mouse_buttons", "input_mouse_dx", "input_mouse_dy"];
    public static readonly string[] Columns =
        ["frame_id", "timestamp_ms", .. C2wColumns, "camera_model", .. IntrinsicColumns, .. DistortionColumns, .. InputColumns];

    public static readonly string[] CameraModels = ["PINHOLE", "OPENCV", "OPENCV_FISHEYE"];

    /// <summary>How a null value is written to CSV. Open question for Client: empty field vs a literal token.</summary>
    public const string Null = "";

    /// <summary>Shortest round-trip text, always with a decimal point or exponent ("0.0", "1.5", "1e-05").</summary>
    public static string FormatDouble(double v)
    {
        string s = v.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
        return s.IndexOfAny(['.', 'e', 'N', 'I']) < 0 ? s + ".0" : s;  // 'N'/'I' cover NaN/Infinity
    }

    public static double ParseDouble(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Folds raw events into one input row per frame. Frame i covers events with frameTimes[i-1] &lt; t &lt;= frameTimes[i].
    /// A value is active in a frame if it is held at the frame's start or pressed during it, so taps shorter than a frame
    /// are kept. Mouse deltas are summed. Mirrored by extractor/src/input.cpp.
    /// </summary>
    public static List<FrameInput> AggregateInputs(IReadOnlyList<double> frameTimes, IEnumerable<InputEvent> events)
    {
        var sorted = events.OrderBy(e => e.TQpcMs).ToList();
        // Insertion-ordered sets: values are listed in press order.
        var held = new Dictionary<string, List<string>> { ["keys"] = [], ["buttons"] = [], ["actions"] = [] };
        var rows = new List<FrameInput>(frameTimes.Count);
        int j = 0;
        foreach (double tFrame in frameTimes)
        {
            var active = held.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
            double dx = 0, dy = 0;
            for (; j < sorted.Count && sorted[j].TQpcMs <= tFrame; j++)
            {
                var e = sorted[j];
                switch (e.Kind)
                {
                    case InputKind.KeyDown or InputKind.ButtonDown or InputKind.ActionStart:
                        AddOnce(held[Group(e.Kind)], e.Value);
                        AddOnce(active[Group(e.Kind)], e.Value);
                        break;
                    case InputKind.KeyUp or InputKind.ButtonUp or InputKind.ActionEnd:
                        held[Group(e.Kind)].Remove(e.Value);
                        break;
                    case InputKind.MouseMove:
                        dx += e.Dx;
                        dy += e.Dy;
                        break;
                }
            }
            rows.Add(new FrameInput(Join(active["keys"]), Join(active["actions"]), Join(active["buttons"]), dx, dy));
        }
        return rows;
    }

    public static void WriteCsv(string path, IEnumerable<FrameRow> rows)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.Write(Csv.Line(Columns));
        foreach (var r in rows) w.Write(Csv.Line(Fields(r)));
    }

    private static IEnumerable<string> Fields(FrameRow r)
    {
        yield return r.FrameId.ToString(CultureInfo.InvariantCulture);
        yield return FormatDouble(r.TimestampMs);
        for (int i = 0; i < 16; i++) yield return r.C2w is { } m ? FormatDouble(m[i / 4, i % 4]) : Null;
        yield return r.CameraModel ?? Null;
        var k = r.Intrinsics;
        foreach (double? v in new[] { k?.Fx, k?.Fy, k?.Cx, k?.Cy }) yield return v is double d ? FormatDouble(d) : Null;
        foreach (var _ in DistortionColumns) yield return Null;
        yield return r.Input.Keys ?? Null;
        yield return r.Input.Actions ?? Null;
        yield return r.Input.MouseButtons ?? Null;
        yield return FormatDouble(r.Input.MouseDx);
        yield return FormatDouble(r.Input.MouseDy);
    }

    private static string Group(InputKind kind) => kind switch
    {
        InputKind.KeyDown or InputKind.KeyUp => "keys",
        InputKind.ButtonDown or InputKind.ButtonUp => "buttons",
        _ => "actions",
    };

    private static void AddOnce(List<string> set, string value)
    {
        if (!set.Contains(value)) set.Add(value);
    }

    private static string? Join(List<string> values) => values.Count == 0 ? null : string.Join("|", values);
}
