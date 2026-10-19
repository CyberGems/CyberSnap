using System.Windows;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using Point = System.Windows.Point;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private string? _placementMonitorDevice;
    private string? _configuredMonitorDevice;
    private int _configuredMonitorIndex = -1;
    private string? _dragMonitorDevice;
    private Rect _dragWorkingArea;
    private DpiScale _dragDpi;
    private bool _dpiPlacementPending;

    private static System.Drawing.Rectangle GetPhysicalWorkingArea(Forms.Screen screen)
        => PopupWindowHelper.TryGetNativeMonitorInfo(screen, out _, out var work) ? work : screen.WorkingArea;

    private void RememberConfiguredMonitor(Forms.Screen[] screens)
    {
        // A DPI/layout notification may precede DisplaySettingsChanged. Do not
        // overwrite the old identity with whichever display inherited its index.
        if (_configuredMonitorIndex == _settings.WidgetMonitorIndex && _configuredMonitorDevice is not null) return;
        _configuredMonitorIndex = _settings.WidgetMonitorIndex;
        _configuredMonitorDevice = _configuredMonitorIndex >= 0 && _configuredMonitorIndex < screens.Length
            ? screens[_configuredMonitorIndex].DeviceName : null;
    }

    // Keep the selected physical monitor when Windows reorders the list. If that
    // monitor disappears, return to following the active window instead of choosing another device.
    private static int ResolveMonitorIndexAfterDisplayChange(int index, string? previousDevice, string[] available)
        => index < 0 ? -1 : previousDevice is not null
            ? Array.FindIndex(available, device => string.Equals(device, previousDevice, StringComparison.OrdinalIgnoreCase))
            : index < available.Length ? index : -1;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF finishes adopting the new DPI after the native move. Re-snap the
        // docked edge then, including when Windows changes scaling without moving it.
        if (_dpiPlacementPending) return;
        _dpiPlacementPending = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _dpiPlacementPending = false;
            if (!IsLoaded || !IsVisible) return;
            _contextMenu?.Close();
            var target = PopupWindowHelper.GetSortedScreens().FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, _placementMonitorDevice, StringComparison.OrdinalIgnoreCase));
            if (target is not null) PositionWindowOnScreen(target);
            else PositionWindow();
        }, DispatcherPriority.Loaded);
    }

    private void RememberDragMonitor(Forms.Screen screen)
    {
        _dragMonitorDevice = screen.DeviceName;
        var work = GetPhysicalWorkingArea(screen);
        _dragWorkingArea = new Rect(work.X, work.Y, work.Width, work.Height);
        var scale = PopupWindowHelper.GetScaleForPoint(new System.Drawing.Point(work.X + work.Width / 2, work.Y + work.Height / 2));
        _dragDpi = new DpiScale(scale.X, scale.Y);
    }

    private void UpdateDragPosition(System.Drawing.Point cursor)
    {
        var target = ResolveTargetScreen(PopupWindowHelper.GetSortedScreens(), _settings.WidgetMonitorIndex, cursor);
        string? oldDevice = _dragMonitorDevice;
        Rect oldArea = _dragWorkingArea;
        DpiScale oldDpi = _dragDpi;
        RememberDragMonitor(target);
        bool rebase = oldDevice != _dragMonitorDevice || oldArea != _dragWorkingArea || !oldDpi.Equals(_dragDpi);
        var point = new Point(cursor.X, cursor.Y);
        var (edge, offset) = CalculateDragPosition(_dragWorkingArea, _dragDpi, point,
            _dragStartPoint, _dragStartOffset, _settings.WidgetDockEdge, rebase);
        if (rebase || edge != _settings.WidgetDockEdge)
        {
            _dragStartPoint = point;
            _dragStartOffset = offset;
        }
        _settings.WidgetDockEdge = edge;
        _settings.WidgetDockPositionOffset = offset;
        UpdatePeekGrip(edge);
        UpdateGripVisibility();
        PositionWindowOnScreen(target);
    }

    private static (CaptureDockSide Edge, double Offset) CalculateDragPosition(
        Rect physicalWork, DpiScale dpi, Point cursorPixels, Point startPixels,
        double startOffset, CaptureDockSide current, bool rebase)
    {
        // Both points use the target monitor's LOCAL DIPs. Dividing global desktop
        // origins by different monitor scales mixes coordinate systems and causes jumps.
        var work = new Rect(0, 0, physicalWork.Width / dpi.DpiScaleX, physicalWork.Height / dpi.DpiScaleY);
        var cursor = new Point((cursorPixels.X - physicalWork.X) / dpi.DpiScaleX,
            (cursorPixels.Y - physicalWork.Y) / dpi.DpiScaleY);
        var edge = ResolvePreferredDockEdge(work, cursor, current);
        if (rebase || edge != current)
            return (edge, OffsetAlongEdgeFromCursor(work, edge, cursor));

        var scale = UiScale.Normalize(UiScale.Current);
        bool horizontal = edge is CaptureDockSide.Top or CaptureDockSide.Bottom;
        double travel = horizontal ? work.Width - PanelWidth * scale : work.Height - PanelHeight * scale;
        double delta = horizontal ? (cursorPixels.X - startPixels.X) / dpi.DpiScaleX
            : (cursorPixels.Y - startPixels.Y) / dpi.DpiScaleY;
        return (edge, travel > 0 ? Math.Clamp(startOffset + delta / travel, 0, 1) : 0.5);
    }
}
