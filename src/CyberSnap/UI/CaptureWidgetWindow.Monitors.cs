using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
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
    private Native.User32.RECT? _monitorMoveBounds;
    private int _peekMoveGeneration;
    private bool _peekMoveCloaked;

    private bool MoveToMonitor(IntPtr hwnd, int left, int top, int width, int height, bool keepBounds, bool dpiChanges)
    {
        if (dpiChanges && IsVisible && !_isExpanded && Opacity == 1 && !_peekMoveCloaked)
        {
            _peekMoveCloaked = Native.Dwm.TrySetCloaked(hwnd, true);
            // Cloak is queued to the compositor. Commit it on the source monitor
            // before SetWindowPos can carry the old surface to the destination.
            if (_peekMoveCloaked)
                FlushMonitorComposition();
        }

        var previousBounds = _monitorMoveBounds;
        _monitorMoveBounds = new Native.User32.RECT
        {
            Left = left, Top = top, Right = left + width, Bottom = top + height
        };
        try
        {
            bool moved = Native.User32.SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height,
                Native.User32.SWP_NOACTIVATE | Native.User32.SWP_NOZORDER);
            if (moved && _peekMoveCloaked)
                SynchronizeMonitorDpi(hwnd, _monitorMoveBounds.Value);
            return moved;
        }
        finally
        {
            if (!keepBounds)
                _monitorMoveBounds = previousBounds;
            if (_peekMoveCloaked)
                RevealPeekAfterMonitorMove(hwnd, ++_peekMoveGeneration);
        }
    }

    private unsafe void SynchronizeMonitorDpi(IntPtr hwnd, Native.User32.RECT bounds)
    {
        uint nativeDpi = Native.User32.GetDpiForWindow(hwnd);
        if (nativeDpi == 0 || Math.Abs(VisualTreeHelper.GetDpi(this).PixelsPerInchX - nativeDpi) < 0.01)
            return;

        // A programmatic move can advance the HWND DPI without updating WPF's
        // visual DPI. Replay the current native DPI while the peek is still hidden.
        Native.User32.SendMessage(hwnd, WM_DPICHANGED, new IntPtr(nativeDpi | (nativeDpi << 16)), (IntPtr)(&bounds));
    }

    private void RevealPeekAfterMonitorMove(IntPtr hwnd, int generation)
    {
        // Let the DPI placement and WPF render work run first. DWM can otherwise
        // show the old layered surface at the destination for a frame.
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (generation != _peekMoveGeneration) return;
            await Task.Run(FlushMonitorComposition);

            if (generation == _peekMoveGeneration && new WindowInteropHelper(this).Handle == hwnd)
            {
                _peekMoveCloaked = !Native.Dwm.TrySetCloaked(hwnd, false);
                if (_peekMoveCloaked)
                    AppDiagnostics.LogWarning("widget.monitor-composition", "Windows could not reveal the moved widget.");
            }
        }), DispatcherPriority.ContextIdle);
    }

    private static void FlushMonitorComposition()
    {
        int result = Native.Dwm.DwmFlush();
        if (result < 0)
            AppDiagnostics.LogWarning("widget.monitor-composition", $"DwmFlush returned 0x{result:X8}.");
    }

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
        => index == WidgetPreferences.FollowCursor ? WidgetPreferences.FollowCursor : index < 0 ? -1 : previousDevice is not null
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
