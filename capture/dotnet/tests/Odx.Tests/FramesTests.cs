using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class FramesTests
{
    private static readonly double[] Times = [0, 17, 33, 50, 67];

    private static InputEvent Ev(double t, InputKind kind, string value = "", double dx = 0, double dy = 0) =>
        new(t, kind, value, dx, dy);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Held_key_spans_frames()
    {
        var rows = Frames.AggregateInputs(Times, [Ev(10, InputKind.KeyDown, "W"), Ev(45, InputKind.KeyUp, "W")]);
        Assert.Equal([null, "W", "W", "W", null], rows.Select(r => r.Keys));
    }

    [Fact]
    public void Tap_shorter_than_a_frame_is_kept()
    {
        var rows = Frames.AggregateInputs(Times, [Ev(20, InputKind.KeyDown, "Space"), Ev(25, InputKind.KeyUp, "Space")]);
        Assert.Equal([null, null, "Space", null, null], rows.Select(r => r.Keys));
    }

    [Fact]
    public void Mouse_deltas_are_summed_per_frame()
    {
        var rows = Frames.AggregateInputs(Times, new[] { 18.0, 20, 30, 33 }.Select(t => Ev(t, InputKind.MouseMove, dx: 1.5, dy: -0.5)));
        Assert.Equal([0.0, 0.0, 6.0, 0.0, 0.0], rows.Select(r => r.MouseDx));
        Assert.Equal([0.0, 0.0, -2.0, 0.0, 0.0], rows.Select(r => r.MouseDy));
    }

    [Fact]
    public void Multiple_values_are_pipe_delimited_in_press_order()
    {
        var row = Frames.AggregateInputs([17],
        [
            Ev(1, InputKind.KeyDown, "W"),
            Ev(2, InputKind.KeyDown, "Shift"),
            Ev(1, InputKind.ActionStart, "Forward"),
            Ev(2, InputKind.ActionStart, "Sprint"),
            Ev(3, InputKind.ButtonDown, "Left"),
        ])[0];
        Assert.Equal(("W|Shift", "Forward|Sprint", "Left"), (row.Keys, row.Actions, row.MouseButtons));
    }

    [Fact]
    public void Reads_odx_input_log_fixture()
    {
        var events = InputLogReader.Read(Fixture("input_events.csv"));
        Assert.Equal(Ev(1000.5, InputKind.KeyDown, "W"), events[0]);
        Assert.Equal(Ev(1001.25, InputKind.MouseMove, "", 3, -1), events[1]);
        var rows = Frames.AggregateInputs([1005, 1015, 1025, 1035], events);
        Assert.Equal(["W", "W", "W", null], rows.Select(r => r.Keys));
        Assert.Equal([null, "Left", "Left", "Left"], rows.Select(r => r.MouseButtons));
        Assert.Equal(3.0, rows[0].MouseDx);
    }

    [Fact]
    public void Reads_odx_record_frame_log_fixture()
    {
        var frames = VideoFrameLogReader.Read(Fixture("video_frames.csv"));
        Assert.Equal([0L, 1, 2], frames.Select(f => f.Index));
        Assert.Equal(1000 + 1000.0 / 60, frames[1].TGridMs);
        Assert.True(frames[1].TSourceMs == 1000.0 && frames[1].Duplicate);
        // Grid times are the per-frame clock for input aggregation.
        var rows = Frames.AggregateInputs(frames.Select(f => f.TGridMs).ToList(), [Ev(1010, InputKind.KeyDown, "W")]);
        Assert.Equal([null, "W", "W"], rows.Select(r => r.Keys));
    }

    [Fact]
    public void Writer_emits_every_column_and_null_convention()
    {
        string path = Path.Combine(Path.GetTempPath(), $"odx-frames-{Guid.NewGuid():N}.csv");
        try
        {
            Frames.WriteCsv(path, [new FrameRow(0, 0, null, null, null, new FrameInput(null, null, null, 0.0, 0.1 + 0.2))]);
            var (header, rows) = Csv.ReadFile(path);
            Assert.Equal(Frames.Columns, header);
            Assert.Equal("0.0", rows[0]["input_mouse_dx"]);
            Assert.Equal(0.1 + 0.2, Frames.ParseDouble(rows[0]["input_mouse_dy"]));  // full float precision preserved
            Assert.Equal(Frames.Null, rows[0]["C2W_M00"]);
            Assert.Equal(Frames.Null, rows[0]["input_keys"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(1.5, "1.5")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(17.0, "17.0")]
    public void Doubles_always_carry_a_decimal_point_or_exponent(double v, string expected) =>
        Assert.Equal(expected, Frames.FormatDouble(v));

    [Fact]
    public void Csv_round_trips_quoted_fields()
    {
        var records = Csv.ReadRecords(new StringReader(Csv.Line(["a", "W,Shift", "say \"hi\""]) + Csv.Line(["", "x", ""])));
        Assert.Equal(["a", "W,Shift", "say \"hi\""], records[0]);
        Assert.Equal(["", "x", ""], records[1]);
    }
}
