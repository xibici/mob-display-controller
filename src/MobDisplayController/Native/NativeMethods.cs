using System.Runtime.InteropServices;
using System.Text;

namespace MobDisplayController.Native;

#region CCD (Connecting and Configuring Displays) API

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

internal enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
{
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER = 0xFFFFFFFF,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HD15 = 0,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_SVIDEO = 1,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_COMPOSITE_VIDEO = 2,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_COMPONENT_VIDEO = 3,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI = 4,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI = 5,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_D_JPN = 8,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_SDI = 9,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL = 10,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL = 12,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_SDTVDONGLE = 14,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST = 15,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED = 16,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL = 17,
    DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000,
}

public enum DISPLAYCONFIG_ROTATION : uint
{
    DISPLAYCONFIG_ROTATION_IDENTITY = 1,
    DISPLAYCONFIG_ROTATION_ROTATE90 = 2,
    DISPLAYCONFIG_ROTATION_ROTATE180 = 3,
    DISPLAYCONFIG_ROTATION_ROTATE270 = 4,
}

internal enum DISPLAYCONFIG_SCALING : uint
{
    DISPLAYCONFIG_SCALING_IDENTITY = 1,
    DISPLAYCONFIG_SCALING_CENTERED = 2,
    DISPLAYCONFIG_SCALING_STRETCHED = 3,
    DISPLAYCONFIG_SCALING_ASPECTRATIOCENTEREDMAX = 4,
    DISPLAYCONFIG_SCALING_CUSTOM = 5,
    DISPLAYCONFIG_SCALING_PREFERRED = 128,
}

