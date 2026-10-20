using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Capture;

/// <summary>
/// A player's video on the orchestrator's clock (spec v3 §4): SNTP samples against its time service give QPC → server UTC,
/// and server events get the video time and frame at which they happened. Used by the agent for the round folder's
/// events.jsonl and by odx merge for players/&lt;id&gt;/events.jsonl.
/// </summary>
public static class PlayerTimeline
{
    /// <summary>
    /// The samples against the time service on <paramref name="timePort"/>, their lower-RTT half (plan §7.4: the shortest
    /// round trips have the least path asymmetry) and the QPC → server UTC line fitted to it; null without samples.
    /// </summary>
    public static (List<NtpSample> Samples, List<NtpSample> Best, ClockMap Map)? ServerClock(string captureDir, int timePort)
    {
        string ntpPath = CaptureFolder.Raw(captureDir, "ntp_samples.csv");
        var samples = File.Exists(ntpPath)
            ? NtpSampleReader.Read(ntpPath).Where(s => s.Server.EndsWith($":{timePort}", StringComparison.Ordinal)).ToList()
            : [];
        if (samples.Count == 0) return null;
        var best = samples.OrderBy(s => s.RttMs).Take((samples.Count + 1) / 2).ToList();
        return (samples, best, ClockMap.Fit(best.Select(s => (s.TQpcMs, s.UtcUnixMs)).ToList()));
    }

    public static List<double> FrameUtc(string captureDir, ClockMap map) =>
        VideoFrameLogReader.Read(CaptureFolder.Raw(captureDir, "video_frames.csv")).Select(f => map.ToUnixMs(f.TGridMs)).ToList();

    /// <summary>Each event with video_time_ms and frame_index (the frame on screen then; null outside the video).</summary>
    public static IEnumerable<JsonObject> OnVideo(IEnumerable<JsonObject> events, List<double> frameUtc) => events.Select(e =>
    {
        double t = GameEvents.UtcMs(e);
        var copy = (JsonObject)e.DeepClone();
        copy["video_time_ms"] = Math.Round(t - frameUtc[0], 1);
        int index = frameUtc.BinarySearch(t);
        if (index < 0) index = ~index - 1;
        copy["frame_index"] = t >= frameUtc[0] && t <= frameUtc[^1] ? index : null;
        return copy;
    });

    public static void WriteEvents(string path, IEnumerable<JsonObject> events) =>
        File.WriteAllLines(path, events.Select(e => e.ToJsonString(AgentMessage.Json)));

    /// <summary>
    /// events.jsonl of a player's round folder: the round's server events (raw/server_events.jsonl) on this player's video;
    /// without clock samples against the time service the events are written without video times.
    /// </summary>
    public static void WriteRoundEvents(string captureDir, RoundRecord round)
    {
        string serverEvents = CaptureFolder.Raw(captureDir, RoundRecord.EventsFileName);
        if (!File.Exists(serverEvents)) return;
        var events = File.ReadLines(serverEvents).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var frameUtc = ServerClock(captureDir, round.TimePort) is { } clock ? FrameUtc(captureDir, clock.Map) : null;
        WriteEvents(Path.Combine(captureDir, "events.jsonl"), frameUtc is { Count: > 0 } ? OnVideo(events, frameUtc) : events);
    }
}
