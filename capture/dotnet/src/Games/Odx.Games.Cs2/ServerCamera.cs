using Odx.Core;

namespace Odx.Games.Cs2;

/// <summary>
/// A player's camera per video frame from the game server's view of them (spec v2 §1.1.2.4 C2W, §1.1.2.5 intrinsics), for
/// games whose client cannot be read (CS2: anti-cheat). The server applies a player's view about half a round trip after
/// their screen shows it, so frame time + <c>leadMs</c> is looked up; ticks are interpolated (yaw the short way round).
/// While the player is dead the screen shows someone else's view: those frames get no camera.
/// </summary>
public static class ServerCamera
{
    /// <param name="frameUtcMs">Each frame's server UTC.</param>
    /// <returns>Frames given a camera, and the ranges of frames [first, last] that are not the player's own view.</returns>
    public static (int WithCamera, List<(int First, int Last)> NotFirstPerson) Fill(List<Dictionary<string, string>> rows,
        IReadOnlyList<double> frameUtcMs, List<Cs2ServerCapture.CameraSample> track, double leadMs, int width, int height)
    {
        int withCamera = 0;
        var notFirstPerson = new List<(int, int)>();
        var times = track.Select(s => s.UtcUs).ToList();
        for (int i = 0; i < rows.Count && i < frameUtcMs.Count; i++)
        {
            long t = (long)Math.Round((frameUtcMs[i] + leadMs) * 1000);
            var row = rows[i];
            int found = times.BinarySearch(t);
            Cs2ServerCapture.CameraSample a, b;
            if (found >= 0) a = b = track[found];
            else if (~found is var next && next > 0 && next < track.Count) (a, b) = (track[next - 1], track[next]);
            else
            {
                Clear(row);  // outside the server's recording
                continue;
            }
            if (!a.Alive || !b.Alive)
            {
                Clear(row);
                if (notFirstPerson.Count > 0 && notFirstPerson[^1].Item2 == i - 1) notFirstPerson[^1] = (notFirstPerson[^1].Item1, i);
                else notFirstPerson.Add((i, i));
                continue;
            }
            double f = b.UtcUs == a.UtcUs ? 0 : (t - a.UtcUs) / (double)(b.UtcUs - a.UtcUs);
            var eye = a.Eye + (b.Eye - a.Eye) * f;
            double pitch = a.Pitch + (b.Pitch - a.Pitch) * f;
            double yaw = a.Yaw + ShortTurn(a.Yaw, b.Yaw) * f;
            double fov = f < 0.5 ? a.FovDeg : b.FovDeg;
            Set(row, Pose(eye, pitch, yaw), Intrinsics(fov, width, height));
            withCamera++;
        }
        return (withCamera, notFirstPerson);
    }

    /// <summary>
    /// Source angles: yaw counter-clockwise from +X (east) toward +Y (north), pitch positive looking down; the world is
    /// right-handed with Z up, the spec's own axes.
    /// </summary>
    public static double[,] Pose(Core.Vec3 eye, double pitchDeg, double yawDeg)
    {
        double p = pitchDeg * Math.PI / 180, y = yawDeg * Math.PI / 180;
        return Geometry.C2wFromForward(new Core.Vec3(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), -Math.Sin(p)), eye);
    }

    /// <summary>CS2's field of view is horizontal at 4:3 whatever the screen; the vertical one follows from it.</summary>
    public static Intrinsics Intrinsics(double fov4x3Deg, int width, int height)
    {
        double vfov = 2 * Math.Atan(Math.Tan(fov4x3Deg * Math.PI / 360) * 3 / 4) * 180 / Math.PI;
        return Geometry.IntrinsicsFromVfov(vfov, width, height);
    }

    private static double ShortTurn(double from, double to) => ((to - from) % 360 + 540) % 360 - 180;

    private static void Set(Dictionary<string, string> row, double[,] c2w, Intrinsics k)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                row[Frames.C2wColumns[r * 4 + c]] = Frames.FormatDouble(c2w[r, c]);
        row["camera_model"] = "pinhole";
        row["camera_fx"] = Frames.FormatDouble(k.Fx);
        row["camera_fy"] = Frames.FormatDouble(k.Fy);
        row["camera_cx"] = Frames.FormatDouble(k.Cx);
        row["camera_cy"] = Frames.FormatDouble(k.Cy);
    }

    private static void Clear(Dictionary<string, string> row)
    {
        foreach (string column in Frames.C2wColumns.Concat(Frames.IntrinsicColumns).Append("camera_model")) row[column] = Frames.Null;
    }
}
