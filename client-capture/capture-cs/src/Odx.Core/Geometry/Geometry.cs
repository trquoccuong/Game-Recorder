namespace Odx.Core;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Norm() => Math.Sqrt(Dot(this));
    public Vec3 Normalized() => this * (1.0 / Norm());
}

/// <summary>Engine world frames that can be mapped to canonical RFU (vectors are remapped individually, spec v2 §2.2).</summary>
public enum Convention
{
    /// <summary>GTA V / RAGE: already RFU.</summary>
    Rfu,
    /// <summary>Left-handed, X right, Y up, Z forward.</summary>
    Unity,
    /// <summary>Left-handed, X forward, Y right, Z up.</summary>
    Unreal,
    /// <summary>Right-handed, X forward, Y left, Z up (Quake / Source).</summary>
    Quake,
}

public readonly record struct Intrinsics(double Fx, double Fy, double Cx, double Cy);

/// <summary>
/// Camera geometry for the canonical format (Capture Spec v2 §1.1.2.3–1.1.2.5). Mirrored by extractor/src/math.cpp.
/// Canonical world: RFU — X right/east, Y forward/north, Z up, right-handed.
/// Canonical camera: C2W 4x4 <c>double[4,4]</c>, row-major, columns = Right | Up | Backward | Translation (RUB).
/// </summary>
public static class Geometry
{
    /// <summary>Lua profile / extractor.json names: "rfu", "unity", "unreal", "quake".</summary>
    public static Convention ParseConvention(string name) => name switch
    {
        "rfu" => Convention.Rfu,
        "unity" => Convention.Unity,
        "unreal" => Convention.Unreal,
        "quake" => Convention.Quake,
        _ => throw new FormatException($"unknown convention \"{name}\""),
    };

    public static Vec3 ToRfu(Vec3 v, Convention convention) => convention switch
    {
        Convention.Rfu => v,
        Convention.Unity => new(v.X, v.Z, v.Y),
        Convention.Unreal => new(v.Y, v.X, v.Z),
        Convention.Quake => new(-v.Y, v.X, v.Z),
        _ => throw new ArgumentOutOfRangeException(nameof(convention)),
    };

    public static double[,] C2wFromAxes(Vec3 right, Vec3 up, Vec3 backward, Vec3 position) => new[,]
    {
        { right.X, up.X, backward.X, position.X },
        { right.Y, up.Y, backward.Y, position.Y },
        { right.Z, up.Z, backward.Z, position.Z },
        { 0, 0, 0, 1 },
    };

    /// <summary>Canonical C2W from an engine's camera basis vectors, expressed in that engine's world frame.</summary>
    public static double[,] C2wFromEngine(Vec3 right, Vec3 up, Vec3 forward, Vec3 position, Convention convention,
        double unitsToWorld = 1.0) =>
        C2wFromAxes(ToRfu(right, convention), ToRfu(up, convention), -ToRfu(forward, convention),
            ToRfu(position, convention) * unitsToWorld);

    /// <summary>Converts Unity's Camera.cameraToWorldMatrix (OpenGL camera: columns right, up, -forward).</summary>
    public static double[,] C2wFromUnityCameraToWorld(double[,] u) =>
        C2wFromEngine(Column(u, 0), Column(u, 1), -Column(u, 2), Column(u, 3), Convention.Unity);

    /// <summary>Forward vector in RFU. Heading is a compass bearing: 0 = north (+Y), 90 = east (+X).</summary>
    public static Vec3 ForwardFromHeadingPitch(double headingDeg, double pitchDeg)
    {
        double h = headingDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180;
        return new(Math.Sin(h) * Math.Cos(p), Math.Cos(h) * Math.Cos(p), Math.Sin(p));
    }

    /// <summary>Roll-free C2W from a forward vector (spec v2 §2.2 reconstruction).</summary>
    public static double[,] C2wFromForward(Vec3 forward, Vec3 position)
    {
        var f = forward.Normalized();
        var right = f.Cross(new Vec3(0, 0, 1)).Normalized();
        var up = right.Cross(f).Normalized();
        return C2wFromAxes(right, up, -f, position);
    }

    /// <summary>(|det(R) - 1|, max |R·Rᵀ - I|) for the rotation block of a C2W.</summary>
    public static (double Det, double Ortho) RotationErrors(double[,] c2w)
    {
        double det = c2w[0, 0] * (c2w[1, 1] * c2w[2, 2] - c2w[1, 2] * c2w[2, 1])
                   - c2w[0, 1] * (c2w[1, 0] * c2w[2, 2] - c2w[1, 2] * c2w[2, 0])
                   + c2w[0, 2] * (c2w[1, 0] * c2w[2, 1] - c2w[1, 1] * c2w[2, 0]);
        double ortho = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double dot = 0;
                for (int k = 0; k < 3; k++) dot += c2w[i, k] * c2w[j, k];
                ortho = Math.Max(ortho, Math.Abs(dot - (i == j ? 1 : 0)));
            }
        return (Math.Abs(det - 1), ortho);
    }

    public static Intrinsics IntrinsicsFromVfov(double vfovDeg, int width, int height)
    {
        double fy = height / (2.0 * Math.Tan(vfovDeg * Math.PI / 180 / 2.0));
        return new(fy, fy, width / 2.0, height / 2.0);
    }

    /// <summary>Square-pixel intrinsics from a horizontal FOV (e.g. Unreal FOVAngle).</summary>
    public static Intrinsics IntrinsicsFromHfov(double hfovDeg, int width, int height)
    {
        double fx = width / (2.0 * Math.Tan(hfovDeg * Math.PI / 180 / 2.0));
        return new(fx, fx, width / 2.0, height / 2.0);
    }

    /// <summary>
    /// From an unjittered projection matrix: fy = P[1][1]·H/2 and fx = fy; never derive fx from P[0][0]
    /// (spec v2 §1.1.3 note).
    /// </summary>
    public static Intrinsics IntrinsicsFromProjection(double[,] proj, int width, int height)
    {
        double fy = proj[1, 1] * height / 2.0;
        return new(fy, fy, width / 2.0, height / 2.0);
    }

    private static Vec3 Column(double[,] m, int c) => new(m[0, c], m[1, c], m[2, c]);
}