internal enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
{
    DISPLAYCONFIG_SCANLINE_ORDERING_UNSPECIFIED = 0,
    DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE = 1,
    DISPLAYCONFIG_SCANLINE_ORDERING_INTERLACED = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
    public DISPLAYCONFIG_ROTATION rotation;
    public DISPLAYCONFIG_SCALING scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
    [MarshalAs(UnmanagedType.Bool)]
    public bool targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

/// <summary>
/// Opaque placeholder matching the native 64-byte size of DISPLAYCONFIG_MODE_INFO.
/// We never need to interpret its contents: we only read it back from
/// QueryDisplayConfig and pass it through unchanged to SetDisplayConfig.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct DISPLAYCONFIG_MODE_INFO
{
}

internal enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
{
    DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1,
    DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2,
    DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3,
    DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4,
    DISPLAYCONFIG_DEVICE_INFO_SET_TARGET_PERSISTENCE = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS
{
    public uint value;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}

internal static class Ccd
{
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;

    public const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    public const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    public const uint SDC_TOPOLOGY_SUPPLIED = 0x00000010;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_VALIDATE = 0x00000040;
    public const uint SDC_APPLY = 0x00000080;
    public const uint SDC_NO_OPTIMIZATION = 0x00000100;
    public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    public const uint SDC_ALLOW_CHANGES = 0x00000400;
    public const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;
    public const uint SDC_FORCE_MODE_ENUMERATION = 0x00001000;
    public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;

    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NOT_SUPPORTED = 50;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
}

#endregion

#region Classic GDI display API (read only: EnumDisplayDevices / EnumDisplaySettingsEx)

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAY_DEVICE
{
    [MarshalAs(UnmanagedType.U4)]
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceString;
    [MarshalAs(UnmanagedType.U4)]
    public DisplayDeviceStateFlags StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceKey;
}

[Flags]
internal enum DisplayDeviceStateFlags : int
{
    AttachedToDesktop = 0x1,
    MultiDriver = 0x2,
    PrimaryDevice = 0x4,
    MirroringDriver = 0x8,
    VGACompatible = 0x10,
    Removable = 0x20,
    ModesPruned = 0x8000000,
    Remote = 0x4000000,
    Disconnect = 0x2000000,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DEVMODE
{
    private const int CCHDEVICENAME = 32;
    private const int CCHFORMNAME = 32;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
    public string dmDeviceName;
    public short dmSpecVersion;
    public short dmDriverVersion;
    public short dmSize;
    public short dmDriverExtra;
    public int dmFields;

    public int dmPositionX;
    public int dmPositionY;
    public int dmDisplayOrientation;
    public int dmDisplayFixedOutput;

    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
    public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel;
    public int dmPelsWidth;
    public int dmPelsHeight;
    public int dmDisplayFlags;
    public int dmDisplayFrequency;
    public int dmICMMethod;
    public int dmICMIntent;
    public int dmMediaType;
    public int dmDitherType;
    public int dmReserved1;
    public int dmReserved2;
    public int dmPanningWidth;
    public int dmPanningHeight;
}

internal static class Gdi
{
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsEx(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode,
        uint dwFlags);
}

#endregion

#region Monitor enumeration + DDC/CI (brightness / volume)

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

internal delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

internal static class MonitorApi
{
    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, IntPtr pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    // VESA MCCS VCP codes
    public const byte VCP_BRIGHTNESS = 0x10;
    public const byte VCP_CONTRAST = 0x12;
    public const byte VCP_AUDIO_VOLUME = 0x62;
    public const byte VCP_POWER_MODE = 0xD6;
}

#endregion

#region Power button device (ACPI fixed button + the ioctl Windows uses to poll it)

/// <summary>
/// The ACPI power button device and the request Windows itself sends to it to find out whether the
/// button was pressed.
///
/// User mode never gets a non-zero answer from this ioctl - measured: 13276 polls across a real
/// press, every one returning 0 - while the system's own request is the one that is answered. That
/// is why the filter driver (btndrv) sits above this device and reads the *completion* of the
/// system's request. This type exists so the app can (a) check the device is there and (b) issue one
/// request at startup, which is what makes the driver create its named event.
/// </summary>
internal static class ButtonDevice
{
    /// <summary>\\?\ACPI#PNP0C0C#&lt;instance&gt;#{4afa3d53-74a7-11d0-be5e-00a0c9062857} - GUID_DEVICE_SYS_BUTTON.</summary>
    public const string InterfacePath =
        @"\\?\ACPI#PNP0C0C#2&daba3ff&1#{4afa3d53-74a7-11d0-be5e-00a0c9062857}";

    /// <summary>IOCTL_GET_SYS_BUTTON_EVENT = CTL_CODE(FILE_DEVICE_BATTERY 0x29, 0x51, METHOD_BUFFERED, FILE_READ_ACCESS).</summary>
    public const uint IoctlGetSysButtonEvent = 0x00294144;

    /// <summary>SYS_BUTTON_POWER: the bit the answer carries when the power button was the cause.</summary>
    public const uint SysButtonPower = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr securityAttributes,
                                             uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuffer, uint inSize,
                                               IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Opens the button device and issues one button-event request. True when the device answered -
    /// the answer itself is always "no event" from here, which is the point: the driver needs the
    /// request to exist so it can hook its completion.
    /// </summary>
    public static bool Poke(out string detail)
    {
        detail = string.Empty;

        IntPtr handle = CreateFileW(InterfacePath, 0x80000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
        {
            detail = $"CreateFile failed, err={Marshal.GetLastWin32Error()}";
            return false;
        }

        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            bool ok = DeviceIoControl(handle, IoctlGetSysButtonEvent, IntPtr.Zero, 0, buffer, 4, out uint returned, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            uint value = unchecked((uint)Marshal.ReadInt32(buffer));

            detail = ok
                ? $"ok, returned={returned}, value=0x{value:X8}, err={error}"
                : $"ioctl failed, err={error}";

            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }
}

#endregion

#region Power button takeover (RegisterPowerSettingNotification + power scheme APIs)

[StructLayout(LayoutKind.Sequential)]
internal struct POWERBROADCAST_SETTING
{
    public Guid PowerSetting;
    public uint DataLength;
    public byte Data;
}

internal static class Power
{
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    /// <summary>
    /// Permission request sent (as a broadcast every top-level window sees) *before* the machine
    /// suspends. Returning <see cref="BROADCAST_QUERY_DENY"/> cancels the transition - including the
    /// display blank that would otherwise come with it. This is what lets the takeover notice a
    /// power-button press without the machine ever going to sleep.
    /// </summary>
    public const int PBT_APMQUERYSUSPEND = 0x0000;

    /// <summary>Sent when a suspend request was refused, by us or by another app.</summary>
    public const int PBT_APMQUERYSUSPENDFAILED = 0x0002;

    /// <summary>Sent once the suspend is already committed and can no longer be stopped.</summary>
    public const int PBT_APMSUSPEND = 0x0004;

    /// <summary>
    /// Veto value for a suspend request: "BMQD" in ASCII, straight out of WinUser.h
    /// (`#define BROADCAST_QUERY_DENY 0x424D5144`). Not 0xFFFFFFFF, despite what the older
    /// documentation snippets suggest.</summary>
    public static readonly IntPtr BROADCAST_QUERY_DENY = new(0x424D5144);

    /// <summary>SUB_BUTTONS: the "power and sleep buttons and lid" power settings subgroup.</summary>
    public static readonly Guid GUID_BUTTONS_SUBGROUP = new("4f971e89-eebd-4455-a8de-9e59040e7347");

    /// <summary>PBUTTONACTION: what pressing the physical power button does.</summary>
    public static readonly Guid GUID_POWERBUTTON_ACTION = new("7648efa3-dd9c-4e3e-b566-50f929386280");

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: fires with 0 = off, 1 = on, 2 = dimmed whenever the console display's power state actually changes.</summary>
    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    /// <summary>SUB_NONE: the subgroup that holds the "require a password on wakeup" setting.</summary>
    public static readonly Guid GUID_NONE_SUBGROUP = new("fea3413e-7e05-4911-9a71-700331f1c294");

    /// <summary>CONSOLELOCK ("require a password on wakeup"): 0 lets Windows resume straight to the desktop,
    /// which is what keeps a power-button press from ending on the sign-in screen.</summary>
    public static readonly Guid GUID_CONSOLE_LOCK = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");

    // PBUTTONACTION value indices, as enumerated by "powercfg /q SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION".
    public const uint PBUTTON_DO_NOTHING = 0;
    public const uint PBUTTON_SLEEP = 1;
    public const uint PBUTTON_HIBERNATE = 2;
    public const uint PBUTTON_SHUTDOWN = 3;
    public const uint PBUTTON_TURN_OFF_DISPLAY = 4;

    public const int DISPLAY_STATE_OFF = 0;
    public const int DISPLAY_STATE_ON = 1;
    public const int DISPLAY_STATE_DIMMED = 2;

    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_MONITORPOWER = 0xF170;
    public const int MONITOR_ON = -1;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Declared with the mouse member only, which is the largest member of the native union, so the
    /// struct size matches the native INPUT (40 bytes on x64).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    /// <summary>
    /// Injects input events. Used to wake the panels: a synthetic input event is a wake source the
    /// platform always honours, unlike SC_MONITORPOWER, which is documented as unsupported on Modern
    /// Standby systems - and empirically left the panels dark for ten to eighteen seconds after a
    /// press on this machine.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Always use the timeout form for HWND_BROADCAST: plain SendMessage waits for every
    /// top-level window in the session, so one hung window blocks the caller indefinitely -
    /// and this gets called from the UI thread, which would take the tray app down with it.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>See https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate </summary>
    public const uint ES_CONTINUOUS = 0x80000000;

    /// <summary>Keeps the system in the working state (no automatic sleep). Does not keep the display on.</summary>
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;

    /// <summary>
    /// Asks for "away mode" instead of sleep: the system keeps running while the display is off,
    /// which is exactly the shape of a power-button takeover. Ignored on platforms without BIOS
    /// away-mode support, in which case ES_SYSTEM_REQUIRED has to carry it.
    /// </summary>
    public const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    /// <summary>
    /// Per-thread: the request belongs to the thread that set it and dies with it, so this must be
    /// called from - and cleared on - the same (UI) thread.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);
}

internal static class Hotkeys
{
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Keeps the tray menu above the taskbar for as long as it is open.
///
/// A drop-down raises itself when it is shown, and that is normally enough. Not here: the pointer is
/// on the taskbar when the tray icon is clicked, and the taskbar - like the icon-overflow flyout a
/// tray icon may live in - is a topmost window that raises itself again on hover and on click, i.e.
/// after the menu has been raised. Repeating the raise is harmless: putting a window that is already
/// at the top of the topmost band there again changes nothing.
/// </summary>
internal static class WindowZOrder
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// Whether a window is really on screen, as opposed to what WinForms believes about its own drop-down.
    ///
    /// That distinction is the whole point: a drop-down WinForms counts as open is never shown again, so if the
    /// window behind it is gone the tray icon goes dead - every later click is silently ignored.
    /// </summary>
    public static bool IsReallyVisible(IntPtr handle)
        => handle != IntPtr.Zero && IsWindowVisible(handle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>
    /// Not used: taking the mouse capture for the menu was tried and made things worse - a drop-down that holds
    /// the capture refuses to hide, so the menu could not be dismissed at all (no close worked, and nothing was
    /// even logged, because the drop-down never became invisible). Kept here with its result recorded so the idea
    /// is not tried again; what actually keeps the menu alive while the shell's panel holds the activation is
    /// AutoClose = false in TopMostMenu.
    /// </summary>
    public static void TakeMouseCapture(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            SetCapture(handle);
    }

    public static void ReleaseMouseCapture()
        => ReleaseCapture();

    /// <summary>
    /// Takes the foreground with one of this process's windows, so a menu can be shown the way the shell shows
    /// its own. Windows is allowed to refuse (the foreground lock); the caller carries on either way.
    /// </summary>
    public static void TakeForeground(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    /// <summary>
    /// Puts a window at the top of the topmost band without moving, resizing or activating it.
    /// </summary>
    public static void RaiseToTopMost(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>
    /// Where the taskbar currently is. The rectangle comes back in the same (physical) coordinates as
    /// the window bounds this app works with - the app is PerMonitorV2, so nothing is virtualised here.
    /// <c>hWnd</c> is deliberately not used: the shell reports it as null on this machine while the
    /// rectangle and edge are still correct.
    /// </summary>
    public static bool TryGetTaskbarRect(out Rectangle rect, out TaskbarEdge edge)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };

        rect = Rectangle.Empty;
        edge = TaskbarEdge.Bottom;

        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        if (data.rc.Right <= data.rc.Left || data.rc.Bottom <= data.rc.Top)
            return false;

        rect = Rectangle.FromLTRB(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
        edge = (TaskbarEdge)data.uEdge;
        return true;
    }
}

/// <summary>
/// Tells whether one of the shell's own panels - the Start menu, Search, the Action Center, the touch
/// keyboard - is in the foreground, i.e. whether it is the panel the user is looking at.
///
/// This matters because those panels are XAML island windows that are drawn above a foreign topmost
/// window: a tray menu opened while one of them is still up ends up underneath it, even though the menu
/// is topmost, sits above the taskbar and reports itself as the window at its own centre. The shell's own
/// tray menu never has the problem, because Explorer shows it only once the Start menu has closed.
///
/// The panel cannot be found by enumerating windows - EnumWindows does not list it on this build - but it
/// is always the foreground window while it is up, and it is a Windows.UI.Core.CoreWindow, so the
/// foreground window is what this looks at. The class alone is not enough: a UWP app (Settings, Store,
/// Photos) uses the same class, so the process has to be one of the shell's hosts as well.
/// </summary>
internal static class ShellPanels
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly string[] s_hostProcesses =
    {
        "SearchHost",
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "TextInputHost",
    };

    public static bool IsForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        return IsPanelWindow(foreground);
    }

    /// <summary>
    /// The shell's panel as it is *on screen*, found by asking what is under a handful of points, or false when
    /// there is none.
    ///
    /// This exists because "in the foreground" is not enough: the Start menu can be opening, or open with the
    /// foreground still somewhere else (measured: the panel hit-tests at 950,1100 while the foreground was
    /// SearchHost and the *focused* window was a different shell CoreWindow). A menu shown in that window of time
    /// is visible but unclickable, because the panel's dismissal takes the click - so the panel's presence has to
    /// be detectable without the foreground. A few points are enough: the panel is hit-testable while it is up
    /// (that is how its rectangle was measured in the first place), and the sample points sit where the Start
    /// menu, Search and the Action Center are but the volume/brightness flyouts are not.
    /// </summary>
    public static bool TryFindOnScreenPanel(out IntPtr panelWindow)
        => TryFindOnScreenPanel(out panelWindow, out _);

    /// <summary>
    /// As <see cref="TryFindOnScreenPanel(out IntPtr)"/> and also says which host it matched, for the log: a
    /// detection that is wrong needs to name the window it saw to be diagnosable.
    /// </summary>
    public static bool TryFindOnScreenPanel(out IntPtr panelWindow, out string host)
        => TryFindPanel(out panelWindow, out host, allowCloaked: false);

    /// <summary>
    /// The same test, but with DWM-cloaked windows counted as well: "a shell panel window is stacked over this
    /// point", whether or not it is being drawn.
    ///
    /// This exists for the log. A cloaked window is invisible and must not hold the menu back - but it is also
    /// exactly what used to hold it back (measured 2026-09-26: all eight sample points hit one cloaked 1920x1128
    /// StartMenuExperienceHost CoreWindow while the user saw no Start menu anywhere), and a fix that ignores
    /// something needs a way to show that the something is still there.
    /// </summary>
    public static bool TryFindAnyShellPanel(out IntPtr panelWindow, out string host)
        => TryFindPanel(out panelWindow, out host, allowCloaked: true);

    private static bool TryFindPanel(out IntPtr panelWindow, out string host, bool allowCloaked)
    {
        panelWindow = IntPtr.Zero;
        host = string.Empty;

        var screen = Screen.FromPoint(Cursor.Position).Bounds;

        foreach (var (fractionX, fractionY) in s_sampleFractions)
        {
            var point = new Point(
                screen.Left + (int)(screen.Width * fractionX),
                screen.Top + (int)(screen.Height * fractionY));

            var window = WindowFromPoint(point);
            if (window == IntPtr.Zero || !IsPanelWindow(window, out var owner, allowCloaked))
                continue;

            panelWindow = window;
            host = owner;
            return true;
        }

        return false;
    }

    public static bool IsOnScreen()
        => TryFindOnScreenPanel(out _);

    /// <summary>
    /// What the presence test sees, as text for the log: the foreground window and what every sample point hits.
    ///
    /// Added 2026-09-26 because "yes, a panel" is not diagnosable on its own: the tray menu then stalled three
    /// seconds at a time on the user's desktop, the log named StartMenuExperienceHost, and six Escapes sent at it
    /// changed nothing - and that has to be told apart from a real Start menu. The tray's own flyout is a shell
    /// XAML surface as well, and the user reaches the icon by touch, so naming the window, its process and its
    /// rectangle is the minimum that can settle it.
    /// </summary>
    public static string DescribeOnScreen()
    {
        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        var text = new StringBuilder($"foreground={DescribeWindow(GetForegroundWindow())}; ");

        foreach (var (fractionX, fractionY) in s_sampleFractions)
        {
            var point = new Point(
                screen.Left + (int)(screen.Width * fractionX),
                screen.Top + (int)(screen.Height * fractionY));

            text.Append($"({point.X},{point.Y})={DescribeWindow(WindowFromPoint(point))} ");
        }

        return text.ToString().TrimEnd();
    }

    private static string DescribeWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return "nothing";

        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);

        var host = IsShellHost(window, out var owner) ? owner : "-";
        GetWindowThreadProcessId(window, out var processId);

        GetWindowRect(window, out var rect);

        return $"0x{window.ToInt64():X} '{name}' [{host}] pid={processId} "
             + $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top}@{rect.Left},{rect.Top} "
             + $"visible={(IsWindowVisible(window) ? 1 : 0)} cloaked={(IsCloaked(window) ? 1 : 0)}";
    }

    /// <summary>
    /// Where the Start menu, Search and the Action Center are, as fractions of the screen - and, just as
    /// importantly, where they are not: the volume and brightness flyouts live in the top-left corner on this
    /// device, so nothing samples above 30% of the height.
    /// </summary>
    private static readonly (double X, double Y)[] s_sampleFractions =
    {
        (0.10, 0.35), (0.12, 0.80), (0.30, 0.45), (0.50, 0.50), (0.35, 0.75),
        (0.90, 0.35), (0.90, 0.70), (0.70, 0.50),
    };

    /// <summary>A window that belongs to one of the shell's panels: a XAML CoreWindow of a host process.</summary>
    private static bool IsPanelWindow(IntPtr window)
        => IsPanelWindow(window, out _, allowCloaked: false);

    private static bool IsPanelWindow(IntPtr window, out string host)
        => IsPanelWindow(window, out host, allowCloaked: false);

    private static bool IsPanelWindow(IntPtr window, out string host, bool allowCloaked)
    {
        host = string.Empty;

        var name = new StringBuilder(64);
        if (GetClassName(window, name, name.Capacity) == 0)
            return false;

        if (!name.ToString().Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            return false;

        // A cloaked window is *not drawn* but keeps WS_VISIBLE, and it still answers WindowFromPoint. Measured
        // 2026-09-26: after the user came back from a game, all eight sample points hit one 1920x1128
        // StartMenuExperienceHost CoreWindow while no Start menu was anywhere on screen - so every request waited
        // three seconds for a panel that was not there and then showed the menu into a losing activation fight.
        // That state is the user's "it cannot be summoned". "On screen" has to mean "not cloaked".
        if (!allowCloaked && IsCloaked(window))
            return false;

        return IsShellHost(window, out host);
    }

    /// <summary>
    /// True while DWM is not drawing the window (DWMWA_CLOAKED). The shell leaves its windows up, with their
    /// rectangles and WS_VISIBLE intact, while it is not showing them, so this is the only way to tell "the Start
    /// menu is up" from "the Start menu's window is still around". A window DWM does not know about answers with an
    /// error, which is not a cloaked window.
    /// </summary>
    private static bool IsCloaked(IntPtr window)
    {
        try
        {
            return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsShellHost(IntPtr hWnd)
        => IsShellHost(hWnd, out _);

    private static bool IsShellHost(IntPtr hWnd, out string host)
    {
        host = string.Empty;

        GetWindowThreadProcessId(hWnd, out var processId);
        if (processId == 0)
            return false;

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
            return false;

        try
        {
            var path = new StringBuilder(260);
            var size = path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref size))
                return false;

            var file = Path.GetFileNameWithoutExtension(path.ToString());
            foreach (var candidate in s_hostProcesses)
            {
                if (string.Equals(file, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    host = file;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}

/// <summary>The screen edge an appbar (the taskbar) is docked to, as reported by ABM_GETTASKBARPOS.</summary>
public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>
/// Asks the shell where one of this process's tray icons is.
///
/// This is not decoration: the answer is the only way to tell whether the shell still knows the icon at all.
/// After a fullscreen application has taken and released the screen, the notification area can stop routing
/// clicks to an icon that is still drawn - the icon is then a ghost (it looks right, clicking it does
/// nothing), measured on this machine on 2026-09-26. Shell_NotifyIconGetRect coming back with an error for an
/// icon we did register is that state, and re-adding the icon is what clears it.
/// </summary>
internal static class NotifyIcons
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_DELETE = 0x00000002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

    /// <summary>
    /// Where the shell thinks this process's tray icon is, or null when it does not know of one.
    ///
    /// NotifyIcon does not expose its message window, so it is found the way the shell sees it: every window
    /// of this process is asked for the icon with uID 1, and the one that answers is the icon's window.
    /// </summary>
    public static Rectangle? TryGetIconRect(out IntPtr iconWindow)
    {
        var processId = (uint)Environment.ProcessId;

        // The result is collected through locals rather than the out parameter: a lambda cannot assign to an
        // out parameter of its enclosing method.
        var window = IntPtr.Zero;
        Rectangle? rect = null;

        EnumWindows(
            (candidate, _) =>
            {
                GetWindowThreadProcessId(candidate, out var owner);
                if (owner != processId)
                    return true;

                var className = new StringBuilder(64);
                if (GetClassName(candidate, className, className.Capacity) == 0)
                    return true;

                if (!className.ToString().StartsWith("WindowsForms10", StringComparison.Ordinal))
                    return true;

                var identifier = new NOTIFYICONIDENTIFIER
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                    hWnd = candidate,
                    uID = 1,
                };

                if (Shell_NotifyIconGetRect(ref identifier, out var shellRect) != 0)
                    return true;

                window = candidate;
                rect = Rectangle.FromLTRB(shellRect.Left, shellRect.Top, shellRect.Right, shellRect.Bottom);
                return false;
            },
            IntPtr.Zero);

        iconWindow = window;
        return rect;
    }

    /// <summary>True while the shell has this process's icon.</summary>
    public static bool IsIconRegistered()
        => TryGetIconRect(out _) is not null;
}

/// <summary>
/// Synthetic input, used for one thing: a tray menu request that lands while the shell has a panel of its
/// own up is deferred, and the panel is dismissed with the same key the shell itself uses (see
/// TrayAppContext.OnMenuOpening).
/// </summary>
internal static class Input
{
    private const byte VK_ESCAPE = 0x1B;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Escape, which is how the shell dismisses its own panels (the Start menu, Search).</summary>
    public static void SendEscape()
    {
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>True while a mouse button is held down - the high bit of GetAsyncKeyState means "down now".</summary>
    public static bool IsMouseButtonDown()
        => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
}

/// <summary>
/// Notices mouse clicks anywhere on the desktop, for as long as a menu this app opened by hand is up.
///
/// Such a menu does not get WinForms' click-outside dismissal - it is not the active window and holds no
/// capture, so the click never reaches it - and polling the button state misses short clicks. The shell's own
/// menus use a low-level mouse hook for the same reason.
///
/// What it records is the *point*, not a verdict: the caller compares it against the menu's bounds when it asks.
/// That matters, because the menu is moved after it is shown (KeepMenuOffTaskbar slides it clear of the
/// taskbar) - a snapshot of the bounds taken at show time is the pre-move rectangle, and a click on an item in
/// the moved part of the menu would then be judged as a click outside it. Measured 2026-09-26: the menu closed
/// on mouse-down, before the mouse-up that the item needs, so clicking an entry did nothing at all.
/// </summary>
internal static class MouseWatcher
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // The system keeps a raw pointer to the callback, so it must not be collected while the hook is in place.
    private static HookProc? s_proc;
    private static IntPtr s_hook;
    private static Point s_lastButtonDown;
    private static bool s_sawButtonDown;
    private static bool s_leftButton;

    /// <summary>
    /// Set when the last recorded press came from touch or pen instead of a mouse.
    ///
    /// Windows turns touch into mouse messages, and a low-level mouse hook can tell them apart the documented way:
    /// <c>GetMessageExtraInfo</c> answers with the signature 0xFF515700, bit 0x80 set for touch and 0x40 for pen,
    /// and the hook structure carries that value in dwExtraInfo. The user presses and holds the tray icon with a
    /// finger, so without this a touch, a pen and a mouse press are the same event in the log.
    /// </summary>
    private static bool s_fromTouch;

    public static void Start()
    {
        Stop();

        s_sawButtonDown = false;
        s_proc = Callback;
        s_hook = SetWindowsHookExW(WH_MOUSE_LL, s_proc, IntPtr.Zero, 0);

        Services.DebugLog.Write(s_hook == IntPtr.Zero
            ? $"mouse hook: could not be installed ({Marshal.GetLastWin32Error()}) - clicks outside the menu cannot close it"
            : "mouse hook: installed");
    }

    /// <summary>Whether the hook is in place, so a caller can tell "no click" from "not watching".</summary>
    public static bool IsWatching => s_hook != IntPtr.Zero;

    /// <summary>
    /// Raised as soon as a button goes down anywhere, with where it was and whether it came from a finger or a pen.
    ///
    /// The menu has to close *at once* when the user presses anywhere else, and the poll in OnMenuTick was visibly
    /// late: it runs on a 200 ms timer, so the menu could stay up for a fifth of a second after the press. The
    /// callback already runs on this app's UI thread (the hook is installed without a thread id), so the handler can
    /// act directly - but it must be quick, and it must not uninstall the hook from inside itself.
    /// </summary>
    public static event Action<Point, bool, bool>? ButtonDown;

    public static void Stop()
    {
        if (s_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(s_hook);
            s_hook = IntPtr.Zero;
        }

        s_proc = null;
    }

    /// <summary>
    /// The last button press since the previous call, or false when there has been none.
    ///
    /// One call reports everything and the caller decides what it means: a press outside the menu closes it, a
    /// left press inside it is an entry being used. It has to be one call - two separate questions would compete
    /// for the same event, and whichever ran first would swallow it (which is exactly what happened when this was
    /// TakeClickedOutside plus TakeClickedInside).
    /// </summary>
    public static bool TakeClick(out Point point, out bool leftButton)
        => TakeClick(out point, out leftButton, out _);

    public static bool TakeClick(out Point point, out bool leftButton, out bool fromTouch)
    {
        point = s_lastButtonDown;
        leftButton = s_leftButton;
        fromTouch = s_fromTouch;

        if (!s_sawButtonDown)
            return false;

        s_sawButtonDown = false;
        return true;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            s_lastButtonDown = new Point(data.x, data.y);
            s_sawButtonDown = true;
            s_leftButton = (int)wParam == WM_LBUTTONDOWN;

            var extra = unchecked((uint)data.dwExtraInfo.ToInt64());
            s_fromTouch = (extra & 0xFFFFFF00) == 0xFF515700 && (extra & 0x80) != 0;

            // Windows removes a hook whose callback throws, silently - and then nothing closes the menu at all. The
            // subscribers post their work instead of doing it here (see TrayAppContext.OnAnyButtonDown), so this
            // catch is the last line of defence rather than the plan.
            try
            {
                ButtonDown?.Invoke(s_lastButtonDown, s_leftButton, s_fromTouch);
            }
            catch (Exception exception)
            {
                Services.DebugLog.Write($"mouse hook: a subscriber threw ({exception.GetType().Name}: {exception.Message})");
            }
        }

        return CallNextHookEx(s_hook, nCode, wParam, lParam);
    }
}

/// <summary>
/// Loads a library into another process the ordinary way: the path is written into the target's memory and
/// a thread is started on its LoadLibraryW. That is all the Start-menu hook needs, because both hosts run
/// as this user - the one that runs in an AppContainer is *lower* privileged than this app, not higher,
/// which is precisely why it can be written to.
///
/// The failure that matters cannot be told from the return of CreateRemoteThread: when the target refuses
/// the library (an AppContainer cannot read a file outside its reach) the remote call still succeeds and
/// the exit code is 0. So the exit code is LoadLibraryW's return value and 0 means "the DLL did not load".
/// </summary>
internal static class ProcessInjector
{
    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint LIST_MODULES_ALL = 0x03;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr threadAttributes, IntPtr stackSize,
        IntPtr startAddress, IntPtr parameter, uint creationFlags, IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint size, out uint needed, uint filter);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleBaseNameW(IntPtr process, IntPtr module, StringBuilder name, int count);

    public static bool InjectLibrary(uint processId, string libraryPath, out string error)
    {
        error = string.Empty;

        var process = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
        if (process == IntPtr.Zero)
        {
            error = $"OpenProcess 失败 (错误 {Marshal.GetLastWin32Error()})";
            return false;
        }

        var remote = IntPtr.Zero;
        try
        {
            var path = Encoding.Unicode.GetBytes(libraryPath + "\0");
            remote = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)path.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                error = $"VirtualAllocEx 失败 (错误 {Marshal.GetLastWin32Error()})";
                return false;
            }

            if (!WriteProcessMemory(process, remote, path, (IntPtr)path.Length, out _))
            {
                error = $"WriteProcessMemory 失败 (错误 {Marshal.GetLastWin32Error()})";
                return false;
            }

            var loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            var thread = CreateRemoteThread(process, IntPtr.Zero, IntPtr.Zero, loadLibrary, remote, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                error = $"CreateRemoteThread 失败 (错误 {Marshal.GetLastWin32Error()})";
                return false;
            }

            try
            {
                WaitForSingleObject(thread, 10000);

                if (!GetExitCodeThread(thread, out var exitCode) || exitCode == 0)
                {
                    error = "目标进程没有加载这个 DLL (LoadLibraryW 返回 0)";
                    return false;
                }

                return true;
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            if (remote != IntPtr.Zero)
                VirtualFreeEx(process, remote, IntPtr.Zero, MEM_RELEASE);

            CloseHandle(process);
        }
    }

    /// <summary>
    /// How many modules the target has whose file name starts with <paramref name="moduleNamePrefix"/>.
    /// Null means "could not look", which is different from 0 on purpose: the caller reports that as
    /// unknown rather than as "not injected".
    ///
    /// The count matters as much as the fact: the DLL is deployed under a new name for every build, so a
    /// second injection loads a *second* copy instead of replacing the first, and two copies each save the
    /// original size and then set theirs - which leaves the menu fighting itself and, measured on this
    /// machine, not on screen at all.
    /// </summary>
    public static int? CountLoadedModules(uint processId, string moduleNamePrefix)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, processId);
        if (process == IntPtr.Zero)
            return null;

        try
        {
            var modules = new IntPtr[512];
            if (!EnumProcessModulesEx(process, modules, (uint)(modules.Length * IntPtr.Size), out var needed, LIST_MODULES_ALL))
                return null;

            var count = 0;
            var total = Math.Min((int)(needed / IntPtr.Size), modules.Length);
            for (var i = 0; i < total; i++)
            {
                var name = new StringBuilder(260);
                if (GetModuleBaseNameW(process, modules[i], name, name.Capacity) == 0)
                    continue;

                if (name.ToString().StartsWith(moduleNamePrefix, StringComparison.OrdinalIgnoreCase))
                    count++;
            }

            return count;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}

#endregion
