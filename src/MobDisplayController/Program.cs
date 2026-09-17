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
            MessageBox.Show(
                "移动显示器控制器已经在系统托盘中运行。",
                "MobDisplayController",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());

        GC.KeepAlive(mutex);
    }
}
