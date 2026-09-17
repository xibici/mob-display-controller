namespace MobDisplayController;

/// <summary>Generates a small monitor-shaped tray icon at runtime, so the project needs no external .ico asset.</summary>
internal static class TrayIcons
{
    public static Icon Default => Create();

    private static Icon Create()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var screenBrush = new SolidBrush(Color.FromArgb(255, 30, 144, 255));
            using var screenPen = new Pen(Color.White, 2f);
            using var standBrush = new SolidBrush(Color.FromArgb(255, 90, 90, 90));

            var screenRect = new Rectangle(2, 3, size - 4, size - 14);
            g.FillRectangle(screenBrush, screenRect);
            g.DrawRectangle(screenPen, screenRect);

            g.FillRectangle(standBrush, size / 2 - 4, size - 10, 8, 4);
            g.FillRectangle(standBrush, size / 2 - 8, size - 6, 16, 3);
        }

        IntPtr hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        // Clone so we own the memory independently of the HICON handle lifetime.
        var clone = (Icon)icon.Clone();
        icon.Dispose();
        return clone;
    }
}
