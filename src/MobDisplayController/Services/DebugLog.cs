namespace MobDisplayController.Services;

/// <summary>
/// Append-only trace of power-button/display-state handling. This path is driven by a
/// hardware button and a system power notification, so when it misbehaves there's nothing
/// on screen to look at - and often no screen at all. The log is how you find out what
/// actually happened after the fact.
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();

    private static string LogPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MobDisplayController");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "power.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;

                // Keep it from growing without bound across sessions.
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Delete(path);

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }
}
