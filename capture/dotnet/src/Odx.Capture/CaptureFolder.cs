namespace Odx.Capture;

/// <summary>
/// A player's round folder: the delivered files at the top, every raw log the capture tools and the agent write in raw/,
/// kept so the session can be built again (odx build) and merged into spec v3 (odx merge).
/// </summary>
public static class CaptureFolder
{
    public const string RawDir = "raw";
    public static readonly string[] Delivered = ["video.mp4", "session.json", "frames.csv", "events.jsonl"];

    /// <summary>Where raw files are: raw/ once the folder is tidied, else the folder itself.</summary>
    public static string RawFolder(string dir)
    {
        string raw = Path.Combine(dir, RawDir);
        return Directory.Exists(raw) ? raw : dir;
    }

    public static string Raw(string dir, string name) => Path.Combine(RawFolder(dir), name);

    /// <summary>Moves every file but the delivered ones into raw/.</summary>
    public static void Tidy(string dir)
    {
        string raw = Directory.CreateDirectory(Path.Combine(dir, RawDir)).FullName;
        foreach (string file in Directory.GetFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (!Delivered.Contains(name)) File.Move(file, Path.Combine(raw, name), overwrite: true);
        }
    }
}
