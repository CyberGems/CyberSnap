using System.Windows;
using CyberSnap.Native;

namespace CyberSnap.Helpers;

/// <summary>
/// Alt+F4 is the suite-wide explicit quit gesture. CyberSnap keeps running in the
/// tray, so the X only closes the window; when Alt is physically down at close time
/// the window still goes through its own confirmations, then the app exits instead of
/// staying in the background. The key state is read from Windows because WPF reports
/// Alt+F4 as the same user close as the X.
/// </summary>
internal static class AltF4Exit
{
    public static bool IsRequested()
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
        var requested = false;
        window.Closing += (_, _) => requested = IsRequested();
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
        var requested = false;
        form.FormClosing += (_, _) => requested = IsRequested();
        form.FormClosed += (_, _) =>
        {
            if (!requested)
                return;
            requested = false;
            ExitApplication();
        };
    }

    private static void ExitApplication()
    {
        var app = Application.Current;
        if (app is null)
            return;
        // Close the window first: shutting down from inside its Closed handler would
        // tear the dispatcher down while WPF is still unwinding that form.
        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }
}
