using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;
using System.Linq;
using System.Runtime.InteropServices;
using CyberSnap.Models;
using CyberSnap.Native;

namespace CyberSnap.UI;

internal static class PopupWindowHelper
{
    // Allows capture code to hint which monitor the Toast should appear on.
    private static System.Drawing.Point? _monitorHintPoint;

    /// <summary>Tell the next GetCurrentWorkArea call to use this screen point (physical pixels).</summary>
    public static void SetMonitorHintPoint(System.Drawing.Point point) => _monitorHintPoint = point;

    /// <summary>Clear any previously set hint point.</summary>
    public static void ClearMonitorHintPoint() => _monitorHintPoint = null;

    public static Screen[] GetSortedScreens()
    {
        // Sort screens: Primary first, then by X coordinate, then by Y.
        return Screen.AllScreens
            .OrderByDescending(s => s.Primary)
            .ThenBy(s => s.Bounds.X)
            .ThenBy(s => s.Bounds.Y)
            .ToArray();
    }

    public static Rect GetCurrentWorkArea(int monitorIndex = -1)
    {
        try
        {
            var screens = GetSortedScreens();
            if (monitorIndex >= 0 && monitorIndex < screens.Length)
            {
                var screen = screens[monitorIndex];
                return ScreenWorkingAreaToDips(screen);
            }

            var pt = _monitorHintPoint ?? Cursor.Position;
            _monitorHintPoint = null; // consume the hint
            return ScreenWorkingAreaToDips(Screen.FromPoint(pt));
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    public static void ApplyNoActivateChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int exStyle = Native.User32.GetWindowLongA(hwnd, Native.User32.GWL_EXSTYLE);
        exStyle |= 0x80; // WS_EX_TOOLWINDOW
        exStyle |= 0x08000000; // WS_EX_NOACTIVATE
        Native.User32.SetWindowLongA(hwnd, Native.User32.GWL_EXSTYLE, exStyle);
        Native.Dwm.DisableBackdrop(hwnd);
    }

    public static (double targetLeft, double targetTop, double startLeft, double startTop, bool animateLeft) GetPlacement(
        ToastPosition position,
        double actualWidth,
        double actualHeight,
        Rect workArea,
        double edge = 8,
        double bottomLift = 0,
        double offScreenDistance = 10)
    {
        var bottomEdge = edge + Math.Max(0, bottomLift);
        return position switch
        {
            ToastPosition.TopLeft =>
                (workArea.Left + edge, workArea.Top + edge, workArea.Left + edge, workArea.Top - actualHeight - offScreenDistance, false),
            ToastPosition.TopRight =>
                (workArea.Right - actualWidth - edge, workArea.Top + edge, workArea.Right - actualWidth - edge, workArea.Top - actualHeight - offScreenDistance, false),
            ToastPosition.BottomLeft =>
                (workArea.Left + edge, workArea.Bottom - actualHeight - bottomEdge, workArea.Left - actualWidth - offScreenDistance, workArea.Bottom - actualHeight - bottomEdge, true),
            ToastPosition.BottomRight =>
                (workArea.Right - actualWidth - edge, workArea.Bottom - actualHeight - bottomEdge, workArea.Right + offScreenDistance, workArea.Bottom - actualHeight - bottomEdge, true),
            ToastPosition.TopCenter =>
                (workArea.Left + (workArea.Width - actualWidth) / 2, workArea.Top + edge, workArea.Left + (workArea.Width - actualWidth) / 2, workArea.Top - actualHeight - offScreenDistance, false),
            ToastPosition.BottomCenter =>
                (workArea.Left + (workArea.Width - actualWidth) / 2, workArea.Bottom - actualHeight - bottomEdge, workArea.Left + (workArea.Width - actualWidth) / 2, workArea.Bottom + offScreenDistance, false),
            _ =>
                (workArea.Right - actualWidth - edge, workArea.Bottom - actualHeight - bottomEdge, workArea.Right + offScreenDistance, workArea.Bottom - actualHeight - bottomEdge, true),
        };
    }

    public static (double exitLeft, double exitTop, bool animateLeft) GetDismissPlacement(
        ToastPosition position,
        double actualWidth,
        double actualHeight,
        Rect workArea,
        double edge = 8,
        double bottomLift = 0,
        double exitDistance = 20)
    {
        var bottomEdge = edge + Math.Max(0, bottomLift);
        return position switch
        {
            ToastPosition.TopLeft =>
                (workArea.Left + edge, workArea.Top - actualHeight - exitDistance, false),
            ToastPosition.TopRight =>
                (workArea.Right - actualWidth - edge, workArea.Top - actualHeight - exitDistance, false),
            ToastPosition.BottomLeft =>
                (workArea.Left - actualWidth - exitDistance, workArea.Bottom - actualHeight - bottomEdge, true),
            ToastPosition.BottomRight =>
                (workArea.Right + exitDistance, workArea.Bottom - actualHeight - bottomEdge, true),
            ToastPosition.TopCenter =>
                (workArea.Left + (workArea.Width - actualWidth) / 2, workArea.Top - actualHeight - exitDistance, false),
            ToastPosition.BottomCenter =>
                (workArea.Left + (workArea.Width - actualWidth) / 2, workArea.Bottom + exitDistance, false),
            _ =>
                (workArea.Right + exitDistance, workArea.Bottom - actualHeight - bottomEdge, true),
        };
    }

    internal static Rect PhysicalPixelsToDips(System.Drawing.Rectangle physicalRect, System.Drawing.Point monitorPoint)
    {
        var (scaleX, scaleY) = GetScaleForPoint(monitorPoint);

        return new Rect(
            physicalRect.Left / scaleX,
            physicalRect.Top / scaleY,
            physicalRect.Width / scaleX,
            physicalRect.Height / scaleY);
    }

    internal static Rect ScreenWorkingAreaToDips(Screen screen)
    {
        var screenBounds = screen.Bounds;
        var screenWorkingArea = screen.WorkingArea;

        if (TryGetNativeMonitorInfo(screen, out var nativeBounds, out var nativeWorkingArea))
        {
            var (scaleX, scaleY) = GetScaleForPoint(nativeBounds.Location);

            if (ScreenBoundsAppearToBeDips(screenBounds, nativeBounds, scaleX, scaleY))
            {
                return new Rect(
                    screenWorkingArea.Left,
                    screenWorkingArea.Top,
                    screenWorkingArea.Width,
                    screenWorkingArea.Height);
            }

            return PhysicalPixelsToDips(nativeWorkingArea, nativeBounds.Location);
        }

        return PhysicalPixelsToDips(screenWorkingArea, screenBounds.Location);
    }

    internal static bool TryGetNativeMonitorInfo(
        Screen screen,
        out System.Drawing.Rectangle bounds,
        out System.Drawing.Rectangle workingArea)
    {
        bounds = System.Drawing.Rectangle.Empty;
        workingArea = System.Drawing.Rectangle.Empty;

        try
        {
            var found = false;
            var nativeBounds = System.Drawing.Rectangle.Empty;
            var nativeWorkingArea = System.Drawing.Rectangle.Empty;
            Native.User32.MonitorEnumProc callback = (
                IntPtr monitor,
                IntPtr hdcMonitor,
                ref Native.User32.RECT monitorRect,
                IntPtr data) =>
            {
                var info = new Native.User32.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<Native.User32.MONITORINFOEX>()
                };

                if (Native.User32.GetMonitorInfoEx(monitor, ref info) &&
                    string.Equals(info.szDevice, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    nativeBounds = info.rcMonitor.ToRectangle();
                    nativeWorkingArea = info.rcWork.ToRectangle();
                    found = true;
                    return false;
                }

                return true;
            };

            Native.User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            bounds = nativeBounds;
            workingArea = nativeWorkingArea;

            return found && !bounds.IsEmpty && !workingArea.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    internal static bool ScreenBoundsAppearToBeDips(
        System.Drawing.Rectangle screenBounds,
        System.Drawing.Rectangle nativeBounds,
        double scaleX,
        double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0)
        {
            return false;
        }

        const int tolerance = 2;
        return Math.Abs(screenBounds.Width - nativeBounds.Width / scaleX) <= tolerance &&
               Math.Abs(screenBounds.Height - nativeBounds.Height / scaleY) <= tolerance;
    }

    internal static (double X, double Y) GetScaleForPoint(System.Drawing.Point point)
    {
        try
        {
            var monitor = User32.MonitorFromPoint(
                new User32.POINT(point.X, point.Y),
                User32.MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero
                && Shcore.GetDpiForMonitor(monitor, Shcore.MonitorDpiType.EffectiveDpi, out uint dpiX, out uint dpiY) == 0
                && dpiX > 0
                && dpiY > 0)
            {
                return (dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch
        {
            // Fall back below.
        }

        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return (Math.Max(1, graphics.DpiX / 96.0), Math.Max(1, graphics.DpiY / 96.0));
    }
}
