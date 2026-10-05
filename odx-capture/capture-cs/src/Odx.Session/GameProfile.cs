using System.Text.Json.Nodes;

namespace Odx.Session;

/// <summary>
/// Per-game settings for building a session: the session.json fields that capture cannot observe, and the key/button
/// → action bindings that fill input_actions. Stored as JSON (see odx-capture/games/).
/// </summary>
/// <param name="WindowProcess">The game's process name (window_process): recorded instead of the monitor when the user
/// picked none, so other windows and notifications stay out of the video.</param>
public sealed record GameProfile(
    string GameTitle,
    string Platform,
    string Localization,
    JsonObject InputMouseConvention,
    IReadOnlyDictionary<string, string> Bindings,
    string? WindowProcess = null)
{
    public static GameProfile Load(string path)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException($"{path}: not a JSON object");
        string Required(string key) =>
            json[key]?.GetValue<string>() ?? throw new FormatException($"{path}: missing \"{key}\"");
        var convention = json["input_mouse_convention"]?.AsObject() ??
                         throw new FormatException($"{path}: missing \"input_mouse_convention\"");
        var bindings = json["bindings"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ??
                       throw new FormatException($"{path}: missing \"bindings\"");
        return new GameProfile(Required("game_title"), Required("platform"), Required("localization"),
            (JsonObject)convention.DeepClone(), bindings, json["window_process"]?.GetValue<string>());
    }
}
