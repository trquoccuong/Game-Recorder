using System.Diagnostics;

namespace Odx.Recorder;

/// <summary>Encodes raw BGRA frames piped to an ffmpeg process into a constant-frame-rate MP4 without B-frames.</summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _proc;
    private readonly Stream _stdin;

    public FfmpegEncoder(string ffmpeg, string output, int width, int height, double fps, string encoder)
    {
        string codec = encoder switch
        {
            "nvenc" => "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -bf 0 -g 120",
            "x264" => "-c:v libx264 -preset veryfast -crf 20 -bf 0 -g 120",
            _ => throw new ArgumentException($"unknown encoder '{encoder}' (nvenc|x264)"),
        };
        var fr = fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // yuv420p needs even dimensions: crop a trailing odd row/column if the window size is odd.
        var args = $"-y -loglevel error -f rawvideo -pix_fmt bgra -s {width}x{height} -framerate {fr} -i - " +
                   $"-vf crop=trunc(iw/2)*2:trunc(ih/2)*2 {codec} -pix_fmt yuv420p -r {fr} \"{output}\"";
        _proc = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("cannot start ffmpeg");
        _stdin = _proc.StandardInput.BaseStream;
    }

    private static readonly string ProbeCache =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odx", "encoder.json");

    /// <summary>
    /// "auto": NVENC when this ffmpeg can open it on this machine (NVIDIA GPU; the bundled build needs driver 471+), else
    /// x264. NVENC keeps the CPU free for the game; x264 runs anywhere. A cold NVENC probe takes ~2 s, so its result is
    /// cached per ffmpeg build in %LOCALAPPDATA%\Odx\encoder.json.
    /// </summary>
    public static string Resolve(string ffmpeg, string encoder)
    {
        if (encoder != "auto") return encoder;
        var file = new FileInfo(ffmpeg);
        string key = file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : ffmpeg;
        try
        {
            var cached = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ProbeCache));
            if ((string?)cached?["ffmpeg"] == key && (string?)cached["encoder"] is { } known) return known;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        string resolved = ProbeNvenc(ffmpeg) ? "nvenc" : "x264";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProbeCache)!);
            File.WriteAllText(ProbeCache, new System.Text.Json.Nodes.JsonObject { ["ffmpeg"] = key, ["encoder"] = resolved }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return resolved;
    }

    /// <summary>Encodes one black frame piped in as raw BGRA, the same input path as a recording (the bundled ffmpeg has no lavfi).</summary>
    private static bool ProbeNvenc(string ffmpeg)
    {
        string output = Path.Combine(Path.GetTempPath(), $"odx-nvenc-probe-{Environment.ProcessId}.mp4");
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(ffmpeg,
                $"-v error -y -f rawvideo -pix_fmt bgra -s 256x144 -framerate 30 -i - -frames:v 1 -c:v h264_nvenc -pix_fmt yuv420p \"{output}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            })!;
            var stderr = probe.StandardError.ReadToEndAsync();
            try
            {
                probe.StandardInput.BaseStream.Write(new byte[256 * 144 * 4]);
                probe.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            stderr.Wait();
            return probe.WaitForExit(15_000) && probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            File.Delete(output);
        }
    }

    public void Write(byte[] bgraFrame) => _stdin.Write(bgraFrame, 0, bgraFrame.Length);

    public int Finish()
    {
        _stdin.Close();
        _proc.WaitForExit();
        return _proc.ExitCode;
    }

    public void Dispose() => _proc.Dispose();
}
