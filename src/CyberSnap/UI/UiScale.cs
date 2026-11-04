using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CyberSnap.UI;

public static class UiScale
{
    // Real LayoutTransform factors. UI labels are renumbered one step down so the
    // former 110% (1.1) reads as 100% default; the former 100% (1.0) is labeled 90%.
    // Labels: 90%→1.0, 100%→1.1, 110%→1.2, 120%→1.3, 130%→1.4.
    public const double Default = 1.1;
    public const double Min = 1.0;
    public const double Max = 1.4;

    private static readonly ConditionalWeakTable<Window, WindowScaleState> WindowStates = new();
    /// <summary>Windows already hooked to re-derive their constant-physical scale on DPI
    /// changes. Weakly held so closed windows never leak.</summary>
    private static readonly ConditionalWeakTable<Window, object> ConstantPhysicalHooks = new();

    public static double Current { get; private set; } = Default;

    public static event Action<double>? Changed;

    public static double Normalize(double scale)
        => Math.Clamp(double.IsFinite(scale) ? scale : Default, Min, Max);

    public static void Set(double scale)
    {
        var normalized = Normalize(scale);
        if (Math.Abs(Current - normalized) < 0.001)
            return;

        Current = normalized;
        Helpers.UiChrome.SetUiScale(normalized);
        Changed?.Invoke(normalized);
    }

    /// <summary>
    /// Constant-physical scaling model (user-approved): chrome windows render at design ×
    /// UiScale regardless of the monitor's DPI, the same model the WinForms editor uses, so
    /// sibling windows keep identical physical sizes on mixed-DPI setups. Only full-chrome
    /// windows (<paramref name="scaleWindowBounds"/>) opt in; self-managed surfaces (widget,
    /// toast, recording bar) keep their own DPI handling.
    /// </summary>
    public static void ApplyToWindow(Window window, FrameworkElement root, bool scaleWindowBounds)
    {
        var scale = Normalize(Current);

        double effective = scale;
        if (scaleWindowBounds)
        {
            double monitorScale = GetWindowMonitorScale(window);
            if (monitorScale > 0)
                effective = scale / monitorScale;

            root.LayoutTransform = Math.Abs(effective - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(effective, effective);
            EnsureConstantPhysicalHooks(window, root, scaleWindowBounds);
        }
        else
        {
            root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
        }

        if (!scaleWindowBounds)
            return;

        var state = WindowStates.GetValue(window, static w => new WindowScaleState(
            w.Width,
            w.Height,
            w.MinWidth,
            w.MinHeight));

        window.MinWidth = Math.Max(320, state.MinWidth * effective);
        window.MinHeight = Math.Max(240, state.MinHeight * effective);
        if (!double.IsNaN(state.Width) && !double.IsInfinity(state.Width) && state.Width > 0)
            window.Width = state.Width * effective;
        if (!double.IsNaN(state.Height) && !double.IsInfinity(state.Height) && state.Height > 0)
            window.Height = state.Height * effective;

        ClampToCurrentMonitor(window);
    }

    private static double GetWindowMonitorScale(Window window)
    {
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (dpi > 0)
                return dpi;
        }
        catch { }
        return 1.0;
    }

    private static void EnsureConstantPhysicalHooks(Window window, FrameworkElement root, bool scaleWindowBounds)
    {
        if (ConstantPhysicalHooks.TryGetValue(window, out _))
            return;
        ConstantPhysicalHooks.Add(window, new object());

        // The render DPI at constructor time is the process/primary context; once the window
        // settles (Loaded) or moves (DpiChanged), re-derive so the physical size stays put.
        window.Loaded += (_, _) => ApplyToWindow(window, root, scaleWindowBounds);
        window.DpiChanged += (_, _) => ApplyToWindow(window, root, scaleWindowBounds);
    }

    private static void ClampToCurrentMonitor(Window window)
    {
        // Physical pixels throughout: DIP work-area conversions can misfire on mixed-DPI
        // setups (wrong monitor scale), shoving correctly placed windows off-center.
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            if (!Native.User32.GetWindowRect(hwnd, out var wr))
                return;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var work = screen.WorkingArea;
            int w = wr.Right - wr.Left;
            int h = wr.Bottom - wr.Top;
            if (w <= 0 || h <= 0)
                return;

            var (scaleX, scaleY) = PopupWindowHelper.GetScaleForPoint(
                new System.Drawing.Point(wr.Left + w / 2, wr.Top + h / 2));
            int margin = scaleX > 0 && scaleY > 0
                ? Math.Max(8, (int)Math.Round(12 * (scaleX + scaleY) / 2.0))
                : 12;

            int minLeft = work.Left + margin;
            int minTop = work.Top + margin;
            int maxLeft = work.Right - w - margin;
            int maxTop = work.Bottom - h - margin;
            int newLeft = Math.Min(Math.Max(wr.Left, minLeft), Math.Max(minLeft, maxLeft));
            int newTop = Math.Min(Math.Max(wr.Top, minTop), Math.Max(minTop, maxTop));
            if (newLeft == wr.Left && newTop == wr.Top)
                return;

            Native.User32.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                newLeft,
                newTop,
                0,
                0,
                Native.User32.SWP_NOSIZE | Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
        }
        catch { }
    }

    private sealed record WindowScaleState(double Width, double Height, double MinWidth, double MinHeight);
}
