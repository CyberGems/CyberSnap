using System.Windows;
using System.Windows.Interop;
using CyberSnap.Native;

namespace CyberSnap.Helpers;

/// <summary>
/// Alt+F4 is the suite-wide explicit quit gesture. CyberSnap keeps running in the
/// tray, so the X only closes the window; when the user asks for the close through the
/// system (Alt+F4, the system menu, a system frame's X) while Alt is physically down,
/// the window still goes through its own confirmations and then the app exits instead
/// of staying in the background. The key state is read from Windows because WPF and
/// WinForms report Alt+F4 as the same user close as the X.
/// Windows identifies that gesture by the message: Alt+F4 arrives as
/// WM_SYSCOMMAND/SC_CLOSE, while a window the app closes on its own (a preview replaced
/// by the next capture, the widget leaving the screen) sends WM_CLOSE directly. Only
/// the system close may exit, so an automatic close that happens to land while Alt is
/// held never takes the whole app down.
/// </summary>
internal static class AltF4Exit
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    private static bool _exiting;

    private static bool IsRequested()
    {
        try
        {
            return (User32.GetAsyncKeyState(User32.VK_MENU) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Attach(Window window)
    {
        var systemClose = false;
        var requested = false;

        window.SourceInitialized += (_, _) =>
        {
            try
            {
                if (PresentationSource.FromVisual(window) is not HwndSource source)
                    return;
                source.AddHook(TrackSystemClose);
            }
            catch
            {
                // A failed hook only costs the Alt+F4 exit; the window keeps working.
            }

            IntPtr TrackSystemClose(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_CLOSE)
                    systemClose = true;
                return IntPtr.Zero;
            }
        };

        window.Closing += (_, _) =>
        {
            requested = systemClose && IsRequested();
            systemClose = false;
        };
        window.Closed += (_, _) =>
        {
            if (!requested)
                return;
            requested = false;
            ExitApplication();
        };
    }

    public static void Attach(System.Windows.Forms.Form form)
    {
        var tracker = new SystemCloseTracker();
        var requested = false;

        void OnHandleCreated(object? sender, EventArgs e) => tracker.Track(form.Handle);

        form.HandleCreated += OnHandleCreated;
        if (form.IsHandleCreated)
            tracker.Track(form.Handle);

        form.FormClosing += (_, _) => requested = tracker.ConsumeSystemClose() && IsRequested();
        form.FormClosed += (_, _) =>
        {
            form.HandleCreated -= OnHandleCreated;
            tracker.Release();
            if (!requested)
                return;
            requested = false;
            ExitApplication();
        };
    }

    private static void ExitApplication()
    {
        if (_exiting)
            return;
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return;
        _exiting = true;
        // Shut down after this window finishes unwinding: calling it from inside its own
        // Closed handler tears the dispatcher down mid-teardown. The flag is released when
        // the shutdown returns, so a shutdown cancelled by a window keeps Alt+F4 usable.
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                app.Shutdown();
            }
            finally
            {
                _exiting = false;
            }
        }));
    }

    /// <summary>
    /// Watches a WinForms window's messages for the system close. Attached to the handle
    /// (not the form) so a recreated handle is tracked again, and every close consumes the
    /// flag once: one system close request maps to at most one exit.
    /// </summary>
    private sealed class SystemCloseTracker : System.Windows.Forms.NativeWindow
    {
        private bool _systemClose;

        public void Track(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            if (Handle != IntPtr.Zero && Handle != handle)
            {
                try { ReleaseHandle(); } catch { }
            }
            AssignHandle(handle);
        }

        public bool ConsumeSystemClose()
        {
            var seen = _systemClose;
            _systemClose = false;
            return seen;
        }

        public void Release()
        {
            try
            {
                if (Handle != IntPtr.Zero)
                    ReleaseHandle();
            }
            catch
            {
                // The window is already gone; nothing is left to unhook.
            }
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_CLOSE)
                _systemClose = true;
            base.WndProc(ref m);
        }
    }
}
