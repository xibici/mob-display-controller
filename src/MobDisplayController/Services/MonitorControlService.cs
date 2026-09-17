using MobDisplayController.Native;

namespace MobDisplayController.Services;

/// <summary>
/// Controls monitor brightness / volume via DDC/CI (VESA MCCS). This only works
/// when the monitor, cable and GPU driver all support DDC/CI passthrough - most
/// DisplayPort/HDMI/USB-C-DP-Alt-Mode portable monitors do, but some USB-only
/// (DisplayLink-style) portable monitors do not expose it at all.
/// </summary>
public sealed class MonitorControlService
{
    public sealed class Capability
    {
        public required IntPtr Handle { get; init; }
        public bool SupportsBrightness { get; set; }
        public bool SupportsVolume { get; set; }
    }

    /// <summary>Finds the physical monitor handle for a given GDI device name (e.g. \\.\DISPLAY2).</summary>
    public IntPtr? GetPhysicalMonitorHandle(string gdiDeviceName)
    {
        IntPtr? hMonitor = null;

        MonitorApi.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (MonitorApi.GetMonitorInfo(hMon, ref info) &&
                string.Equals(info.szDevice, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                hMonitor = hMon;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);

        if (hMonitor is null)
            return null;

        if (!MonitorApi.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor.Value, out uint count) || count == 0)
            return null;

        var physicalMonitors = new PHYSICAL_MONITOR[count];
        if (!MonitorApi.GetPhysicalMonitorsFromHMONITOR(hMonitor.Value, count, physicalMonitors))
            return null;

        // Use the first physical monitor behind this logical monitor.
        var handle = physicalMonitors[0].hPhysicalMonitor;

        // Destroy the rest (if any) we are not going to use, keep the first one open.
        if (physicalMonitors.Length > 1)
        {
            var rest = physicalMonitors[1..];
            MonitorApi.DestroyPhysicalMonitors((uint)rest.Length, rest);
        }

        return handle;
    }

    public void ReleaseHandle(IntPtr handle)
    {
        var arr = new[] { new PHYSICAL_MONITOR { hPhysicalMonitor = handle } };
        MonitorApi.DestroyPhysicalMonitors(1, arr);
    }

    public bool TryGetBrightness(IntPtr handle, out uint current, out uint max)
    {
        current = 0;
        max = 100;
        return MonitorApi.GetVCPFeatureAndVCPFeatureReply(handle, MonitorApi.VCP_BRIGHTNESS, IntPtr.Zero, out current, out max);
    }

    public bool TrySetBrightness(IntPtr handle, uint value)
        => MonitorApi.SetVCPFeature(handle, MonitorApi.VCP_BRIGHTNESS, value);

    public bool TryGetVolume(IntPtr handle, out uint current, out uint max)
    {
        current = 0;
        max = 100;
        return MonitorApi.GetVCPFeatureAndVCPFeatureReply(handle, MonitorApi.VCP_AUDIO_VOLUME, IntPtr.Zero, out current, out max);
    }

    public bool TrySetVolume(IntPtr handle, uint value)
        => MonitorApi.SetVCPFeature(handle, MonitorApi.VCP_AUDIO_VOLUME, value);

    /// <summary>Sends the DDC/CI "power off" VCP command (0xD6 = 4/5), a soft power-down distinct from disconnecting the display in Windows.</summary>
    public bool TryPowerOff(IntPtr handle)
        => MonitorApi.SetVCPFeature(handle, MonitorApi.VCP_POWER_MODE, 4);

    public bool TryPowerOn(IntPtr handle)
        => MonitorApi.SetVCPFeature(handle, MonitorApi.VCP_POWER_MODE, 1);
}
