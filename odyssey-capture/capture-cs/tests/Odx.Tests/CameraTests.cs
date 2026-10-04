using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class CameraTests
{
    [Fact]
    public void Reads_the_extractor_fixture_and_converts_unreal_axes_to_canonical()
    {
        var samples = CameraSampleReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "camera_samples.csv"));
        Assert.Equal(2, samples.Count);
        Assert.Equal(1017.25, samples[1].TQpcMs);
        Assert.Equal(new Vec3(0.6, 0.8, 0), samples[1].Forward);

        var info = new ExtractorInfo("Test", Convention.Unreal, 0.01, FovVertical: false, "PINHOLE");
        var (c2w, k) = info.ToCanonical(samples[0], 1920, 1080);
        // Unreal +X forward is north; columns are Right | Up | Backward | Translation (RFU metres).
        var expected = new double[,] { { 1, 0, 0, -0.5025 }, { 0, 0, -1, 1 }, { 0, 1, 0, 1.7 }, { 0, 0, 0, 1 } };
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(expected[r, c], c2w[r, c], 1e-12);
        Assert.Equal(960, k.Fx, 1e-9);  // 90° horizontal at 1920 px
        Assert.Equal(k.Fx, k.Fy);
    }

    [Theory]
    [InlineData("unreal", Convention.Unreal)]
    [InlineData("quake", Convention.Quake)]
    public void Parses_profile_convention_names(string name, Convention expected) =>
        Assert.Equal(expected, Geometry.ParseConvention(name));
}
