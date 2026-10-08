using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>
/// Deployment settings in odx-capture.json next to the executables, preset once for every machine of a team instead of
/// in each user's settings. A missing file or field leaves the user's own setting in charge.
/// </summary>
/// <param name="TimeServer">NTP server ("host" or "host:port") every machine measures its clock against (spec v3 §4 baseline).</param>
/// <param name="Orchestrator">odx-orchestrator's address (http://host:5180); players then only type a room's invite code.
/// Null: odx-app records on its own only.</param>
/// <param name="AccessKey">Shared secret the orchestrator asks of every client (its --access-key); null sends none.</param>
public sealed record CaptureConfig(string? TimeServer, Uri? Orchestrator = null, string? AccessKey = null)
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
            string? orchestrator = json["orchestrator"]?.GetValue<string>().Trim();
            Uri? uri = null;
            if (!string.IsNullOrEmpty(orchestrator) &&
                (!Uri.TryCreate(orchestrator, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https")))
                throw new InvalidDataException($"{path}: orchestrator must be an http:// address, not \"{orchestrator}\"");
            string? key = json["access_key"]?.GetValue<string>().Trim();
            return new CaptureConfig(string.IsNullOrEmpty(server) ? null : server, uri, string.IsNullOrEmpty(key) ? null : key);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }
}
