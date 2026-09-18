using System.Runtime.InteropServices;
using System.Windows.Forms;
using MobDisplayController.Native;

namespace MobDisplayController.Services;

/// <summary>
/// Lets the app take over the physical power button so pressing it switches to
/// "external screen only" instead of Windows' native "turn off all displays" DPMS
/// action, which has no per-monitor granularity. This works by pointing the active
/// power scheme's button action at "do nothing" and reacting to the resulting
/// power-setting notification ourselves.
/// </summary>
public sealed class PowerButtonService : IDisposable
{
    public event Action? PowerButtonPressed;

    private MessageWindow? _window;
    private IntPtr _notificationHandle;

    public bool IsListening => _window is not null;

    /// <summary>Reads the active power scheme's current AC/DC power button action (0=do nothing, 1=sleep, 2=hibernate, 3=shut down), or null on failure.</summary>
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

    /// <summary>Points the power button action at "do nothing" (both AC and DC) and starts listening for the physical press.</summary>
    public bool TryEnable(out string error)
    {
        error = string.Empty;

        if (!TryGetActiveScheme(out var scheme))
        {
            error = "无法读取当前电源方案。";
            return false;
        }

        var subgroup = Power.GUID_BUTTONS_SUBGROUP;
        var setting = Power.GUID_POWERBUTTON_ACTION;

        uint rc = Power.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, 0);
        if (rc != 0)
        {
            error = $"设置电源按钮动作失败,错误码 {rc}。";
            return false;
        }

        rc = Power.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, 0);
        if (rc != 0)
        {
            error = $"设置电源按钮动作失败,错误码 {rc}。";
            return false;
        }

        Power.PowerSetActiveScheme(IntPtr.Zero, ref scheme);

        StartListening();
        return true;
    }

    /// <summary>Restores the power button action to the given values and stops listening.</summary>
    public void Disable(uint? restoreAc, uint? restoreDc)
    {
        StopListening();

        if (restoreAc is null && restoreDc is null)
            return;

        if (!TryGetActiveScheme(out var scheme))
            return;

        var subgroup = Power.GUID_BUTTONS_SUBGROUP;
        var setting = Power.GUID_POWERBUTTON_ACTION;

        if (restoreAc is not null)
            Power.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, restoreAc.Value);
        if (restoreDc is not null)
            Power.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, restoreDc.Value);

        Power.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
    }

    public void StartListening()
    {
        if (_window is not null)
            return;

        _window = new MessageWindow();
        _window.PowerSettingChanged += OnPowerSettingChanged;

        var guid = Power.GUID_POWERBUTTON_ACTION;
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

    private void OnPowerSettingChanged(Guid powerSetting)
    {
        if (powerSetting == Power.GUID_POWERBUTTON_ACTION)
            PowerButtonPressed?.Invoke();
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

        public event Action<Guid>? PowerSettingChanged;

        public MessageWindow()
        {
            CreateHandle(new CreateParams { Parent = (IntPtr)HWND_MESSAGE });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Power.WM_POWERBROADCAST && m.WParam.ToInt32() == Power.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                PowerSettingChanged?.Invoke(setting.PowerSetting);
            }

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
