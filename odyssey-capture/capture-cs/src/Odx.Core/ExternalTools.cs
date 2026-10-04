namespace Odx.Core;

public static class ExternalTools
{
    /// <summary>
    /// Path of an ffmpeg tool ("ffmpeg", "ffprobe"). The package built by publish.ps1 ships them in <c>ffmpeg/</c> at
    /// its root, two levels above <c>tools/odx/</c>; the search walks up that far. Otherwise the bare name is
    /// returned so it is looked up on PATH (development machines).
    /// </summary>
    public static string Ffmpeg(string tool)
    {
        string file = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up <= 2 && dir is not null; up++, dir = dir.Parent)
        {
            string bundled = Path.Combine(dir.FullName, "ffmpeg", file);
            if (File.Exists(bundled)) return bundled;
        }
        return tool;
    }

    /// <summary>
    /// Path of the <c>odx</c> command-line tool: <c>tools/odx/</c> in a published package, otherwise next to the caller,
    /// where a development build copies it.
    /// </summary>
    public static string Odx()
    {
        string exe = OperatingSystem.IsWindows() ? "odx.exe" : "odx";
        string packaged = Path.Combine(AppContext.BaseDirectory, "tools", "odx", exe);
        return File.Exists(packaged) ? packaged : Path.Combine(AppContext.BaseDirectory, exe);
    }
}
