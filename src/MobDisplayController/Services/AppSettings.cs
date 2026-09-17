using System.Text.Json;
using System.Text.Json.Serialization;

namespace MobDisplayController.Services;

public sealed class AppSettings
{
    /// <summary>Substring (case-insensitive) matched against a monitor's friendly name to identify "the DP monitor".</summary>
    public string TargetMonitorNameFilter { get; set; } = "DP";

    /// <summary>Last-selected target monitor key (adapter LUID + target id), used to remember the exact device across renames.</summary>
    public string? TargetMonitorKey { get; set; }

    public bool StartWithWindows { get; set; }

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MobDisplayController");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
        File.WriteAllText(SettingsPath, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
