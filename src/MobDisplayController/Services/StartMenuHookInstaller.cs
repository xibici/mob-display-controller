using System.Diagnostics;
using MobDisplayController.Native;

namespace MobDisplayController.Services;

/// <summary>
/// Puts the Start-menu sizing hook (tools/start-menu-size) into the processes that own the Start menu,
/// and says whether it is in there.
///
/// The hook is a plain DLL that is loaded into the shell's process with the ordinary
/// CreateRemoteThread(LoadLibraryW) route - no service, no elevation: both hosts run as the same user, and
/// the one that runs in an AppContainer (SearchHost, at Low integrity) is lower than this app rather than
/// higher. What that host *does* refuse is a library it cannot read, which is why the folder gets the
/// ALL APPLICATION PACKAGES ACE below.
///
/// The hosts cannot be told to reload a library that is already in them, so a rebuild is deployed under a
/// new file name (start_menu_size_hook_&lt;time&gt;.dll) and the newest one is what gets injected - same
/// trick as the button driver's btndrv.sys / btndrv_alt.sys.
/// </summary>
internal static class StartMenuHookInstaller
{
    private const string LibraryPattern = "start_menu_size_hook*.dll";

    /// <summary>The processes that host the Start menu's XAML (see ShellPanels - same list, narrowed).</summary>
    private static readonly string[] s_hostProcesses = { "SearchHost", "StartMenuExperienceHost" };

    /// <summary>
    /// The hook library to inject: the most recently written one in the folder the hook lives in, plus any
    /// copy shipped next to this executable (which is what a fresh install has before anything is built).
    /// </summary>
    public static string? FindLibrary()
    {
        var candidates = new List<string>();

        try
        {
            if (Directory.Exists(StartMenuSizeSettings.FolderPath))
                candidates.AddRange(Directory.GetFiles(StartMenuSizeSettings.FolderPath, LibraryPattern));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"start menu hook: cannot list {StartMenuSizeSettings.FolderPath}: {ex.Message}");
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "start_menu_size_hook.dll");
        if (File.Exists(beside))
            candidates.Add(beside);

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// One line for the menu, in plain words: whether the hook is in the shell right now, and how many
    /// copies of it. Being wrong about this is what makes "I changed the size and nothing happened" hard to
    /// diagnose, so it says "unknown" rather than guessing when the shell processes cannot be inspected.
    ///
    /// More than one copy is called out because it is a broken state, not a curiosity: each copy saves the
    /// original size and then applies its own, and the copies fight over the same three properties (Width,
    /// MinWidth, MaxWidth). Measured: with three copies loaded the Start menu did not appear at all.
    /// </summary>
    public static string DescribeAttachment()
    {
        int hosts = 0, withHook = 0, copies = 0;
        bool unknown = false;

        foreach (var name in s_hostProcesses)
        {
            foreach (var process in GetHostProcesses(name))
            {
                hosts++;

                var count = ProcessInjector.CountLoadedModules((uint)process.Id, "start_menu_size_hook");
                if (count is null)
                {
                    unknown = true;
                    continue;
                }

                if (count.Value > 0)
                    withHook++;

                copies += count.Value;
            }
        }

        if (hosts == 0)
            return "开始菜单宿主进程未运行";
        if (withHook == hosts)
            return copies > hosts
                ? $"钩子装重复了 ({copies} 份 / {hosts} 个进程) — 需重启开始菜单进程"
                : $"钩子已注入 ({withHook}/{hosts})";
        if (unknown && withHook == 0)
            return "注入状态未知";
        if (withHook == 0)
            return "钩子未注入,尺寸不会生效";
        return $"钩子部分注入 ({withHook}/{hosts})";
    }

