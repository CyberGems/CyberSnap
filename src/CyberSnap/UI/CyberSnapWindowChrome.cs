using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using CyberSnap.Native;
using CyberSnap.Services;

namespace CyberSnap.UI;

public static class CyberSnapWindowChrome
{
    private const double DefaultCornerRadius = 10;

    /// <summary>
    /// Brings a freshly shown result window to the foreground. A single Activate()
    /// fails silently when another app still owns the foreground (then Enter/Escape
    /// never reach the window), so retry briefly until it sticks.
    /// Trusts the OS foreground window, not WPF's IsActive: IsActive can read true
    /// from the first millisecond while the window is visibly behind another app,
    /// which used to take the "already foreground" exit without attempting anything.
    /// </summary>
    public static void EnsureForeground(Window window, IInputElement? focusTarget = null)
    {
        if (window is null)
            return;

        int remaining = 4;
        int notLoadedTicks = 0;
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!window.IsLoaded)
                {
                    // Pre-first-Loaded is transient; a closed window never loads.
                    if (++notLoadedTicks > 40)
                        timer?.Stop();
                    return;
                }
                if (IsEffectivelyForeground(window))
                {
                    timer?.Stop();
                    try { window.Topmost = false; } catch { }
                    if (focusTarget is not null)
                    {
                        try { focusTarget.Focus(); } catch { }
                    }
                    return;
                }
                // Independent steps: a failing Topmost flip must not kill the
                // Activate retries (and vice versa). The old single try/catch
                // stopped the timer silently on the first hiccup.
                try { window.Topmost = true; }
                catch (Exception ex)
                {
                    AppDiagnostics.LogWarning("window.topmost-flip-failed",
                        $"Could not re-pin {window.GetType().Name} topmost.", ex);
                }
                try { window.Activate(); }
                catch (Exception ex)
                {
                    AppDiagnostics.LogWarning("window.activate-failed",
                        $"Activate threw for {window.GetType().Name}.", ex);
                }
                if (IsEffectivelyForeground(window))
                {
                    timer?.Stop();
                    try { window.Topmost = false; } catch { }
                    if (focusTarget is not null)
                    {
                        try { focusTarget.Focus(); } catch { }
                    }
                    return;
                }
                if (--remaining <= 0)
                {
                    timer?.Stop();
                    if (TryForceForeground(window))
                    {
                        try { window.Topmost = false; } catch { }
                        if (focusTarget is not null)
                        {
                            try { focusTarget.Focus(); } catch { }
                        }
                        AppDiagnostics.LogWarning("window.foreground-forced",
                            $"Normal activation failed for {window.GetType().Name}; thread-input forcing succeeded.");
                        return;
                    }
                    // Do NOT demote here: dropping Topmost on a never-activated
                    // window can restack it behind everything. Leave it pinned;
                    // the one-shot below clears it once the user actually sees it.
                    void ClearOnFirstActivated(object? s, EventArgs e)
                    {
                        try { window.Topmost = false; } catch { }
                        try { window.Activated -= ClearOnFirstActivated; } catch { }
                    }
                    try { window.Activated += ClearOnFirstActivated; } catch { }
                    try { window.Closed += (_, _) => timer?.Stop(); } catch { }
                    AppDiagnostics.LogWarning("window.foreground-giveup",
                        $"Foreground retries exhausted for {window.GetType().Name}; IsActive={window.IsActive}. Kept topmost until first activation.");
                }
            }
            catch (Exception ex)
            {
                timer?.Stop();
                AppDiagnostics.LogWarning("window.foreground-tick-failed",
                    $"Foreground retry tick failed for {window.GetType().Name}.", ex);
            }
        };
        timer.Start();
    }

    /// <summary>
    /// True only when WPF and the OS agree the window is foreground. Guards against
    /// the cold-show state where IsActive reads true while GetForegroundWindow still
    /// points at another app.
    /// </summary>
    private static bool IsEffectivelyForeground(Window window)
    {
        try
        {
            if (!window.IsActive)
                return false;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return false;
            return User32.GetForegroundWindow() == hwnd;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Last resort: borrow the foreground thread's input so SetForegroundWindow is
    /// honored (same technique as the recording overlay's hotkey reclaim). Only used
    /// after the polite retries failed, for a window the user explicitly summoned.
    /// </summary>
    private static bool TryForceForeground(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !window.IsLoaded)
                return false;
            IntPtr fg = User32.GetForegroundWindow();
            if (fg == hwnd)
                return true;
            uint fgThread = fg != IntPtr.Zero ? User32.GetWindowThreadProcessId(fg, out _) : 0;
            uint thisThread = Kernel32.GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != thisThread)
                    attached = User32.AttachThreadInput(fgThread, thisThread, true);
                User32.SetForegroundWindow(hwnd);
                User32.SetFocus(hwnd);
            }
            finally
            {
                if (attached)
                    User32.AttachThreadInput(fgThread, thisThread, false);
            }
            return IsEffectivelyForeground(window);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("window.force-foreground-failed",
                $"Thread-input forcing failed for {window.GetType().Name}.", ex);
            return false;
        }
    }

    public static void Apply(Window window)
    {
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(DefaultCornerRadius),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(8),
            UseAeroCaptionButtons = false
        });

        ApplyRoundedCorners(window, DefaultCornerRadius);
    }

    public static void ApplyRoundedCorners(Window window, double radius)
    {
        void ApplyCurrentRegion()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            Dwm.TrySetWindowCornerPreference(hwnd, Dwm.DWMWCP_ROUND);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return;

            SetRoundedWindowRegion(window, hwnd, radius);
        }

        window.SourceInitialized += (_, _) => ApplyCurrentRegion();
        window.SizeChanged += (_, _) => ApplyCurrentRegion();
        window.Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
                User32.SetWindowRgn(hwnd, IntPtr.Zero, true);
        };
    }

    private static void SetRoundedWindowRegion(Window window, IntPtr hwnd, double radius)
    {
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0)
            return;

        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * transform.M11));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * transform.M22));
        int diameterX = Math.Max(1, (int)Math.Round(radius * 2 * transform.M11));
        int diameterY = Math.Max(1, (int)Math.Round(radius * 2 * transform.M22));

        var region = Gdi32.CreateRoundRectRgn(0, 0, width + 1, height + 1, diameterX, diameterY);
        if (region == IntPtr.Zero)
            return;

        if (User32.SetWindowRgn(hwnd, region, true) == 0)
            Gdi32.DeleteObject(region);
    }
}
