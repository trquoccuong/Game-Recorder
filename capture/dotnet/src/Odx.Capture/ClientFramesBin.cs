using Google.Protobuf;
using Odx.Capture.ClientMp;
using Odx.Core;

namespace Odx.Capture;

/// <summary>frames_bin.bin of a player's round folder (client_mp.proto): frames.csv at full precision plus server time.</summary>
public static class ClientFramesBin
{
    /// <summary>As in the delivered player folder AMRF-100-S01 (2026-10-27); spec v3 §1 names it frames.bin.</summary>
    public const string FileName = "frames_bin.bin";

    /// <param name="timestampsMs">Unrounded ms since frame 0, one per row.</param>
    /// <param name="frameUtcMs">UTC of each frame on the session's clock (the orchestrator's time service in a room).</param>
    public static void Write(string path, string sessionId, IReadOnlyList<FrameRow> rows, IReadOnlyList<double> timestampsMs,
        IReadOnlyList<double> frameUtcMs, CaptureConfig config)
    {
        using var output = File.Create(path);
        new FileHeader
        {
            Magic = config.FramesBinMagic,
            FormatVersion = 1,
            RecordType = config.FramesBinRecordType,
            SessionId = sessionId,
            Producer = $"Odx Capture {BuildInfo.Version}",
            CreatedUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            Description = "One FrameRecord per frame of video.mp4; same content as frames.csv with full double precision.",
        }.WriteDelimitedTo(output);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var record = new FrameRecord
            {
                FrameId = r.FrameId,
                TimestampMs = timestampsMs[i],
                ServerTimeUs = (long)Math.Round(frameUtcMs[i] * 1000),
                InputMouseDx = r.Input.MouseDx,
                InputMouseDy = r.Input.MouseDy,
            };
            if (r.C2w is { } m && r.Intrinsics is { } k)
            {
                for (int j = 0; j < 16; j++) record.C2W.Add(m[j / 4, j % 4]);
                record.HasCamera = true;
                record.CameraModel = r.CameraModel?.ToLowerInvariant() ?? "";
                (record.CameraFx, record.CameraFy, record.CameraCx, record.CameraCy) = (k.Fx, k.Fy, k.Cx, k.Cy);
                record.Distortion.Add(new double[6]);
                record.VerticalFovDeg = 2 * Math.Atan(k.Cy / k.Fy) * 180 / Math.PI;
            }
            record.InputKeys.Add(Split(r.Input.Keys));
            record.InputActions.Add(Split(r.Input.Actions));
            record.InputMouseButtons.Add(Split(r.Input.MouseButtons));
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

    private static string[] Split(string? values) => values?.Split('|') ?? [];
}
