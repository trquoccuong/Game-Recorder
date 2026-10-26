using System.IO;
using System.Text.Json;
using Odx.Capture;
using Odx.Core;

namespace Odx.App;

/// <summary>Persisted in %APPDATA%\Odx\odx-app.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Odx", "odx-app.json");

    /// <summary>Process name passed to odx record --window; null records the primary monitor.</summary>
    public string? WindowProcess { get; set; }
    public int Fps { get; set; } = 30;
    public bool RecordAudio { get; set; }
    /// <summary>"en" or "vi"; null follows the Windows display language.</summary>
    public string? Language { get; set; }
    /// <summary>Game profile JSON used to build the session after recording; null skips the build.</summary>
    public string? GameProfile { get; set; }
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    public string PlayerId { get; set; } = Environment.MachineName.ToLowerInvariant();

    /// <param name="timeServer">time_server of odx-capture.json; null uses the Windows clock only.</param>
    /// <remarks>Recordings go to the package's data\captures\ (<see cref="PackageFolders"/>).</remarks>
    public CaptureOptions ToCaptureOptions(string? timeServer) =>
        new(PackageFolders.Captures, WindowProcess, Fps, GameProfile, timeServer, Audio: RecordAudio);

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
