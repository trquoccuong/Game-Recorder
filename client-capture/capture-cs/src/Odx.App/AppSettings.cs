using System.IO;
using System.Text.Json;
using Odx.Core;
using Odx.Session;

namespace Odx.App;

/// <summary>Persisted in %APPDATA%\Client\odx-app.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Client", "odx-app.json");

    public string OutputRoot { get; set; } = @"C:\odx_capture";
    /// <summary>Process name passed to odx record --window; null records the primary monitor.</summary>
    public string? WindowProcess { get; set; }
    public int Fps { get; set; } = 60;
    /// <summary>Game profile JSON used to build the session after recording; null skips the build.</summary>
    public string? GameProfile { get; set; }
    public Hotkey Hotkey { get; set; } = Hotkey.Default;

    public CaptureOptions ToCaptureOptions() => new(OutputRoot, WindowProcess, Fps, GameProfile);

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
