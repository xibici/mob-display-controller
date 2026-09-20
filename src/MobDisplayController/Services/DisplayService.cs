using MobDisplayController.Models;
using MobDisplayController.Native;

namespace MobDisplayController.Services;

public sealed class DisplayService
{
    /// <summary>
    /// Enumerates each physical monitor Windows can drive, including ones that are currently
    /// disconnected/disabled (mirrors what Settings > Display shows).
    ///
    /// QDC_ALL_PATHS isn't a list of monitors: it pairs every target with every source it
    /// could be driven from (5 or 10 entries per monitor), and includes every empty connector
    /// slot and virtual display adapter on the system - 250 entries for 2 real screens on the
    /// Legion Go. Those are filtered to targets with a monitor actually attached, one entry each.
    /// </summary>
    public List<MonitorEntry> GetAllMonitors()
    {
        var result = new List<MonitorEntry>();

        if (!TryQueryAllPaths(out var paths, out _))
            return result;

        var byTarget = new Dictionary<(uint, int, uint), MonitorEntry>();

        foreach (var path in paths)
        {
            // Empty connector slots and idle virtual adapters report no attached monitor.
            if (!path.targetInfo.targetAvailable)
                continue;

            bool isActive = (path.flags & Ccd.DISPLAYCONFIG_PATH_ACTIVE) != 0;
            var id = (path.targetInfo.adapterId.LowPart, path.targetInfo.adapterId.HighPart, path.targetInfo.id);

            // Keep one entry per physical target, preferring the active path - it's the one
            // carrying the real source and GDI device name.
            if (byTarget.TryGetValue(id, out var existing) && (existing.IsActive || !isActive))
                continue;

            var info = GetTargetInfo(path.targetInfo.adapterId, path.targetInfo.id);

            string? gdiName = null;
            if (isActive)
                gdiName = GetSourceGdiDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id);

            bool isInternal = path.targetInfo.outputTechnology
                is DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                or DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;

            byTarget[id] = new MonitorEntry
            {
                AdapterLuidLow = path.targetInfo.adapterId.LowPart,
                AdapterLuidHigh = path.targetInfo.adapterId.HighPart,
                SourceId = path.sourceInfo.id,
                TargetId = path.targetInfo.id,
                FriendlyName = info?.FriendlyName ?? "未知显示器",
                EdidManufacturerId = info?.EdidManufacturerId ?? 0,
                EdidProductCodeId = info?.EdidProductCodeId ?? 0,
                IsActive = isActive,
                IsInternal = isInternal,
                GdiDeviceName = gdiName,
            };
        }

