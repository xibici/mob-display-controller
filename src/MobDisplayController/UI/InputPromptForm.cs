namespace MobDisplayController.UI;

/// <summary>
/// The small "one value" prompt the tray menu uses for its custom entries - a number (a Start menu size, a
/// pixel gap) or a short piece of text (the monitor name filter).
///
/// WinForms has nothing built in for this that is not either a full dialog or the Visual Basic input box,
/// and the menu needs it in a dozen places, so it lives here once with the DPI handling of a normal form.
/// </summary>
internal sealed class InputPromptForm : Form
{
    private readonly NumericUpDown? _number;
    private readonly TextBox? _text;

    private InputPromptForm(string title, string label, string initial, int minimum, int maximum, bool numeric)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(14),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        if (numeric)
        {
            _number = new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(int.TryParse(initial, out var value) ? value : minimum, minimum, maximum),
                Dock = DockStyle.Top,
            };
        }
        else
        {
            _text = new TextBox { Text = initial, Dock = DockStyle.Top };
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0),
        };

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        layout.Controls.Add(numeric ? _number! : _text!);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>The entered number, or null when the prompt was cancelled.</summary>
    public static int? AskNumber(IWin32Window? owner, string title, string label, int initial, int minimum, int maximum)
    {
        using var form = new InputPromptForm(title, label, initial.ToString(), minimum, maximum, numeric: true);
        return form.ShowDialog(owner) == DialogResult.OK ? (int)form._number!.Value : null;
    }

    /// <summary>The entered text, or null when the prompt was cancelled or left empty.</summary>
    public static string? AskText(IWin32Window? owner, string title, string label, string initial)
    {
        using var form = new InputPromptForm(title, label, initial, 0, 0, numeric: false);
        if (form.ShowDialog(owner) != DialogResult.OK)
            return null;

        var text = form._text!.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
