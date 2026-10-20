using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class ReforgerCameraTests
{
    private const double T0 = 5_000_000.0, UtcAtT0 = 1_791_108_000_000.0;  // QPC ms of video frame 0 and its UTC
    private const long TickAtT0 = 7000;
    private const double Phase = 437.25;  // UTC − tick is not a whole number of seconds

    [Fact]
    public void Fixture_parses_and_the_end_line_is_skipped()
    {
        var rows = ReforgerCamera.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", ReforgerCamera.RawLog));
        Assert.Equal(3, rows.Count);
        Assert.Equal(412906, rows[2].TickMs);
        Assert.Equal(1792485333, rows[2].UnixS);
        Assert.Equal(1, rows[2].ThirdPerson);
        Assert.Equal(new Vec3(0.5, 0, 0.866025), rows[0].Forward);
        Assert.Equal(new Vec3(4523.125, 12.5, 6020.75), rows[0].Position);
        Assert.Equal(72.0, rows[2].VfovDeg);
    }

    [Fact]
    public void A_round_log_lands_on_qpc_and_fills_the_camera_columns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-reforger-{Guid.NewGuid():N}");
        string capture = Path.Combine(root, "capture"), odx = Path.Combine(root, "profile", "odx");
        try
        {
            WriteCapture(capture);
            ReforgerCamera.Begin(odx, "round-1");
            Assert.Equal("round-1", File.ReadAllText(Path.Combine(odx, ReforgerCamera.Control)).Trim());
            var ticks = WriteModLog(Path.Combine(odx, "round-1.csv"));

            Assert.Null(ReforgerCamera.Collect(odx, "round-1", capture, TimeSpan.FromSeconds(1)));

            Assert.False(File.Exists(Path.Combine(odx, ReforgerCamera.Control)));
            Assert.False(File.Exists(Path.Combine(odx, "round-1.csv")));
            var samples = CameraSampleReader.Read(Path.Combine(capture, "camera_samples.csv"));
            Assert.Equal(ticks.Count, samples.Count);
            for (int i = 0; i < ticks.Count; i++) Assert.InRange(samples[i].TQpcMs - QpcOf(ticks[i]), -3, 3);

            var report = SessionBuilder.Build(capture, GameProfile.Load(WriteProfile(capture)));
            Assert.Equal(Frames, report.CameraFrames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_log_is_a_warning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-reforger-{Guid.NewGuid():N}");
        try
        {
            ReforgerCamera.Begin(root, "round-1");
            Assert.NotNull(ReforgerCamera.Collect(root, "round-1", root, TimeSpan.Zero));
            Assert.False(File.Exists(Path.Combine(root, ReforgerCamera.Control)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const int Frames = 600, Fps = 60;

    private static double QpcOf(long tick) => T0 + Phase + (tick - TickAtT0);

    /// <summary>The mod's log at an uneven game frame rate (15–18 ms), starting before video frame 0, ending with "end".</summary>
    private static List<long> WriteModLog(string path)
    {
        var ticks = new List<long>();
        var lines = new List<string> { "tick_ms,unix_s,third_person,right_x,right_y,right_z,up_x,up_y,up_z,fwd_x,fwd_y,fwd_z,pos_x,pos_y,pos_z,vfov_deg" };
        for (long tick = TickAtT0 - (long)Phase - 200, k = 0; QpcOf(tick) < T0 + Frames * 1000.0 / Fps + 50; tick += 15 + k++ * 7 % 4)
        {
            ticks.Add(tick);
            long unix = (long)Math.Floor((UtcAtT0 + QpcOf(tick) - T0) / 1000);
            lines.Add($"{tick},{unix},0,1.000000,0.000000,0.000000,0.000000,1.000000,0.000000,0.000000,0.000000,1.000000,{tick * 0.001:F6},1.700000,0.000000,74.000000");
        }
        lines.Add(ReforgerCamera.EndLine);
        File.WriteAllLines(path, lines);
        return ticks;
    }

    private static void WriteCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        using (var frames = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv"))))
            for (int i = 0; i < Frames; i++)
                frames.Write(new GridTick(i, T0 + i * 1000.0 / Fps, T0 + i * 1000.0 / Fps, false));
        using (var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(dir, "clock_samples.csv"))))
            for (int s = -1; s < 12; s++)
                clock.Write(T0 + s * 1000, (long)(UtcAtT0 + s * 1000));
        File.WriteAllText(Path.Combine(dir, "recording.json"), """
            {"capture_id": "cap-0001", "width": 320, "height": 180, "screen_width": 1920, "screen_height": 1080, "fps": 60}
            """);
    }

    private static string WriteProfile(string dir)
    {
        string path = Path.Combine(dir, "profile.json");
        File.WriteAllText(path, """
            {"game_title": "Arma Reforger", "platform": "PC", "localization": "en-US", "camera_source": "reforger-mod",
             "input_mouse_convention": {"maps_to": "camera_look_velocity", "dx_positive": "right",
               "dx_negative": "left", "dy_positive": "down", "dy_negative": "up"},
             "bindings": {"W": "Forward"}}
            """);
        return path;
    }
}
