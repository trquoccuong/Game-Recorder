using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Merge;
using Odx.Merge.Proto;
using Odx.Protocol;

namespace Odx.Games.Cs2;

/// <summary>Counter-Strike 2: the SourceTV demo and the odx_netlog log (<see cref="Cs2ServerCapture"/>).</summary>
public sealed class Cs2Merger : IGameMerger
{
    public string Game => "Counter-Strike 2";

    public bool HasServerSide(string stagedServerDir) => Cs2ServerCapture.IsStaged(stagedServerDir);

    public ServerSide WriteServer(string stagedServerDir, string sessionServerDir, RoundRecord round)
    {
        // A Steam account several players share (two agents on one machine) names none of them.
        var playerIds = round.Players.Where(p => p.SteamId is not null).GroupBy(p => p.SteamId!).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().PlayerId);
        var result = Cs2ServerCapture.Write(stagedServerDir, sessionServerDir,
            new FileHeader { SpecVersion = MultiplayerSession.SpecVersion, SessionId = round.SessionId, ToolVersion = BuildInfo.Version },
            playerIds);
        var cameras = round.Players.Where(p => p.SteamId is { } s && result.Cameras.ContainsKey(s))
            .ToDictionary(p => p.PlayerId, p => (IServerCameraTrack)new CameraTrack(result.Cameras[p.SteamId!]));
        return new ServerSide(result.Events, result.Info, cameras, Cs2ServerCapture.Problems(stagedServerDir));
    }

    private sealed class CameraTrack(List<Cs2ServerCapture.CameraSample> track) : IServerCameraTrack
    {
        public (int WithCamera, List<(int First, int Last)> NotFirstPerson) Fill(List<Dictionary<string, string>> rows,
            IReadOnlyList<double> frameUtcMs, double leadMs, int width, int height) =>
            ServerCamera.Fill(rows, frameUtcMs, track, leadMs, width, height);

        public JsonObject Describe(double leadMs) => new()
        {
            ["source"] = "game server: the player's eye position and view angles in the server's recording, at frame time + half the ping",
            ["lead_ms"] = Math.Round(leadMs, 3),
            ["c2w_layout"] = "row-major 4x4, RUB camera axes (spec v2 §1.1.2.4)",
            ["world_axes"] = "right-handed, Z up: X east, Y north (the game's own axes, the spec's RFU)",
            ["world_units"] = "game units (CS2: 1 inch, 39.37 per metre)",
            ["intrinsics"] = "PINHOLE from the field of view: horizontal at 4:3 as CS2 defines it (90 degrees, less when a scope zooms)",
        };
    }
}
