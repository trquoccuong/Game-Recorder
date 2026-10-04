namespace Odx.Core;

public static class ExternalTools
{
    /// <summary>
    /// Path of an ffmpeg tool ("ffmpeg", "ffprobe"): the copy publish.ps1 ships in <c>ffmpeg/</c> next to the executable,
    /// otherwise the bare name so it is looked up on PATH (development machines).
    /// </summary>
    public static string Ffmpeg(string tool)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        return File.Exists(bundled) ? bundled : tool;
    }

    /// <summary>The <c>odx</c> command-line tool next to the caller (the package ships both side by side; a development
    /// build copies it next to odx-app).</summary>
    public static string Odx() => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "odx.exe" : "odx");
}
