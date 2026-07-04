using System.Drawing;
using CyberSnap.Models;
using CyberSnap.Native;

namespace CyberSnap.Capture;

/// <summary>
/// Provides point lookup for window-only snapping.
/// Resolves the top-most snappable top-level window at the pointer.
/// </summary>
public static class WindowDetector
{
    private enum WindowHitResult
    {
        PassThrough,
        Snappable,
        Blocked
    }

    private sealed class SimpleWindowInfo
    {
        public IntPtr Handle { get; }
        public Rectangle Rectangle { get; }
        public bool IsWindow { get; }

        public SimpleWindowInfo(IntPtr handle, Rectangle rect, bool isWindow)
        {
            Handle = handle;
            Rectangle = rect;
            IsWindow = isWindow;
        }
    }

    private static readonly List<SimpleWindowInfo> CachedWindows = new();
    private static readonly object CacheLock = new();

    private static readonly HashSet<IntPtr> IgnoredHandles = new();
    private static readonly object IgnoredHandleLock = new();
    private static readonly string[] IgnoredWindowClasses =
    {
        "Progman",
        "WorkerW",
        "NotifyIconOverflowWindow",
        "tooltips_class32",
        "#32768"
    };

    public static Rectangle GetDetectionRectAtPoint(
        Point screenPoint,
        Rectangle virtualBounds,
        WindowDetectionMode mode)
    {
        if (mode == WindowDetectionMode.Off)
            return Rectangle.Empty;

        return GetTopLevelWindowRectAtPoint(screenPoint, virtualBounds);
    }

    public static Rectangle GetWindowRectAtPoint(Point screenPoint, Rectangle virtualBounds)
        => GetDetectionRectAtPoint(screenPoint, virtualBounds, WindowDetectionMode.WindowOnly);

    /// <summary>Populates the window bounds cache on a background thread.</summary>
    public static void SnapshotWindows(Rectangle virtualBounds)
    {
        var tempWindows = new List<SimpleWindowInfo>();
        var seen = new HashSet<IntPtr>();

        User32.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !seen.Add(hwnd))
                return true;

            if (IsIgnoredWindowHandle(hwnd) || !User32.IsWindowVisible(hwnd) || Dwm.IsWindowCloaked(hwnd))
                return true;

            int style = User32.GetWindowLongA(hwnd, User32.GWL_STYLE);
            int exStyle = User32.GetWindowLongA(hwnd, User32.GWL_EXSTYLE);
            string className = GetClassName(hwnd);
            string title = GetWindowTitle(hwnd);

            // Skip overlays (Nvidia GeForce Overlay, etc.)
            if (!string.IsNullOrEmpty(className) &&
                (string.Equals(className, "CEF-OSC-WIDGET", StringComparison.OrdinalIgnoreCase) ||
                 IgnoredWindowClasses.Any(ignored => string.Equals(ignored, className, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }

            // Skip non-activatable tool windows
            if ((exStyle & User32.WS_EX_TOOLWINDOW) != 0 && (exStyle & User32.WS_EX_NOACTIVATE) != 0)
            {
                return true;
            }

            if (!IsSnappableWindowCandidate(style, exStyle, className, title))
            {
                return true;
            }

            var screenRect = GetSnappableBounds(hwnd);
            if (screenRect.Width <= 2 || screenRect.Height <= 2)
                return true;

            // Save the client area if it differs significantly from the main window rect (helps capture inside content cleanly)
            var clientScreenRect = GetClientRect(hwnd);
            if (clientScreenRect.Width > 2 && clientScreenRect.Height > 2)
            {
                Rectangle clientVirtual = new Rectangle(
                    clientScreenRect.X - virtualBounds.X,
                    clientScreenRect.Y - virtualBounds.Y,
                    clientScreenRect.Width,
                    clientScreenRect.Height);

                // Add client area first (top in Z-order selection precedence)
                if (clientVirtual != new Rectangle(screenRect.Left - virtualBounds.X, screenRect.Top - virtualBounds.Y, screenRect.Width, screenRect.Height))
                {
                    tempWindows.Add(new SimpleWindowInfo(hwnd, clientVirtual, false));
                }
            }

            Rectangle rect = new Rectangle(
                screenRect.Left - virtualBounds.X,
                screenRect.Top - virtualBounds.Y,
                screenRect.Width,
                screenRect.Height);

            tempWindows.Add(new SimpleWindowInfo(hwnd, rect, true));

            return true;
        }, IntPtr.Zero);

        lock (CacheLock)
        {
            CachedWindows.Clear();
            CachedWindows.AddRange(tempWindows);
        }
    }

    /// <summary>Clears the cached snapshot of window bounds.</summary>
    public static void ClearSnapshot()
    {
        lock (CacheLock)
        {
            CachedWindows.Clear();
        }
    }