    /// <summary>
    /// Injects the newest hook library into the host processes that do not have one yet.
    ///
    /// A host that already carries a hook is left alone, and that is the important part: the library is
    /// deployed under a new file name for every build, so loading the newest one into a host that already has
    /// an older one gives that process *two* copies - they both rewrite the same XAML properties, and the
    /// Start menu stops showing up. The way to change version is to let the host restart, which
    /// <see cref="RestartHosts"/> does.
    /// </summary>
    public static bool Attach(out string report)
    {
        report = string.Empty;

        var library = FindLibrary();
        if (library is null)
        {
            report = $"没有找到 {LibraryPattern}(先运行 tools\\start-menu-size 里的构建脚本)";
            return false;
        }

        EnsureFolderRights();

        var injected = 0;
        var alreadyLoaded = 0;
        var hosts = 0;
        var failures = new List<string>();

        foreach (var name in s_hostProcesses)
        {
            foreach (var process in GetHostProcesses(name))
            {
                hosts++;

                if (ProcessInjector.CountLoadedModules((uint)process.Id, "start_menu_size_hook") > 0)
                {
                    alreadyLoaded++;
                    continue;
                }

                if (!ProcessInjector.InjectLibrary((uint)process.Id, library, out var error))
                {
                    failures.Add($"{name} ({process.Id}): {error}");
                    continue;
                }

                injected++;
            }
        }

        if (hosts == 0)
        {
            report = "开始菜单宿主进程未运行";
            return false;
        }

        DebugLog.Write($"start menu hook: injected {Path.GetFileName(library)} into {injected} host process(es), " +
                       $"{alreadyLoaded} already had one");

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
                DebugLog.Write($"start menu hook: {failure}");

            report = $"注入 {injected}/{hosts} 失败: {failures[0]}";
            return false;
        }

        if (injected == 0)
        {
            report = $"宿主进程里已经有钩子了({alreadyLoaded}/{hosts}),要换版本请用「重启开始菜单进程并重新注入」";
            return false;
        }

        report = alreadyLoaded > 0
            ? $"已注入 {injected} 个宿主进程,另外 {alreadyLoaded} 个已有钩子(没动它们)"
            : $"已注入 {injected}/{hosts} 个宿主进程";

        return true;
    }

    /// <summary>
    /// Restarts the processes that host the Start menu, which is the only way to drop a loaded hook: a DLL
    /// cannot be unloaded from another process without running code in it, and the copy that is already there
    /// would keep its XAML handlers installed. The shell starts both of them again on its own.
    /// </summary>
    public static bool RestartHosts(out string report)
    {
        var killed = 0;

        foreach (var name in s_hostProcesses)
        {
            foreach (var process in GetHostProcesses(name))
            {
                try
                {
                    process.Kill();
                    killed++;
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"start menu hook: could not stop {name} ({process.Id}): {ex.Message}");
                }
            }
        }

        if (killed == 0)
        {
            report = "没有可重启的宿主进程";
            return false;
        }

        DebugLog.Write($"start menu hook: restarted {killed} shell host process(es) to drop the loaded hook");

        // They come back in a second or two; the injection that follows needs them to exist.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(500);

            var running = 0;
            foreach (var name in s_hostProcesses)
                running += GetHostProcesses(name).Count();

            if (running >= killed)
                break;
        }

        report = $"已重启 {killed} 个宿主进程";
        return true;
    }

    /// <summary>
    /// Gives ALL APPLICATION PACKAGES modify rights on the hook's folder. SearchHost runs in an AppContainer
    /// and its LoadLibraryW silently returns 0 for a library outside its reach; the hook also writes its log
    /// there. Re-applied every time rather than checked, because icacls is idempotent and reading back a
    /// DACL to decide is more code than the command it would save.
    /// </summary>
    private static void EnsureFolderRights()
    {
        try
        {
            Directory.CreateDirectory(StartMenuSizeSettings.FolderPath);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{StartMenuSizeSettings.FolderPath}\" /grant *S-1-15-2-1:(OI)(CI)(M)",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"start menu hook: could not grant the AppContainer ACE: {ex.Message}");
        }
    }

    private static IEnumerable<Process> GetHostProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }
}
