using MobDisplayController.Models;
using MobDisplayController.Services;

namespace MobDisplayController.UI;

public sealed class SettingsForm : Form
{
    private readonly DisplayService _displayService;
    private readonly StartupService _startupService;
    private readonly AppSettings _settings;

    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly TextBox _filterBox = new() { Dock = DockStyle.Top };
    private readonly CheckBox _autostartCheck = new() { Text = "开机自动启动", Dock = DockStyle.Top, AutoSize = true };
    private readonly Label _statusLabel = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40 };

    private List<MonitorEntry> _monitors = new();

    public SettingsForm(DisplayService displayService, StartupService startupService, AppSettings settings)
    {
        _displayService = displayService;
        _startupService = startupService;
        _settings = settings;

        Text = "MobDisplayController 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 320);
        ShowInTaskbar = true;

        BuildLayout();
        LoadMonitors();

        _filterBox.Text = _settings.TargetMonitorNameFilter;
        _autostartCheck.Checked = _settings.StartWithWindows;
    }

    private void BuildLayout()
    {
        var padding = new Padding(12);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = padding,
            AutoSize = true,
        };

        var title = new Label
        {
            Text = "选择要控制的便携/远程显示器 (例如名称包含 \"DP\" 的那台):",
            Dock = DockStyle.Top,
            AutoSize = true,
        };

        var filterLabel = new Label { Text = "名称匹配关键字 (用于开机后自动重新识别该显示器):", Dock = DockStyle.Top, AutoSize = true };

        var refreshButton = new Button { Text = "刷新显示器列表", Dock = DockStyle.Top };
        refreshButton.Click += (_, _) => LoadMonitors();

        var infoLabel = new Label
        {
            Text = "提示: 断开/连接通过 Windows 显示配置接口实现,等价于\n\"高级显示器设置\"里的断开/连接;亮度和音量通过\nDDC/CI 调节,部分纯 USB 便携屏可能不支持。",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 60,
            ForeColor = Color.DimGray,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
        };

        var saveButton = new Button { Text = "保存并关闭" };
        saveButton.Click += (_, _) => SaveAndClose();

        var cancelButton = new Button { Text = "取消" };
        cancelButton.Click += (_, _) => Close();

        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(cancelButton);

        Controls.Add(infoLabel);
        Controls.Add(_autostartCheck);
        Controls.Add(_statusLabel);
        Controls.Add(_monitorCombo);
        Controls.Add(title);
        Controls.Add(refreshButton);
        Controls.Add(_filterBox);
        Controls.Add(filterLabel);
        Controls.Add(buttonPanel);

        // Dock order matters for TopDown stacking; reverse-add ensures correct visual order.
        Controls.SetChildIndex(filterLabel, Controls.Count - 1);
        Controls.SetChildIndex(_filterBox, Controls.Count - 1);
        Controls.SetChildIndex(refreshButton, Controls.Count - 1);
        Controls.SetChildIndex(title, Controls.Count - 1);
        Controls.SetChildIndex(_monitorCombo, Controls.Count - 1);
        Controls.SetChildIndex(_statusLabel, Controls.Count - 1);
        Controls.SetChildIndex(_autostartCheck, Controls.Count - 1);
        Controls.SetChildIndex(infoLabel, Controls.Count - 1);
    }

    private void LoadMonitors()
    {
        _monitors = _displayService.GetAllMonitors();
        _monitorCombo.Items.Clear();

        foreach (var m in _monitors)
            _monitorCombo.Items.Add(m);

        int selectIndex = -1;
        if (!string.IsNullOrEmpty(_settings.TargetMonitorKey))
            selectIndex = _monitors.FindIndex(m => m.Key == _settings.TargetMonitorKey);

        if (selectIndex < 0 && !string.IsNullOrWhiteSpace(_settings.TargetMonitorNameFilter))
            selectIndex = _monitors.FindIndex(m => m.FriendlyName.Contains(_settings.TargetMonitorNameFilter, StringComparison.OrdinalIgnoreCase));

        if (selectIndex >= 0)
            _monitorCombo.SelectedIndex = selectIndex;
        else if (_monitors.Count > 0)
            _monitorCombo.SelectedIndex = 0;

        _statusLabel.Text = $"共检测到 {_monitors.Count} 台显示器 (含已断开的)。";
    }

    private void SaveAndClose()
    {
        if (_monitorCombo.SelectedItem is MonitorEntry selected)
        {
            _settings.TargetMonitorKey = selected.Key;
            _settings.TargetMonitorNameFilter = string.IsNullOrWhiteSpace(_filterBox.Text)
                ? selected.FriendlyName
                : _filterBox.Text.Trim();
        }
        else
        {
            _settings.TargetMonitorNameFilter = _filterBox.Text.Trim();
        }

        _settings.StartWithWindows = _autostartCheck.Checked;
        _startupService.SetEnabled(_settings.StartWithWindows);

        Close();
    }
}
