using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly TopMostMenu _menu = new();
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
    private ToolStripMenuItem _rotationMenuItem = new();
    private ToolStripMenuItem _brightnessMenuItem = new();
    private ToolStripMenuItem _volumeMenuItem = new();
    private ToolStripMenuItem _startupMenuItem = new();
    private ToolStripMenuItem _powerButtonTakeoverMenuItem = new();

    private ToolStripMenuItem _startMenuSizeMenuItem = new();
    private ToolStripMenuItem _trayMenuMenuItem = new();
    private ToolStripMenuItem _advancedMenuItem = new();

    private SettingsForm? _settingsForm;

    /// <summary>
    /// Re-raises the menu while it is open - see WindowZOrder - because the taskbar raises itself
    /// again after the menu has been shown.
    /// </summary>
    private readonly System.Windows.Forms.Timer _menuOnTopTimer = new() { Interval = 200 };

    /// <summary>Comes back for the menu once the shell's panel is gone (see OnMenuOpening).</summary>
    private readonly System.Windows.Forms.Timer _menuRetryTimer = new() { Interval = 60 };

    /// <summary>When the current request for the menu stops waiting for the shell's panel.</summary>
    private long _panelWaitDeadline;

    /// <summary>Where the pointer was when the menu was asked for, so it can be opened there afterwards.</summary>
    private Point _menuAnchor;

    /// <summary>Set while this app has dismissed the shell's panel for the request that is in flight.</summary>
    private bool _panelDismissed;

    /// <summary>
    /// How many times the dismissal is retried for one request. One Escape can be sent while the panel is still
    /// animating in, in which case it lands on nothing.
    /// </summary>
    private int _panelDismissTries;

    /// <summary>
    /// How many times the panel is asked to go away while the menu waits for it.
    ///
    /// Two, not six: the first attempt is the one that dismisses it, and each later attempt hands the panel the
    /// foreground again through TakeForeground - which re-activates the very window the wait is trying to get rid
    /// of. Measured 2026-09-26 09:42-09:43: requests that found a panel took 1.0-1.4 s, seven of them spent in
    /// "focus and Escape sent to StartMenuExperienceHost". After the first Escape the useful thing is to watch, not
    /// to keep prodding.
    /// </summary>
    private const int MaxPanelDismissTries = 2;

    /// <summary>
    /// The menu is re-shown when the shell closed it again almost immediately.
    ///
    /// That is what the sticky state looks like: the shell's panel (or a shell window that outlives it) keeps the
    /// foreground, and a WinForms drop-down loses the activation and closes itself - measured 8 ms after it had
    /// been shown, which the user sees as "the menu cannot be summoned at all". The state usually clears within a
    /// moment, so a retry or two turns that into a menu that opens.
    /// </summary>
    private int _instantCloseRetries;

    /// <summary>
    /// How many times a menu the shell dropped may be shown again, and how long to wait before each attempt.
    ///
    /// Eight at 250 ms rather than three at 450 ms, because how often the shell wins varies from attempt to
    /// attempt: measured 2026-09-26 09:27:55, the menu was closed 16 ms after it opened, and then closed again
    /// 593 ms after the retry - which with a 500 ms cut-off was the last attempt, so the user saw nothing. That is
    /// their "sometimes it works, sometimes it does not".
    /// </summary>
    private const int MaxInstantCloseRetries = 8;

    private const int RetryDelayMs = 250;

    /// <summary>When the menu was last shown, to tell "closed at once" from a normal close.</summary>
    private long _menuOpenedTick;

    private readonly System.Windows.Forms.Timer _reopenTimer = new() { Interval = 450 };

    /// <summary>Why the pending re-show is happening - carried into the log when it fires.</summary>
    private string _pendingShowReason = "after the shell dropped it";

    /// <summary>
    /// Set while this app shows the menu itself (ShowMenuByHand), so the Opening handler does not cancel its own
    /// show. That is not a detail: Show() raises Opening again, so without this the two would argue - Opening
    /// cancelled the show, the retry called Show() again, and a Show() that was cancelled part-way leaves the
    /// drop-down in a state WinForms still counts as open. From then on every click on the tray icon did nothing
    /// at all (no Opening, no menu, no log), which is what the log showed on 2026-09-26 at 08:45:45.
    /// </summary>
    private bool _showingByHand;

    /// <summary>
    /// Watches the foreground, so that a panel which was up when the tray icon was clicked is still known
    /// a moment later, when the panel itself is no longer the foreground window.
    /// </summary>
    private readonly System.Windows.Forms.Timer _shellWatchTimer = new() { Interval = 250 };

    /// <summary>
    /// Whether a shell panel window was over the screen the last time the watch looked, so only the changes are
    /// logged. Cloaked windows are counted here (see ShellPanels.TryFindAnyShellPanel), which is the point: it says
    /// whether what is stacked over the screen is being drawn or is a leftover from earlier.
    /// </summary>
    private bool _shellPanelWasThere;

    private long _lastShellPanelTick;

    /// <summary>
    /// Checks, every ten seconds and off the UI thread, that the shell still knows the tray icon.
    ///
    /// The icon can be drawn while the notification area has stopped routing clicks to it - after a fullscreen
    /// application released the screen, on this machine on 2026-09-26 - and that looks exactly like "clicking
    /// the icon does nothing". Asking the shell where the icon is (Shell_NotifyIconGetRect) is a direct answer,
    /// and re-adding the icon is the fix. The check runs on a background thread because it is a round trip to
    /// the shell: a hung shell must not be able to block the thread that owns the menu.
    /// </summary>
    private readonly System.Threading.Timer _iconHealthTimer = new(OnIconHealthCheckStatic, null, 10000, 10000);

    private static TrayAppContext? s_current;

    private bool _iconWasRegistered = true;

    private int _iconLossesInARow;

    private long _lastIconReregisterTick;

    private HotkeyWindow? _menuHotkey;

    public TrayAppContext()
    {
        s_current = this;

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIcons.Default,
            Visible = true,
            Text = "移动显示器控制器",
            ContextMenuStrip = _menu,
        };

        _trayIcon.DoubleClick += (_, _) => OpenSettings();

        // These are logged, all of them: "I right-clicked the icon and nothing happened" cannot be told apart
        // from "the shell never delivered the click" without it, and that was the shape of the report on
        // 2026-09-26 (after running a game and coming back to the desktop). With the log, the next occurrence
        // says which half of the path is broken.
        _trayIcon.MouseDown += (_, e) => DebugLog.Write($"tray icon: mouse down ({e.Button} at {e.X},{e.Y})");
        _trayIcon.MouseUp += (_, e) => DebugLog.Write($"tray icon: mouse up ({e.Button} at {e.X},{e.Y})");

        BuildStaticMenuItems();
        RefreshMenu();

        // Everything about this menu is shaped by one fact: the pointer is on the taskbar when it is
        // opened. So (a) it must not open while the shell has a panel of its own up, because those are
        // drawn above a foreign topmost window (OnMenuOpening); (b) WinForms deliberately places it
        // over the taskbar, so it is moved clear of it (KeepMenuOffTaskbar); and (c) the taskbar and the
        // icon flyout take their topmost place back after the fact, so it is raised again and again
        // (TopMostMenu and _menuOnTopTimer).
        _menu.Opening += OnMenuOpening;
        _menu.Opened += (_, _) => OnMenuOpened();
        _menu.Closed += (_, e) =>
        {
            var openFor = Environment.TickCount64 - _menuOpenedTick;
            DebugLog.Write($"tray menu: closed ({e.CloseReason}) after {openFor} ms");
            _menuOnTopTimer.Stop();
            MouseWatcher.Stop();

            // Retry when the shell is the one closing it. "A shell window holds the foreground" alone is not enough:
            // taking the foreground is exactly what ShowMenuByHand does, so at close time the foreground is usually
            // this app instead (measured 09:32:04: closed after 0 ms, and no retry at all because of that test). A
            // close within a moment of opening is the shell either way - a person needs longer than that to click
            // anything, so a real dismissal (the user picking another app) cannot be mistaken for this.
            if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange
                && (openFor < 1500 || ShellPanels.IsForeground())
                && _instantCloseRetries < MaxInstantCloseRetries)
            {
                _instantCloseRetries++;
                DebugLog.Write($"tray menu: the shell dropped it again after {openFor} ms - showing it again ({_instantCloseRetries}/{MaxInstantCloseRetries})");
                ShowMenuByHandSoon(RetryDelayMs, "after the shell dropped it");
                return;
            }

            _instantCloseRetries = 0;
        };
        _menuOnTopTimer.Tick += (_, _) => OnMenuTick();
        _menuRetryTimer.Tick += (_, _) => OnMenuRetry();
        MouseWatcher.ButtonDown += OnAnyButtonDown;

        _reopenTimer.Tick += (_, _) =>
        {
            _reopenTimer.Stop();
            ShowMenuByHand(Cursor.Position, _pendingShowReason);
        };

        _shellWatchTimer.Tick += (_, _) => OnShellWatch();
        _shellWatchTimer.Start();

        SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            RefreshMenuSafe();

            // A display mode switch is what a game does on the way in and out, and it is the moment the
            // notification area was seen to drop its idea of where our icon is. Re-adding the icon here costs
            // a flicker and removes the wait for OnIconHealthCheck.
            DebugLog.Write("display settings changed - re-adding the tray icon");
            ReRegisterTrayIcon();

            // Windows can land back in Clone on its own - a wake, a reconnect, a driver reset - without
            // ever going through TrySwitchLayout. The menu already refuses to let a user leave the
            // external panel rotated while cloned (see BuildRotationSubmenu), so that's the invariant to
            // restore here too, not just the state the app itself last set.
            if (_displayService.GetTopologyMode() == DisplayService.TopologyMode.Clone)
            {
                if (_displayService.TryNormalizeExternalRotation(out var rotationError))
                    DebugLog.Write("display settings changed: external rotation re-normalised to 0 (clone)");
                else
                    DebugLog.Write($"display settings changed: external rotation left as Windows set it ({rotationError})");
            }

            // Opportunistically refreshes the DPI baseline TrySwitchLayout restores from. The power
            // button's own press is only reported after Windows has already carried out the real
            // suspend/resume round trip (the filter driver's event fires on completion, and the
            // Kernel-Power 506 log entry is written after the fact too) - by the time that signal
            // reaches OnPowerButtonPressed, a capture taken right there is already reading whatever
            // DPI the resume itself landed on, not the pre-sleep value. Catching it here instead, on
            // every mode change during the day (cloned or not), means a real, fresh baseline is
            // usually already on hand well before any sleep happens.
            var monitorsNow = _displayService.GetAllMonitors();
            _displayService.CaptureModeBaseline(monitorsNow);
            _displayService.CaptureDpiScaleBaseline(monitorsNow);
        };

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

        // Ctrl+Alt+Shift+M opens the same menu from the keyboard. It exists for the one thing the tray icon
        // cannot cover: an icon the notification area has stopped answering for. It also tells the two apart -
        // if this works while the icon does not, the app is fine and the shell is not.
        _menuHotkey = new HotkeyWindow(Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT | Hotkeys.MOD_SHIFT, (uint)Keys.M);
        _menuHotkey.Pressed += OnMenuHotkey;
        if (!_menuHotkey.IsRegistered)
            DebugLog.Write("menu hotkey Ctrl+Alt+Shift+M is already taken - no keyboard way to the menu");

        // Reconcile the on-disk preference with the actual power scheme setting, the same way
        // autostart is reconciled above - re-applying "do nothing" is idempotent and guards
        // against the setting having drifted back (e.g. a Windows update reset power schemes).
        if (_settings.PowerButtonTakeoverEnabled)
            ApplyPowerButtonTakeover(true);

        // Same reconciliation, for rotation: if Windows already has the screens cloned by the time
        // this process starts (a boot, a wake, a reconnect that happened before we were running to see
        // DisplaySettingsChanged for it), the external panel can already be carrying the built-in's
        // 90 degrees with nothing left to fire the fix. Checking once here closes that gap.
        if (_displayService.GetTopologyMode() == DisplayService.TopologyMode.Clone)
        {
            if (_displayService.TryNormalizeExternalRotation(out var startupRotationError))
                DebugLog.Write("startup: already cloned - external rotation normalised to 0");
            else
                DebugLog.Write($"startup: already cloned - external rotation left as Windows set it ({startupRotationError})");
        }

        // Whatever is active right now is as good a baseline as any to start the DPI cache with -
        // without it, the very first switch of the session has nothing to restore from.
        var monitorsAtStart = _displayService.GetAllMonitors();
        _displayService.CaptureModeBaseline(monitorsAtStart);
        _displayService.CaptureDpiScaleBaseline(monitorsAtStart);
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

        _powerButtonTakeoverMenuItem.Text = "接管电源键";
        _powerButtonTakeoverMenuItem.CheckOnClick = true;
        _powerButtonTakeoverMenuItem.Checked = _settings.PowerButtonTakeoverEnabled;
        _powerButtonTakeoverMenuItem.Click += (_, _) => ApplyPowerButtonTakeover(_powerButtonTakeoverMenuItem.Checked);

        var refreshItem = new ToolStripMenuItem("刷新");
        refreshItem.Click += (_, _) => RefreshMenu();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();

        _displayModeMenuItem.Text = "显示模式";

        // Named here rather than only inside their builders: those bail out early when no matching monitor
        // is connected, and the three rows then came up blank.
        _rotationMenuItem.Text = "旋转";
        _brightnessMenuItem.Text = "亮度";
        _volumeMenuItem.Text = "音量";

        _startMenuSizeMenuItem.Text = "开始菜单尺寸";
        _trayMenuMenuItem.Text = "托盘菜单";
        _advancedMenuItem.Text = "高级";

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_connectToggleItem);
        _menu.Items.Add(_displayModeMenuItem);
        _menu.Items.Add(_rotationMenuItem);
        _menu.Items.Add(_brightnessMenuItem);
        _menu.Items.Add(_volumeMenuItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startMenuSizeMenuItem);
        _menu.Items.Add(_trayMenuMenuItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_startupMenuItem);
        _menu.Items.Add(_powerButtonTakeoverMenuItem);
        _menu.Items.Add(_advancedMenuItem);
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
    }

    /// <summary>
    /// Holds the menu back while the shell has one of its own panels up, and puts that panel away so the menu can
    /// open in its place.
    ///
    /// Those panels keep the activation, and a WinForms drop-down closes itself as soon as it loses it: measured,
    /// with the Start menu up the menu was gone 40 ms after it was shown, so every click belonged to the panel and
    /// no entry could be used. Turning AutoClose off to prevent that made the menu impossible to dismiss instead -
    /// WinForms' own close path stops working - so the panel is waited for.
    ///
    /// The show that follows is this app's own (ShowMenuByHand) and is guarded by _showingByHand, because Show()
    /// raises Opening again: without that guard the deferral cancelled its own show, and a cancelled show left the
    /// drop-down in a state WinForms counted as open, after which the tray icon did nothing at all.
    /// </summary>
    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        RefreshMenu();

        if (_showingByHand)
            return;

        if (!_settings.MenuDeferWhileShellPanel || !IsShellPanelInTheWay())
        {
            _panelDismissed = false;
            _panelDismissTries = 0;

            // A shell window that keeps the foreground after its panel has gone (measured: StartMenuExperienceHost
            // does so for seconds) is not something to wait for - waiting is what made the menu look dead for three
            // seconds. It does steal the activation from a drop-down shown the normal way, though, so show the menu
            // by hand instead: that takes the foreground first.
            if (ShellPanels.IsForeground())
            {
                e.Cancel = true;
                _menuAnchor = Cursor.Position;
                ShowMenuByHandSoon(1, "a shell window still had the foreground");
                return;
            }

            return;
        }

        e.Cancel = true;

        // An in-flight wait is not extended by further clicks: the user clicking again is impatience, not a new
        // situation, and resetting the deadline here meant the menu could be held back indefinitely.
        if (!_panelDismissed)
        {
            _panelDismissed = true;
            _menuAnchor = Cursor.Position;
            _panelWaitDeadline = Environment.TickCount64 + Math.Max(_settings.MenuPanelWaitMs, 500);
            DebugLog.Write($"tray menu: a shell panel is in the way, waiting up to {_settings.MenuPanelWaitMs} ms - {ShellPanels.DescribeOnScreen()}");
            TryDismissShellPanel(takeForeground: true);
        }

        _menuRetryTimer.Start();
    }

    /// <summary>
    /// True while one of the shell's panels is really on screen, or was a moment ago.
    ///
    /// "On screen" is checked by hit-testing a few points (ShellPanels.IsOnScreen) because it is the only signal
    /// that reflects the panel itself: the foreground can be a *different* shell window while the Start menu is up
    /// (measured: foreground SearchHost, panel StartMenuExperienceHost), so a foreground-only test both misses the
    /// panel and, worse, aims the dismissal at the wrong window.
    ///
    /// Deliberately NOT "the foreground window is a shell window": measured, StartMenuExperienceHost keeps the
    /// foreground for seconds after the Start menu has closed, and waiting for that cost a three-second stall on
    /// every right-click - which is what "the menu cannot be summoned" looked like. A stale foreground is dealt
    /// with by showing the menu by hand instead (see OnMenuOpening).
    /// </summary>
    private bool IsShellPanelInTheWay() => ShellPanels.IsOnScreen() || ShellPanelSeenRecently();

    /// <summary>
    /// Puts the shell's panel away with the Escape key the shell uses for that - never with a click, because a
    /// click while the Start menu is up is passed on to whatever window is underneath (measured), which on a game
    /// is its fire button.
    ///
    /// The Escape goes to the *panel's own window*, which is found by hit-testing: sending it to whatever has the
    /// foreground is what failed in the user's log (2026-09-26 09:18) - the foreground was SearchHost while the
    /// Start menu belonged to StartMenuExperienceHost, so the key went to the wrong shell window and the panel
    /// stayed up through every retry. The panel is given the focus first so that the key cannot land anywhere else.
    /// </summary>
    /// <param name="takeForeground">
    /// Whether to give the panel the focus before sending the key. True for the first attempt only: doing it again
    /// returns the activation to the panel and keeps it alive (see MaxPanelDismissTries).
    /// </param>
    private bool TryDismissShellPanel(bool takeForeground)
    {
        if (ShellPanels.TryFindOnScreenPanel(out var panel, out var host) && panel != IntPtr.Zero)
        {
            if (takeForeground)
                WindowZOrder.TakeForeground(panel);

            Input.SendEscape();
            DebugLog.Write(takeForeground
                ? $"tray menu: focus and Escape sent to {host}"
                : $"tray menu: Escape sent to {host} again, without taking the focus");
            return true;
        }

        if (ShellPanels.IsForeground())
        {
            Input.SendEscape();
            DebugLog.Write("tray menu: Escape sent to the panel that has the focus");
            return true;
        }

        DebugLog.Write("tray menu: a shell panel is in the way but could not be found to dismiss");
        return false;
    }

    /// <summary>
    /// True while a shell panel is in the foreground, and for a moment after it has gone: the panel hands the
    /// foreground back as soon as the click that dismisses it has been processed - which is exactly when this app
    /// is told to open its menu - and it stays on screen for the rest of its close animation.
    /// </summary>
    private bool ShellPanelSeenRecently()
        => Environment.TickCount64 - _lastShellPanelTick < _settings.MenuShellPanelGraceMs;

    /// <summary>
    /// Opens the menu by hand once the shell's panel is off the screen, because the NotifyIcon path only runs on the
    /// click that asked for it.
    /// </summary>
    private void OnMenuRetry()
    {
        _menuRetryTimer.Stop();

        if (IsShellPanelInTheWay())
        {
            if (_panelDismissTries < MaxPanelDismissTries)
            {
                _panelDismissTries++;
                TryDismissShellPanel(takeForeground: _panelDismissTries == 1);
            }

            if (Environment.TickCount64 < _panelWaitDeadline)
            {
                _menuRetryTimer.Start();
                return;
            }

            // The wait is over and something still looks like a panel. Open the menu anyway: a menu that may not be
            // clickable is better than no menu at all if that reading is wrong, and the log says what happened.
            // The description is repeated here on purpose - "what did it see for those three seconds, and did that
            // change?" is the question this line exists to answer.
            DebugLog.Write($"tray menu: something still looked like a shell panel after {_settings.MenuPanelWaitMs} ms - opening anyway - {ShellPanels.DescribeOnScreen()}");
        }

        _panelDismissTries = 0;

        // Only where the user asked for it: anywhere else the menu would appear away from the icon. The tolerance is
        // 80 px rather than 12 because a finger on a touchscreen does not move the cursor the way a mouse does -
        // measured, the cursor read 1296,1166 at one request and 1296,1178 at the next, and with a 12 px window the
        // show was refused *silently*: the log had "opening anyway" and then nothing, which is the user seeing
        // nothing happen at all. If it ever declines again, it says so.
        if (_panelDismissed)
        {
            var drift = Math.Abs(Cursor.Position.X - _menuAnchor.X) + Math.Abs(Cursor.Position.Y - _menuAnchor.Y);
            if (drift < 80)
                ShowMenuByHand(_menuAnchor, "after the shell panel went");
            else
                DebugLog.Write($"tray menu: not opening - the pointer moved {drift} px from where the menu was asked for ({_menuAnchor.X},{_menuAnchor.Y})");
        }

        _panelDismissed = false;
    }

    /// <summary>
    /// Keeps an eye on the shell's panels while the menu is up: a panel that takes the screen takes it for
    /// good, because it is drawn above a foreign topmost window (measured), and every click is its dismissal -
    /// so the menu is closed instead of being left there looking usable.
    ///
    /// The test is "on screen", not "in the foreground": a panel that is still opening is already hit-testable
    /// while the foreground has not moved to it yet, and that is the state a menu must not be left in.
    /// </summary>
    private void OnShellWatch()
    {
        // Self-heal first, whatever else is on screen: a drop-down WinForms believes is open is never shown
        // again, so if the window behind it is gone the tray icon goes dead - every later click is ignored and
        // nothing is logged, because Opening is not raised again. Clearing the state is what makes the icon work.
        if (_menu.Visible && !WindowZOrder.IsReallyVisible(_menu.Handle))
        {
            DebugLog.Write("tray menu: it counted as open with no window behind it - clearing it");
            _menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        }

        // Record whether a shell panel window is stacked over the screen, and log the *changes* only - without
        // anyone asking for the menu. The state the user reports as "sometimes it just does not open" comes and goes
        // while they are doing something else entirely (a game, a swipe up from the bottom), so catching it in the
        // act is the only honest way to diagnose it. Cloaked windows count here (see
        // ShellPanels.TryFindAnyShellPanel) and the line carries what the presence test sees, so a leftover shell
        // window cannot hide behind a fix that silently ignores it.
        var shellPanelThere = ShellPanels.TryFindAnyShellPanel(out _, out _);
        if (shellPanelThere != _shellPanelWasThere)
        {
            _shellPanelWasThere = shellPanelThere;
            DebugLog.Write($"shell panel window {(shellPanelThere ? "is over the screen" : "is gone")} - {ShellPanels.DescribeOnScreen()}");
        }

        if (!ShellPanels.IsForeground())
            return;

        _lastShellPanelTick = Environment.TickCount64;

        // The menu is deliberately *not* closed here any more. It stays usable while a panel is up - that is what
        // AutoClose = false and the mouse capture are for - and closing it whenever a panel held the foreground
        // was what made it disappear the moment it appeared.
    }

    /// <summary>
    /// Opens the menu by hand once the shell's panel is off the screen, because the NotifyIcon path only runs
    /// on the click that asked for it. A drop-down opened this way is not dismissed by a click outside it, so
    /// it is given the mouse capture: TopMostMenu then sees every click and closes on the ones outside.
    /// </summary>
    private void OnMenuOpened()
    {
        _menuOpenedTick = Environment.TickCount64;
        DebugLog.Write($"tray menu: opened ({_menu.Items.Count} items at {_menu.Bounds.X},{_menu.Bounds.Y})");
        KeepMenuOffTaskbar();

        // No SetCapture here: a drop-down that holds the mouse capture refuses to hide, so every attempt to close
        // the menu (an entry being used, a click outside) silently did nothing and the menu could never be got rid
        // of. What keeps the menu alive while the shell's panel holds the activation is AutoClose = false alone.
        MouseWatcher.Start();

        _menuOnTopTimer.Start();
    }

    /// <summary>
    /// Shows the menu without a click on the tray icon - the two paths that need it are OnMenuRetry (after the
    /// shell's own panel was dismissed for the request) and the keyboard shortcut.
    ///
    /// The app has to be the foreground process first, which is what the shell's own path does for us
    /// (NotifyIcon calls SetForegroundWindow before showing the menu). Without it a drop-down closes itself the
    /// moment it opens: measured, "opened by hand" followed three milliseconds later by "closed
    /// (AppFocusChange)", which is indistinguishable from "the shortcut did nothing".
    ///
    /// A drop-down opened this way gets no click-outside handling from WinForms either, because it never held
    /// the capture and the click does not reach it, so the mouse hook takes that over (see MouseWatcher).
    /// </summary>
    /// <summary>
    /// Shows the menu a moment from now, after the current message has been handled.
    ///
    /// Show() cannot be called from inside the Opening handler: Opening would run again, find a shell panel, cancel
    /// the show, and leave the drop-down in the half-open state that made every later click do nothing (see
    /// _showingByHand).
    /// </summary>
    private void ShowMenuByHandSoon(int delayMs, string why)
    {
        _pendingShowReason = why;
        _reopenTimer.Interval = delayMs;
        _reopenTimer.Start();
    }

    private void ShowMenuByHand(Point anchor, string why)
    {
        MakeThisAppForeground();

        RefreshMenu();

        // A drop-down WinForms counts as open is never shown again, so any leftover state is cleared first.
        if (_menu.Visible && !WindowZOrder.IsReallyVisible(_menu.Handle))
        {
            DebugLog.Write("tray menu: it counted as open with no window behind it - clearing that first");
            _menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        }

        _showingByHand = true;
        try
        {
            _menu.Show(anchor, ToolStripDropDownDirection.AboveLeft);
        }
        finally
        {
            _showingByHand = false;
        }

        DebugLog.Write($"tray menu: opened by hand at {anchor.X},{anchor.Y} ({why})");
    }

    /// <summary>
    /// Asks for the foreground with the window the tray icon already owns.
    ///
    /// Any window of this process would do, and that one is already to hand (NotifyIcons finds it). A request
    /// that Windows refuses is harmless: the drop-down then behaves as it did before, closing itself.
    /// </summary>
    private static void MakeThisAppForeground()
    {
        var icon = NotifyIcons.TryGetIconRect(out var window);
        if (icon is not null && window != IntPtr.Zero)
            WindowZOrder.TakeForeground(window);
    }

    private void OnMenuHotkey()
    {
        if (_menu.Visible)
        {
            _menu.Close();
            return;
        }

        ShowMenuByHand(Cursor.Position, "hotkey Ctrl+Alt+Shift+M");
    }

    /// <summary>
    /// Puts the icon back into the notification area. NotifyIcon does not expose its message window, so this is
    /// the visible/invisible pair the type offers: that is a Shell_NotifyIcon delete followed by an add, which
    /// is what the shell needs to start answering for it again.
    /// </summary>
    private void ReRegisterTrayIcon()
    {
        if (_uiDispatcher.InvokeRequired)
        {
            try { _uiDispatcher.BeginInvoke(new Action(ReRegisterTrayIcon)); } catch { }
            return;
        }

        try
        {
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
            _lastIconReregisterTick = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"tray icon: could not be re-added: {ex.Message}");
        }
    }

    /// <summary>Background half of the icon check - see _iconHealthTimer.</summary>
    private static void OnIconHealthCheckStatic(object? state)
        => s_current?.CheckIconHealth();

    private void CheckIconHealth()
    {
        var rect = NotifyIcons.TryGetIconRect(out _);

        if (rect is not null)
        {
            if (!_iconWasRegistered)
                DebugLog.Write($"tray icon: the shell has the icon again (at {rect.Value.X},{rect.Value.Y})");

            _iconWasRegistered = true;
            _iconLossesInARow = 0;
            return;
        }

        _iconLossesInARow++;

        // Two misses in a row, and never more often than once a minute: a re-add costs an icon flicker, and
        // the shell can answer "no" for a moment while it is moving the icon about.
        if (_iconLossesInARow < 2 || Environment.TickCount64 - _lastIconReregisterTick < 60000)
            return;

        DebugLog.Write("tray icon: the shell does not know the icon - clicks on it would do nothing; re-adding it");
        _iconWasRegistered = false;
        _iconLossesInARow = 0;
        ReRegisterTrayIcon();
    }

    /// <summary>
    /// Keeps the menu raised, and dismisses it on a click outside.
    ///
    /// The raising is needed because the taskbar and the icon flyout take their topmost place back after
    /// the menu has been shown (see TopMostMenu). The dismissal is needed for the menus this app opens by
    /// hand - after dismissing a shell panel (see OnMenuRetry): those do not get WinForms' click-outside
    /// handling, because without the mouse capture they never see the click.
    /// </summary>
    private void OnMenuTick()
    {
        if (_settings.MenuKeepTopMost)
            WindowZOrder.RaiseToTopMost(_menu.Handle);

        // The menu's bounds are read here rather than remembered from show time: KeepMenuOffTaskbar moves the
        // menu after it appears, so an honest test has to use where it is now.
        if (MouseWatcher.TakeClick(out var click, out _, out var fromTouch))
        {
            var inside = _menu.Bounds.Contains(click);

            // A click inside the menu is an entry being used - WinForms closes the drop-down itself in that case
            // (AutoClose is on), so only the click outside is acted on here.
            if (!inside)
            {
                DebugLog.Write($"tray menu: click at {click.X},{click.Y}{(fromTouch ? " (touch)" : string.Empty)} was outside {_menu.Bounds} - closing");
                _menu.Close(ToolStripDropDownCloseReason.AppClicked);
            }
        }
    }

    /// <summary>
    /// Closes the menu the moment the user presses anywhere outside it.
    ///
    /// This is here and not only in the 200 ms watch (OnMenuTick) because the user's words are "pressing anywhere
    /// else should close it at once", and the watch could leave the menu up for up to a fifth of a second after the
    /// press. The close is *posted* rather than done here: this runs inside the hook callback, and closing a window
    /// runs WinForms' message pumping - and a hook callback that throws is removed by Windows, after which nothing
    /// closes the menu at all.
    /// </summary>
    private void OnAnyButtonDown(Point point, bool leftButton, bool fromTouch)
    {
        if (!_menu.Visible || !MouseWatcher.IsWatching)
            return;

        try
        {
            _menu.BeginInvoke(() =>
            {
                if (!_menu.Visible || _menu.Bounds.Contains(point))
                    return;

                DebugLog.Write($"tray menu: press at {point.X},{point.Y}{(fromTouch ? " (touch)" : string.Empty)} was outside {_menu.Bounds} - closing at once");
                _menu.Close(ToolStripDropDownCloseReason.AppClicked);
            });
        }
        catch (InvalidOperationException)
        {
            // The menu's handle went away between the check and the post - nothing to close.
        }
    }

    /// <summary>
    /// Moves the menu off the taskbar if WinForms put it there.
    ///
    /// NotifyIcon hands the menu to ContextMenuStrip.ShowInTaskbar, which clears the drop-down's "keep
    /// inside the working area" flag on purpose - its comment says "this will allow us to overlap the
    /// system tray". The menu is then placed with its bottom-right corner on the pointer, and the
    /// pointer is on the taskbar, so its last items land under the taskbar. That is the whole problem:
    /// the taskbar is topmost too and re-raises itself while the pointer is on it. The shell's own tray
    /// menu never overlaps the taskbar, so this moves ours clear of it, by as little as it can.
    /// </summary>
    private void KeepMenuOffTaskbar()
    {
        if (!_settings.MenuAvoidTaskbar)
            return;

        if (!WindowZOrder.TryGetTaskbarRect(out var taskbar, out var edge))
            return;

        var bounds = _menu.Bounds;
        var screen = Screen.FromRectangle(bounds).Bounds;
        var gap = Math.Max(_settings.MenuTaskbarGap, 0);

        switch (edge)
        {
            case TaskbarEdge.Bottom when bounds.Bottom > taskbar.Top - gap:
                bounds.Y = taskbar.Top - bounds.Height - gap;
                bounds.Y = Math.Max(bounds.Y, screen.Top);
                break;
            case TaskbarEdge.Top when bounds.Top < taskbar.Bottom + gap:
                bounds.Y = taskbar.Bottom + gap;
                bounds.Y = Math.Min(bounds.Y, screen.Bottom - bounds.Height);
                break;
            case TaskbarEdge.Left when bounds.Left < taskbar.Right + gap:
                bounds.X = taskbar.Right + gap;
                bounds.X = Math.Min(bounds.X, screen.Right - bounds.Width);
                break;
            case TaskbarEdge.Right when bounds.Right > taskbar.Left - gap:
                bounds.X = taskbar.Left - bounds.Width - gap;
                bounds.X = Math.Max(bounds.X, screen.Left);
                break;
            default:
                return;
        }

        _menu.Bounds = bounds;

        if (_settings.MenuKeepTopMost)
            WindowZOrder.RaiseToTopMost(_menu.Handle);
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
        // Independent of which monitor is connected, so they are built even when none is found.
        BuildStartMenuSizeSubmenu();
        BuildTrayMenuSubmenu();
        BuildAdvancedSubmenu();

        var monitors = _displayService.GetAllMonitors();
        var target = FindTargetMonitor(monitors);

        if (target is null)
        {
            _statusItem.Text = "未匹配";
            _connectToggleItem.Text = "断开 / 连接 (未匹配)";
            _connectToggleItem.Enabled = false;
            _displayModeMenuItem.Enabled = false;
            _displayModeMenuItem.DropDownItems.Clear();
            _rotationMenuItem.Enabled = false;
            _rotationMenuItem.DropDownItems.Clear();
            _brightnessMenuItem.Enabled = false;
            _brightnessMenuItem.DropDownItems.Clear();
            _volumeMenuItem.Enabled = false;
            _volumeMenuItem.DropDownItems.Clear();
            _trayIcon.Text = "移动显示器控制器 - 未匹配";
            return;
        }

        _statusItem.Text = target.IsActive ? $"{target.FriendlyName}: 已连接" : $"{target.FriendlyName}: 已断开";
        _trayIcon.Text = $"移动显示器控制器 - {(target.IsActive ? "已连接" : "已断开")}";

        _connectToggleItem.Enabled = true;
        _connectToggleItem.Text = target.IsActive ? "断开连接" : "重新连接";

        BuildDisplayModeSubmenu(target);
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
    /// Switches layouts, then puts the monitor that stays active back on the resolution, refresh rate
    /// and DPI scale it had before: switching screens on and off should not also re-zoom them.
    ///
    /// An earlier version restored the mode through ChangeDisplaySettingsEx with CDS_UPDATEREGISTRY,
    /// which *writes the mode into the registry for that display* - so whatever the desktop happened
    /// to be at while a switch ran became what Windows restored at every boot (a rotated duplicate's
    /// 1200-wide surface). It was removed for that. The mode is restored again now, but with
    /// dwFlags 0 (this session only, nothing written) and refusing a portrait/landscape flip; see
    /// <see cref="DisplayService.TrySetMode"/>. Do not add CDS_UPDATEREGISTRY back.
    /// </summary>
    private bool TrySwitchLayout(DisplayService.TopologyMode mode, out string error)
    {
        var before = GetDesktopMode();

        // Captured while each monitor still has the source it had a moment ago - once the topology
        // below is applied, Windows is free to hand the new one a mode and DPI scale of its own
        // choosing.
        var monitorsBefore = _displayService.GetAllMonitors();
        _displayService.CaptureModeBaseline(monitorsBefore);
        _displayService.CaptureDpiScaleBaseline(monitorsBefore);

        if (!_displayService.TrySetTopology(mode, out error))
            return false;

        if (mode == DisplayService.TopologyMode.Clone)
        {
            // Building a duplicate copies the built-in panel's 90 degrees onto the external monitor
            // too, which leaves the external picture lying on its side; only the built-in needs that
            // rotation. Rotation is a property of the display path rather than the shared source mode,
            // so this one is still set - and unlike a mode it does not decide anything for the boot.
            if (_displayService.TryNormalizeExternalRotation(out var rotationError))
                DebugLog.Write("duplicate: external rotation normalised to 0");
            else
                DebugLog.Write($"duplicate: external rotation left as Windows set it ({rotationError})");
        }

        // Re-read monitors now that the switch is done: a dedicated source's id and GDI name can
        // change when the topology does, so they have to come from after this point, not from the
        // list captured above. Mode first, then DPI: the scale steps a source offers depend on the
        // mode it is running.
        var monitorsAfter = _displayService.GetAllMonitors();
        foreach (var line in _displayService.RestoreModeBaseline(monitorsAfter))
            DebugLog.Write($"switch: {line}");

        monitorsAfter = _displayService.GetAllMonitors();
        foreach (var line in _displayService.RestoreDpiScaleBaseline(monitorsAfter))
            DebugLog.Write($"switch: {line}");

        if (GetDesktopMode() is { } after)
        {
            if (before is { } was)
                DebugLog.Write(after == was
                    ? $"switch: mode untouched at {after.Width}x{after.Height}@{after.Hz}Hz"
                    : $"switch: Windows moved the mode {was.Width}x{was.Height}@{was.Hz}Hz -> {after.Width}x{after.Height}@{after.Hz}Hz");
            else
                DebugLog.Write($"switch: desktop mode is now {after.Width}x{after.Height}@{after.Hz}Hz");
        }

        return true;
    }

    /// <summary>
    /// The mode the desktop is at right now, read only. The external panel is the one present in both
    /// layouts, and in a mirror it shares the source mode, so its mode is the desktop's either way.
    /// </summary>
    private (int Width, int Height, int Hz)? GetDesktopMode()
        => FindActiveExternal()?.GdiDeviceName is { } gdi ? _displayService.GetCurrentMode(gdi) : null;

    private MonitorEntry? FindActiveExternal()
        => _displayService.GetAllMonitors().FirstOrDefault(m => !m.IsInternal && m.IsActive && m.GdiDeviceName is not null);

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

    /// <summary>
    /// Sizes offered for the Start menu, in DIP, and 0 standing for "what Windows would have used".
    ///
    /// The width list is the range this machine can actually show, not a round number. Measured on a 150%
    /// display: 400 DIP arrives as a 600 px panel and 700 as 1052 px, while anything under about 325 DIP stops
    /// the Start menu drawing at all - and it stays broken until the shell hosts restart (see the hook's
    /// kMinWidth). 350 is therefore the narrow end, and Windows' own default measures 668 x 716 DIP here, so
    /// 700 is the wide end.
    /// </summary>
    private static readonly int[] StartMenuWidthPresets = { 0, 350, 400, 450, 500, 550, 600, 650, 700 };

    private static readonly int[] StartMenuHeightPresets = { 0, 600, 650, 700, 750, 890, 1000 };

    /// <summary>
    /// Whole sizes, all of them inside the same width range the 宽度 entry offers - a preset that exceeds it
    /// would quietly contradict the limit next to it.
    /// </summary>
    private static readonly (string Label, int Width, int Height)[] StartMenuSizePresets =
    {
        ("很窄 (350×700)", 350, 700),
        ("窄 (400×750)", 400, 750),
        ("中 (500×890)", 500, 890),
        ("宽 (650×1000)", 650, 1000),
    };

    /// <summary>
    /// The narrowest width this app will write, and the same limit the hook enforces on anything that arrives
    /// another way - a hand-edited ini file, for instance, cannot put the Start menu into a state it cannot
    /// draw (see the hook's kMinWidth for what was measured).
    /// </summary>
    private const int StartMenuMinimumWidth = StartMenuSizeSettings.MinimumWidth;

    private const int StartMenuMaximumWidth = StartMenuSizeSettings.MaximumWidth;

    private const int StartMenuMaximumHeight = StartMenuSizeSettings.MaximumHeight;

    /// <summary>
    /// The Start menu's size, as the injected hook reads it. Writing the file is all it takes: the hook
    /// re-reads it every time the menu is opened, so the change lands on the next open with no re-injection.
    /// </summary>
    private void BuildStartMenuSizeSubmenu()
    {
        var size = StartMenuSizeSettings.Load();

        _startMenuSizeMenuItem.DropDownItems.Clear();

        _startMenuSizeMenuItem.DropDownItems.Add(new ToolStripMenuItem(
            $"当前: {DescribeStartMenuSize(size.Width)} × {DescribeStartMenuSize(size.Height)} — {StartMenuHookInstaller.DescribeAttachment()}")
        {
            Enabled = false,
        });

        _startMenuSizeMenuItem.DropDownItems.Add(new ToolStripSeparator());

        _startMenuSizeMenuItem.DropDownItems.Add(BuildStartMenuValueMenu(
            "宽度", size.Width, StartMenuWidthPresets, StartMenuMinimumWidth, StartMenuMaximumWidth, SetStartMenuWidth));

        _startMenuSizeMenuItem.DropDownItems.Add(BuildStartMenuValueMenu(
            "高度", size.Height, StartMenuHeightPresets, 0, StartMenuMaximumHeight, SetStartMenuHeight));

        var presetsItem = new ToolStripMenuItem("预设尺寸");
        foreach (var (label, width, height) in StartMenuSizePresets)
        {
            var (presetWidth, presetHeight) = (width, height);
            var item = new ToolStripMenuItem(label)
            {
                Checked = size.Width == presetWidth && size.Height == presetHeight,
            };
            item.Click += (_, _) => SetStartMenuSize(presetWidth, presetHeight);
            presetsItem.DropDownItems.Add(item);
        }
        _startMenuSizeMenuItem.DropDownItems.Add(presetsItem);

        var resetItem = new ToolStripMenuItem("还原系统默认尺寸") { Enabled = !size.IsSystemDefault };
        resetItem.Click += (_, _) => SetStartMenuSize(0, 0);
        _startMenuSizeMenuItem.DropDownItems.Add(resetItem);

        _startMenuSizeMenuItem.DropDownItems.Add(new ToolStripSeparator());

        var attachItem = new ToolStripMenuItem("把钩子注入开始菜单进程");
        attachItem.Click += (_, _) => AttachStartMenuHook();
        _startMenuSizeMenuItem.DropDownItems.Add(attachItem);

        var reattachItem = new ToolStripMenuItem("重启开始菜单进程并重新注入");
        reattachItem.Click += (_, _) => RestartHostsAndAttach();
        _startMenuSizeMenuItem.DropDownItems.Add(reattachItem);

        var clearLogItem = new ToolStripMenuItem("清空钩子日志");
        clearLogItem.Click += (_, _) => ClearStartMenuHookLog();
        _startMenuSizeMenuItem.DropDownItems.Add(clearLogItem);
    }

    /// <summary>
    /// One "宽度"-style entry: the preset numbers, then a custom one. The current value is ticked, so the
    /// line reads as a state rather than a set of buttons. <paramref name="minimum"/> is a real limit - the
    /// hook clamps to it - and not a suggestion, so the prompt does not offer anything below it.
    /// </summary>
    private ToolStripMenuItem BuildStartMenuValueMenu(string title, int current, int[] presets,
        int minimum, int maximum, Action<int> apply)
    {
        var menu = new ToolStripMenuItem($"{title} (当前 {DescribeStartMenuSize(current)})");

        foreach (var value in presets)
        {
            var preset = value;
            var item = new ToolStripMenuItem(DescribeStartMenuSize(preset)) { Checked = current == preset };
            item.Click += (_, _) => apply(preset);
            menu.DropDownItems.Add(item);
        }

        var customItem = new ToolStripMenuItem("自定义…");
        customItem.Click += (_, _) =>
        {
            var initial = Math.Clamp(current, minimum, maximum);
            if (AskForNumber($"开始菜单{title}", $"{title} (DIP, {minimum}~{maximum}):", initial, minimum, maximum) is { } entered)
                apply(entered);
        };

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(customItem);

        return menu;
    }

    private static string DescribeStartMenuSize(int value)
        => value <= 0 ? "系统默认" : value.ToString();

    private void SetStartMenuWidth(int width)
        => SetStartMenuSize(width, StartMenuSizeSettings.Load().Height);

    private void SetStartMenuHeight(int height)
        => SetStartMenuSize(StartMenuSizeSettings.Load().Width, height);

    private void SetStartMenuSize(int width, int height)
    {
        var size = new StartMenuSizeSettings { Width = width, Height = height };

        if (!size.Save(out var error))
        {
            ShowBalloon("开始菜单尺寸", error, ToolTipIcon.Error);
            return;
        }

        DebugLog.Write($"start menu size: set to {(size.IsSystemDefault ? "system default" : $"{width}x{height}")}");

        var what = size.IsSystemDefault ? "已还原系统默认尺寸" : $"已设置为 {width}×{height}";
        ShowBalloon("开始菜单尺寸", $"{what}(下次打开开始菜单时生效) — {StartMenuHookInstaller.DescribeAttachment()}", ToolTipIcon.Info);

        RefreshMenu();
    }

    private void AttachStartMenuHook()
    {
        var library = StartMenuHookInstaller.FindLibrary();
        DebugLog.Write($"start menu hook: attaching {library ?? "(no library found)"}");

        var attached = StartMenuHookInstaller.Attach(out var report);

        ShowBalloon("开始菜单尺寸",
            attached ? $"{report},打开开始菜单就能看到尺寸生效" : report,
            attached ? ToolTipIcon.Info : ToolTipIcon.Error);

        RefreshMenu();
    }

    /// <summary>
    /// Restarts the shell's Start-menu hosts and then injects.
    ///
    /// A DLL that is already in a process cannot be unloaded from outside it, and it cannot be replaced
    /// either - so a rebuilt hook would either be ignored (the old copy does all the work) or, worse, loaded
    /// next to the old one. Restarting the hosts clears both problems; the shell starts them again by itself.
    /// </summary>
    private void RestartHostsAndAttach()
    {
        DebugLog.Write("start menu hook: restarting the shell hosts to drop the loaded hook");

        StartMenuHookInstaller.RestartHosts(out var restartReport);
        var attached = StartMenuHookInstaller.Attach(out var attachReport);

        ShowBalloon("开始菜单尺寸",
            attached ? $"{restartReport},{attachReport}" : $"{restartReport} — {attachReport}",
            attached ? ToolTipIcon.Info : ToolTipIcon.Error);

        RefreshMenu();
    }

    private void ClearStartMenuHookLog()
    {
        try
        {
            if (File.Exists(StartMenuSizeSettings.LogPath))
                File.Delete(StartMenuSizeSettings.LogPath);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"start menu hook: could not clear the log: {ex.Message}");
        }

        RefreshMenu();
    }

    /// <summary>
    /// The tray menu's own behaviour - the things that used to be constants while its placement against the
    /// taskbar and the shell's panels was being worked out.
    /// </summary>
    private void BuildTrayMenuSubmenu()
    {
        _trayMenuMenuItem.DropDownItems.Clear();

        var avoidItem = new ToolStripMenuItem("避让任务栏(菜单不压住任务栏)")
        {
            CheckOnClick = true,
            Checked = _settings.MenuAvoidTaskbar,
        };
        avoidItem.Click += (_, _) => UpdateMenuBehaviour(s => s.MenuAvoidTaskbar = avoidItem.Checked);
        _trayMenuMenuItem.DropDownItems.Add(avoidItem);

        _trayMenuMenuItem.DropDownItems.Add(BuildTrayMenuValueMenu(
            "与任务栏间距", _settings.MenuTaskbarGap, new[] { 0, 1, 4, 8, 16 }, "像素",
            value => UpdateMenuBehaviour(s => s.MenuTaskbarGap = value)));

        var deferItem = new ToolStripMenuItem("打开前先等开始菜单/搜索面板关掉")
        {
            CheckOnClick = true,
            Checked = _settings.MenuDeferWhileShellPanel,
        };
        deferItem.Click += (_, _) => UpdateMenuBehaviour(s => s.MenuDeferWhileShellPanel = deferItem.Checked);
        _trayMenuMenuItem.DropDownItems.Add(deferItem);

        _trayMenuMenuItem.DropDownItems.Add(BuildTrayMenuValueMenu(
            "等面板消失最多等", _settings.MenuPanelWaitMs, new[] { 1000, 2000, 3000, 5000 }, "毫秒",
            value => UpdateMenuBehaviour(s => s.MenuPanelWaitMs = value)));

        _trayMenuMenuItem.DropDownItems.Add(BuildTrayMenuValueMenu(
            "面板消失后的宽限时间", _settings.MenuShellPanelGraceMs, new[] { 150, 350, 600, 1000 }, "毫秒",
            value => UpdateMenuBehaviour(s => s.MenuShellPanelGraceMs = value)));

        var topMostItem = new ToolStripMenuItem("定时保持在最前")
        {
            CheckOnClick = true,
            Checked = _settings.MenuKeepTopMost,
        };
        topMostItem.Click += (_, _) => UpdateMenuBehaviour(s => s.MenuKeepTopMost = topMostItem.Checked);
        _trayMenuMenuItem.DropDownItems.Add(topMostItem);
    }

    private ToolStripMenuItem BuildTrayMenuValueMenu(string title, int current, int[] presets, string unit, Action<int> apply)
    {
        var menu = new ToolStripMenuItem($"{title} (当前 {current} {unit})");

        foreach (var value in presets)
        {
            var preset = value;
            var item = new ToolStripMenuItem($"{preset} {unit}") { Checked = current == preset };
            item.Click += (_, _) => apply(preset);
            menu.DropDownItems.Add(item);
        }

        var customItem = new ToolStripMenuItem("自定义…");
        customItem.Click += (_, _) =>
        {
            if (AskForNumber(title, $"{title} ({unit}, 0 以上):", current, 0, 60000) is { } entered)
                apply(entered);
        };

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(customItem);

        return menu;
    }

    /// <summary>Changes one of the tray menu's own settings and saves it, so it survives a restart.</summary>
    private void UpdateMenuBehaviour(Action<AppSettings> change)
    {
        change(_settings);
        _settings.Save();
        DebugLog.Write($"tray menu behaviour: avoid={_settings.MenuAvoidTaskbar} gap={_settings.MenuTaskbarGap} " +
                       $"defer={_settings.MenuDeferWhileShellPanel} wait={_settings.MenuPanelWaitMs}ms " +
                       $"grace={_settings.MenuShellPanelGraceMs}ms topmost={_settings.MenuKeepTopMost}");
        RefreshMenu();
    }

    /// <summary>
    /// The settings and files that are not part of any other submenu: which monitor counts as "the" monitor,
    /// and where the things this app writes actually are.
    /// </summary>
    private void BuildAdvancedSubmenu()
    {
        _advancedMenuItem.DropDownItems.Clear();

        var filterItem = new ToolStripMenuItem($"显示器名称匹配关键字: {_settings.TargetMonitorNameFilter}");
        filterItem.Click += (_, _) => PromptForMonitorFilter();
        _advancedMenuItem.DropDownItems.Add(filterItem);

        _advancedMenuItem.DropDownItems.Add(new ToolStripMenuItem(
            $"已识别的显示器: {_settings.TargetMonitorKey ?? "(未记录)"}")
        {
            Enabled = false,
        });

        _advancedMenuItem.DropDownItems.Add(new ToolStripSeparator());

        _advancedMenuItem.DropDownItems.Add(BuildOpenItem("打开应用配置文件夹", SettingsFolderPath));
        _advancedMenuItem.DropDownItems.Add(BuildOpenItem("打开开始菜单钩子文件夹", StartMenuSizeSettings.FolderPath));
        _advancedMenuItem.DropDownItems.Add(BuildOpenItem("打开应用日志", DebugLog.LogPath));
        _advancedMenuItem.DropDownItems.Add(BuildOpenItem("打开开始菜单钩子日志", StartMenuSizeSettings.LogPath));
    }

    private ToolStripMenuItem BuildOpenItem(string title, string path)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) => OpenInExplorer(path);
        return item;
    }

    private static string SettingsFolderPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MobDisplayController");

    private void OpenInExplorer(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                ShowBalloon("打开失败", $"还没有这个文件或文件夹: {path}", ToolTipIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("打开失败", ex.Message, ToolTipIcon.Error);
        }
    }

    private void PromptForMonitorFilter()
    {
        var entered = AskForText("显示器名称匹配关键字",
            "显示器的友好名称里包含它就选中它 (例如 DP / HDMI):", _settings.TargetMonitorNameFilter);

        if (entered is null)
            return;

        _settings.TargetMonitorNameFilter = entered;
        _settings.Save();
        RefreshMenu();
    }

    /// <summary>
    /// Asks for a value with the menu out of the way first.
    ///
    /// The menu cannot be the dialog's owner: TopMostMenu forces WS_EX_TOPMOST on it, so it would draw over
    /// the dialog it owns. Closing it first and leaving the dialog unowned is what puts the dialog in front.
    /// </summary>
    private int? AskForNumber(string title, string label, int initial, int minimum, int maximum)
    {
        _menu.Close();
        return InputPromptForm.AskNumber(null, title, label, initial, minimum, maximum);
    }

    private string? AskForText(string title, string label, string initial)
    {
        _menu.Close();
        return InputPromptForm.AskText(null, title, label, initial);
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
        _menuHotkey?.Dispose();
        _iconHealthTimer.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
