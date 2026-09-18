using System.Runtime.InteropServices;
using System.Windows.Forms;
using MobDisplayController.Native;

namespace MobDisplayController.Services;

/// <summary>
/// Makes the physical power button turn off only the built-in panel, leaving the
/// external monitor lit.
///
/// Windows' own "turn off display" action is a single all-displays DPMS signal with no
/// per-monitor granularity, and the power button is an ACPI fixed button that never
/// surfaces as HID input, so the press itself can't be captured directly. Instead the
/// button keeps its native "turn off display" action and we react to the resulting
/// GUID_CONSOLE_DISPLAY_STATE change - a real state-change notification that fires on
/// every press, unlike GUID_POWERBUTTON_ACTION, which only reports setting-value edits.
///
/// Keeping the native action also means that if this app isn't running, or misses an
/// event, the button still behaves normally (press to blank, press again to wake)
/// rather than becoming completely dead.
/// </summary>
public sealed class PowerButtonService : IDisposable
{
    /// <summary>Raised when the displays were just turned off by a deliberate user action (rather than an idle timeout).</summary>
    public event Action? DisplayTurnedOff;

    private MessageWindow? _window;
    private IntPtr _notificationHandle;

    /// <summary>Set while we're driving the display state ourselves, so our own changes don't re-enter the handler.</summary>
    private DateTime _suppressUntil = DateTime.MinValue;

    /// <summary>Guards against the display state flapping - blanking and unblanking can race and produce a burst of events.</summary>
    private DateTime _lastAcceptedPress = DateTime.MinValue;
    private static readonly TimeSpan PressDebounce = TimeSpan.FromSeconds(2);

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

    /// <summary>Points the power button at Windows' native "turn off display" action and starts watching the display state.</summary>
    public bool TryEnable(out string error)
    {
        if (!TryWriteAction(Power.PBUTTON_TURN_OFF_DISPLAY, Power.PBUTTON_TURN_OFF_DISPLAY, out error))
            return false;

        StartListening();
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

        _window = new MessageWindow();
        _window.PowerSettingChanged += OnPowerSettingChanged;

        var guid = Power.GUID_CONSOLE_DISPLAY_STATE;
        _notificationHandle = Power.RegisterPowerSettingNotification(_window.Handle, ref guid, Power.DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public void StopListening()
    {
        if (_notificationHandle != IntPtr.Zero)
        {
            Power.UnregisterPowerSettingNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }

        if (_window is not null)
        {
            _window.PowerSettingChanged -= OnPowerSettingChanged;
            _window.Dispose();
            _window = null;
        }
    }

    /// <summary>Brings the displays back out of DPMS standby. Any monitor whose CCD path we deactivated stays dark.</summary>
    public void ForceDisplaysOn()
    {
        // Our own blank/unblank shouldn't look like another button press.
        _suppressUntil = DateTime.UtcNow.AddSeconds(3);
        Power.SendMessage(Power.HWND_BROADCAST, Power.WM_SYSCOMMAND, Power.SC_MONITORPOWER, Power.MONITOR_ON);
    }

    private void OnPowerSettingChanged(Guid powerSetting, int value)
    {
        if (powerSetting != Power.GUID_CONSOLE_DISPLAY_STATE)
            return;

        DebugLog.Write($"display state notification: value={value} (0=off 1=on 2=dimmed), idle={GetIdleTime().TotalSeconds:F1}s");

        if (value != Power.DISPLAY_STATE_OFF)
            return;

        if (DateTime.UtcNow < _suppressUntil)
        {
            DebugLog.Write("  -> ignored: within self-inflicted suppression window");
            return;
        }

        // The same notification fires for the idle screen-off timeout, which shouldn't flip
        // the displays around. Someone who just pressed the power button was almost certainly
        // touching the device moments ago, whereas an idle blank needs minutes of no input.
        if (GetIdleTime() > TimeSpan.FromSeconds(60))
        {
            DebugLog.Write("  -> ignored: looks like an idle timeout, not a button press");
            return;
        }

        if (DateTime.UtcNow - _lastAcceptedPress < PressDebounce)
        {
            DebugLog.Write("  -> ignored: debounced, too soon after the last press");
            return;
        }

        _lastAcceptedPress = DateTime.UtcNow;
        DebugLog.Write("  -> treating as power button press");
        DisplayTurnedOff?.Invoke();
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new Power.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Power.LASTINPUTINFO>() };
        if (!Power.GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
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

    /// <summary>Invisible message-only window used solely to receive WM_POWERBROADCAST.</summary>
    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        private const int HWND_MESSAGE = -3;

        public event Action<Guid, int>? PowerSettingChanged;

        public MessageWindow()
        {
            CreateHandle(new CreateParams { Parent = (IntPtr)HWND_MESSAGE });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Power.WM_POWERBROADCAST && m.WParam.ToInt32() == Power.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                PowerSettingChanged?.Invoke(setting.PowerSetting, setting.Data);
            }

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
