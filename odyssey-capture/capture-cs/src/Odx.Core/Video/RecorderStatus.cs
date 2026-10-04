using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odx.Core;

/// <summary>
/// One line of <c>odx record --json-status</c> stdout: <c>started</c> once capture runs, <c>progress</c> every second,
/// <c>stopped</c> at the end (with <c>Error</c> if it failed). Read by the capture host to show live counters.
/// </summary>
public sealed record RecorderStatus(string Event, long Frames = 0, long Duplicates = 0, long Dropped = 0, int Width = 0,
    int Height = 0, double Fps = 0, string? Error = null)
{
    public const string Started = "started", Progress = "progress", Stopped = "stopped";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Null for lines that are not status JSON (e.g. a human-readable message).</summary>
    public static RecorderStatus? TryParse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            return JsonSerializer.Deserialize<RecorderStatus>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
