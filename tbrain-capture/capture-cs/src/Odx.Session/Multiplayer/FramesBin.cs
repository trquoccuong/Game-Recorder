using System.IO.Compression;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Odx.Core;
using Odx.Session.Proto;

namespace Odx.Session;

/// <summary>
/// frames.bin (spec v3 §2.2) and schema.bin.gz. frames.csv already holds shortest round-trip doubles, so converting it
/// loses nothing; the binary adds each frame's server time.
/// </summary>
public static class FramesBin
{
    public const string FileName = "frames.bin", SchemaFileName = "schema.bin.gz";

    /// <param name="frameServerUtcMs">Server UTC of each frame (row i); null when the player has no clock samples.</param>
    public static void Write(string framesCsv, IReadOnlyList<double>? frameServerUtcMs, FileHeader header, string path)
    {
        var (_, rows) = Csv.ReadFile(framesCsv);
        using var output = File.Create(path);
        header.RecordType = FrameRecord.Descriptor.FullName;
        header.WriteDelimitedTo(output);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var record = new FrameRecord
            {
                FrameId = long.Parse(r["frame_id"], System.Globalization.CultureInfo.InvariantCulture),
                TimestampMs = long.Parse(r["timestamp_ms"], System.Globalization.CultureInfo.InvariantCulture),
                ServerTimeUs = frameServerUtcMs is { } utc && i < utc.Count ? (long)Math.Round(utc[i] * 1000) : 0,
                InputMouseDx = Number(r["input_mouse_dx"]) ?? 0,
                InputMouseDy = Number(r["input_mouse_dy"]) ?? 0,
            };
            if (Frames.C2wColumns.All(c => r[c] != Frames.Null)) record.C2W.AddRange(Frames.C2wColumns.Select(c => Number(r[c])!.Value));
            if (Frames.DistortionColumns.All(c => r[c] != Frames.Null))
                record.Distortion.AddRange(Frames.DistortionColumns.Select(c => Number(r[c])!.Value));
            if (Text(r["camera_model"]) is { } model) record.CameraModel = model;
            if (Number(r["camera_fx"]) is { } fx) record.CameraFx = fx;
            if (Number(r["camera_fy"]) is { } fy) record.CameraFy = fy;
            if (Number(r["camera_cx"]) is { } cx) record.CameraCx = cx;
            if (Number(r["camera_cy"]) is { } cy) record.CameraCy = cy;
            if (Text(r["input_keys"]) is { } keys) record.InputKeys = keys;
            if (Text(r["input_actions"]) is { } actions) record.InputActions = actions;
            if (Text(r["input_mouse_buttons"]) is { } buttons) record.InputMouseButtons = buttons;
            record.WriteDelimitedTo(output);
        }
    }

    public static (FileHeader Header, List<FrameRecord> Frames) Read(string path)
    {
        using var input = File.OpenRead(path);
        var header = FileHeader.Parser.ParseDelimitedFrom(input);
        var frames = new List<FrameRecord>();
        while (input.Position < input.Length) frames.Add(FrameRecord.Parser.ParseDelimitedFrom(input));
        return (header, frames);
    }

    /// <summary>The schema of every .bin record, as a gzip-compressed google.protobuf.FileDescriptorSet.</summary>
    public static void WriteSchema(string path)
    {
        var set = new FileDescriptorSet { File = { FileDescriptorProto.Parser.ParseFrom(CaptureReflection.Descriptor.SerializedData) } };
        using var gzip = new GZipStream(File.Create(path), CompressionLevel.Optimal);
        set.WriteTo(gzip);
    }

    private static double? Number(string s) => s == Frames.Null ? null : Frames.ParseDouble(s);

    private static string? Text(string s) => s == Frames.Null ? null : s;
}
