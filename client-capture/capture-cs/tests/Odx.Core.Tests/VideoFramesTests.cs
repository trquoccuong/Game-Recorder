using Odx.Core;
using Xunit;

namespace Odx.Core.Tests;

public class GridResamplerTests
{
    private const double Fps = 50;  // 20 ms grid keeps the arithmetic exact

    private static List<GridTick> Run(GridResampler r, double[] frames, double stop)
    {
        var all = new List<GridTick>();
        foreach (var t in frames) all.AddRange(r.Advance(t));
        all.AddRange(r.Finish(stop));
        return all;
    }

    [Fact]
    public void Frames_on_the_grid_map_one_to_one()
    {
        var r = new GridResampler(Fps);
        var ticks = Run(r, [1000, 1020, 1040, 1060], stop: 1080);
        Assert.Equal([1000.0, 1020, 1040, 1060], ticks.Select(t => t.TSourceMs));
        Assert.Equal([1000.0, 1020, 1040, 1060], ticks.Select(t => t.TGridMs));
        Assert.Equal([0L, 1, 2, 3], ticks.Select(t => t.Index));
        Assert.All(ticks, t => Assert.False(t.Duplicate));
        Assert.Equal(0, r.Duplicates);
        Assert.Equal(0, r.DroppedSourceFrames);
    }

    [Fact]
    public void Half_rate_capture_duplicates_every_other_tick()
    {
        var r = new GridResampler(Fps);
        var ticks = Run(r, [1000, 1040, 1080], stop: 1120);
        Assert.Equal([1000.0, 1000, 1040, 1040, 1080, 1080], ticks.Select(t => t.TSourceMs));
        Assert.Equal([false, true, false, true, false, true], ticks.Select(t => t.Duplicate));
        Assert.Equal(3, r.Duplicates);
    }

    [Fact]
    public void Frames_faster_than_the_grid_are_dropped_and_counted()
    {
        var r = new GridResampler(Fps);
        var ticks = Run(r, [1000, 1005, 1010, 1025], stop: 1040);
        // tick 1000 -> 1000; 1005 is replaced by 1010 before tick 1020; tick 1020 -> 1010
        Assert.Equal([1000.0, 1010], ticks.Select(t => t.TSourceMs));
        Assert.Equal(1, r.DroppedSourceFrames);  // 1005 never shown
        // 1025 replaces 1010 after tick 1020; the next tick (1040) is not before the stop time, so it is never shown
        Assert.Equal(2, ticks.Count);
    }

    [Fact]
    public void Tick_at_exactly_the_new_frame_time_shows_the_new_frame()
    {
        var r = new GridResampler(Fps);
        var ticks = Run(r, [1000, 1020], stop: 1021);
        Assert.Equal([1000.0, 1020], ticks.Select(t => t.TSourceMs));
    }

    [Fact]
    public void No_frames_means_no_ticks()
    {
        Assert.Empty(new GridResampler(Fps).Finish(5000));
    }
}

public class VideoFrameLogTests
{
    [Fact]
    public void Writer_output_matches_shared_fixture()
    {
        var sw = new StringWriter();
        var log = new VideoFrameLogWriter(sw);
        log.Write(new GridTick(0, 1000, 1000, false));
        log.Write(new GridTick(1, 1016.6666666666666, 1000, true));
        log.Write(new GridTick(2, 1033.3333333333333, 1031.25, false));
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "video_frames.csv"));
        Assert.Equal(expected, sw.ToString());
    }
}
