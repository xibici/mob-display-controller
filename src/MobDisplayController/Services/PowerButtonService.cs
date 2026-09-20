using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using MobDisplayController.Native;

namespace MobDisplayController.Services;

/// <summary>
/// Turns the physical power button into the layout switch between the two screens the tray menu
/// offers (external only / both panels showing).
///
/// The press cannot be read directly on this machine, and that was measured rather than assumed:
/// the ACPI fixed button here exposes GUID_DEVICE_SYS_BUTTON but never answers
/// IOCTL_GET_SYS_BUTTON_EVENT - 13276 polls across a real press returned zero events - and with the
/// button set to "do nothing" a press leaves no trace anywhere, not even in the event log. So the
/// press only becomes observable if Windows is allowed to act on it, and the action that *names* its
/// cause is sleep: entering Modern Standby logs Microsoft-Windows-Kernel-Power event 506 carrying
/// Reason = 1 (power button). That stated reason is what this service watches.
///
/// It is also what the open-source handheld shells rely on - HandheldCompanion maps the same Reason
/// field to WakeReason.PowerButton - and it is the piece the previous "guess it from the console
/// going dark" logic was missing: the cause is *reported*, not inferred, so an idle blank or our own
/// display switch can no longer be mistaken for a press.
///
/// The standby round trip itself cannot be avoided: a button-initiated sleep ignores
/// ES_SYSTEM_REQUIRED / ES_AWAYMODE_REQUIRED, and no PBT_APMQUERYSUSPEND ever arrives on a Modern
/// Standby machine, so there is nothing to refuse. What can be fixed is the aftermath - the caller
/// turns CONSOLELOCK off while the takeover is armed, so coming back no longer lands on the sign-in
/// screen, and the display is woken as soon as the standby entry is reported.
///
/// Keeping a native action also means that if this app isn't running the button still behaves like a
/// power button rather than becoming completely dead.
/// </summary>
public sealed class PowerButtonService : IDisposable
{
    /// <summary>Raised whenever Windows reports that it entered standby because the power button was
    /// pressed. Raised on the thread that called <see cref="StartListening"/> (the UI thread), so the
    /// caller can touch the menu directly.</summary>
    public event Action? PowerButtonPressed;

    private MessageWindow? _window;
    private IntPtr _notificationHandle;

    /// <summary>Captured from the thread that owns the tray menu, so press notifications land back there.</summary>
    private SynchronizationContext? _uiContext;

    /// <summary>Polls the System event log for the power-button standby entry. See <see cref="PowerEventLoop"/>.</summary>
    private Thread? _powerEventThread;
    private ManualResetEvent? _powerEventStop;
    private DateTime _lastPowerEventCheckUtc;
    private long _lastHandledRecordId;

    /// <summary>Kernel-Power reason code meaning "this standby entry was caused by the power button".</summary>
    private const int KernelPowerReasonPowerButton = 1;

    /// <summary>Set while we're driving the display state ourselves, so our own changes don't re-enter the handler.</summary>
    private DateTime _suppressUntil = DateTime.MinValue;

    /// <summary>Guards against the display state flapping - blanking and unblanking can race and produce a burst of events.</summary>
    private DateTime _lastAcceptedPress = DateTime.MinValue;
    private static readonly TimeSpan PressDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When the display last reported itself on. Blanking and unblanking can get into a
    /// tug-of-war that alternates off/on every couple of hundred milliseconds, and those
    /// spurious "off"s would otherwise read as presses and toggle the screens repeatedly.
    /// A real press is always preceded by the display having been steadily on.
    ///
    /// Three seconds, not one and a half: switching layouts makes the console report off/on by
    /// itself, and when that cycle landed just outside the old 1.5s window the app read its own
    /// switch as a fresh press and toggled again - a self-sustaining flicker loop.
    /// </summary>
    private DateTime _lastReportedOn = DateTime.MinValue;
    private static readonly TimeSpan MinStableOnTime = TimeSpan.FromSeconds(3);

    /// <summary>Last state the console display reported. Starts "on": someone using the machine is the normal case.</summary>
    private bool _displayIsOn = true;

    public bool IsListening => _window is not null;

