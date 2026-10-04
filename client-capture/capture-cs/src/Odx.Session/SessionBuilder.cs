using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Session;

/// <param name="UnboundKeys">Keys pressed but not in the profile's bindings, with press counts. Spec v2 §1.5 requires
/// every active key to map to an action, so they are left out of input_keys.</param>
/// <param name="CameraFrames">Frames with camera columns; 0 for an action-only capture (no camera samples).</param>
public sealed record BuildReport(string SessionId, int FrameCount, double DurationMs, IReadOnlyDictionary<string, int> UnboundKeys,
    int CameraFrames);

/// <summary>
/// Turns a capture folder (odx input + odx record logs) into a spec v2 session: writes frames.csv and session.json next
/// to video.mp4. Frame clock: video_frames.csv grid times. Camera columns come from the extractor DLL's
/// camera_samples.csv + extractor.json when present (in the capture folder or <c>cameraDir</c>), else they are null.
/// </summary>
public static class SessionBuilder
{
    /// <summary>A frame gets no camera if the newest sample before it is older than this (game hitch, profile failure).</summary>
    public const double MaxCameraAgeMs = 100;

    public static BuildReport Build(string captureDir, GameProfile profile, string vendorName = "odx", string? cameraDir = null)
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
        int width = Required<int>(recording, "width"), height = Required<int>(recording, "height");
        var cameras = CameraPerFrame(cameraDir ?? captureDir, frames, width, height);
        Frames.WriteCsv(Path.Combine(captureDir, "frames.csv"), frames.Select((f, i) => new FrameRow(i,
            (long)Math.Round(f.TGridMs - times[0]), cameras?[i]?.C2w, cameras?[i] is null ? null : cameras.Model,
            cameras?[i]?.Intrinsics, inputs[i])));

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
            ["record_width_px"] = width,
            ["record_height_px"] = height,
            ["screen_width_px"] = Required<int>(recording, "screen_width"),
            ["screen_height_px"] = Required<int>(recording, "screen_height"),
            ["localization"] = profile.Localization,
            ["platform"] = profile.Platform,
            ["input_mouse_convention"] = profile.InputMouseConvention.DeepClone(),
            // Not in the spec (additionalProperties is allowed): which odx / odx-app build produced the session.
            ["vendor_tool_version"] = BuildInfo.Version,
        };
        File.WriteAllText(Path.Combine(captureDir, "session.json"),
            session.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        return new BuildReport(sessionId, frames.Count, durationMs, unbound, cameras?.Count(c => c is not null) ?? 0);
    }

    private sealed class FrameCameras(string model, int count) : List<(double[,] C2w, Intrinsics Intrinsics)?>(count)
    {
        public string Model { get; } = model;
    }

    /// <summary>
    /// For each video frame, the newest camera sample taken at or before the frame was captured (t_source): the Present
    /// that produced the image comes just before Windows.Graphics.Capture delivers it. Null when there are no samples.
    /// </summary>
    private static FrameCameras? CameraPerFrame(string dir, List<GridTick> frames, int width, int height)
    {
        string samplesPath = Path.Combine(dir, "camera_samples.csv"), infoPath = Path.Combine(dir, "extractor.json");
        if (!File.Exists(samplesPath) || !File.Exists(infoPath)) return null;
        var info = ExtractorInfo.Load(infoPath);
        var samples = CameraSampleReader.Read(samplesPath).OrderBy(s => s.TQpcMs).ToList();
        var result = new FrameCameras(info.CameraModel, frames.Count);
        int j = -1;
        foreach (var f in frames)
        {
            while (j + 1 < samples.Count && samples[j + 1].TQpcMs <= f.TSourceMs) j++;
            result.Add(j >= 0 && f.TSourceMs - samples[j].TQpcMs <= MaxCameraAgeMs
                ? info.ToCanonical(samples[j], width, height)
                : null);
        }
        return result;
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
