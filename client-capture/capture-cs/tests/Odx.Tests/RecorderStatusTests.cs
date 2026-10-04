using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class RecorderStatusTests
{
    [Fact]
    public void Round_trips_as_one_snake_case_json_line()
    {
        var status = new RecorderStatus(RecorderStatus.Stopped, 100, 82, 1, 1920, 1080, 60, "ffmpeg exited with code 1");
        string json = status.ToJson();
        Assert.DoesNotContain('\n', json);
        Assert.Contains("\"event\":\"stopped\"", json);
        Assert.Equal(status, RecorderStatus.TryParse(json));
    }

    [Theory]
    [InlineData("recording 1920x1080 @ 60 fps to C:/x (Ctrl+C to stop)")]
    [InlineData("{not json")]
    public void Other_lines_are_ignored(string line) => Assert.Null(RecorderStatus.TryParse(line));
}
