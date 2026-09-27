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

    /// <summary>
    /// The tray menu's own behaviour, all of it visible under "托盘菜单" in that menu.
    ///
    /// These were constants while the menu's placement was being worked out, because each one is a guess
    /// about the shell that the answers on the screen disagree with from machine to machine: whether the
    /// taskbar covers the menu's last items, and how long the Start menu takes to get out of the way. They
    /// are settings now so the answers can be tried without a rebuild.
    /// </summary>
    public bool MenuAvoidTaskbar { get; set; } = true;

    /// <summary>Pixels of daylight kept between the menu and the taskbar. One is enough to be visible; more
    /// is only useful on a scaled display where the taskbar's own rect is reported in physical pixels.</summary>
    public int MenuTaskbarGap { get; set; } = 1;

    /// <summary>
    /// Whether a request for the menu is held back while the shell has the Start menu, Search or the Action Center
    /// on screen.
    ///
    /// It has to be: those panels keep the activation, and a WinForms drop-down closes itself as soon as it loses
    /// the activation - measured, 40 ms after it was shown, so the menu flashed and no entry could be clicked.
    /// Turning AutoClose off to stop that made the menu impossible to dismiss at all (WinForms' own close path
    /// stops working), so the panel is waited for instead.
    /// </summary>
    public bool MenuDeferWhileShellPanel { get; set; } = true;

    /// <summary>
    /// How long a request for the menu waits for the shell's panel to go, in milliseconds. A time rather than a
    /// number of tries because the panel's window keeps answering hit-tests for a second or two after the panel
    /// has been dismissed (it stays while it animates away).
    /// </summary>
    public int MenuPanelWaitMs { get; set; } = 3000;

    /// <summary>
    /// How long a shell panel still counts as "just here" after it has handed the foreground back. It hands
    /// it back as soon as the click that dismisses it is processed - which is when the menu is asked for -
    /// and it stays on screen for the rest of its close animation.
    /// </summary>
    public int MenuShellPanelGraceMs { get; set; } = 120;

    /// <summary>Whether the menu is re-raised on a timer while it is open, because the taskbar and the icon
    /// flyout take their topmost place back after the menu has been shown.</summary>
    public bool MenuKeepTopMost { get; set; } = true;

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
