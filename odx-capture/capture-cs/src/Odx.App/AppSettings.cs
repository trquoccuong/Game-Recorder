using System.IO;
using System.Text.Json;
using Odx.Core;
using Odx.Session;

namespace Odx.App;

/// <summary>Persisted in %APPDATA%\Odx\odx-app.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Odx", "odx-app.json");

    public string OutputRoot { get; set; } = @"C:\odx_capture";
    /// <summary>Process name passed to odx record --window; null records the primary monitor.</summary>
    public string? WindowProcess { get; set; }
    public int Fps { get; set; } = 60;
    /// <summary>"en" or "vi"; null follows the Windows display language.</summary>
    public string? Language { get; set; }
    /// <summary>Game profile JSON used to build the session after recording; null skips the build.</summary>
    public string? GameProfile { get; set; }
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    /// <summary>odx-orchestrator URL (multiplayer, spec v3); empty records on its own with the hotkey.</summary>
    public string Orchestrator { get; set; } = "";
    public string PlayerId { get; set; } = Environment.MachineName.ToLowerInvariant();
    /// <summary>In agent mode, the hotkey starts and ends rounds for every player (no game server plugin needed).</summary>
    public bool IsHost { get; set; }

    /// <param name="timeServer">time_server of odx-capture.json; null uses the Windows clock only.</param>
    public CaptureOptions ToCaptureOptions(string? timeServer) => new(OutputRoot, WindowProcess, Fps, GameProfile, timeServer);

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
