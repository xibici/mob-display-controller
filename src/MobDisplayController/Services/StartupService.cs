using Microsoft.Win32;

namespace MobDisplayController.Services;

/// <summary>Manages "start with Windows" via the per-user Run registry key (no admin rights required).</summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MobDisplayController";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrEmpty(value))
            return false;

        // Treat as enabled only if it still points at the current executable location.
        return string.Equals(value.Trim('"'), ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
            key.SetValue(ValueName, $"\"{ExePath}\" --minimized", RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExePath => Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("无法确定当前程序路径。");
}
