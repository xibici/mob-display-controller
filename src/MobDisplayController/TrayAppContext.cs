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
    private readonly PowerButtonService _powerButtonService = new();

    /// <summary>
    /// Handle-less control that exists only to give press notifications - which arrive on the
    /// watcher thread - a way back onto the thread that owns the tray menu.
    /// </summary>
    private readonly Control _uiDispatcher = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private HotkeyWindow? _recoveryHotkey;
    private HotkeyWindow? _layoutHotkey;

    private ToolStripMenuItem _statusItem = new();
    private ToolStripMenuItem _connectToggleItem = new();
    private ToolStripMenuItem _displayModeMenuItem = new();
    private ToolStripMenuItem _resolutionMenuItem = new();
    private ToolStripMenuItem _rotationMenuItem = new();
    private ToolStripMenuItem _brightnessMenuItem = new();
    private ToolStripMenuItem _volumeMenuItem = new();
    private ToolStripMenuItem _startupMenuItem = new();
    private ToolStripMenuItem _powerButtonTakeoverMenuItem = new();

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

        // Force the dispatcher's handle to exist now, while we are still on the thread that owns the
        // menu: presses are reported from the watcher thread and are marshalled back through it.
        _ = _uiDispatcher.Handle;

        _powerButtonService.PowerButtonPressed += OnPowerButtonPressed;

        // Ctrl+Alt+Shift+D forces both screens back on, for when you can't see to fix it.
        _recoveryHotkey = new HotkeyWindow(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT | Hotkeys.MOD_SHIFT, (uint)Keys.D);
        _recoveryHotkey.Pressed += ForceRecoverDisplays;
        if (!_recoveryHotkey.IsRegistered)
            DebugLog.Write("recovery hotkey Ctrl+Alt+Shift+D is already taken - no blind recovery available");

        // Ctrl+Alt+Shift+L switches layouts from the keyboard. This is the route that does not make
        // the console blank or the machine dip into standby, so it also works over a remote session
        // where the power button is out of reach.
        _layoutHotkey = new HotkeyWindow(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT | Hotkeys.MOD_SHIFT, (uint)Keys.L);
        _layoutHotkey.Pressed += OnLayoutHotkey;
        if (!_layoutHotkey.IsRegistered)
            DebugLog.Write("layout hotkey Ctrl+Alt+Shift+L is already taken - no keyboard layout switch");

        // Reconcile the on-disk preference with the actual power scheme setting, the same way
        // autostart is reconciled above - re-applying "do nothing" is idempotent and guards
        // against the setting having drifted back (e.g. a Windows update reset power schemes).
        if (_settings.PowerButtonTakeoverEnabled)
            ApplyPowerButtonTakeover(true);
    }

    /// <summary>
    /// True when the built-in panel is part of the desktop right now. A machine with no built-in
    /// panel at all counts as "active" - there is then nothing to switch off or bring back.
    /// </summary>
    private bool IsBuiltInPanelActive()
    {
        var builtIn = _displayService.GetAllMonitors().Where(m => m.IsInternal).ToList();
        return builtIn.Count == 0 || builtIn.Any(m => m.IsActive);
    }

    /// <summary>
    /// True when an external panel is part of the desktop. Without one the takeover has nothing to
    /// do: "external only" would leave no screen at all, and there'd be nothing to keep lit.
    /// </summary>
    private bool IsExternalPanelActive()
        => _displayService.GetAllMonitors().Any(m => !m.IsInternal && m.IsActive);

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

        _powerButtonTakeoverMenuItem.Text = "接管电源键(按一下在两种显示模式间切换)";
        _powerButtonTakeoverMenuItem.CheckOnClick = true;
        _powerButtonTakeoverMenuItem.Checked = _settings.PowerButtonTakeoverEnabled;
        _powerButtonTakeoverMenuItem.Click += (_, _) => ApplyPowerButtonTakeover(_powerButtonTakeoverMenuItem.Checked);

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
        _menu.Items.Add(_powerButtonTakeoverMenuItem);
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _menu.Opening += (_, _) => RefreshMenu();
    }

    private void RefreshMenuSafe()
    {
        // A press is reported from the watcher thread, so hop back to the menu's thread first -
        // touching the menu items from the wrong thread throws instead of refreshing.
        if (_uiDispatcher.InvokeRequired)
        {
            try { _uiDispatcher.BeginInvoke(new Action(RefreshMenuSafe)); } catch { }
            return;
        }

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

    /// <summary>
    /// The "both panels on" layout: duplicate - the two panels show the same picture, which is what
    /// this mode is for.
    ///
    /// The built-in panel is a portrait panel that Windows presents landscape with a 90 degree target
    /// rotation, and the external one is an ordinary landscape monitor, so a duplicate hands the same
    /// source mode and rotation to both. That inherited rotation is left alone deliberately: it is
    /// what makes the shared portrait picture come out upright on both panels, and changing one
    /// target's rotation while they share a source is what previously threw the external monitor off
    /// its signal. What made the duplicate look unstable before was more likely the mode being
    /// re-applied after every switch (see TrySwitchLayout) - on a shared source that means touching
    /// the timing of both panels at once.
    /// </summary>
    private const DisplayService.TopologyMode BothScreensMode = DisplayService.TopologyMode.Clone;

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

        // Deliberately only these two. Windows offers four layouts (PC screen only / duplicate /
        // extend / second screen only), but this device is only ever used with the built-in panel
        // mirrored to the external one or switched off - keeping the other two around just made the
        // menu ambiguous, and "second screen only" could strand the built-in panel off with no way
        // back from the menu.
        var externalOnlyItem = new ToolStripMenuItem("仅外屏 (关闭内置屏)")
        {
            Checked = currentMode == DisplayService.TopologyMode.ExternalOnly,
        };
        externalOnlyItem.Click += (_, _) => ApplyTopology(DisplayService.TopologyMode.ExternalOnly);

        var bothScreensItem = new ToolStripMenuItem("外屏+内屏 都显示 (同画面)")
        {
            Checked = currentMode == BothScreensMode,
        };
        bothScreensItem.Click += (_, _) => ApplyTopology(BothScreensMode);

        _displayModeMenuItem.DropDownItems.Add(externalOnlyItem);
        _displayModeMenuItem.DropDownItems.Add(bothScreensItem);
    }

    private void ApplyTopology(DisplayService.TopologyMode mode)
    {
        if (!TrySwitchLayout(mode, out var error))
            ShowBalloon("切换显示模式失败", error, ToolTipIcon.Error);

        RefreshMenu();
    }

    /// <summary>
    /// Switches layouts while leaving the desktop at the resolution it already has.
    ///
    /// Asking Windows for a topology by name (SDC_TOPOLOGY_CLONE / _EXTERNAL) also lets it pick the
    /// modes that make that layout valid, which is how mirroring used to drop the desktop to
    /// 1280x800 - a mode both panels happen to share. So the current mode is captured first and put
    /// back afterwards; only the panel coming or going changes.
    /// </summary>
    private bool TrySwitchLayout(DisplayService.TopologyMode mode, out string error)
    {
        var keepMode = GetDesktopMode();

        if (!_displayService.TrySetTopology(mode, out error))
            return false;

        if (mode == DisplayService.TopologyMode.Clone)
        {
            // Building a duplicate copies the built-in panel's 90 degrees onto the external monitor
            // too, which leaves the external picture lying on its side; only the built-in needs that
            // rotation. Done before the mode check below, because this is the step that has to happen
            // on every duplicate switch, not just the ones that also move the mode.
            if (_displayService.TryNormalizeExternalRotation(out var rotationError))
                DebugLog.Write("duplicate: external rotation normalised to 0");
            else
                DebugLog.Write($"duplicate: external rotation left as Windows set it ({rotationError})");
        }

        if (keepMode is not { } wanted)
            return true;

        // Only put the mode back when the switch actually moved it. In a duplicate the two panels
        // share one source mode and Windows normally keeps the mode that was already there, so most
        // switches need nothing at all - and re-applying it anyway meant setting a mode on a source
        // that two panels were sharing, which is what made the external monitor lose its signal and
        // drop off. "If it is already right, don't touch it."
        if (GetDesktopMode() is { } now && now == wanted)
        {
            DebugLog.Write($"mode already kept: {wanted.Width}x{wanted.Height}@{wanted.Hz}Hz");
            return true;
        }

        RestoreDesktopMode(wanted);
        return true;
    }

    /// <summary>
    /// The mode the desktop is at right now. The external panel is the one present in both layouts,
    /// and in a mirror it shares the source mode, so its mode is the desktop's either way.
    /// </summary>
    private (int Width, int Height, int Hz)? GetDesktopMode()
        => FindActiveExternal()?.GdiDeviceName is { } gdi ? _displayService.GetCurrentMode(gdi) : null;

    private MonitorEntry? FindActiveExternal()
        => _displayService.GetAllMonitors().FirstOrDefault(m => !m.IsInternal && m.IsActive && m.GdiDeviceName is not null);

    /// <summary>
    /// Puts the desktop back to the mode it had before the switch. The panels need a moment to settle
    /// into the new layout, so this retries briefly; if it still won't take, Windows' own choice is
    /// left in place rather than the whole switch being undone.
    /// </summary>
    private void RestoreDesktopMode((int Width, int Height, int Hz) mode)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (FindActiveExternal()?.GdiDeviceName is not { } gdi)
            {
                DebugLog.Write($"mode keep: no active external panel to set {mode.Width}x{mode.Height} on");
                return;
            }

            if (_displayService.TrySetResolution(gdi, mode.Width, mode.Height, mode.Hz, out var error))
            {
                DebugLog.Write($"mode kept: {mode.Width}x{mode.Height}@{mode.Hz} on {gdi}");
                return;
            }

            DebugLog.Write($"mode keep attempt {attempt} on {gdi} failed: {error}");
            Thread.Sleep(150);
        }
    }

    private void ApplyPowerButtonTakeover(bool enable)
    {
        if (enable)
        {
            // Only remember the pre-takeover action the first time - once the setting is
            // already ours from a previous session, re-reading it would just save our own
            // "sleep" over the user's real original action.
            if (_settings.SavedPowerButtonActionAc is null || _settings.SavedPowerButtonActionDc is null)
            {
                var current = _powerButtonService.ReadCurrentAction();
                if (current is not null)
                {
                    _settings.SavedPowerButtonActionAc = current.Value.Ac;
                    _settings.SavedPowerButtonActionDc = current.Value.Dc;
                }
            }

            // The sign-in prompt only matters on the fallback path, where a press really does enter
            // standby - that round trip is what used to end on the lock screen. With the filter driver
            // the button is set to do nothing at all, so the setting is left alone; and if an earlier
            // version turned it off, this puts the machine back to how it was.
            if (_powerButtonService.IsDriverBacked)
            {
                RestoreConsoleLock();
            }
            else
            {
                if (_settings.SavedConsoleLockAc is null || _settings.SavedConsoleLockDc is null)
                {
                    var currentLock = _powerButtonService.ReadConsoleLock();
                    if (currentLock is not null)
                    {
                        _settings.SavedConsoleLockAc = currentLock.Value.Ac;
                        _settings.SavedConsoleLockDc = currentLock.Value.Dc;
                    }
                }

                if (!_powerButtonService.WriteConsoleLock(0, 0, out var lockError))
                    DebugLog.Write($"could not turn off \"require a password on wakeup\": {lockError}");
            }

            if (!_powerButtonService.TryEnable(_settings.SavedPowerButtonActionAc, _settings.SavedPowerButtonActionDc, out var error))
            {
                // The button is untouched, so stop claiming the takeover is on: leave the checkbox
                // and the saved preference agreeing with what's actually in the power scheme,
                // instead of retrying (and failing) silently on every launch.
                _powerButtonTakeoverMenuItem.Checked = false;
                _settings.PowerButtonTakeoverEnabled = false;
                RestoreConsoleLock();
                _settings.Save();
                ShowBalloon("接管电源键失败", error, ToolTipIcon.Error);
                return;
            }
        }
        else
        {
            // Turning the takeover off while the built-in panel is down would leave it down with
            // no way to bring it back by button, so put both panels back before letting go.
            if (!IsBuiltInPanelActive())
                ForceRecoverDisplays();

            _powerButtonService.Disable(_settings.SavedPowerButtonActionAc, _settings.SavedPowerButtonActionDc);
            _settings.SavedPowerButtonActionAc = null;
            _settings.SavedPowerButtonActionDc = null;

            RestoreConsoleLock();
        }

        _settings.PowerButtonTakeoverEnabled = enable;
        _settings.Save();
    }

    /// <summary>
    /// Puts "require a password on wakeup" back to the value saved before the takeover turned it off.
    /// </summary>
    private void RestoreConsoleLock()
    {
        if (_settings.SavedConsoleLockAc is null || _settings.SavedConsoleLockDc is null)
            return;

        if (!_powerButtonService.WriteConsoleLock(_settings.SavedConsoleLockAc.Value, _settings.SavedConsoleLockDc.Value, out var error))
            DebugLog.Write($"could not restore \"require a password on wakeup\": {error}");

        _settings.SavedConsoleLockAc = null;
        _settings.SavedConsoleLockDc = null;
    }

    /// <summary>
    /// Runs when Windows reports that it entered standby because the power button was pressed, and
    /// flips between the two layouts the menu offers: "external only" and "both screens" (mirrored).
    ///
    /// The signal is the reason code on the standby entry (Kernel-Power 506, Reason = power button),
    /// not the console going dark: an idle blank, a driver reset or our own display switch all look
    /// like "displays off", and reading those as presses is what used to flip the screens on their own.
    ///
    /// Which way to flip is read from what the panels are actually doing rather than from a flag
    /// remembered between presses. That's deliberate: a remembered flag has to survive a reboot to
    /// keep working, and the reboot an OS update performs restored the saved topology (built-in
    /// panel still off) while wiping the flag - so the next press pushed the wrong way, turning the
    /// built-in panel off again instead of bringing it back. Reading the panels removes that state.
    /// </summary>
    private void OnPowerButtonPressed()
    {
        // Wake first: the standby entry the press was reported through would otherwise keep the
        // panels dark for the whole round trip. A monitor we deactivate at the CCD level in the
        // switch below stays dark regardless.
        _powerButtonService.ForceDisplaysOn();

        ToggleLayout("press");
        RefreshMenuSafe();
    }

    /// <summary>
    /// Flips between the two layouts the menu offers: "external only" and both panels showing.
    ///
    /// Deliberately shared with the layout hotkey: the hotkey exists because the button route cannot
    /// avoid the console blanking and the machine dipping into standby - which on this hardware
    /// means the lock screen on the way back - so there has to be a way to switch without it.
    /// </summary>
    private bool ToggleLayout(string trigger)
    {
        // Nothing to switch with no external panel on the desktop: "external only" on its own would
        // leave no screen at all.
        if (!IsExternalPanelActive())
        {
            DebugLog.Write($"{trigger}: ignored, no active external panel to switch to");
            return false;
        }

        bool builtInWasOn = IsBuiltInPanelActive();
        bool ok;
        string error;

        if (builtInWasOn)
            ok = TrySwitchLayout(DisplayService.TopologyMode.ExternalOnly, out error);
        else
            ok = TryShowBothScreens(out error);

        DebugLog.Write($"{trigger}: built-in was {(builtInWasOn ? "on" : "off")} -> {(builtInWasOn ? "external only" : "both screens")}: ok={ok} {error}");
        return ok;
    }

    /// <summary>
    /// Brings both panels back on, in the layout the menu offers. Kept as a named step so the
    /// recovery path and the button toggle both read the same way.
    /// </summary>
    private bool TryShowBothScreens(out string error)
        => TrySwitchLayout(BothScreensMode, out error);

    /// <summary>
    /// Escape hatch for when the screens are in a state you can't see to fix: brings both panels
    /// back on and powers them up. Usable blind, by feel.
    /// </summary>
    private void ForceRecoverDisplays()
    {
        bool ok = TryShowBothScreens(out var error);
        DebugLog.Write($"recovery: both screens on: ok={ok} {error}");

        _powerButtonService.ForceDisplaysOn();
        RefreshMenuSafe();
    }

    /// <summary>Ctrl+Alt+Shift+L: the same toggle the power button does, minus the blank and standby.</summary>
    private void OnLayoutHotkey()
    {
        ToggleLayout("hotkey");
        RefreshMenuSafe();
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

        if (!target.IsActive)
        {
            _rotationMenuItem.Enabled = false;
            return;
        }

        // In Duplicate/clone mode this monitor shares its source mode with another display.
        // Windows can't give two cloned targets different rotations, and asking it to try
        // doesn't fail cleanly - it can silently drop this target's path entirely instead.
        if (_displayService.GetTopologyMode() == DisplayService.TopologyMode.Clone)
        {
            _rotationMenuItem.Enabled = false;
            _rotationMenuItem.DropDownItems.Add(new ToolStripMenuItem("复制模式下两块屏同画面,无法单独旋转;请先切到「仅外屏」") { Enabled = false });
            return;
        }

        var current = _displayService.GetCurrentOrientation(target);
        if (current is null)
        {
            _rotationMenuItem.Enabled = false;
            return;
        }

        _rotationMenuItem.Enabled = true;

        foreach (var (label, rotation) in RotationOptions)
        {
            var item = new ToolStripMenuItem(label) { Checked = current == rotation };
            item.Click += (_, _) =>
            {
                if (!_displayService.TrySetOrientation(target, rotation, out var err) && err.Length > 0)
                    ShowBalloon("设置旋转失败", err, ToolTipIcon.Error);
                RefreshMenu();
            };
            _rotationMenuItem.DropDownItems.Add(item);
        }
    }

    private static readonly (string Label, DISPLAYCONFIG_ROTATION Rotation)[] RotationOptions =
    {
        ("0°(默认方向)", DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_IDENTITY),
        ("90°", DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE90),
        ("180°(翻转)", DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE180),
        ("270°", DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE270),
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
        // Put back what the takeover changed before going away.
        //
        // This used to just stop listening and leave the action alone, on the grounds that the action
        // is a perfectly usable button behaviour by itself - true while it was "sleep". With the
        // filter driver the button is set to "do nothing", so leaving it would make the button dead
        // until the app is started again (no sleep, no anything).
        //
        // The *preference* is deliberately left on, so the next launch re-arms it; only the machine's
        // state is restored.
        _powerButtonService.Disable(_settings.SavedPowerButtonActionAc, _settings.SavedPowerButtonActionDc);
        RestoreConsoleLock();
        _settings.Save();
        DebugLog.Write("exiting: power button action and wake-password setting restored");

        _recoveryHotkey?.Dispose();
        _layoutHotkey?.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
