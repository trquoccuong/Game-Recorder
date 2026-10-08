using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Odx.Core;

namespace Odx.Capture;

/// <summary>Runs the vendor validation checks of Capture Spec v2 §1.5 (<see cref="Rules.All"/>) on one session folder.</summary>
public static class SessionValidator
{
    public static List<Issue> ValidateSession(string sessionDir, bool video = true, ValidationTolerances? tolerances = null)
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
        var data = new SessionData(session, tolerances ?? ValidationTolerances.Default);
        var issues = Run(data, Needs.Session, Needs.Timestamps, Needs.AwareTimestamps, Needs.Durations);

        try
        {
            var (header, rows) = Csv.ReadFile(Path.Combine(sessionDir, "frames.csv"));
            data.LoadFrames(header, rows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(Issue.Error("frames.unreadable", e.Message));
            return issues;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            issues.Add(Issue.Error("frames.unparseable", $"could not parse frames.csv: {e.Message}"));
            return issues;
        }
        issues.AddRange(Run(data, Needs.FramesFile, Needs.FramesHeader, Needs.FrameRows, Needs.Camera));

        if (video && VideoIssue(data, Path.Combine(sessionDir, "video.mp4")) is { } videoIssue) issues.Add(videoIssue);
        if (video) issues.AddRange(Run(data, Needs.Video));
        return issues;
    }

    private static List<Issue> Run(SessionData data, params Needs[] stage) =>
        Rules.All.Where(r => stage.Contains(r.Needs) && Rules.Applies(r.Needs, data))
            .SelectMany(r => r.Check(data).Select(message => new Issue(r.Id, r.Severity, message)))
            .ToList();

    /// <summary>Reads video.mp4 into <paramref name="data"/>; returns the issue that stops the video checks, if any.</summary>
    private static Issue? VideoIssue(SessionData data, string videoPath)
    {
        if (!File.Exists(videoPath)) return Issue.Error("video.missing", $"{Path.GetFileName(videoPath)} not found");
        try
        {
            var mp4 = Mp4Reader.Read(videoPath);
            data.Video = new VideoInfo(mp4.Width, mp4.Height, mp4.Frames, mp4.DurationSeconds);
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Issue.Error("video.unreadable", $"cannot read {Path.GetFileName(videoPath)}: {e.Message}");
        }
    }
}

/// <summary>session.json against the JSON Schema of spec v2 §1.1.2.1 (embedded from config/session_schema.json).</summary>
internal static class SessionSchema
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        using var stream = typeof(SessionSchema).Assembly.GetManifestResourceStream("session_schema.json")!;
        return JsonSchema.FromText(new StreamReader(stream).ReadToEnd());
    });

    public static IEnumerable<string> Errors(SessionData data)
    {
        var result = Schema.Value.Evaluate(data.Session, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        return FailedNodes(result)
            .SelectMany(d => d.Errors!.Values.Select(msg =>
                $"{Location(d.InstanceLocation.ToString())}: {msg.Replace("\\u0022", "")}"))
            .Distinct();
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

    private static string Location(string pointer) => pointer.TrimStart('/') is { Length: > 0 } p ? p : "<root>";
}
