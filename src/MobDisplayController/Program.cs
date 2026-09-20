using MobDisplayController.Services;

namespace MobDisplayController;

internal static class Program
{
    private const string MutexName = "Local\\MobDisplayController-SingleInstance-7B1F0B2E";

    [STAThread]
    private static void Main(string[] args)
    {
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
