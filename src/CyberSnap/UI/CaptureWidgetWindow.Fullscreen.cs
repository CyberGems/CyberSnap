using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CyberSnap.Capture;
using CyberSnap.Native;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private bool _fullscreenSuppressed;

    private void RefreshFullscreenSuppression()
    {
        if (_isDragging || _isDragArmed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        var info = new User32.MONITORINFO { cbSize = Marshal.SizeOf<User32.MONITORINFO>() };
        if (User32.GetMonitorInfo(monitor, ref info))
            SetFullscreenSuppressed(HasFullscreenWindow(info.rcMonitor.ToRectangle(), hwnd));
    }

    private void SetFullscreenSuppressed(bool suppress)
    {
        if (_fullscreenSuppressed == suppress || !SetWidgetRegionClipped(suppress || _sessionRestoreInProgress))
            return;

        _fullscreenSuppressed = suppress;
        if (suppress)
        {
            _contextMenu?.Close();
            DismissWidgetToolTip();
            _mouseInWindow = false;
            _hoverDelayTimer.Stop();
            _collapseTimer.Stop();
        }
        else if (IsVisible && !_sessionRestoreInProgress)
        {
            BeginPostDragGrace();
            if (_isExpanded) _collapseTimer.Start();
            AssertWidgetTopmost();
        }
    }

    private bool HasFullscreenWindow(Forms.Screen screen)
    {
        var monitor = PopupWindowHelper.TryGetNativeMonitorInfo(screen, out var bounds, out _)
            ? bounds : screen.Bounds;
        return HasFullscreenWindow(monitor, new WindowInteropHelper(this).Handle);
    }

    private static bool HasFullscreenWindow(Rectangle monitor, IntPtr widget)
    {
        var foreground = User32.GetForegroundWindow();
        if (foreground != widget && CoversMonitorInFullscreen(foreground, monitor)) return true;

        // Focus can move to another display without covering a video or game on this one.
        var center = new User32.POINT(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
        var atCenter = User32.GetAncestor(User32.WindowFromPoint(center), User32.GA_ROOT);
        return atCenter != foreground && atCenter != widget && CoversMonitorInFullscreen(atCenter, monitor);
    }

    private static bool CoversMonitorInFullscreen(IntPtr hwnd, Rectangle monitor)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)
            || WindowDetector.IsIgnoredWindowHandle(hwnd) || Dwm.IsWindowCloaked(hwnd))
            return false;

        int style = User32.GetWindowLongA(hwnd, User32.GWL_STYLE);
        if ((style & User32.WS_CAPTION) == User32.WS_CAPTION
            || ((style & User32.WS_THICKFRAME) != 0 && User32.IsZoomed(hwnd)))
            return false;

        // DWM bounds exclude the invisible resize border that can make a maximized
        // window's raw HWND rectangle look larger than the display.
        var visible = Dwm.GetExtendedFrameBounds(hwnd);
        if (!visible.Contains(monitor)) return false;

        string windowClass = MonitorWindowClass(hwnd);
        return !IsDesktopWindowClass(windowClass) && !WindowDetector.IsTaskbarClass(windowClass);
    }
}
