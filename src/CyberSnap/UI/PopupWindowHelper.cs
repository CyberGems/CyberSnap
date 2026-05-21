using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;
using System.Linq;
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
                return PhysicalPixelsToDips(screen.WorkingArea, screen.Bounds.Location);
            }

            var pt = _monitorHintPoint ?? Cursor.Position;
            _monitorHintPoint = null; // consume the hint
            return PhysicalPixelsToDips(Screen.FromPoint(pt).WorkingArea, pt);
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
            ToastPosition.Left =>
                (workArea.Left + edge, workArea.Top + (workArea.Height - actualHeight) / 2, workArea.Left - actualWidth - offScreenDistance, workArea.Top + (workArea.Height - actualHeight) / 2, true),
            ToastPosition.TopLeft =>
                (workArea.Left + edge, workArea.Top + edge, workArea.Left + edge, workArea.Top - actualHeight - offScreenDistance, false),
            ToastPosition.TopRight =>
                (workArea.Right - actualWidth - edge, workArea.Top + edge, workArea.Right - actualWidth - edge, workArea.Top - actualHeight - offScreenDistance, false),
            ToastPosition.Right =>
                (workArea.Right - actualWidth - edge, workArea.Top + (workArea.Height - actualHeight) / 2, workArea.Right + offScreenDistance, workArea.Top + (workArea.Height - actualHeight) / 2, true),
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
            ToastPosition.Left =>
                (workArea.Left - actualWidth - exitDistance, workArea.Top + (workArea.Height - actualHeight) / 2, true),
            ToastPosition.TopLeft =>
                (workArea.Left + edge, workArea.Top - actualHeight - exitDistance, false),
            ToastPosition.TopRight =>
                (workArea.Right - actualWidth - edge, workArea.Top - actualHeight - exitDistance, false),
            ToastPosition.Right =>
                (workArea.Right + exitDistance, workArea.Top + (workArea.Height - actualHeight) / 2, true),
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

    private static (double X, double Y) GetScaleForPoint(System.Drawing.Point point)
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
