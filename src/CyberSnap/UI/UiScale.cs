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
    /// <summary>Windows hooked to re-apply their scale when the user moves the UI-scale
    /// slider. Weakly held so closed windows never leak.</summary>
    private static readonly ConditionalWeakTable<Window, object> LiveScaleHooks = new();

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
    /// Primary-anchored constant-physical model for chrome windows: content and bounds render
    /// at design × UiScale × the PRIMARY monitor's DPI, captured at process start. Windows
    /// therefore keep one physical size across every monitor (user-verified ideal), and the
    /// size is immune to WPF per-monitor DPI adoption races — both contexts produce the same
    /// physical result. The user's UI-scale slider is the only growth knob and re-applies to
    /// open windows live. Self-managed surfaces (widget, toast, recording bar) opt out via
    /// <paramref name="scaleWindowBounds"/> = false.
    /// </summary>
    public static void ApplyToWindow(Window window, FrameworkElement root, bool scaleWindowBounds)
    {
        var scale = Normalize(Current);
        if (BaseDpi <= 0)
            BaseDpi = GetWindowMonitorScale(window);

        double effective = scale;
        if (scaleWindowBounds)
        {
            var adopted = GetWindowMonitorScale(window);
            if (adopted > 0)
                effective = scale * BaseDpi / adopted;

            root.LayoutTransform = Math.Abs(effective - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(effective, effective);
            EnsureLiveScaleHook(window, root, scaleWindowBounds);
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

    /// <summary>Primary monitor DPI at process start; the anchor every chrome window
    /// renders at, on any monitor. Captured once from the first pre-show window.</summary>
    private static double BaseDpi;

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

    private static void EnsureLiveScaleHook(Window window, FrameworkElement root, bool scaleWindowBounds)
    {
        if (LiveScaleHooks.TryGetValue(window, out _))
            return;
        LiveScaleHooks.Add(window, new object());

        void OnScaleChanged(double _)
        {
            if (!window.IsLoaded && !window.IsVisible)
                return;
            ApplyToWindow(window, root, scaleWindowBounds);
        }

        Changed += OnScaleChanged;
        window.Closed += (_, _) => Changed -= OnScaleChanged;
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
