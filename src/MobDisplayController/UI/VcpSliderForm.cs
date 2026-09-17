namespace MobDisplayController.UI;

/// <summary>Small popup with a slider for adjusting a single DDC/CI VCP value (brightness or volume).</summary>
public sealed class VcpSliderForm : Form
{
    public VcpSliderForm(string title, uint current, uint max, Action<uint> onApply)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(280, 110);
        ShowInTaskbar = false;
        TopMost = true;

        var slider = new TrackBar
        {
            Minimum = 0,
            Maximum = (int)Math.Max(1, max),
            Value = (int)Math.Min(current, max),
            TickFrequency = Math.Max(1, (int)max / 10),
            Dock = DockStyle.Top,
            Height = 45,
        };

        var valueLabel = new Label
        {
            Text = $"{slider.Value} / {max}",
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 24,
        };

        slider.Scroll += (_, _) =>
        {
            valueLabel.Text = $"{slider.Value} / {max}";
            onApply((uint)slider.Value);
        };

        var closeButton = new Button
        {
            Text = "关闭",
            Dock = DockStyle.Bottom,
            Height = 30,
        };
        closeButton.Click += (_, _) => Close();

        Controls.Add(closeButton);
        Controls.Add(valueLabel);
        Controls.Add(slider);
    }
}
