using Microsoft.Win32;
using MobDisplayController.Models;
using MobDisplayController.Native;
using MobDisplayController.Services;
using MobDisplayController.UI;

namespace MobDisplayController;

/// <summary>
/// Owns the tray icon and its context menu. This is the application's main
/// "window" - there is no visible main form, everything happens from the tray.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly DisplayService _displayService = new();
    private readonly MonitorControlService _monitorControlService = new();
    private readonly StartupService _startupService = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private ToolStripMenuItem _statusItem = new();
    private ToolStripMenuItem _connectToggleItem = new();
    private ToolStripMenuItem _displayModeMenuItem = new();
    private ToolStripMenuItem _resolutionMenuItem = new();
    private ToolStripMenuItem _rotationMenuItem = new();
    private ToolStripMenuItem _brightnessMenuItem = new();
    private ToolStripMenuItem _volumeMenuItem = new();
    private ToolStripMenuItem _startupMenuItem = new();

    private SettingsForm? _settingsForm;

    public TrayAppContext()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = TrayIcons.Default,
            Visible = true,
            Text = "移动显示器控制器",
            ContextMenuStrip = _menu,
        };

        _trayIcon.DoubleClick += (_, _) => OpenSettings();

        BuildStaticMenuItems();
        RefreshMenu();

        SystemEvents.DisplaySettingsChanged += (_, _) => RefreshMenuSafe();

        // Reconcile the on-disk autostart preference with what's actually in the registry.
        _startupService.SetEnabled(_settings.StartWithWindows);
    }

    private void BuildStaticMenuItems()
    {
        _statusItem.Enabled = false;

        _connectToggleItem.Click += (_, _) => ToggleTargetMonitor();

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => OpenSettings();

        _startupMenuItem.Text = "开机自动启动";
        _startupMenuItem.CheckOnClick = true;
        _startupMenuItem.Checked = _settings.StartWithWindows;
        _startupMenuItem.Click += (_, _) =>
        {
            _settings.StartWithWindows = _startupMenuItem.Checked;
            _startupService.SetEnabled(_settings.StartWithWindows);
            _settings.Save();
        };

        var refreshItem = new ToolStripMenuItem("刷新");
        refreshItem.Click += (_, _) => RefreshMenu();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        _displayModeMenuItem.Text = "显示模式";

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_connectToggleItem);
        _menu.Items.Add(_displayModeMenuItem);
        _menu.Items.Add(_resolutionMenuItem);
        _menu.Items.Add(_rotationMenuItem);
        _menu.Items.Add(_brightnessMenuItem);
        _menu.Items.Add(_volumeMenuItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_startupMenuItem);
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _menu.Opening += (_, _) => RefreshMenu();
    }

    private void RefreshMenuSafe()
    {
        try { RefreshMenu(); } catch { /* best effort */ }
    }

    private void RefreshMenu()
    {
        var monitors = _displayService.GetAllMonitors();
        var target = FindTargetMonitor(monitors);

        if (target is null)
        {
            _statusItem.Text = $"未找到匹配 “{_settings.TargetMonitorNameFilter}” 的显示器";
            _connectToggleItem.Text = "断开 / 连接 (未找到)";
            _connectToggleItem.Enabled = false;
            _displayModeMenuItem.Enabled = false;
            _displayModeMenuItem.DropDownItems.Clear();
            _resolutionMenuItem.Enabled = false;
            _resolutionMenuItem.DropDownItems.Clear();
            _rotationMenuItem.Enabled = false;
            _rotationMenuItem.DropDownItems.Clear();
            _brightnessMenuItem.Enabled = false;
            _brightnessMenuItem.DropDownItems.Clear();
            _volumeMenuItem.Enabled = false;
            _volumeMenuItem.DropDownItems.Clear();
            _trayIcon.Text = "移动显示器控制器 - 未找到 DP 显示器";
            return;
        }

        _statusItem.Text = target.IsActive ? $"{target.FriendlyName}: 已连接" : $"{target.FriendlyName}: 已断开";
        _trayIcon.Text = $"移动显示器控制器 - {(target.IsActive ? "已连接" : "已断开")}";

        _connectToggleItem.Enabled = true;
        _connectToggleItem.Text = target.IsActive ? "断开连接" : "重新连接";

        BuildDisplayModeSubmenu(target);
        BuildResolutionSubmenu(target);
        BuildRotationSubmenu(target);
        BuildBrightnessSubmenu(target);
        BuildVolumeSubmenu(target);
    }

    private void BuildDisplayModeSubmenu(MonitorEntry target)
    {
        _displayModeMenuItem.DropDownItems.Clear();

        if (!target.IsActive)
        {
            _displayModeMenuItem.Enabled = false;
            return;
        }

        _displayModeMenuItem.Enabled = true;
        var currentMode = _displayService.GetTopologyMode();

        var cloneItem = new ToolStripMenuItem("复制屏幕 (与主屏同画面)")
        {
            Checked = currentMode == DisplayService.TopologyMode.Clone,
        };
        cloneItem.Click += (_, _) => ApplyTopology(DisplayService.TopologyMode.Clone);

        var extendItem = new ToolStripMenuItem("扩展屏幕 (作为独立桌面)")
        {
            Checked = currentMode == DisplayService.TopologyMode.Extend,
        };
        extendItem.Click += (_, _) => ApplyTopology(DisplayService.TopologyMode.Extend);

        _displayModeMenuItem.DropDownItems.Add(cloneItem);
        _displayModeMenuItem.DropDownItems.Add(extendItem);
    }

    private void ApplyTopology(DisplayService.TopologyMode mode)
    {
        if (!_displayService.TrySetTopology(mode, out var error))
            ShowBalloon("切换显示模式失败", error, ToolTipIcon.Error);

        RefreshMenu();
    }

    private void BuildResolutionSubmenu(MonitorEntry target)
    {
        _resolutionMenuItem.DropDownItems.Clear();
        _resolutionMenuItem.Text = "分辨率";

        if (!target.IsActive || target.GdiDeviceName is null)
        {
            _resolutionMenuItem.Enabled = false;
            return;
        }

        var modes = _displayService.GetSupportedModes(target.GdiDeviceName);
        var current = _displayService.GetCurrentMode(target.GdiDeviceName);

        if (modes.Count == 0)
        {
            _resolutionMenuItem.Enabled = false;
            return;
        }

        _resolutionMenuItem.Enabled = true;

        // Only offer the two resolutions this monitor is actually used at; for each,
        // pick the highest refresh rate it supports (e.g. 144Hz) rather than listing
        // every width/height/Hz combination the driver reports.
        foreach (var (width, height) in PreferredResolutions)
        {
            var candidates = modes
                .Where(m => m.Width == width && m.Height == height)
                .OrderByDescending(m => m.Hz)
                .ToList();

            (int Width, int Height, int Hz)? best = candidates.Count > 0 ? candidates[0] : null;

            if (best is null)
            {
                _resolutionMenuItem.DropDownItems.Add(new ToolStripMenuItem($"{width} x {height} (不支持)") { Enabled = false });
                continue;
            }

            var mode = best.Value;
            bool isCurrent = current is not null && current.Value.Width == mode.Width && current.Value.Height == mode.Height;

            var item = new ToolStripMenuItem($"{mode.Width} x {mode.Height} @ {mode.Hz}Hz")
            {
                Checked = isCurrent,
            };
            item.Click += (_, _) =>
            {
                if (!_displayService.TrySetResolution(target.GdiDeviceName!, mode.Width, mode.Height, mode.Hz, out var err) && err.Length > 0)
                    ShowBalloon("设置分辨率失败", err, ToolTipIcon.Error);
            };
            _resolutionMenuItem.DropDownItems.Add(item);
        }
    }

    private static readonly (int Width, int Height)[] PreferredResolutions =
    {
        (1920, 1080),
        (1920, 1200),
    };

    private void BuildRotationSubmenu(MonitorEntry target)
    {
        _rotationMenuItem.DropDownItems.Clear();
        _rotationMenuItem.Text = "旋转";

        if (!target.IsActive || target.GdiDeviceName is null)
        {
            _rotationMenuItem.Enabled = false;
            return;
        }

        var current = _displayService.GetCurrentOrientation(target.GdiDeviceName);
        if (current is null)
        {
            _rotationMenuItem.Enabled = false;
            return;
        }

        _rotationMenuItem.Enabled = true;

        foreach (var (label, orientation) in RotationOptions)
        {
            var item = new ToolStripMenuItem(label) { Checked = current == orientation };
            item.Click += (_, _) =>
            {
                if (!_displayService.TrySetOrientation(target.GdiDeviceName!, orientation, out var err) && err.Length > 0)
                    ShowBalloon("设置旋转失败", err, ToolTipIcon.Error);
                RefreshMenu();
            };
            _rotationMenuItem.DropDownItems.Add(item);
        }
    }

    private static readonly (string Label, int Orientation)[] RotationOptions =
    {
        ("0°(横向)", Gdi.DMDO_DEFAULT),
        ("90°", Gdi.DMDO_90),
        ("180°(横向翻转)", Gdi.DMDO_180),
        ("270°", Gdi.DMDO_270),
    };

    private void BuildBrightnessSubmenu(MonitorEntry target)
    {
        _brightnessMenuItem.DropDownItems.Clear();
        _brightnessMenuItem.Text = "亮度";

        if (!target.IsActive || target.GdiDeviceName is null)
        {
            _brightnessMenuItem.Enabled = false;
            return;
        }

        var handle = _monitorControlService.GetPhysicalMonitorHandle(target.GdiDeviceName);
        if (handle is null || !_monitorControlService.TryGetBrightness(handle.Value, out uint current, out uint max))
        {
            _brightnessMenuItem.Enabled = false;
            if (handle is not null) _monitorControlService.ReleaseHandle(handle.Value);
            return;
        }

        _brightnessMenuItem.Enabled = true;

        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            uint value = (uint)Math.Round(max * (pct / 100.0));
            bool isCurrent = Math.Abs((int)current - (int)value) <= Math.Max(1, (int)max / 20);
            var item = new ToolStripMenuItem($"{pct}%") { Checked = isCurrent };
            item.Click += (_, _) =>
            {
                var h = _monitorControlService.GetPhysicalMonitorHandle(target.GdiDeviceName!);
                if (h is not null)
                {
                    if (!_monitorControlService.TrySetBrightness(h.Value, value))
                        ShowBalloon("设置亮度失败", "该显示器可能不支持 DDC/CI 亮度调节。", ToolTipIcon.Error);
                    _monitorControlService.ReleaseHandle(h.Value);
                }
            };
            _brightnessMenuItem.DropDownItems.Add(item);
        }

        var customItem = new ToolStripMenuItem("自定义…");
        customItem.Click += (_, _) => ShowVcpSliderDialog("亮度", target.GdiDeviceName!, isVolume: false);
        _brightnessMenuItem.DropDownItems.Add(new ToolStripSeparator());
        _brightnessMenuItem.DropDownItems.Add(customItem);

        _monitorControlService.ReleaseHandle(handle.Value);
    }

    private void BuildVolumeSubmenu(MonitorEntry target)
    {
        _volumeMenuItem.DropDownItems.Clear();
        _volumeMenuItem.Text = "音量";

        if (!target.IsActive || target.GdiDeviceName is null)
        {
            _volumeMenuItem.Enabled = false;
            return;
        }

        var handle = _monitorControlService.GetPhysicalMonitorHandle(target.GdiDeviceName);
        if (handle is null || !_monitorControlService.TryGetVolume(handle.Value, out uint current, out uint max))
        {
            _volumeMenuItem.Enabled = false;
            if (handle is not null) _monitorControlService.ReleaseHandle(handle.Value);
            return;
        }

        _volumeMenuItem.Enabled = true;

        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            uint value = (uint)Math.Round(max * (pct / 100.0));
            bool isCurrent = Math.Abs((int)current - (int)value) <= Math.Max(1, (int)max / 20);
            var item = new ToolStripMenuItem($"{pct}%") { Checked = isCurrent };
            item.Click += (_, _) =>
            {
                var h = _monitorControlService.GetPhysicalMonitorHandle(target.GdiDeviceName!);
                if (h is not null)
                {
                    if (!_monitorControlService.TrySetVolume(h.Value, value))
                        ShowBalloon("设置音量失败", "该显示器可能不支持 DDC/CI 音量调节(无内置扬声器或线材不支持)。", ToolTipIcon.Error);
                    _monitorControlService.ReleaseHandle(h.Value);
                }
            };
            _volumeMenuItem.DropDownItems.Add(item);
        }

        var customItem = new ToolStripMenuItem("自定义…");
        customItem.Click += (_, _) => ShowVcpSliderDialog("音量", target.GdiDeviceName!, isVolume: true);
        _volumeMenuItem.DropDownItems.Add(new ToolStripSeparator());
        _volumeMenuItem.DropDownItems.Add(customItem);

        _monitorControlService.ReleaseHandle(handle.Value);
    }

    private void ShowVcpSliderDialog(string title, string gdiDeviceName, bool isVolume)
    {
        var handle = _monitorControlService.GetPhysicalMonitorHandle(gdiDeviceName);
        if (handle is null)
        {
            ShowBalloon(title, "无法访问该显示器的 DDC/CI 接口。", ToolTipIcon.Error);
            return;
        }

        bool ok = isVolume
            ? _monitorControlService.TryGetVolume(handle.Value, out uint current, out uint max)
            : _monitorControlService.TryGetBrightness(handle.Value, out current, out max);

        if (!ok)
        {
            _monitorControlService.ReleaseHandle(handle.Value);
            ShowBalloon(title, "该显示器不支持此项 DDC/CI 调节。", ToolTipIcon.Error);
            return;
        }

        using var dlg = new VcpSliderForm(title, current, max, value =>
        {
            if (isVolume) _monitorControlService.TrySetVolume(handle.Value, value);
            else _monitorControlService.TrySetBrightness(handle.Value, value);
        });

        dlg.ShowDialog();
        _monitorControlService.ReleaseHandle(handle.Value);
    }

    private void ToggleTargetMonitor()
    {
        var monitors = _displayService.GetAllMonitors();
        var target = FindTargetMonitor(monitors);
        if (target is null)
            return;

        bool wantActive = !target.IsActive;
        if (!_displayService.TrySetActive(target, wantActive, out var error))
        {
            ShowBalloon("操作失败", error, ToolTipIcon.Error);
        }
        else
        {
            ShowBalloon("MobDisplayController", wantActive ? $"{target.FriendlyName} 已重新连接" : $"{target.FriendlyName} 已断开连接", ToolTipIcon.Info);
        }

        RefreshMenu();
    }

    private MonitorEntry? FindTargetMonitor(List<MonitorEntry> monitors)
    {
        if (monitors.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(_settings.TargetMonitorKey))
        {
            var byKey = monitors.FirstOrDefault(m => m.Key == _settings.TargetMonitorKey);
            if (byKey is not null)
                return byKey;
        }

        var filter = _settings.TargetMonitorNameFilter;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var byName = monitors.FirstOrDefault(m => m.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        // Name-based matching fails for monitors whose EDID friendly name doesn't
        // actually contain "DP" (e.g. some DP-connected panels report themselves as
        // "HDMI"). If there's exactly one non-built-in display, it's unambiguous -
        // use it and remember its exact ID so future lookups no longer depend on the name.
        var externalCandidates = monitors.Where(m => !m.IsInternal).ToList();
        if (externalCandidates.Count == 1)
        {
            var external = externalCandidates[0];
            _settings.TargetMonitorKey = external.Key;
            _settings.Save();
            return external;
        }

        return null;
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_displayService, _startupService, _settings);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settings.Save();
            RefreshMenu();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = text;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(4000);
    }

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
