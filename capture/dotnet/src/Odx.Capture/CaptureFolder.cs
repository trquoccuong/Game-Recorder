namespace Odx.Capture;

/// <summary>
/// A player's round folder: the delivered files at the top, every raw log the capture tools and the agent write in raw/,
/// kept so the session can be built again (odx build) and merged into spec v3 (odx merge).
/// </summary>
public static class CaptureFolder
{
    public const string RawDir = "raw";
    /// <summary>
    /// As in the client's multiplayer delivery (sample round 20261017-100551-2907ed-r03), plus key_bindings.json; the spec v2
    /// session.json goes to raw/, its fields are in player.json.
    /// </summary>
    public static readonly string[] Delivered =
        ["video.mp4", PlayerFile.FileName, "frames.csv", ClientFramesBin.FileName, "events.jsonl", SessionBuilder.KeyBindingsFile];

    /// <summary>Where raw files are: raw/ once the folder is tidied, else the folder itself.</summary>
    public static string RawFolder(string dir)
    {
        string raw = Path.Combine(dir, RawDir);
        return Directory.Exists(raw) ? raw : dir;
    }

    public static string Raw(string dir, string name) => Path.Combine(RawFolder(dir), name);

    /// <summary>A file at the top of the folder, else in raw/: session.json is on top until the folder is tidied, and stays
    /// there in folders tidied by 0.1.6 and earlier.</summary>
    public static string Find(string dir, string name) =>
        Path.Combine(dir, name) is var top && File.Exists(top) ? top : Raw(dir, name);

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
