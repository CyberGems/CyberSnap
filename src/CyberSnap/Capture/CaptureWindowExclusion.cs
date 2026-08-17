using System.Windows.Forms;
using CyberSnap.Native;

namespace CyberSnap.Capture;

internal static class CaptureWindowExclusion
{
    private readonly record struct HiddenWindow(IntPtr Handle, bool WasTopmost);
    private sealed record RegisteredWindow(IntPtr Handle, Func<Rectangle>? BoundsProvider, bool HasDisplayAffinity);

    private static readonly object Sync = new();
    private static readonly List<RegisteredWindow> RegisteredWindows = new();

    public static void Apply(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated)
            return;

        Apply(form.Handle);
    }

    public static void Apply(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        bool hasAffinity = false;
        try
        {
            hasAffinity = User32.SetWindowDisplayAffinity(handle, User32.WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            // Best-effort only. Older Windows builds or unusual window styles can reject this.
        }

        Register(handle, hasAffinity);
    }

    public static void SetLogicalBounds(IntPtr handle, Func<Rectangle>? boundsProvider)
    {
        if (handle == IntPtr.Zero)
            return;

        lock (Sync)
        {
            PruneDeadHandles();
            int index = RegisteredWindows.FindIndex(window => window.Handle == handle);
            bool hasAffinity = index >= 0 && RegisteredWindows[index].HasDisplayAffinity;
            var registered = new RegisteredWindow(handle, boundsProvider, hasAffinity);
            if (index >= 0)
                RegisteredWindows[index] = registered;
            else
                RegisteredWindows.Add(registered);
        }
    }

    public static void Unregister(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        lock (Sync)
        {
            RegisteredWindows.RemoveAll(window => window.Handle == handle);
        }
    }

    public static T RunWithoutIntersectingWindows<T>(Rectangle captureRegion, Func<T> capture)
    {
        var hiddenHandles = HideIntersectingWindows(captureRegion);
        try
        {
            return capture();
        }
        finally
        {
            RestoreWindows(hiddenHandles);
        }
    }

    public static void RunWithoutIntersectingWindows(Rectangle captureRegion, Action capture)
    {
        var hiddenHandles = HideIntersectingWindows(captureRegion);
        try
        {
            capture();
        }
        finally
        {
            RestoreWindows(hiddenHandles);
        }
    }

    public static void Register(IntPtr handle, bool hasDisplayAffinity = false)
    {
        lock (Sync)
        {
            PruneDeadHandles();
            int index = RegisteredWindows.FindIndex(window => window.Handle == handle);
            if (index < 0)
            {
                RegisteredWindows.Add(new RegisteredWindow(handle, null, hasDisplayAffinity));
            }
            else if (hasDisplayAffinity && !RegisteredWindows[index].HasDisplayAffinity)
            {
                RegisteredWindows[index] = RegisteredWindows[index] with { HasDisplayAffinity = true };
            }
        }
    }

    private static List<HiddenWindow> HideIntersectingWindows(Rectangle captureRegion)
    {
        List<RegisteredWindow> windows;
        lock (Sync)
        {
            PruneDeadHandles();
            windows = RegisteredWindows.ToList();
        }

        var hiddenHandles = new List<HiddenWindow>();
        foreach (var window in windows)
        {
            if (!ShouldHide(window, captureRegion))
                continue;

            var handle = window.Handle;
            var wasTopmost = (User32.GetWindowLongA(handle, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
            if (User32.ShowWindow(handle, User32.SW_HIDE))
                hiddenHandles.Add(new HiddenWindow(handle, wasTopmost));
        }

        if (hiddenHandles.Count > 0)
            Thread.Sleep(16);

        return hiddenHandles;
    }

    private static bool ShouldHide(RegisteredWindow window, Rectangle captureRegion)
    {
        // When WDA_EXCLUDEFROMCAPTURE is successfully applied, DWM and DXGI automatically
        // exclude this window from screen captures without requiring SW_HIDE or DWM sleeps.
        if (window.HasDisplayAffinity)
            return false;

        var handle = window.Handle;
        if (handle == IntPtr.Zero || !User32.IsWindow(handle) || !User32.IsWindowVisible(handle))
            return false;

        Rectangle bounds;
        if (window.BoundsProvider is not null)
        {
            try
            {
                bounds = window.BoundsProvider();
            }
            catch
            {
                bounds = Rectangle.Empty;
            }
        }
        else
        {
            if (!User32.GetWindowRect(handle, out var rect))
                return false;
            bounds = rect.ToRectangle();
        }

        return bounds.Width > 0
            && bounds.Height > 0
            && captureRegion.IntersectsWith(bounds);
    }

    private static void RestoreWindows(List<HiddenWindow> windows)
    {
        foreach (var window in windows)
        {
            var handle = window.Handle;
            if (handle == IntPtr.Zero || !User32.IsWindow(handle))
                continue;

            User32.ShowWindow(handle, User32.SW_SHOWNOACTIVATE);
            if (window.WasTopmost)
            {
                User32.SetWindowPos(handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            }
        }
    }

    private static void PruneDeadHandles()
    {
        RegisteredWindows.RemoveAll(static window => window.Handle == IntPtr.Zero || !User32.IsWindow(window.Handle));
    }
}
