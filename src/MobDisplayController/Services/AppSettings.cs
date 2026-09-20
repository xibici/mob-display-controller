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

    /// <summary>When true, the app takes over the physical power button: pressing it switches to
    /// "external screen only" instead of Windows' native "turn off all displays".</summary>
    public bool PowerButtonTakeoverEnabled { get; set; }

    /// <summary>The power button's original AC (plugged in) action, saved before we overwrote it
    /// with "do nothing", so it can be restored when the takeover is turned back off.</summary>
    public uint? SavedPowerButtonActionAc { get; set; }

    /// <summary>Same as <see cref="SavedPowerButtonActionAc"/> but for DC (on battery).</summary>
    public uint? SavedPowerButtonActionDc { get; set; }

    /// <summary>The "require a password on wakeup" setting's original AC value, saved before the takeover
    /// turns it off so the sign-in screen stops appearing after a power-button press.</summary>
    public uint? SavedConsoleLockAc { get; set; }

    /// <summary>Same as <see cref="SavedConsoleLockAc"/> but for DC (on battery).</summary>
    public uint? SavedConsoleLockDc { get; set; }

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

        // Write-then-replace rather than write-in-place: a crash or power loss mid-write would leave a
        // truncated file, and Load() would then quietly fall back to defaults - losing exactly the
        // "original action" values that are supposed to survive.
        var path = SettingsPath;
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
