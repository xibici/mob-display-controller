using MobDisplayController.Services;

namespace MobDisplayController;

internal static class Program
{
    private const string MutexName = "Local\\MobDisplayController-SingleInstance-7B1F0B2E";

    [STAThread]
    private static void Main(string[] args)
    {
        // A way to run the Start-menu hook installation without the tray, for testing it from a script: the
        // same call the menu's entry makes, so what is verified here is what the menu does. It runs before
        // the single-instance check, because it has nothing to do with the running instance.
        if (args.Length > 0 && args[0].Equals("--inject-start-menu-hook", StringComparison.OrdinalIgnoreCase))
        {
            var attached = StartMenuHookInstaller.Attach(out var report);
            DebugLog.Write($"start menu hook (command line): ok={attached} {report}");
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Deliberately no modal dialog: a MessageBox here would sit waiting for a click and show
            // up as a second, apparently running, process. The tray icon is already there to say the
            // app is running, so just step aside.
            DebugLog.Write("another instance owns the tray - exiting");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());

        GC.KeepAlive(mutex);
    }
}
