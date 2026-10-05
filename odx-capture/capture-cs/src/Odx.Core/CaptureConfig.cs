using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>
/// Deployment settings in odx-capture.json next to the executables, preset once for every machine of a team instead of
/// in each user's settings. A missing file or field leaves the user's own setting in charge.
/// </summary>
/// <param name="TimeServer">NTP server ("host" or "host:port") every machine measures its clock against (spec v3 §4 baseline).</param>
public sealed record CaptureConfig(string? TimeServer)
{
    public const string FileName = "odx-capture.json";

    /// <exception cref="InvalidDataException">The file exists but is not valid.</exception>
    public static CaptureConfig Load(string? dir = null)
    {
        string path = Path.Combine(dir ?? AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) return new CaptureConfig((string?)null);
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new InvalidDataException($"{path}: not a JSON object");
            string? server = json["time_server"]?.GetValue<string>().Trim();
            return new CaptureConfig(string.IsNullOrEmpty(server) ? null : server);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }
}
