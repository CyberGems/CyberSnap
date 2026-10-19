using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private readonly DispatcherTimer _activeMonitorTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private IntPtr _observedForeground;
    private string? _observedForegroundMonitor;
    private string? _pendingForegroundMonitor;
    private long _foregroundStableSince;

    private (IntPtr Window, Forms.Screen Screen)? GetForegroundMonitor(Forms.Screen[] screens)
    {
        var hwnd = Native.User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Native.User32.IsWindowVisible(hwnd) || Native.User32.IsIconic(hwnd)
            || hwnd == new System.Windows.Interop.WindowInteropHelper(this).Handle
            || Forms.Control.FromHandle(hwnd) is Forms.ToolStripDropDown) return null;
        var name = new char[256];
        int length = Native.User32.GetClassNameW(hwnd, name, name.Length);
        string windowClass = new(name, 0, length);
        if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768") return null;
        var foregroundScreen = Forms.Screen.FromHandle(hwnd);
        var screen = screens.FirstOrDefault(item => item.DeviceName == foregroundScreen.DeviceName);
        return screen is null ? null : (hwnd, screen);
    }

    private Forms.Screen ResolvePlacementScreen(Forms.Screen[] screens)
    {
        if (_settings.WidgetMonitorIndex >= 0 && _settings.WidgetMonitorIndex < screens.Length)
            return screens[_settings.WidgetMonitorIndex];
        // Layout, expansion and preference refreshes preserve the current monitor.
        // Only a foreground change, a deliberate drag or a topology change moves it.
        return screens.FirstOrDefault(screen => screen.DeviceName == _placementMonitorDevice)
            ?? GetForegroundMonitor(screens)?.Screen
            ?? ResolveTargetScreen(screens, -1, Forms.Control.MousePosition);
    }

    private void FollowActiveMonitorTick(object? sender, EventArgs e)
    {
        CloseMenuOutsideForeground();
        if (_settings.WidgetMonitorIndex != -1 || !IsVisible || !IsLoaded) return;
        var foreground = GetForegroundMonitor(PopupWindowHelper.GetSortedScreens());
        if (foreground is { } value)
            ObserveForegroundMonitor(value.Window, value.Screen, Environment.TickCount64);
    }

    private void CloseMenuOutsideForeground()
    {
        if (_contextMenu is not { Visible: true, AutoClose: true } menu) return;
        var hwnd = Native.User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        var popup = Forms.Control.FromHandle(hwnd) as Forms.ToolStripDropDown;
        while (popup is not null)
        {
            if (ReferenceEquals(popup, menu)) return;
            popup = popup.OwnerItem?.Owner as Forms.ToolStripDropDown;
        }
        // WinForms dismisses on another process receiving foreground, but its
        // modal filter can miss activation of a WPF window on this same thread.
        menu.Close(Forms.ToolStripDropDownCloseReason.AppFocusChange);
    }

    private void ObserveForegroundMonitor(IntPtr hwnd, Forms.Screen screen, long now)
    {
        if (_settings.WidgetMonitorIndex != -1) return;
        if (_observedForeground != hwnd || _observedForegroundMonitor != screen.DeviceName)
        {
            _observedForeground = hwnd;
            _observedForegroundMonitor = screen.DeviceName;
            _pendingForegroundMonitor = screen.DeviceName;
            _foregroundStableSince = now;
        }
        if (!IsVisible || _mouseInWindow || _isExpanded || _contextMenuOpen || _isDragArmed || _isDragging
            || _suppressHoverExpand || _awaitingSessionRestore || _sessionRestoreInProgress) return;
        if (_pendingForegroundMonitor is null || now - _foregroundStableSince < 400) return;
        _pendingForegroundMonitor = null;
        if (_placementMonitorDevice != screen.DeviceName) PositionWindowOnScreen(screen);
    }

    private void ResetActiveMonitorAfterDrag()
    {
        var foreground = GetForegroundMonitor(PopupWindowHelper.GetSortedScreens());
        _observedForeground = foreground?.Window ?? IntPtr.Zero;
        _observedForegroundMonitor = foreground?.Screen.DeviceName;
        _pendingForegroundMonitor = null;
    }
}
