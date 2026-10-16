using System;
using System.IO;
using System.Text.Json;

namespace clony;

/// <summary>User settings persisted to %APPDATA%\clony\settings.json.</summary>
internal sealed class AppSettings
{
    /// <summary>Accent colour (hex) used across the GUI. Default white.</summary>
    public string AccentColor { get; set; } = "#FFFFFF";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "clony", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable or corrupt settings: fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Saves; returns an error message instead of throwing so a read-only profile can't crash the app.</summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
