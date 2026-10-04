using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Session;

/// <param name="UnboundKeys">Keys pressed but not in the profile's bindings, with press counts. Spec v2 §1.5 requires
/// every active key to map to an action, so they are left out of input_keys.</param>
public sealed record BuildReport(string SessionId, int FrameCount, double DurationMs, IReadOnlyDictionary<string, int> UnboundKeys);

/// <summary>
/// Turns a capture folder (odx-input + odx-record logs) into a spec v2 session: writes frames.csv and session.json next
/// to video.mp4. Frame clock: video_frames.csv grid times. Camera columns are null (action-only capture).
/// </summary>
public static class SessionBuilder
{
    public static BuildReport Build(string captureDir, GameProfile profile, string vendorName = "odx")
    {
        var frames = VideoFrameLogReader.Read(Path.Combine(captureDir, "video_frames.csv"));
        if (frames.Count == 0) throw new InvalidDataException("video_frames.csv has no frames");
        var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(captureDir, "recording.json")))!.AsObject();
        double fps = Required<double>(recording, "fps");
        string inputPath = Path.Combine(captureDir, "input_events.csv");
        var events = File.Exists(inputPath) ? InputLogReader.Read(inputPath) : [];

        var times = frames.Select(f => f.TGridMs).ToList();
        var (mapped, unbound) = MapInputs(events, profile.Bindings, times[0] - 1000.0 / fps);
        var inputs = Frames.AggregateInputs(times, mapped);
        Frames.WriteCsv(Path.Combine(captureDir, "frames.csv"), frames.Select((f, i) =>
            new FrameRow(i, (long)Math.Round(f.TGridMs - times[0]), null, null, null, inputs[i])));

        // Duration covers the last frame's display interval, so timestamp_ms[-1] + 1 frame ≈ duration_ms.
        double durationMs = frames.Count * 1000.0 / fps;
        double startUnixMs = times[0] + QpcToUnixOffsetMs(captureDir, recording);
        string sessionId = Required<string>(recording, "capture_id");
        var session = new JsonObject
        {
            ["vendor_name"] = vendorName,
            ["game_title"] = profile.GameTitle,
            ["session_id"] = sessionId,
            ["created_at_utc"] = IsoUtc(startUnixMs),
            ["ended_at_utc"] = IsoUtc(startUnixMs + durationMs),
            ["duration_ms"] = durationMs,
            ["duration_seconds"] = durationMs / 1000,
            ["fps"] = fps,
            ["frame_count"] = frames.Count,
            ["record_width_px"] = Required<int>(recording, "width"),
            ["record_height_px"] = Required<int>(recording, "height"),
            ["screen_width_px"] = Required<int>(recording, "screen_width"),
            ["screen_height_px"] = Required<int>(recording, "screen_height"),
            ["localization"] = profile.Localization,
            ["platform"] = profile.Platform,
            ["input_mouse_convention"] = profile.InputMouseConvention.DeepClone(),
        };
        File.WriteAllText(Path.Combine(captureDir, "session.json"),
            session.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new BuildReport(sessionId, frames.Count, durationMs, unbound);
    }

    /// <summary>
    /// Keeps bound keys and all mouse buttons, adds ActionStart/ActionEnd for bound inputs (an action stays active while
    /// any of its inputs is held), drops typematic key repeats and mouse motion from before the first frame's interval.
    /// </summary>
    public static (List<InputEvent> Events, SortedDictionary<string, int> UnboundKeys) MapInputs(
        IEnumerable<InputEvent> events, IReadOnlyDictionary<string, string> bindings, double firstFrameStartMs)
    {
        var result = new List<InputEvent>();
        var unbound = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var down = new HashSet<(bool Key, string Value)>();
        var actionHolders = new Dictionary<string, int>();
        foreach (var e in events.OrderBy(e => e.TQpcMs))
        {
            bool isKey = e.Kind is InputKind.KeyDown or InputKind.KeyUp;
            bool press = e.Kind is InputKind.KeyDown or InputKind.ButtonDown;
            switch (e.Kind)
            {
                case InputKind.MouseMove:
                    if (e.TQpcMs > firstFrameStartMs) result.Add(e);
                    continue;
                case InputKind.KeyDown or InputKind.KeyUp or InputKind.ButtonDown or InputKind.ButtonUp:
                    break;
                default:
                    continue;
            }
            bool bound = bindings.TryGetValue(e.Value, out var action);
            if (isKey && !bound)
            {
                if (press && down.Add((true, e.Value))) unbound[e.Value] = unbound.GetValueOrDefault(e.Value) + 1;
                if (!press) down.Remove((true, e.Value));
                continue;
            }
            if (press ? !down.Add((isKey, e.Value)) : !down.Remove((isKey, e.Value))) continue;
            result.Add(e);
            if (!bound) continue;
            int holders = actionHolders.GetValueOrDefault(action!) + (press ? 1 : -1);
            actionHolders[action!] = holders;
            if (press && holders == 1) result.Add(new InputEvent(e.TQpcMs, InputKind.ActionStart, action!));
            if (!press && holders == 0) result.Add(new InputEvent(e.TQpcMs, InputKind.ActionEnd, action!));
        }
        return (result, unbound);
    }

    /// <summary>Median of utc_unix_ms − t_qpc_ms over clock_samples.csv; recording.json's start pair if there are none.</summary>
    private static double QpcToUnixOffsetMs(string captureDir, JsonObject recording)
    {
        string path = Path.Combine(captureDir, "clock_samples.csv");
        var offsets = File.Exists(path)
            ? Csv.ReadFile(path).Rows
                .Select(r => Frames.ParseDouble(r["utc_unix_ms"]) - Frames.ParseDouble(r["t_qpc_ms"])).Order().ToList()
            : [];
        if (offsets.Count > 0) return offsets[offsets.Count / 2];
        var started = DateTimeOffset.Parse(Required<string>(recording, "started_utc"), CultureInfo.InvariantCulture);
        return started.ToUnixTimeMilliseconds() - Required<double>(recording, "started_qpc_ms");
    }

    private static string IsoUtc(double unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(unixMs)).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static T Required<T>(JsonObject json, string key) =>
        json[key] is JsonValue v && v.TryGetValue(out T? value) && value is not null
            ? value
            : throw new InvalidDataException($"recording.json: missing or invalid \"{key}\"");
}
