using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class GeometryTests
{
    private static readonly Vec3 East = new(1, 0, 0), North = new(0, 1, 0), Up = new(0, 0, 1);

    public static TheoryData<Convention, Vec3, Vec3, Vec3> FacingForward => new()
    {
        { Convention.Rfu, East, Up, North },
        { Convention.Unity, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) },   // Unity +Z forward
        { Convention.Unreal, new(0, 1, 0), new(0, 0, 1), new(1, 0, 0) },  // Unreal +X forward, +Y right
        { Convention.Quake, new(0, -1, 0), new(0, 0, 1), new(1, 0, 0) },  // Quake +X forward, +Y left
    };

    [Theory]
    [MemberData(nameof(FacingForward))]
    public void Engine_camera_facing_forward_maps_to_north(Convention convention, Vec3 right, Vec3 up, Vec3 forward)
    {
        var c2w = Geometry.C2wFromEngine(right, up, forward, default, convention);
        AssertClose(East, Column(c2w, 0), 1e-12);
        AssertClose(Up, Column(c2w, 1), 1e-12);
        AssertClose(-North, Column(c2w, 2), 1e-12);  // camera looks along −column 2
        Assert.True(Geometry.RotationErrors(c2w).Det < 1e-12);
    }

    [Fact]
    public void Engine_round_trip_is_proper_rotation()
    {
        var rng = new Random(1234);
        for (int n = 0; n < 2000; n++)
        {
            var rot = RandomRotation(rng);
            Vec3 right = Apply(rot, East), up = Apply(rot, Up), forward = Apply(rot, North);
            var convention = (Convention)rng.Next(4);
            var c2w = Geometry.C2wFromEngine(FromRfu(right, convention), FromRfu(up, convention),
                FromRfu(forward, convention), FromRfu(new Vec3(1, 2, 3), convention), convention);
            AssertClose(right, Column(c2w, 0), 1e-9);
            AssertClose(up, Column(c2w, 1), 1e-9);
            AssertClose(-forward, Column(c2w, 2), 1e-9);
            AssertClose(new Vec3(1, 2, 3), Column(c2w, 3), 1e-9);
            var (det, ortho) = Geometry.RotationErrors(c2w);
            Assert.True(det < 1e-9 && ortho < 1e-9);
        }
    }

    [Fact]
    public void Roll_free_camera_passes_level_checks()
    {
        for (double heading = -180; heading <= 180; heading += 7.5)
            for (double pitch = -89; pitch <= 89; pitch += 4.45)
            {
                var c2w = Geometry.C2wFromForward(Geometry.ForwardFromHeadingPitch(heading, pitch), default);
                Assert.True(Math.Abs(c2w[2, 0]) < 1e-9);  // right axis horizontal
                Assert.True(c2w[2, 1] > 0);               // up axis points up
                var (det, ortho) = Geometry.RotationErrors(c2w);
                Assert.True(Math.Max(det, ortho) < 1e-9);
                Assert.Equal([0.0, 0.0, 0.0, 1.0], [c2w[3, 0], c2w[3, 1], c2w[3, 2], c2w[3, 3]]);
            }
    }

    [Fact]
    public void Heading_is_compass_bearing()
    {
        AssertClose(North, Geometry.ForwardFromHeadingPitch(0, 0), 1e-12);
        AssertClose(East, Geometry.ForwardFromHeadingPitch(90, 0), 1e-12);
        AssertClose(Up, Geometry.ForwardFromHeadingPitch(0, 90), 1e-12);
    }

    [Fact]
    public void Unity_camera_to_world_identity_camera()
    {
        // Unity camera at origin looking +Z: cameraToWorldMatrix = diag(1, 1, -1, 1) (OpenGL camera space).
        var c2w = Geometry.C2wFromUnityCameraToWorld(Diag(1, 1, -1, 1));
        AssertClose(East, Column(c2w, 0), 1e-12);
        AssertClose(Up, Column(c2w, 1), 1e-12);
        AssertClose(-North, Column(c2w, 2), 1e-12);
    }

    [Fact]
    public void Unity_position_maps_y_up_to_z_up()
    {
        var m = Diag(1, 1, -1, 1);
        (m[0, 3], m[1, 3], m[2, 3]) = (2, 5, 7);  // x right, y up, z forward
        AssertClose(new Vec3(2, 7, 5), Column(Geometry.C2wFromUnityCameraToWorld(m), 3), 1e-12);
    }

    [Fact]
    public void Intrinsics()
    {
        AssertClose(new Intrinsics(540, 540, 960, 540), Geometry.IntrinsicsFromVfov(90, 1920, 1080));
        double vfov = 60;
        double hfov = 2 * Math.Atan(Math.Tan(vfov * Math.PI / 180 / 2) * 1920 / 1080) * 180 / Math.PI;
        AssertClose(Geometry.IntrinsicsFromVfov(vfov, 1920, 1080), Geometry.IntrinsicsFromHfov(hfov, 1920, 1080));
        var proj = new double[4, 4];
        (proj[0, 0], proj[1, 1]) = (0.5625, 1.0);  // P00 encodes aspect; fx must not come from it
        AssertClose(new Intrinsics(540, 540, 960, 540), Geometry.IntrinsicsFromProjection(proj, 1920, 1080));
    }

    // Inverse of Geometry.ToRfu: the transpose of each convention's axis permutation.
    private static Vec3 FromRfu(Vec3 v, Convention c) => c switch
    {
        Convention.Rfu => v,
        Convention.Unity => new(v.X, v.Z, v.Y),
        Convention.Unreal => new(v.Y, v.X, v.Z),
        Convention.Quake => new(v.Y, -v.X, v.Z),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    private static double[,] RandomRotation(Random rng)
    {
        double w, x, y, z, n;
        do
        {
            (w, x, y, z) = (rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
            n = Math.Sqrt(w * w + x * x + y * y + z * z);
        } while (n < 0.1);
        (w, x, y, z) = (w / n, x / n, y / n, z / n);
        return new[,]
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y) },
            { 2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x) },
            { 2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y) },
        };
    }

    private static Vec3 Apply(double[,] r, Vec3 v) => new(
        r[0, 0] * v.X + r[0, 1] * v.Y + r[0, 2] * v.Z,
        r[1, 0] * v.X + r[1, 1] * v.Y + r[1, 2] * v.Z,
        r[2, 0] * v.X + r[2, 1] * v.Y + r[2, 2] * v.Z);

    private static double[,] Diag(double a, double b, double c, double d) =>
        new[,] { { a, 0, 0, 0 }, { 0, b, 0, 0 }, { 0, 0, c, 0 }, { 0, 0, 0, d } };

    private static Vec3 Column(double[,] m, int c) => new(m[0, c], m[1, c], m[2, c]);

    private static void AssertClose(Vec3 expected, Vec3 actual, double tol) =>
        Assert.True((expected - actual).Norm() <= tol * 3, $"expected {expected}, got {actual}");

    private static void AssertClose(Intrinsics expected, Intrinsics actual)
    {
        Assert.Equal(expected.Fx, actual.Fx, 1e-6);
        Assert.Equal(expected.Fy, actual.Fy, 1e-6);
        Assert.Equal(expected.Cx, actual.Cx, 1e-6);
        Assert.Equal(expected.Cy, actual.Cy, 1e-6);
    }
}
