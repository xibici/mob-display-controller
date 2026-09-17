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

    /// <summary>GDI device name such as \\.\DISPLAY1. Only populated when IsActive is true.</summary>
    public string? GdiDeviceName { get; init; }

    public string Key => $"{AdapterLuidHigh}:{AdapterLuidLow}:{TargetId}";

    public override string ToString() =>
        IsActive ? $"{FriendlyName} ({GdiDeviceName})" : $"{FriendlyName} [已断开]";
}