    /// <summary>Reads the active power scheme's current AC/DC power button action, or null on failure.</summary>
    public (uint Ac, uint Dc)? ReadCurrentAction()
    {
        if (!TryGetActiveScheme(out var scheme))
            return null;

        var subgroup = Power.GUID_BUTTONS_SUBGROUP;
        var setting = Power.GUID_POWERBUTTON_ACTION;

        if (Power.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint ac) != 0)
            return null;
        if (Power.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint dc) != 0)
            return null;

        return (ac, dc);
    }

    /// <summary>
    /// Points the power button at Windows' native "sleep" action and starts watching for the standby
    /// entry that names the button as its cause. <paramref name="restoreAc"/>/<paramref name="restoreDc"/>
    /// are the user's original action, which is put back if the notification can't be registered.
    ///
    /// Sleep rather than "do nothing": with no native action there is nothing left to observe at all
    /// (measured - with the button set to do nothing, six presses left the event log completely
    /// empty). Sleep is the only power-button action that reports *why* it happened, and the caller
    /// keeps the sign-in screen out of the way by turning CONSOLELOCK off while the takeover is armed.
    /// </summary>
    public bool TryEnable(uint? restoreAc, uint? restoreDc, out string error)
    {
        if (!TryWriteAction(Power.PBUTTON_SLEEP, Power.PBUTTON_SLEEP, out error))
            return false;

        StartListening();

        if (_notificationHandle == IntPtr.Zero)
        {
            // Without the notification the service would be blind, so don't leave the setting
            // changed and claim success.
            error = "无法注册显示器状态通知,已恢复电源键原有行为。";
            DebugLog.Write("RegisterPowerSettingNotification failed - restoring the original power button action");
            StopListening();

            if (restoreAc is not null && restoreDc is not null && !TryWriteAction(restoreAc.Value, restoreDc.Value, out var restoreError))
                DebugLog.Write($"  -> could not restore power button action: {restoreError}");

            return false;
        }

        return true;
    }

    /// <summary>Restores the power button action to the given values and stops watching.</summary>
    public void Disable(uint? restoreAc, uint? restoreDc)
    {
        StopListening();

        if (restoreAc is not null && restoreDc is not null)
            TryWriteAction(restoreAc.Value, restoreDc.Value, out _);
    }

    public void StartListening()
    {
        if (_window is not null)
            return;

        // Remember the thread that owns the tray menu: presses are reported by our own polling
        // thread, which posts them back here so callers can touch the menu directly.
        _uiContext = SynchronizationContext.Current;

        _window = new MessageWindow();
        _window.PowerSettingChanged += OnPowerSettingChanged;
        _window.SuspendRequested += OnSuspendRequested;

        var guid = Power.GUID_CONSOLE_DISPLAY_STATE;
        _notificationHandle = Power.RegisterPowerSettingNotification(_window.Handle, ref guid, Power.DEVICE_NOTIFY_WINDOW_HANDLE);

        StartPowerEventWatcher();
    }

    public void StopListening()
    {
        StopPowerEventWatcher();

        if (_notificationHandle != IntPtr.Zero)
        {
            Power.UnregisterPowerSettingNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }

        if (_window is not null)
        {
            _window.PowerSettingChanged -= OnPowerSettingChanged;
            _window.SuspendRequested -= OnSuspendRequested;
            _window.Dispose();
            _window = null;
        }
    }

    /// <summary>Brings the displays back out of DPMS standby. Any monitor whose CCD path we deactivated stays dark.</summary>
    public void ForceDisplaysOn()
    {
        // Our own blank/unblank shouldn't look like another button press. One second is enough now:
        // the echo a switch produces a second or two later is caught by the stable-on rule below,
        // which is the real defence - this window is just for the unblank we trigger ourselves. (It
        // was raised to four seconds when a duplicate-mode switch echoed late enough to slip past a
        // shorter window and start a flicker loop; extend does not echo like that.)
        _suppressUntil = DateTime.UtcNow.AddSeconds(1);
        Power.SendMessageTimeout(Power.HWND_BROADCAST, Power.WM_SYSCOMMAND, Power.SC_MONITORPOWER, Power.MONITOR_ON,
            Power.SMTO_ABORTIFHUNG, 1000, out _);

        NudgeInput();
    }

    /// <summary>
    /// Moves the pointer one pixel and back.
    ///
    /// The broadcast above is not enough on its own: SC_MONITORPOWER is documented as unsupported on
    /// Modern Standby systems, and on this machine the panels stayed dark for ten to eighteen seconds
    /// after a press while it was the only wake attempt - which is the "switching keeps leaving me on
    /// a black screen" complaint. An input event, however, is a wake source the platform always
    /// honours, and a one-pixel move is not enough to disturb anything.
    /// </summary>
    private static void NudgeInput()
    {
        var moves = new[]
        {
            new Power.INPUT
            {
                type = Power.INPUT_MOUSE,
                mi = new Power.MOUSEINPUT { dx = 1, dy = 0, dwFlags = Power.MOUSEEVENTF_MOVE },
            },
            new Power.INPUT
            {
                type = Power.INPUT_MOUSE,
                mi = new Power.MOUSEINPUT { dx = -1, dy = 0, dwFlags = Power.MOUSEEVENTF_MOVE },
            },
        };

        uint sent = Power.SendInput((uint)moves.Length, moves, Marshal.SizeOf<Power.INPUT>());
        DebugLog.Write($"display wake: broadcast sent, input nudge {sent}/{moves.Length}");
    }

    /// <summary>
    /// Answers a suspend request. On a Modern Standby machine this never fires for a power-button
    /// press - no PBT_APMQUERYSUSPEND is broadcast at all - which is why the press is read from the
    /// standby entry's reason code instead. On platforms that do ask first (classic S3 machines) the
    /// refusal is still what keeps the machine awake, so the takeover keeps working there too.
    /// </summary>
    /// <returns>True to refuse the suspend.</returns>
    private bool OnSuspendRequested()
    {
        if (!_displayIsOn)
        {
            DebugLog.Write("suspend allowed: displays are already off, looks like the idle timeout");
            return false;
        }

        if (DateTime.UtcNow - _lastReportedOn < MinStableOnTime)
        {
            DebugLog.Write("suspend allowed: displays only just came on, part of the same transition");
            return false;
        }

        DebugLog.Write("suspend refused: displays are on and stable, treating as a power button press");
        return true;
    }

    private void OnPowerSettingChanged(Guid powerSetting, int value)
    {
        if (powerSetting != Power.GUID_CONSOLE_DISPLAY_STATE)
            return;

        _displayIsOn = value != Power.DISPLAY_STATE_OFF;

        DebugLog.Write($"display state notification: value={value} (0=off 1=on 2=dimmed), idle={GetIdleTime().TotalSeconds:F1}s");

        if (value != Power.DISPLAY_STATE_OFF)
        {
            _lastReportedOn = DateTime.UtcNow;
            return;
        }

        // Informational only - the display going dark is deliberately *not* read as a press any more.
        // The same notification covers the idle timeout, a driver reset and our own layout switch,
        // and treating those as presses is what made the panels flip on their own. Presses come from
        // the standby entry's reason code instead; see PowerEventLoop.
        DebugLog.Write("  -> not a press: presses are reported by Kernel-Power 506 reason=1");
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new Power.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Power.LASTINPUTINFO>() };
        if (!Power.GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Reads the "require a password on wakeup" (CONSOLELOCK) AC/DC values, or null on failure.</summary>
    public (uint Ac, uint Dc)? ReadConsoleLock()
    {
        if (!TryGetActiveScheme(out var scheme))
            return null;

        var subgroup = Power.GUID_NONE_SUBGROUP;
        var setting = Power.GUID_CONSOLE_LOCK;

        if (Power.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint ac) != 0)
            return null;
        if (Power.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint dc) != 0)
            return null;

        return (ac, dc);
    }

    /// <summary>
    /// Writes the "require a password on wakeup" setting. 0 makes Windows resume straight to the
    /// desktop, which is what stops a power-button press from ending on the sign-in screen.
    /// </summary>
    public bool WriteConsoleLock(uint ac, uint dc, out string error)
    {
        error = string.Empty;

        if (!TryGetActiveScheme(out var scheme))
        {
            error = "无法读取当前电源方案。";
            return false;
        }

        var subgroup = Power.GUID_NONE_SUBGROUP;
        var setting = Power.GUID_CONSOLE_LOCK;

        uint rc = Power.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, ac);
        if (rc == 0)
            rc = Power.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dc);
        if (rc == 0)
            Power.PowerSetActiveScheme(IntPtr.Zero, ref scheme);

        if (rc != 0)
        {
            error = $"设置「唤醒时需要密码」失败,错误码 {rc}。";
            return false;
        }

        return true;
    }

    private void StartPowerEventWatcher()
    {
        if (_powerEventThread is not null)
            return;

        _lastPowerEventCheckUtc = DateTime.UtcNow;
        _powerEventStop = new ManualResetEvent(false);
        _powerEventThread = new Thread(PowerEventLoop)
        {
            IsBackground = true,
            Name = "power-button-events",
        };
        _powerEventThread.Start();
        DebugLog.Write("power button watcher started: polling the System log for Kernel-Power 506 with reason=1");
    }

    private void StopPowerEventWatcher()
    {
        _powerEventStop?.Set();
        _powerEventThread = null;
        _powerEventStop = null;
    }

    /// <summary>
    /// Polls the System event log for the standby entry that names the power button as its cause.
    ///
    /// Polling rather than an EventLogWatcher subscription: a one-second poll is imperceptible next to
    /// the standby round trip it is reporting on, and it keeps the whole thing on a thread this class
    /// owns and can stop cleanly.
    /// </summary>
    private void PowerEventLoop()
    {
        while (!_powerEventStop!.WaitOne(1000))
        {
            try
            {
                // Two seconds of overlap so an entry written between polls is never missed; the
                // record id below keeps the overlap from being handled twice.
                var since = _lastPowerEventCheckUtc.AddSeconds(-2);
                _lastPowerEventCheckUtc = DateTime.UtcNow;

                var xpath = "*[System[(EventID=506) and TimeCreated[@SystemTime>='" + since.ToString("o") +
                            "'] and Provider[@Name='Microsoft-Windows-Kernel-Power']]]";

                using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
                for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                {
                    using (record)
                        HandlePowerEvent(record);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"power event poll failed: {ex.Message}");
            }
        }
    }

    private void HandlePowerEvent(EventRecord record)
    {
        if (record.RecordId is long id && id <= _lastHandledRecordId)
            return;

        if (record.RecordId is long newId)
            _lastHandledRecordId = newId;

        int reason = -1;
        try
        {
            var doc = XDocument.Parse(record.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var data = doc.Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == "Reason");
            if (data is not null)
                int.TryParse(data.Value, out reason);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Kernel-Power {record.Id}: could not read Reason: {ex.Message}");
        }

        if (reason != KernelPowerReasonPowerButton)
        {
            DebugLog.Write($"Kernel-Power {record.Id}: reason={reason} (not the power button) - ignored");
            return;
        }

        if (DateTime.UtcNow - _lastAcceptedPress < PressDebounce)
        {
            DebugLog.Write("Kernel-Power 506 reason=1: debounced, too soon after the last press");
            return;
        }

        _lastAcceptedPress = DateTime.UtcNow;
        DebugLog.Write("power button press: Kernel-Power 506 reported reason=1 (Power Button)");

        if (_uiContext is not null)
            _uiContext.Post(_ => PowerButtonPressed?.Invoke(), null);
        else
            PowerButtonPressed?.Invoke();
    }

    private static bool TryWriteAction(uint ac, uint dc, out string error)
    {
        error = string.Empty;

        if (!TryGetActiveScheme(out var scheme))
        {
            error = "无法读取当前电源方案。";
            return false;
        }

        var subgroup = Power.GUID_BUTTONS_SUBGROUP;
        var setting = Power.GUID_POWERBUTTON_ACTION;

        uint rc = Power.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, ac);
        if (rc != 0)
        {
            error = $"设置电源按钮动作失败,错误码 {rc}。";
            return false;
        }

        rc = Power.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dc);
        if (rc != 0)
        {
            error = $"设置电源按钮动作失败,错误码 {rc}。";
            return false;
        }

        Power.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        return true;
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;

        if (Power.PowerGetActiveScheme(IntPtr.Zero, out IntPtr schemePtr) != 0 || schemePtr == IntPtr.Zero)
            return false;

        scheme = Marshal.PtrToStructure<Guid>(schemePtr);
        Power.LocalFree(schemePtr);
        return true;
    }

    public void Dispose() => StopListening();

    /// <summary>
    /// Invisible window used solely to receive WM_POWERBROADCAST.
    ///
    /// Deliberately a hidden *top-level* window rather than a message-only one: suspend queries are
    /// delivered by broadcasting to top-level windows, and message-only windows are skipped by
    /// broadcasts - a message-only window would silently never see the very query it exists to
    /// refuse. Power-setting notifications are addressed straight at the handle, so those worked
    /// either way and masked the problem.
    /// </summary>
    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public event Action<Guid, int>? PowerSettingChanged;

        /// <summary>Raised for a suspend request; the handler returns true to refuse it.</summary>
        public event Func<bool>? SuspendRequested;

        public MessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "MobDisplayController power notifications",
                // No WS_VISIBLE - the window exists only to be a broadcast target. WS_EX_TOOLWINDOW
                // keeps it out of the taskbar and Alt+Tab.
                Style = 0,
                ExStyle = WS_EX_TOOLWINDOW,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Power.WM_POWERBROADCAST)
            {
                switch (m.WParam.ToInt32())
                {
                    case Power.PBT_POWERSETTINGCHANGE when m.LParam != IntPtr.Zero:
                        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                        PowerSettingChanged?.Invoke(setting.PowerSetting, setting.Data);
                        break;

                    case Power.PBT_APMQUERYSUSPEND:
                        if (SuspendRequested?.Invoke() == true)
                        {
                            m.Result = Power.BROADCAST_QUERY_DENY;
                            return; // the veto *is* the answer, so don't hand it to the default proc
                        }
                        break;

                    case Power.PBT_APMQUERYSUSPENDFAILED:
                        DebugLog.Write("suspend refused (by us or by another app)");
                        break;

                    case Power.PBT_APMSUSPEND:
                        DebugLog.Write("suspend committed - the machine is going to sleep");
                        break;
                }
            }

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
