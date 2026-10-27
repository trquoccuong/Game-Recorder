using System.Diagnostics;

namespace Odx.Core;

/// <summary>Which H.264 encoder "auto" means on this machine; odx-app asks at start-up so the first recording does not wait.</summary>
public static class EncoderChoice
{
    private static readonly string ProbeCache =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Odx", "encoder.json");

    /// <summary>
    /// "auto": the first hardware encoder this ffmpeg can open on this machine, else x264: NVENC (NVIDIA GPU; the bundled
    /// build needs driver 471+), then Media Foundation's ("mf": Intel Quick Sync, AMD). A hardware encoder keeps the CPU
    /// free for the game (1080p30: ~0.25 core against ~1.4 for x264); x264 runs anywhere. A cold probe takes ~2 s, so the
    /// result is cached per ffmpeg build in %LOCALAPPDATA%\Odx\encoder.json.
    /// </summary>
    public static string Resolve(string ffmpeg, string encoder)
    {
        if (encoder != "auto") return encoder;
        var file = new FileInfo(ffmpeg);
        // "2": results cached before "mf" was tried are stale.
        string key = "2|" + (file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : ffmpeg);
        try
        {
            var cached = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ProbeCache));
            if ((string?)cached?["ffmpeg"] == key && (string?)cached["encoder"] is { } known) return known;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        string resolved = Probe(ffmpeg, "h264_nvenc") ? "nvenc" : Probe(ffmpeg, "h264_mf -hw_encoding 1") ? "mf" : "x264";
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

    /// <summary>Encodes one black frame piped in as raw NV12, the same input path as a recording (the bundled ffmpeg has no lavfi).</summary>
    private static bool Probe(string ffmpeg, string codec)
    {
        string output = Path.Combine(Path.GetTempPath(), $"odx-encoder-probe-{Environment.ProcessId}.mp4");
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(ffmpeg,
                $"-v error -y -f rawvideo -pix_fmt nv12 -s 640x360 -framerate 30 -i - -frames:v 1 -c:v {codec} \"{output}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            })!;
            var stderr = probe.StandardError.ReadToEndAsync();
            try
            {
                probe.StandardInput.BaseStream.Write(new byte[640 * 360 * 3 / 2]);
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
}
