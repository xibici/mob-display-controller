namespace MobDisplayController.Models;

/// <summary>
/// One display path as reported by the Windows CCD API (QDC_ALL_PATHS).
/// Includes displays that are currently disconnected/disabled, which is
/// what lets us re-enable a display that was switched off earlier.
/// </summary>
public sealed class MonitorEntry
{
    public required uint AdapterLuidLow { get; init; }
    public required int AdapterLuidHigh { get; init; }
    public required uint SourceId { get; init; }
    public required uint TargetId { get; init; }
    public required string FriendlyName { get; init; }
    public required bool IsActive { get; init; }

    /// <summary>True for the laptop/handheld's built-in panel (internal or embedded DisplayPort output).</summary>
    public required bool IsInternal { get; init; }

    /// <summary>GDI device name such as \\.\DISPLAY1. Only populated when IsActive is true.</summary>
    public string? GdiDeviceName { get; init; }

    /// <summary>EDID manufacturer / product code, burned into the monitor itself. 0 when the EDID couldn't be read.</summary>
    public ushort EdidManufacturerId { get; init; }
    public ushort EdidProductCodeId { get; init; }

    /// <summary>
    /// Identifies the physical monitor across reboots. The adapter LUID can't be part of this:
    /// Windows hands it out fresh at every boot (it changed three times in one day on the
    /// Legion Go), so a key built on it stops matching after every restart. The EDID codes
    /// come from the monitor's own firmware and don't move.
    /// </summary>
    public string Key => EdidManufacturerId != 0 || EdidProductCodeId != 0
        ? $"EDID:{EdidManufacturerId:X4}:{EdidProductCodeId:X4}"
        : $"TGT:{TargetId}";

    public override string ToString() =>
        IsActive ? $"{FriendlyName} ({GdiDeviceName})" : $"{FriendlyName} [已断开]";
}
