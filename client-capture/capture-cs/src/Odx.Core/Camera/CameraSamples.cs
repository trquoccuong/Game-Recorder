using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>One camera reading from the extractor DLL, in engine axes and units, stamped with QPC milliseconds.</summary>
public sealed record CameraSample(double TQpcMs, Vec3 Position, Vec3 Forward, Vec3 Right, Vec3 Up, double FovDeg);

/// <summary>extractor.json: how to read the samples (the Lua profile's axis fields).</summary>
public sealed record ExtractorInfo(string Game, Convention Convention, double UnitsToWorld, bool FovVertical, string CameraModel)
{
    public static ExtractorInfo Load(string path)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string Text(string key) => json[key]?.GetValue<string>() ?? throw new FormatException($"{path}: missing \"{key}\"");
        string fovAxis = Text("fov_axis");
        if (fovAxis is not ("vertical" or "horizontal")) throw new FormatException($"{path}: unknown fov_axis \"{fovAxis}\"");
        return new ExtractorInfo(Text("game"), Geometry.ParseConvention(Text("convention")),
            json["units_to_world"]?.GetValue<double>() ?? 1.0, fovAxis == "vertical", Text("camera_model"));
    }

    /// <summary>Canonical C2W and intrinsics for a frame of the given (recorded video) resolution.</summary>
    public (double[,] C2w, Intrinsics Intrinsics) ToCanonical(CameraSample s, int width, int height) => (
        Geometry.C2wFromEngine(s.Right, s.Up, s.Forward, s.Position, Convention, UnitsToWorld),
        FovVertical ? Geometry.IntrinsicsFromVfov(s.FovDeg, width, height) : Geometry.IntrinsicsFromHfov(s.FovDeg, width, height));
}

/// <summary>Reads camera_samples.csv written by the extractor DLL; the format is pinned by tests/fixtures/camera_samples.csv.</summary>
public static class CameraSampleReader
{
    public static List<CameraSample> Read(string path)
    {
        var (_, rows) = Csv.ReadFile(path);
        Vec3 V(Dictionary<string, string> r, string prefix) =>
            new(Frames.ParseDouble(r[prefix + "_x"]), Frames.ParseDouble(r[prefix + "_y"]), Frames.ParseDouble(r[prefix + "_z"]));
        return rows.Select(r => new CameraSample(Frames.ParseDouble(r["t_qpc_ms"]), V(r, "pos"), V(r, "fwd"), V(r, "right"),
            V(r, "up"), Frames.ParseDouble(r["fov_deg"]))).ToList();
    }
}
