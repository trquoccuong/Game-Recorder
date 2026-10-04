using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Session;
using Xunit;

namespace Odx.Tests;

public class SessionBuilderTests
{
    private static readonly Dictionary<string, string> Bindings = new() { ["W"] = "Forward", ["Up"] = "Forward", ["Left"] = "Shoot" };

    private static InputEvent Ev(double t, InputKind kind, string value = "", double dx = 0) => new(t, kind, value, dx, 0);

    [Fact]
    public void Bound_inputs_get_actions_and_unbound_keys_are_dropped()
    {
        var (events, unbound) = SessionBuilder.MapInputs(
        [
            Ev(10, InputKind.KeyDown, "W"), Ev(11, InputKind.KeyDown, "Ctrl"), Ev(12, InputKind.ButtonDown, "Left"),
            Ev(13, InputKind.ButtonDown, "Right"), Ev(20, InputKind.KeyUp, "W"), Ev(21, InputKind.KeyUp, "Ctrl"),
        ], Bindings, 0);
        Assert.Equal(new[]
        {
            (InputKind.KeyDown, "W"), (InputKind.ActionStart, "Forward"), (InputKind.ButtonDown, "Left"),
            (InputKind.ActionStart, "Shoot"), (InputKind.ButtonDown, "Right"), (InputKind.KeyUp, "W"),
            (InputKind.ActionEnd, "Forward"),
        }, events.Select(e => (e.Kind, e.Value)));
        Assert.Equal(new Dictionary<string, int> { ["Ctrl"] = 1 }, unbound);
    }

    [Fact]
    public void Typematic_repeats_do_not_keep_an_action_alive()
    {
        var (events, _) = SessionBuilder.MapInputs(
            [Ev(10, InputKind.KeyDown, "W"), Ev(40, InputKind.KeyDown, "W"), Ev(70, InputKind.KeyDown, "W"), Ev(90, InputKind.KeyUp, "W")],
            Bindings, 0);
        Assert.Equal([InputKind.KeyDown, InputKind.ActionStart, InputKind.KeyUp, InputKind.ActionEnd], events.Select(e => e.Kind));
    }

    [Fact]
    public void Action_stays_active_while_any_of_its_keys_is_held()
    {
        var (events, _) = SessionBuilder.MapInputs(
            [Ev(10, InputKind.KeyDown, "W"), Ev(20, InputKind.KeyDown, "Up"), Ev(30, InputKind.KeyUp, "W"), Ev(40, InputKind.KeyUp, "Up")],
            Bindings, 0);
        var actions = events.Where(e => e.Kind is InputKind.ActionStart or InputKind.ActionEnd).Select(e => (e.Kind, e.TQpcMs));
        Assert.Equal([(InputKind.ActionStart, 10.0), (InputKind.ActionEnd, 40.0)], actions);
    }

    [Fact]
    public void Mouse_motion_before_the_first_frame_is_dropped()
    {
        var (events, _) = SessionBuilder.MapInputs([Ev(5, InputKind.MouseMove, dx: 9), Ev(15, InputKind.MouseMove, dx: 1)], Bindings, 10);
        Assert.Equal([15.0], events.Select(e => e.TQpcMs));
    }

    [Fact]
    public void Built_capture_passes_validation()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-build-{Guid.NewGuid():N}");
        try
        {
            WriteCapture(dir);
            string profile = Path.Combine(dir, "profile.json");
            File.WriteAllText(profile, """
                {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
                 "input_mouse_convention": {"maps_to": "camera_look_velocity", "dx_positive": "right",
                   "dx_negative": "left", "dy_positive": "down", "dy_negative": "up"},
                 "bindings": {"W": "Forward", "Left": "Shoot"}}
                """);

            var report = SessionBuilder.Build(dir, GameProfile.Load(profile));

            Assert.Equal(SessionFixtures.NFrames, report.FrameCount);
            Assert.Equal(new Dictionary<string, int> { ["Ctrl"] = 1 }, report.UnboundKeys);
            Assert.Empty(SessionValidator.ValidateSession(dir));
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!;
            Assert.Equal("cap-0001", session["session_id"]!.GetValue<string>());
            Assert.Equal("2026-10-04T10:00:00.000Z", session["created_at_utc"]!.GetValue<string>());
            var (_, rows) = Csv.ReadFile(Path.Combine(dir, "frames.csv"));
            Assert.Equal("W", rows[30]["input_keys"]);
            Assert.Equal("Forward", rows[30]["input_actions"]);
            Assert.DoesNotContain(rows, r => r["input_keys"].Contains("Ctrl"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A capture folder as odx-input + odx-record would leave it: 120 frames at 60 fps on an irregular-free grid.</summary>
    private static void WriteCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        const double t0 = 5_000_000.0, utcAtT0 = 1_791_108_000_000.0;  // QPC ms of frame 0; 2026-10-04T10:00:00Z
        using (var frames = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv"))))
            for (int i = 0; i < SessionFixtures.NFrames; i++)
                frames.Write(new GridTick(i, t0 + i * 1000.0 / SessionFixtures.Fps, t0 + i * 1000.0 / SessionFixtures.Fps, false));
        using (var input = new InputLogWriter(new StreamWriter(Path.Combine(dir, "input_events.csv"))))
        {
            input.Write(new InputEvent(t0 - 3000, InputKind.MouseMove, "", 50, 0));  // before capture: dropped
            input.Write(new InputEvent(t0 + 400, InputKind.KeyDown, "W"));
            input.Write(new InputEvent(t0 + 450, InputKind.KeyDown, "W"));          // typematic repeat
            input.Write(new InputEvent(t0 + 900, InputKind.KeyUp, "W"));
            input.Write(new InputEvent(t0 + 1000, InputKind.KeyDown, "Ctrl"));      // unbound
            input.Write(new InputEvent(t0 + 1100, InputKind.KeyUp, "Ctrl"));
            input.Write(new InputEvent(t0 + 1200, InputKind.MouseMove, "", 3, -1));
        }
        using (var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(dir, "clock_samples.csv"))))
            for (int s = -1; s < 3; s++)
                clock.Write(t0 + s * 1000, (long)(utcAtT0 + s * 1000));
        File.WriteAllText(Path.Combine(dir, "recording.json"), """
            {"capture_id": "cap-0001", "width": 320, "height": 180, "screen_width": 1920, "screen_height": 1080, "fps": 60}
            """);
        SessionFixtures.MakeVideo(Path.Combine(dir, "video.mp4"));
    }
}