    public static void RegisterIgnoredWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (IgnoredHandleLock)
            IgnoredHandles.Add(hwnd);
    }

    public static void UnregisterIgnoredWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (IgnoredHandleLock)
            IgnoredHandles.Remove(hwnd);
    }

    private static Rectangle GetTopLevelWindowRectAtPoint(Point screenPoint, Rectangle virtualBounds)
    {
        // Try the cached snapshot first (very fast memory lookup)
        lock (CacheLock)
        {
            if (CachedWindows.Count > 0)
            {
                var found = CachedWindows.FirstOrDefault(w => w.Rectangle.Contains(screenPoint));
                if (found != null)
                {
                    // Check if there is a more specific child control/window under the cursor
                    var childRect = GetControlRectAtPoint(found.Handle, screenPoint, virtualBounds);
                    if (childRect.Width > 0 && childRect.Height > 0)
                    {
                        return childRect;
                    }
                    return found.Rectangle;
                }
                return Rectangle.Empty;
            }
        }

        // Live fallback
        var pt = ToScreenPoint(screenPoint, virtualBounds);
        Rectangle detected = Rectangle.Empty;
        bool blockedByRealWindow = false;
        var seen = new HashSet<IntPtr>();

        User32.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !seen.Add(hwnd))
                return true;

            var hit = TryGetWindowRect(hwnd, pt, virtualBounds, out var rect);
            if (hit == WindowHitResult.PassThrough)
                return true;

            if (hit == WindowHitResult.Snappable)
                detected = rect;
            else
                blockedByRealWindow = true;
            return false;
        }, IntPtr.Zero);

        return detected.IsEmpty && !blockedByRealWindow
            ? GetTopLevelWindowRectFallbackFromPoint(pt, virtualBounds)
            : detected;
    }

    private static Rectangle GetTopLevelWindowRectFallbackFromPoint(User32.POINT pt, Rectangle virtualBounds)
    {
        IntPtr rawHwnd = User32.WindowFromPoint(pt);

        Span<IntPtr> visited = stackalloc IntPtr[32];
        int visitedCount = 0;
        IntPtr hwnd = rawHwnd;

        for (int depth = 0; depth < 32 && hwnd != IntPtr.Zero; depth++)
        {
            IntPtr candidate = NormalizeTopLevelWindowForHitTest(hwnd);
            if (candidate == IntPtr.Zero || visited[..visitedCount].Contains(candidate))
                break;
            visited[visitedCount++] = candidate;

            var hit = TryGetWindowRect(candidate, pt, virtualBounds, out var rect);
            if (hit == WindowHitResult.Snappable)
                return rect;
            if (hit == WindowHitResult.Blocked)
                break;

            hwnd = User32.GetWindow(candidate, User32.GW_HWNDNEXT);
        }

        return Rectangle.Empty;
    }

    private static WindowHitResult TryGetWindowRect(IntPtr hwnd, User32.POINT? point, Rectangle virtualBounds, out Rectangle rect)
    {
        rect = Rectangle.Empty;

        if (hwnd == IntPtr.Zero || IsIgnoredWindowHandle(hwnd) || !User32.IsWindowVisible(hwnd) || Dwm.IsWindowCloaked(hwnd))
            return WindowHitResult.PassThrough;

        var screenRect = GetSnappableBounds(hwnd);
        if (screenRect.Width <= 2 || screenRect.Height <= 2)
            return WindowHitResult.PassThrough;
        if (point.HasValue && !screenRect.Contains(point.Value.X, point.Value.Y))
            return WindowHitResult.PassThrough;

        int style = User32.GetWindowLongA(hwnd, User32.GWL_STYLE);
        int exStyle = User32.GetWindowLongA(hwnd, User32.GWL_EXSTYLE);
        string className = GetClassName(hwnd);
        string title = GetWindowTitle(hwnd);
        if (!IsSnappableWindowCandidate(style, exStyle, className, title))
            return IsPassThroughWindowCandidate(exStyle, className)
                ? WindowHitResult.PassThrough
                : WindowHitResult.Blocked;

        rect = new Rectangle(
            screenRect.Left - virtualBounds.X,
            screenRect.Top - virtualBounds.Y,
            screenRect.Width,
            screenRect.Height);
        return rect.Width > 2 && rect.Height > 2
            ? WindowHitResult.Snappable
            : WindowHitResult.PassThrough;
    }

    private static IntPtr NormalizeTopLevelWindowForHitTest(IntPtr hwnd)
    {
        IntPtr rootOwner = User32.GetAncestor(hwnd, User32.GA_ROOTOWNER);
        if (rootOwner != IntPtr.Zero)
            return rootOwner;

        IntPtr root = User32.GetAncestor(hwnd, User32.GA_ROOT);
        return root != IntPtr.Zero ? root : hwnd;
    }

    internal static bool IsSnappableWindowCandidate(int style, int exStyle, string className, string windowTitle)
    {
        if ((style & User32.WS_CHILD) != 0)
            return false;

        if ((style & User32.WS_DISABLED) != 0)
            return false;

        if ((exStyle & User32.WS_EX_TRANSPARENT) != 0)
            return false;

        bool isTaskbar = string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);

        if (!isTaskbar)
        {
            if ((exStyle & User32.WS_EX_NOACTIVATE) != 0 && (exStyle & User32.WS_EX_APPWINDOW) == 0)
                return false;

            if ((exStyle & User32.WS_EX_TOOLWINDOW) != 0 && (exStyle & User32.WS_EX_APPWINDOW) == 0)
                return false;
        }

        if (IgnoredWindowClasses.Any(ignored => string.Equals(ignored, className, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (string.IsNullOrWhiteSpace(windowTitle) && (exStyle & User32.WS_EX_APPWINDOW) == 0)
            return false;

        return true;
    }

    internal static bool IsPassThroughWindowCandidate(int exStyle, string className)
    {
        if ((exStyle & User32.WS_EX_TRANSPARENT) != 0)
            return true;

        return IgnoredWindowClasses.Any(ignored => string.Equals(ignored, className, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSnappableWindow(IntPtr hwnd)
    {
        int style = User32.GetWindowLongA(hwnd, User32.GWL_STYLE);
        int exStyle = User32.GetWindowLongA(hwnd, User32.GWL_EXSTYLE);
        string className = GetClassName(hwnd);
        string title = GetWindowTitle(hwnd);
        return IsSnappableWindowCandidate(style, exStyle, className, title);
    }

    internal static Rectangle ChoosePreferredBounds(Rectangle dwmRect, Rectangle rawRect)
    {
        if (rawRect.Width <= 2 || rawRect.Height <= 2)
            return dwmRect;

        if (dwmRect.Width <= 2 || dwmRect.Height <= 2)
            return rawRect;

        if (!rawRect.Contains(dwmRect))
            return dwmRect;

        int leftInset = dwmRect.Left - rawRect.Left;
        int topInset = dwmRect.Top - rawRect.Top;
        int rightInset = rawRect.Right - dwmRect.Right;
        int bottomInset = rawRect.Bottom - dwmRect.Bottom;
        int largestInset = Math.Max(Math.Max(leftInset, topInset), Math.Max(rightInset, bottomInset));

        return largestInset >= 12 ? rawRect : dwmRect;
    }

    private static Rectangle GetSnappableBounds(IntPtr hwnd)
    {
        var dwmRect = Dwm.GetExtendedFrameBounds(hwnd);
        if (!User32.GetWindowRect(hwnd, out var rawRect))
            return dwmRect;

        return ChoosePreferredBounds(dwmRect, rawRect.ToRectangle());
    }

    private static Rectangle GetControlRectAtPoint(IntPtr parentHwnd, Point screenPoint, Rectangle virtualBounds)
    {
        var pt = ToScreenPoint(screenPoint, virtualBounds);
        IntPtr bestChild = IntPtr.Zero;
        Rectangle bestRect = Rectangle.Empty;
        int bestArea = int.MaxValue;

        User32.EnumChildWindows(parentHwnd, (hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd))
                return true;

            // Get window rect
            if (!User32.GetWindowRect(hwnd, out var rawRect))
                return true;

            var rect = rawRect.ToRectangle();
            if (rect.Width <= 2 || rect.Height <= 2)
                return true;

            if (rect.Contains(pt.X, pt.Y))
            {
                int area = rect.Width * rect.Height;
                // We seek the most specific (smallest) child control containing the point
                if (area < bestArea)
                {
                    bestArea = area;
                    bestChild = hwnd;
                    bestRect = new Rectangle(
                        rect.Left - virtualBounds.X,
                        rect.Top - virtualBounds.Y,
                        rect.Width,
                        rect.Height);
                }
            }
            return true;
        }, IntPtr.Zero);

        return bestChild != IntPtr.Zero ? bestRect : Rectangle.Empty;
    }

    private static Rectangle GetClientRect(IntPtr hwnd)
    {
        if (!User32.GetClientRect(hwnd, out var rect))
            return Rectangle.Empty;

        var clientRect = rect.ToRectangle();
        var clientLeftTop = new User32.POINT(0, 0);
        if (!User32.ClientToScreen(hwnd, ref clientLeftTop))
            return Rectangle.Empty;

        return new Rectangle(clientLeftTop.X, clientLeftTop.Y, clientRect.Width, clientRect.Height);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = User32.GetWindowTextLengthW(hwnd);
        if (length <= 0)
            return string.Empty;

        var buffer = new char[length + 1];
        int copied = User32.GetWindowTextW(hwnd, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        int copied = User32.GetClassNameW(hwnd, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    private static User32.POINT ToScreenPoint(Point overlayPoint, Rectangle virtualBounds)
        => new(overlayPoint.X + virtualBounds.X, overlayPoint.Y + virtualBounds.Y);

    private static bool IsIgnoredWindowHandle(nint hwnd)
    {
        if (hwnd == 0) return false;
        lock (IgnoredHandleLock)
            return IgnoredHandles.Contains(hwnd);
    }
}
