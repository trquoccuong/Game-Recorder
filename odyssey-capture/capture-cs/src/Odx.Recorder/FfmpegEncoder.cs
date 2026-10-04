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

    public void Write(byte[] bgraFrame) => _stdin.Write(bgraFrame, 0, bgraFrame.Length);

    /// <summary>Closes the pipe and waits for ffmpeg; returns its exit code.</summary>
    public int Finish()
    {
        _stdin.Close();
        _proc.WaitForExit();
        return _proc.ExitCode;
    }

    public void Dispose() => _proc.Dispose();
}
