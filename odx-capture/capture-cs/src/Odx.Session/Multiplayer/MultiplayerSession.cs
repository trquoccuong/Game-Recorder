using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Session;

/// <param name="StopReason">"round_end", "permanent_death" or "abort"; null while recording.</param>
/// <param name="StopUtcMs">Server UTC when this player's recording was stopped.</param>
public sealed record RoundPlayer(string PlayerId, string Name, string? StopReason = null, double? StopUtcMs = null,
    bool Done = false, bool UploadOk = false, string? Error = null);

/// <summary>
/// One round as the orchestrator saw it (staging round.json). One round = one session (spec v3 §1: task boundaries are
/// rounds). <c>StartUtcMs</c> is when play starts: the round_start event plus its countdown, during which players already
/// record. <c>TimePort</c> is the UDP port of the orchestrator's NTP responder the players measured against; the host part
/// of their samples' server differs with how each agent reaches the orchestrator.
/// </summary>
public sealed record RoundRecord(string SessionId, string MatchId, string Game, string? Map, int Round, double StartUtcMs,
    double? EndUtcMs, int TimePort, List<RoundPlayer> Players, int CountdownMs = 0, string? EndReason = null)
{
    public const string FileName = "round.json", EventsFileName = "server_events.jsonl";

    public static RoundRecord Load(string stagingDir) =>
        JsonSerializer.Deserialize<RoundRecord>(File.ReadAllText(Path.Combine(stagingDir, FileName)), AgentMessage.Json)
        ?? throw new InvalidDataException("empty round.json");

    public void Save(string stagingDir) =>
        File.WriteAllText(Path.Combine(stagingDir, FileName), JsonSerializer.Serialize(this, PrettyJson));

    internal static readonly JsonSerializerOptions PrettyJson = new(AgentMessage.Json) { WriteIndented = true };
}

/// <summary>Limits of the v3 sync gate (spec v3 §4: "more prototyping is needed to decide what a good tolerance is").</summary>
public sealed record SyncTolerances
{
    public static readonly SyncTolerances Default = new();

    /// <summary>The clock offset is known to ±RTT/2; above this the player's timestamps are not trusted.</summary>
    public double MaxRttMs { get; init; } = 100;
    /// <summary>First frame may come this long after round_start (capture start-up: ~0.7 s warm, ~3.5 s cold in a test here).</summary>
    public double StartGraceMs { get; init; } = 3000;
    /// <summary>Last frame may end this long before the player's stop (round end or permanent death).</summary>
    public double EndGraceMs { get; init; } = 1000;
    /// <summary>K of the "K of N player streams valid" rule; null: N for up to 3 players, else N−1 (plan Q6 proposal).</summary>
    public int? MinValidPlayers { get; init; }
}

public sealed record PlayerReport(string PlayerId, bool Pass, List<Issue> Issues);

public sealed record SessionReport(string SessionId, bool Pass, int ValidPlayers, int RequiredPlayers, List<Issue> Issues,
    List<PlayerReport> Players);

/// <summary>
/// Turns a staged round (round.json, server_events.jsonl, players/&lt;id&gt;/ with each agent's uploaded capture) into the
/// spec v3 tree and checks it:
/// <code>
/// &lt;vendor&gt;/&lt;mm-dd-yyyy&gt;/&lt;game&gt;/&lt;session-id&gt;/
///   session.json  validation.json  server/events.jsonl
///   players/&lt;id&gt;/ player.json frames.csv video.mp4 events.jsonl capture/(v2 session.json, raw logs)
/// </code>
/// Every frame gets a server UTC from the player's samples against the orchestrator's time service, so player
/// events.jsonl carry the frame index at which each server event happened in that player's video.
/// </summary>
public static class MultiplayerSession
{
    private static readonly string[] CaptureFiles =
        ["session.json", "recording.json", "video_frames.csv", "input_events.csv", "clock_samples.csv", "ntp_samples.csv"];

    /// <summary>Version of the client's multiplayer spec this tree follows (document header of spec v3).</summary>
    public const string SpecVersion = "3.1";

