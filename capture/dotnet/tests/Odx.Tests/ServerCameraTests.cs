using Odx.Core;
using Odx.Games.Cs2;
using Odx.Merge;
using Xunit;

namespace Odx.Tests;

public class ServerCameraTests
{
    private static Dictionary<string, string> EmptyRow() => Frames.Columns.ToDictionary(c => c, _ => Frames.Null);

    private static Cs2ServerCapture.CameraSample Sample(long utcUs, double yaw, bool alive = true, double pitch = 0) =>
        new(utcUs, new Core.Vec3(100, 200, 64), pitch, yaw, Cs2ServerCapture.DefaultFovDeg, alive);

    [Fact]
    public void Source_angles_become_a_rub_camera_in_the_specs_axes()
    {
        var north = ServerCamera.Pose(new Core.Vec3(1, 2, 3), pitchDeg: 0, yawDeg: 90);  // yaw 90: looking +Y
        Assert.Equal(1, north[0, 0], 9);   // right = +X (east)
        Assert.Equal(1, north[2, 1], 9);   // up = +Z
        Assert.Equal(-1, north[1, 2], 9);  // backward = -Y
        Assert.Equal(3, north[2, 3]);
        var (det, ortho) = Geometry.RotationErrors(north);
        Assert.True(det < 1e-12 && ortho < 1e-12);

        var down = ServerCamera.Pose(default, pitchDeg: 30, yawDeg: 0);  // Source pitch > 0 looks down
        Assert.Equal(Math.Sin(30 * Math.PI / 180), down[2, 2], 9);    // backward points up
        Assert.Equal(0, down[2, 0], 9);                                   // no roll
    }

    [Fact]
    public void Cs2s_fov_is_horizontal_at_4_by_3()
    {
        var k = ServerCamera.Intrinsics(90, 1920, 1080);  // vertical 73.74°: 540 / tan(36.87°) = 720
        Assert.Equal(720, k.Fy, 6);
        Assert.Equal(k.Fy, k.Fx);
        Assert.Equal((960.0, 540.0), (k.Cx, k.Cy));
    }

    [Fact]
    public void Frames_take_the_interpolated_camera_and_none_while_dead_or_outside_the_recording()
    {
        var rows = Enumerable.Range(0, 5).Select(_ => EmptyRow()).ToList();
        // Frames at 0.5, 1.5, 2.5, 3.5, 9 ms; with the lead of 0.5 ms they look up 1, 2, 3, 4, 9.5 ms.
        double[] frames = [0.5, 1.5, 2.5, 3.5, 9];
        List<Cs2ServerCapture.CameraSample> track =
        [
            Sample(0, 170), Sample(2_000, -170),  // across ±180: the short way round
            Sample(3_000, 0, alive: false), Sample(5_000, 0, alive: false),
        ];

        var (withCamera, notFirstPerson) = ServerCamera.Fill(rows, frames, track, leadMs: 0.5, 1920, 1080);

        Assert.Equal(2, withCamera);
        double Yaw(Dictionary<string, string> r) =>
            Math.Atan2(-Frames.ParseDouble(r["C2W_M12"]), -Frames.ParseDouble(r["C2W_M02"])) * 180 / Math.PI;
        Assert.Equal(180, Math.Abs(Yaw(rows[0])), 6);  // halfway from 170 to -170
        Assert.Equal(-170, Yaw(rows[1]), 6);
        Assert.Equal("pinhole", rows[0]["camera_model"]);
        Assert.Equal(Frames.Null, rows[2]["C2W_M00"]);  // between a living and a dead tick
        Assert.Equal(Frames.Null, rows[3]["C2W_M00"]);
        Assert.Equal(Frames.Null, rows[4]["C2W_M00"]);  // after the recording
        Assert.Equal([(2, 3)], notFirstPerson);
    }

    [Fact]
    public void Mouse_and_camera_turning_together_is_found_at_the_expected_frame()
    {
        var random = new Random(7);
        double[] dx = Enumerable.Range(0, 200).Select(_ => random.NextDouble() * 20 - 10).ToArray();
        var rows = new List<Dictionary<string, string>>();
        double yaw = 0;
        for (int i = 0; i < dx.Length; i++)
        {
            yaw -= dx[i] * 0.1;  // a frame's mouse turns the camera into that frame; right turns yaw down
            var row = EmptyRow();
            var c2w = ServerCamera.Pose(default, 0, yaw);
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    row[Frames.C2wColumns[r * 4 + c]] = Frames.FormatDouble(c2w[r, c]);
            row["input_mouse_dx"] = Frames.FormatDouble(dx[i]);
            rows.Add(row);
        }

        var sync = MultiplayerSession.InputCameraSync(rows)!;

        Assert.Equal(-1, sync.BestShift);
        Assert.True(sync.Correlation > 0.999);
    }
}
