namespace MobDisplayController.Services;

/// <summary>
/// The size the injected Start-menu hook gives the Start menu.
///
/// The file is the interface between this app and the hook DLL: the hook runs inside the shell's own
/// process (StartMenuExperienceHost.exe), so the two cannot share a settings object and the app cannot
/// tell the hook anything directly. The hook re-reads this file every time the menu is opened, so a change
/// made here shows up the next time the Start menu is shown, with no re-injection and no restart.
///
/// The format is deliberately the plain "key=value" the hook parses (it is a tiny C++ DLL with no JSON
/// parser, and Windhawk's own mod takes the same two numbers).
/// </summary>
public sealed class StartMenuSizeSettings
{
    /// <summary>
    /// Where the hook and its settings live. The folder has an explicit ACE for ALL APPLICATION PACKAGES,
    /// because SearchHost runs in an AppContainer and cannot read a file the package has no right to
    /// (see StartMenuHookInstaller).
    /// </summary>
    public const string FolderPath = @"C:\ProgramData\MobDisplayController";

    public static string SettingsPath => Path.Combine(FolderPath, "start-menu-size.ini");

    public static string LogPath => Path.Combine(FolderPath, "start-menu-size.log");

    /// <summary>
    /// The width range the Start menu can actually be shown at, measured: 488 px (about 325 DIP) is the
    /// narrowest that draws, and anything below it stops the Start menu drawing at all - it then stays broken
    /// for every later open until the shell hosts restart. 350 is the narrowest this app will write, with
    /// daylight above that boundary; the hook clamps at the same idea for values that arrive by hand.
    /// </summary>
    public const int MinimumWidth = 350;

    public const int MaximumWidth = 700;

    public const int MaximumHeight = 1600;

    /// <summary>Width in DIP, or 0 for the width the system would use.</summary>
    public int Width { get; set; }

    /// <summary>Height in DIP, or 0 for the height the system would use.</summary>
    public int Height { get; set; }

    public bool IsSystemDefault => Width <= 0 && Height <= 0;

    /// <summary>0 stays 0 (meaning "system default"); anything else is brought inside the range.</summary>
    public static int ClampWidth(int value)
        => value <= 0 ? 0 : Math.Clamp(value, MinimumWidth, MaximumWidth);

    public static int ClampHeight(int value)
        => value <= 0 ? 0 : Math.Clamp(value, 1, MaximumHeight);

    public static StartMenuSizeSettings Load()
    {
        var settings = new StartMenuSizeSettings();

        try
        {
            if (!File.Exists(SettingsPath))
                return settings;

            foreach (var rawLine in File.ReadAllLines(SettingsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is ';' or '#')
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                if (!int.TryParse(line[(separator + 1)..].Trim(), out var value))
                    continue;

                var key = line[..separator].Trim();
                if (key.Equals("width", StringComparison.OrdinalIgnoreCase))
                    settings.Width = ClampWidth(value);
                else if (key.Equals("height", StringComparison.OrdinalIgnoreCase))
                    settings.Height = ClampHeight(value);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"start menu size: could not read {SettingsPath}: {ex.Message}");
        }

        return settings;
    }

    public bool Save(out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(FolderPath);

            Width = ClampWidth(Width);
            Height = ClampHeight(Height);

            // Written whole and fresh rather than edited in place: the hook parses whatever it finds, and a
            // half-written file would be read as "0", i.e. as "system default" for one open of the menu.
            var text = $"width={Width}\r\nheight={Height}\r\n";
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{SettingsPath} 写入失败: {ex.Message}";
            DebugLog.Write($"start menu size: {error}");
            return false;
        }
    }
}