        result.AddRange(byTarget.Values);
        return result;
    }

    public enum TopologyMode
    {
        Unknown,
        Clone,
        Extend,
        InternalOnly,
        ExternalOnly,
    }

    /// <summary>
    /// Infers the current multi-display topology the same way Win+P does: multiple
    /// active paths sharing one source id means clone, distinct source ids means extend.
    /// </summary>
    public TopologyMode GetTopologyMode()
    {
        if (!TryQueryPaths(Ccd.QDC_ONLY_ACTIVE_PATHS, out var paths, out _))
            return TopologyMode.Unknown;

        if (paths.Length == 0)
            return TopologyMode.Unknown;

        if (paths.Length == 1)
        {
            bool isInternal = paths[0].targetInfo.outputTechnology
                is DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                or DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;
            return isInternal ? TopologyMode.InternalOnly : TopologyMode.ExternalOnly;
        }

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
            TopologyMode.InternalOnly => Ccd.SDC_TOPOLOGY_INTERNAL,
            TopologyMode.ExternalOnly => Ccd.SDC_TOPOLOGY_EXTERNAL,
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

    public (int Width, int Height, int Hz)? GetCurrentMode(string gdiDeviceName)
    {
        var devMode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
        if (!Gdi.EnumDisplaySettingsEx(gdiDeviceName, Gdi.ENUM_CURRENT_SETTINGS, ref devMode, 0))
            return null;
        return (devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency);
    }

    /// <summary>Returns this monitor's own rotation as recorded on its CCD display path.</summary>
    public DISPLAYCONFIG_ROTATION? GetCurrentOrientation(MonitorEntry monitor)
    {
        if (!TryQueryPaths(Ccd.QDC_ONLY_ACTIVE_PATHS, out var paths, out _))
            return null;

        int idx = Array.FindIndex(paths, p =>
            p.targetInfo.adapterId.LowPart == monitor.AdapterLuidLow &&
            p.targetInfo.adapterId.HighPart == monitor.AdapterLuidHigh &&
            p.targetInfo.id == monitor.TargetId);

        return idx >= 0 ? paths[idx].targetInfo.rotation : null;
    }

    /// <summary>
    /// Rotates only this monitor's own display path. Unlike the legacy DEVMODE orientation
    /// API (which rotates the shared source mode), this touches only the one CCD path, so a
    /// display cloned with another one won't drag it along - the classic symptom being "I
    /// rotated the external screen and the built-in one rotated too" when using Duplicate mode.
    /// </summary>
    public bool TrySetOrientation(MonitorEntry monitor, DISPLAYCONFIG_ROTATION rotation, out string error)
    {
        error = string.Empty;

        // Two cloned targets share one source mode, and Windows can't honor different
        // rotations for each - rather than failing cleanly it can silently drop the target's
        // path from the topology instead. Only ever touch an already-active, non-cloned path.
        if (GetTopologyMode() == TopologyMode.Clone)
        {
            error = "复制模式下两块屏同画面,无法单独旋转某一块屏幕,请先切到「仅外屏」。";
            return false;
        }

        // Scoped to only-active paths (rather than the full historical QDC_ALL_PATHS database)
        // to keep the array SetDisplayConfig has to re-validate small - rotation only ever
        // applies to a monitor that's already active anyway.
        if (!TryQueryPaths(Ccd.QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes))
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
        target.targetInfo.rotation = rotation;
        paths[idx] = target;

        int rc = Ccd.SetDisplayConfig(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            Ccd.SDC_APPLY | Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_SAVE_TO_DATABASE | Ccd.SDC_ALLOW_CHANGES);

        if (rc == Ccd.ERROR_SUCCESS)
            return true;

        error = $"设置旋转失败,错误码 {rc}。";
        return false;
    }

    /// <summary>
    /// Puts every non-built-in panel on the active topology back to rotation 0.
    ///
    /// A duplicate hands one rotation to both of its targets, and Windows copies the built-in panel's
    /// 90 degrees - its panel is physically portrait - onto the external monitor as well, which
    /// leaves the external showing the picture on its side. Only the built-in needs that rotation.
    ///
    /// Done by supplying the paths with the corrected rotation rather than by asking for a rotation
    /// change: the supplied-configuration form is accepted (verified here, rc=0, both paths still
    /// active), whereas the "just rotate this target" form is what Windows answers by quietly dropping
    /// the cloned path.
    /// </summary>
    public bool TryNormalizeExternalRotation(out string error)
    {
        error = string.Empty;

        if (!TryQueryPaths(Ccd.QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes))
        {
            error = "无法读取当前显示器配置 (QueryDisplayConfig 失败)。";
            return false;
        }

        bool changed = false;

        for (int i = 0; i < paths.Length; i++)
        {
            var path = paths[i];

            bool isInternal = path.targetInfo.outputTechnology
                is DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                or DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED;

            if (isInternal || path.targetInfo.rotation == DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_IDENTITY)
                continue;

            path.targetInfo.rotation = DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_IDENTITY;
            paths[i] = path;
            changed = true;
        }

        if (!changed)
            return true;

        int rc = Ccd.SetDisplayConfig(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            Ccd.SDC_APPLY | Ccd.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Ccd.SDC_SAVE_TO_DATABASE);

        if (rc == Ccd.ERROR_SUCCESS)
            return true;

        error = $"归正外接屏旋转失败,错误码 {rc}。";
        return false;
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

    private readonly record struct TargetInfo(string FriendlyName, ushort EdidManufacturerId, ushort EdidProductCodeId);

    private static TargetInfo? GetTargetInfo(LUID adapterId, uint targetId)
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

        string name = !string.IsNullOrWhiteSpace(request.monitorFriendlyDeviceName)
            ? request.monitorFriendlyDeviceName
            : request.outputTechnology switch
            {
                DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DisplayPort 显示器",
                DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => "DisplayPort 显示器",
                DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => "HDMI 显示器",
                DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => "内置显示器",
                _ => "未知显示器",
            };

        return new TargetInfo(name, request.edidManufactureId, request.edidProductCodeId);
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
