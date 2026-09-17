using MobDisplayController.Models;
using MobDisplayController.Native;

namespace MobDisplayController.Services;

public sealed class DisplayService
{
    /// <summary>
    /// Enumerates every display path Windows knows about, including ones that
    /// are currently disconnected/disabled (mirrors what Settings > Display shows).
    /// </summary>
    public List<MonitorEntry> GetAllMonitors()
    {
        var result = new List<MonitorEntry>();

        if (!TryQueryAllPaths(out var paths, out _))
            return result;

        foreach (var path in paths)
        {
            bool isActive = (path.flags & Ccd.DISPLAYCONFIG_PATH_ACTIVE) != 0;

            string friendlyName = GetTargetFriendlyName(path.targetInfo.adapterId, path.targetInfo.id)
                                   ?? "未知显示器";

            string? gdiName = null;
            if (isActive)
                gdiName = GetSourceGdiDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id);

            result.Add(new MonitorEntry
            {
                AdapterLuidLow = path.targetInfo.adapterId.LowPart,
                AdapterLuidHigh = path.targetInfo.adapterId.HighPart,
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                FriendlyName = friendlyName,
                IsActive = isActive,
                GdiDeviceName = gdiName,
            });
        }

        return result;
    }

    public enum TopologyMode
    {
        Unknown,
        Single,
        Clone,
        Extend,
    }

    /// <summary>
    /// Infers the current multi-display topology the same way Win+P does: multiple
    /// active paths sharing one source id means clone, distinct source ids means extend.
    /// </summary>
    public TopologyMode GetTopologyMode()
    {
        if (!TryQueryPaths(Ccd.QDC_ONLY_ACTIVE_PATHS, out var paths, out _))
            return TopologyMode.Unknown;

        if (paths.Length <= 1)
            return TopologyMode.Single;

        int distinctSources = paths
            .Select(p => (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id))
            .Distinct()
            .Count();

        return distinctSources < paths.Length ? TopologyMode.Clone : TopologyMode.Extend;
    }

    /// <summary>Switches between "duplicate these displays" and "extend desktop across these displays", same as Win+P.</summary>
    public bool TrySetTopology(TopologyMode mode, out string error)
    {
        error = string.Empty;

        uint topologyFlag = mode switch
        {
            TopologyMode.Clone => Ccd.SDC_TOPOLOGY_CLONE,
            TopologyMode.Extend => Ccd.SDC_TOPOLOGY_EXTEND,
            _ => 0,
        };

        if (topologyFlag == 0)
        {
            error = "不支持的显示模式。";
            return false;
        }

        int rc = Ccd.SetDisplayConfig(0, null, 0, null, Ccd.SDC_APPLY | topologyFlag);
        if (rc == Ccd.ERROR_SUCCESS)
            return true;

        error = $"切换显示模式失败,错误码 {rc}。";
        return false;
    }

    public bool TrySetActive(MonitorEntry monitor, bool active, out string error)
    {
        error = string.Empty;

        if (!TryQueryAllPaths(out var paths, out var modes))
        {
            error = "无法读取当前显示器配置 (QueryDisplayConfig 失败)。";
            return false;
        }

        int idx = Array.FindIndex(paths, p =>
            p.targetInfo.adapterId.LowPart == monitor.AdapterLuidLow &&
            p.targetInfo.adapterId.HighPart == monitor.AdapterLuidHigh &&
            p.targetInfo.id == monitor.TargetId);

        if (idx < 0)
        {
            error = "找不到该显示器,可能已被物理拔出。";
            return false;
        }

        var target = paths[idx];

        if (active)
            target.flags |= Ccd.DISPLAYCONFIG_PATH_ACTIVE;
        else
            target.flags &= ~Ccd.DISPLAYCONFIG_PATH_ACTIVE;

        paths[idx] = target;

        int rc = Ccd.SetDisplayConfig(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            Ccd.SDC_APPLY | Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_SAVE_TO_DATABASE | Ccd.SDC_ALLOW_CHANGES);

        if (rc == Ccd.ERROR_SUCCESS)
            return true;

        // Retry once allowing Windows to reorder/re-optimize paths, which is
        // sometimes required when re-activating a previously disabled target.
        rc = Ccd.SetDisplayConfig(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            Ccd.SDC_APPLY | Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_SAVE_TO_DATABASE | Ccd.SDC_ALLOW_CHANGES | Ccd.SDC_ALLOW_PATH_ORDER_CHANGES);

        if (rc == Ccd.ERROR_SUCCESS)
            return true;

        error = $"SetDisplayConfig 失败,错误码 {rc}。该显示器可能不支持通过软件重新连接,可尝试在 Windows 设置的高级显示设置中手动重新连接。";
        return false;
    }

    /// <summary>Returns the list of supported resolutions/refresh rates for an active monitor's GDI device.</summary>
    public List<(int Width, int Height, int Hz)> GetSupportedModes(string gdiDeviceName)
    {
        var modes = new List<(int, int, int)>();
        var seen = new HashSet<(int, int, int)>();
        var devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };

        int i = 0;
        while (Gdi.EnumDisplaySettingsEx(gdiDeviceName, i, ref devMode, 0))
        {
            var tuple = (devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency);
            if (devMode.dmPelsWidth > 0 && devMode.dmPelsHeight > 0 && seen.Add(tuple))
                modes.Add(tuple);
            i++;
            devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
        }

        modes.Sort((a, b) =>
        {
            int c = (b.Item1 * b.Item2).CompareTo(a.Item1 * a.Item2);
            return c != 0 ? c : b.Item3.CompareTo(a.Item3);
        });

        return modes;
    }

    public (int Width, int Height, int Hz)? GetCurrentMode(string gdiDeviceName)
    {
        var devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
        if (!Gdi.EnumDisplaySettingsEx(gdiDeviceName, Gdi.ENUM_CURRENT_SETTINGS, ref devMode, 0))
            return null;
        return (devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency);
    }

    public bool TrySetResolution(string gdiDeviceName, int width, int height, int hz, out string error)
    {
        error = string.Empty;
        var devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };

        if (!Gdi.EnumDisplaySettingsEx(gdiDeviceName, Gdi.ENUM_CURRENT_SETTINGS, ref devMode, 0))
        {
            error = "无法读取当前显示模式。";
            return false;
        }

        devMode.dmPelsWidth = width;
        devMode.dmPelsHeight = height;
        devMode.dmDisplayFrequency = hz;
        devMode.dmFields = Gdi.DM_PELSWIDTH | Gdi.DM_PELSHEIGHT | Gdi.DM_DISPLAYFREQUENCY;

        int rc = Gdi.ChangeDisplaySettingsEx(gdiDeviceName, ref devMode, IntPtr.Zero, Gdi.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (rc == Gdi.DISP_CHANGE_SUCCESSFUL)
            return true;

        error = rc switch
        {
            Gdi.DISP_CHANGE_BADMODE => "该显示器不支持此分辨率/刷新率组合。",
            Gdi.DISP_CHANGE_RESTART => "需要重启才能生效。",
            _ => $"设置分辨率失败,错误码 {rc}。",
        };
        return rc == Gdi.DISP_CHANGE_RESTART; // restart-required still counts as "applied"
    }

    /// <summary>Returns the monitor's current rotation as one of Gdi.DMDO_DEFAULT/90/180/270.</summary>
    public int? GetCurrentOrientation(string gdiDeviceName)
    {
        var devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
        if (!Gdi.EnumDisplaySettingsEx(gdiDeviceName, Gdi.ENUM_CURRENT_SETTINGS, ref devMode, 0))
            return null;
        return devMode.dmDisplayOrientation;
    }

    /// <summary>Sets display orientation (0/90/180/270), same as the "显示方向" dropdown in Windows Settings.</summary>
    public bool TrySetOrientation(string gdiDeviceName, int orientation, out string error)
    {
        error = string.Empty;
        var devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };

        if (!Gdi.EnumDisplaySettingsEx(gdiDeviceName, Gdi.ENUM_CURRENT_SETTINGS, ref devMode, 0))
        {
            error = "无法读取当前显示模式。";
            return false;
        }

        // Rotating by 90 or 270 relative to the current orientation swaps portrait/landscape,
        // so width/height must be swapped too or ChangeDisplaySettingsEx will reject the mode.
        int delta = ((orientation - devMode.dmDisplayOrientation) % 4 + 4) % 4;
        if (delta == 1 || delta == 3)
            (devMode.dmPelsWidth, devMode.dmPelsHeight) = (devMode.dmPelsHeight, devMode.dmPelsWidth);

        devMode.dmDisplayOrientation = orientation;
        devMode.dmFields = Gdi.DM_DISPLAYORIENTATION | Gdi.DM_PELSWIDTH | Gdi.DM_PELSHEIGHT;

        int rc = Gdi.ChangeDisplaySettingsEx(gdiDeviceName, ref devMode, IntPtr.Zero, Gdi.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (rc == Gdi.DISP_CHANGE_SUCCESSFUL)
            return true;

        error = rc switch
        {
            Gdi.DISP_CHANGE_BADMODE => "该显示器不支持此旋转方向。",
            Gdi.DISP_CHANGE_RESTART => "需要重启才能生效。",
            _ => $"设置旋转失败,错误码 {rc}。",
        };
        return rc == Gdi.DISP_CHANGE_RESTART; // restart-required still counts as "applied"
    }

    private static bool TryQueryAllPaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        => TryQueryPaths(Ccd.QDC_ALL_PATHS, out paths, out modes);

    private static bool TryQueryPaths(uint queryFlags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();

        int rc = Ccd.GetDisplayConfigBufferSizes(queryFlags, out uint pathCount, out uint modeCount);
        if (rc != Ccd.ERROR_SUCCESS)
            return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var pathArray = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modeArray = new DISPLAYCONFIG_MODE_INFO[modeCount];

            rc = Ccd.QueryDisplayConfig(queryFlags, ref pathCount, pathArray, ref modeCount, modeArray, IntPtr.Zero);

            if (rc == Ccd.ERROR_SUCCESS)
            {
                paths = pathCount == pathArray.Length ? pathArray : pathArray[..(int)pathCount];
                modes = modeCount == modeArray.Length ? modeArray : modeArray[..(int)modeCount];
                return true;
            }

            if (rc != Ccd.ERROR_INSUFFICIENT_BUFFER)
                return false;

            // Buffer sizes can change between calls (e.g. a monitor was plugged in); retry.
            rc = Ccd.GetDisplayConfigBufferSizes(queryFlags, out pathCount, out modeCount);
            if (rc != Ccd.ERROR_SUCCESS)
                return false;
        }

        return false;
    }

    private static string? GetTargetFriendlyName(LUID adapterId, uint targetId)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId,
            },
        };

        int rc = Ccd.DisplayConfigGetDeviceInfo(ref request);
        if (rc != Ccd.ERROR_SUCCESS)
            return null;

        if (!string.IsNullOrWhiteSpace(request.monitorFriendlyDeviceName))
            return request.monitorFriendlyDeviceName;

        return request.outputTechnology switch
        {
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DisplayPort 显示器",
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => "DisplayPort 显示器",
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => "HDMI 显示器",
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => "内置显示器",
            _ => "未知显示器",
        };
    }

    private static string? GetSourceGdiDeviceName(LUID adapterId, uint sourceId)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId,
            },
        };

        int rc = Ccd.DisplayConfigGetDeviceInfo(ref request);
        return rc == Ccd.ERROR_SUCCESS ? request.viewGdiDeviceName : null;
    }
}
