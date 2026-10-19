using System.Runtime.InteropServices;
using CyberSnap.Services;
using CyberSnap.Native;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private delegate IntPtr MonitorMouseProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetMonitorMouseHook(int id, MonitorMouseProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private IntPtr _monitorPointerHook;
    private MonitorMouseProc? _monitorPointerCallback;
    private bool _monitorPointerAttempted;
    private (IntPtr Window, User32.POINT Point)? _monitorPointerActivation;

    private void UpdateMonitorPointerObserver()
    {
        if (_settings.WidgetMonitorIndex != -1 || !IsVisible || !IsLoaded)
        {
            StopMonitorPointerObserver();
            return;
        }
        if (_monitorPointerAttempted) return;
        _monitorPointerAttempted = true;
        _monitorPointerCallback = MonitorMouseCallback;
        _monitorPointerHook = SetMonitorMouseHook(14 /* WH_MOUSE_LL */, _monitorPointerCallback, Kernel32.GetModuleHandle(null), 0);
        if (_monitorPointerHook == IntPtr.Zero)
            AppDiagnostics.LogWarning("widget.monitor-pointer", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private IntPtr MonitorMouseCallback(int code, IntPtr message, IntPtr data)
    {
        // Observe button-down only; never intercept input or follow mouse motion.
        // The hook runs on this dispatcher. All layout/focus work is deferred.
        if (code >= 0 && message.ToInt64() is 0x0201 or 0x0204 or 0x0207)
            RecordMonitorPointerActivation(Marshal.PtrToStructure<User32.POINT>(data));
        return User32.CallNextHookEx(_monitorPointerHook, code, message, data);
    }

    private void RecordMonitorPointerActivation(User32.POINT point)
    {
        var hwnd = User32.GetAncestor(User32.WindowFromPoint(point), 2 /* GA_ROOT */);
        _monitorPointerActivation = (hwnd, point);
    }

    private void ConsumeMonitorPointerActivation()
    {
        var activation = _monitorPointerActivation;
        _monitorPointerActivation = null;
        if (activation is not { } value || value.Window == IntPtr.Zero) return;
        bool desktop = IsDesktopWindowClass(MonitorWindowClass(value.Window));
        var foreground = User32.GetForegroundWindow();
        // Ignore non-activating shell controls and clicks on covered windows.
        if (desktop ? !IsDesktopWindowClass(MonitorWindowClass(foreground)) : value.Window != foreground) return;
        // WorkerW can receive the click while Progman receives foreground.
        ObserveMonitorPointerActivation(foreground,
            Forms.Screen.FromPoint(new System.Drawing.Point(value.Point.X, value.Point.Y)), desktop);
    }

    private void StopMonitorPointerObserver()
    {
        if (_monitorPointerHook != IntPtr.Zero)
        {
            if (!User32.UnhookWindowsHookEx(_monitorPointerHook))
            {
                AppDiagnostics.LogWarning("widget.monitor-pointer-unhook", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                return; // Keep the delegate alive until the hook is released.
            }
            _monitorPointerHook = IntPtr.Zero;
        }
        _monitorPointerCallback = null;
        _monitorPointerAttempted = false;
        _monitorPointerActivation = null;
        CancelPendingForegroundMonitor();
        _contextForeground = IntPtr.Zero;
        _contextForegroundMonitor = null;
        _desktopMonitor = null;
        _minimizeFallbackWindow = IntPtr.Zero;
        _minimizeTransitionUntil = 0;
        _explicitForeground = IntPtr.Zero;
    }
}