    public static (string Dir, SessionReport Report) Assemble(string stagingDir, string outRoot, string vendor,
        SyncTolerances? tolerances = null)
    {
        var tol = tolerances ?? SyncTolerances.Default;
        var round = RoundRecord.Load(stagingDir);
        var events = ReadEvents(Path.Combine(stagingDir, RoundRecord.EventsFileName));
        string dir = Path.Combine(outRoot, vendor, DateTime.UtcNow.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
            Slug(round.Game), round.SessionId);
        Directory.CreateDirectory(Path.Combine(dir, "server"));
        WriteEvents(Path.Combine(dir, "server", "events.jsonl"), events);

        var reports = round.Players.Select(p => AssemblePlayer(round, p, events, stagingDir, dir, tol)).ToList();
        int valid = reports.Count(r => r.Pass);
        int required = tol.MinValidPlayers ?? (round.Players.Count <= 3 ? round.Players.Count : round.Players.Count - 1);
        var issues = new List<Issue>();
        foreach (string type in new[] { GameEvents.RoundStart, GameEvents.RoundEnd })
            if (!events.Any(e => GameEvents.Type(e) == type))
                issues.Add(Issue.Error("session.events", $"no {type} event from the game server"));
        if (valid < required)
            issues.Add(Issue.Error("session.min_players", $"{valid} of {round.Players.Count} player streams valid, {required} required"));
        var report = new SessionReport(round.SessionId, issues.Count == 0, valid, required, issues, reports);

        var session = new JsonObject
        {
            ["spec_version"] = SpecVersion,
            ["session_id"] = round.SessionId,
            ["match_id"] = round.MatchId,
            ["vendor_name"] = vendor,
            ["game_title"] = round.Game,
            ["map"] = round.Map,
            ["round_index"] = round.Round,
            ["countdown_ms"] = round.CountdownMs,
            ["end_reason"] = round.EndReason,
            ["created_at_utc"] = SessionBuilder.IsoUtc(round.StartUtcMs),
            ["ended_at_utc"] = round.EndUtcMs is double end ? SessionBuilder.IsoUtc(end) : null,
            ["duration_ms"] = round.EndUtcMs is double e2 ? e2 - round.StartUtcMs : null,
            ["players"] = new JsonArray(round.Players.Select(p => (JsonNode?)JsonValue.Create(p.PlayerId)).ToArray()),
            ["valid_players"] = valid,
            ["time_service_port"] = round.TimePort,
            ["vendor_tool_version"] = BuildInfo.Version,
        };
        File.WriteAllText(Path.Combine(dir, "session.json"), session.ToJsonString(RoundRecord.PrettyJson));
        File.WriteAllText(Path.Combine(dir, "validation.json"), JsonSerializer.Serialize(report, RoundRecord.PrettyJson));
        return (dir, report);
    }

