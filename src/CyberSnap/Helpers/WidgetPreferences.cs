using CyberSnap.Models;

namespace CyberSnap.Helpers;

/// <summary>The widget's placement and activation preferences, independent of capture delivery.</summary>
public readonly record struct WidgetPreferences(
    bool AlwaysOnTop, CaptureDockSide DockEdge, int MonitorIndex, double PositionOffset, int HoverDelayMs)
{
    public const int FollowActiveMonitor = -1;
    public const int FollowCursor = -2;

    public static WidgetPreferences FromSettings(AppSettings settings) => new(
        settings.WidgetAlwaysOnTop, settings.WidgetDockEdge, settings.WidgetMonitorIndex,
        settings.WidgetDockPositionOffset, settings.WidgetHoverDelayMs);

    public void ApplyTo(AppSettings settings)
    {
        settings.WidgetAlwaysOnTop = AlwaysOnTop;
        settings.WidgetDockEdge = DockEdge;
        settings.WidgetMonitorIndex = MonitorIndex;
        settings.WidgetDockPositionOffset = PositionOffset;
        settings.WidgetHoverDelayMs = HoverDelayMs;
    }
}
