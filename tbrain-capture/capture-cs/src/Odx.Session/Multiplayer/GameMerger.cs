using System.Text.Json.Nodes;

namespace Odx.Session;

/// <summary>
/// The game-specific part of a merge: reading the game server's recording of a round (spec v3 §3). A game without one
/// still gets a valid session from its players and the round record; only server/ stays empty.
/// </summary>
public interface IGameMerger
{
    /// <summary>The game profile's game_title this module reads.</summary>
    string Game { get; }

    bool HasServerSide(string stagedServerDir);

    ServerSide WriteServer(string stagedServerDir, string sessionServerDir, RoundRecord round);
}

/// <param name="Events">For events.jsonl, with utc_ms.</param>
/// <param name="Info">For session.json "server".</param>
/// <param name="Cameras">By player id: the camera the server saw, for players whose own capture has none.</param>
/// <param name="Problems">What went wrong recording the server's side.</param>
public sealed record ServerSide(List<JsonObject> Events, JsonObject Info, IReadOnlyDictionary<string, IServerCameraTrack> Cameras,
    List<string> Problems)
{
    public const string StateFile = "server_state.bin";
}

public interface IServerCameraTrack
{
    /// <param name="frameUtcMs">Each frame's server UTC.</param>
    /// <returns>Frames given a camera, and the ranges of frames [first, last] that are not the player's own view.</returns>
    (int WithCamera, List<(int First, int Last)> NotFirstPerson) Fill(List<Dictionary<string, string>> rows,
        IReadOnlyList<double> frameUtcMs, double leadMs, int width, int height);

    /// <summary>player.json "camera".</summary>
    JsonObject Describe(double leadMs);
}

public static class GameMergers
{
    private static readonly IGameMerger[] All = [new Cs2Merger()];

    public static IGameMerger? For(string game) => All.FirstOrDefault(m => m.Game == game);
}