    private static PlayerReport AssemblePlayer(RoundRecord round, RoundPlayer player, List<JsonObject> events, string stagingDir,
        string sessionDir, SyncTolerances tol)
    {
        string src = Path.Combine(stagingDir, "players", player.PlayerId);
        string dst = Path.Combine(sessionDir, "players", player.PlayerId);
        Directory.CreateDirectory(Path.Combine(dst, "capture"));
        var issues = new List<Issue>();
        var info = new JsonObject { ["player_id"] = player.PlayerId, ["name"] = player.Name, ["is_bot"] = false,
            ["stop_reason"] = player.StopReason };

        if (!File.Exists(Path.Combine(src, "frames.csv")) || !File.Exists(Path.Combine(src, "video_frames.csv")))
        {
            issues.Add(Issue.Error("player.missing", player.Error ?? "no built capture was uploaded"));
            return Finish(dst, info, player.PlayerId, issues);
        }
        var v2 = SessionValidator.ValidateSession(src).Where(i => i.Severity == Issue.ErrorSeverity).ToList();
        if (v2.Count > 0)
            issues.Add(Issue.Error("player.v2", $"{v2.Count} spec v2 error(s): {string.Join(", ", v2.Select(i => i.Rule).Distinct())}"));

        File.Copy(Path.Combine(src, "frames.csv"), Path.Combine(dst, "frames.csv"), true);
        if (File.Exists(Path.Combine(src, "video.mp4"))) File.Copy(Path.Combine(src, "video.mp4"), Path.Combine(dst, "video.mp4"), true);
        foreach (string f in CaptureFiles.Where(f => File.Exists(Path.Combine(src, f))))
            File.Copy(Path.Combine(src, f), Path.Combine(dst, "capture", f), true);

        string ntpPath = Path.Combine(src, "ntp_samples.csv");
        var samples = File.Exists(ntpPath)
            ? NtpSampleReader.Read(ntpPath).Where(s => s.Server.EndsWith($":{round.TimePort}", StringComparison.Ordinal)).ToList()
            : [];
        if (samples.Count == 0)
        {
            issues.Add(Issue.Error("sync.clock", $"no clock samples against the time service (port {round.TimePort})"));
            return Finish(dst, info, player.PlayerId, issues);
        }
        double rtt = Median(samples.Select(s => s.RttMs));
        info["ping_ms"] = Math.Round(rtt, 1);
        info["clock_offset_ms"] = Math.Round(Median(samples.Select(s => s.SystemOffsetMs)), 1);
        if (rtt > tol.MaxRttMs)
            issues.Add(Issue.Error("sync.clock", $"median RTT to the time service {rtt:F0} ms > {tol.MaxRttMs} ms"));

        var best = samples.OrderBy(s => s.RttMs).Take((samples.Count + 1) / 2).ToList();
        var map = ClockMap.Fit(best.Select(s => (s.TQpcMs, s.UtcUnixMs)).ToList());
        var frameUtc = VideoFrameLogReader.Read(Path.Combine(src, "video_frames.csv")).Select(f => map.ToUnixMs(f.TGridMs)).ToList();
        if (frameUtc.Count == 0)
        {
            issues.Add(Issue.Error("player.missing", "no video frames"));
            return Finish(dst, info, player.PlayerId, issues);
        }
        info["first_frame_utc"] = SessionBuilder.IsoUtc(frameUtc[0]);
        info["last_frame_utc"] = SessionBuilder.IsoUtc(frameUtc[^1]);
        info["frame_count"] = frameUtc.Count;
        int roundStartFrame = frameUtc.FindIndex(t => t >= round.StartUtcMs);
        info["round_start_frame"] = roundStartFrame < 0 ? null : roundStartFrame;
        info["round_start_video_time_ms"] = Math.Round(round.StartUtcMs - frameUtc[0], 1);

        double lateStart = frameUtc[0] - round.StartUtcMs;
        if (lateStart > tol.StartGraceMs)
            issues.Add(Issue.Error("sync.start", $"first frame {lateStart:F0} ms after round_start (> {tol.StartGraceMs} ms)"));
        if ((player.StopUtcMs ?? round.EndUtcMs) is double stopAt && stopAt - frameUtc[^1] > tol.EndGraceMs)
            issues.Add(Issue.Error("sync.end", $"last frame {stopAt - frameUtc[^1]:F0} ms before the player's stop (> {tol.EndGraceMs} ms)"));

        var playerEvents = new List<JsonObject>();
        foreach (var e in events)
        {
            double t = GameEvents.UtcMs(e);
            if (t < frameUtc[0] || t > frameUtc[^1]) continue;
            int index = frameUtc.BinarySearch(t);
            if (index < 0) index = ~index - 1;  // the frame on screen when the event happened
            var copy = (JsonObject)e.DeepClone();
            copy["frame_index"] = index;
            playerEvents.Add(copy);
        }
        WriteEvents(Path.Combine(dst, "events.jsonl"), playerEvents);
        return Finish(dst, info, player.PlayerId, issues);
    }

    private static PlayerReport Finish(string dst, JsonObject info, string playerId, List<Issue> issues)
    {
        bool pass = issues.All(i => i.Severity != Issue.ErrorSeverity);
        info["valid"] = pass;
        File.WriteAllText(Path.Combine(dst, "player.json"), info.ToJsonString(RoundRecord.PrettyJson));
        return new PlayerReport(playerId, pass, issues);
    }

    public static List<JsonObject> ReadEvents(string path) =>
        File.Exists(path)
            ? File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList()
            : [];

    public static void WriteEvents(string path, IEnumerable<JsonObject> events) =>
        File.WriteAllLines(path, events.Select(e => e.ToJsonString(AgentMessage.Json)));

    public static string Slug(string text) =>
        string.Concat(text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')).Trim('-');

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
