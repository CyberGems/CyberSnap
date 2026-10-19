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
    private IntPtr _contextForeground;
    private string? _contextForegroundMonitor;
    private string? _desktopMonitor;
    private IntPtr _minimizeFallbackWindow;
    private long _minimizeTransitionUntil;
    private IntPtr _explicitForeground;

    private (IntPtr Window, Forms.Screen Screen, bool Desktop)? GetForegroundMonitor(Forms.Screen[] screens)
    {
        var hwnd = Native.User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Native.User32.IsWindowVisible(hwnd) || Native.User32.IsIconic(hwnd)
            || hwnd == new System.Windows.Interop.WindowInteropHelper(this).Handle
            || Forms.Control.FromHandle(hwnd) is Forms.ToolStripDropDown) return null;
        string windowClass = MonitorWindowClass(hwnd);
        bool desktop = IsDesktopWindowClass(windowClass);
        if (windowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768"
            or "MultitaskingViewFrame" or "TaskSwitcherWnd") return null;
        if (desktop)
        {
            // The shell desktop can span all displays. Its HWND bounds do not
            // identify the desktop the user clicked, and cursor motion is not focus.
            var desktopScreen = screens.FirstOrDefault(screen => screen.DeviceName == _desktopMonitor)
                ?? screens.FirstOrDefault(screen => screen.DeviceName == _placementMonitorDevice)
                ?? ResolveTargetScreen(screens, -1, Forms.Control.MousePosition);
            return (hwnd, desktopScreen, true);
        }
        var foregroundScreen = Forms.Screen.FromHandle(hwnd);
        var screen = screens.FirstOrDefault(item => item.DeviceName == foregroundScreen.DeviceName);
        return screen is null ? null : (hwnd, screen, false);
    }

    private static string MonitorWindowClass(IntPtr hwnd)
    {
        var name = new char[256];
        int length = Native.User32.GetClassNameW(hwnd, name, name.Length);
        return new string(name, 0, length);
    }

    private static bool IsDesktopWindowClass(string name) => name is "Progman" or "WorkerW" or "SHELLDLL_DefView";

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
        UpdateMonitorPointerObserver();
        if (_settings.WidgetMonitorIndex != -1 || !IsVisible || !IsLoaded) return;
        ConsumeMonitorPointerActivation();
        if ((GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0)
        {
            PauseForKeyboardFocusSwitch();
            return;
        }
        var screens = PopupWindowHelper.GetSortedScreens();
        var foreground = GetForegroundMonitor(screens);
        if (foreground is { } value)
            ObserveForegroundContext(value.Window, value.Screen, value.Desktop,
                _contextForeground != IntPtr.Zero && Native.User32.IsIconic(_contextForeground), Environment.TickCount64);
        else
            CancelPendingForegroundMonitor();
    }

    private void ObserveForegroundContext(IntPtr hwnd, Forms.Screen screen, bool desktop, bool previousMinimized, long now)
    {
        string foregroundMonitor = screen.DeviceName;
        bool explicitlyActivated = _explicitForeground == hwnd;
        _explicitForeground = IntPtr.Zero;
        if (!desktop && _contextForeground == hwnd && _minimizeFallbackWindow == hwnd
            && _contextForegroundMonitor != foregroundMonitor)
        {
            // Moving the replacement window is also an intentional selection,
            // including a keyboard move that does not change foreground HWND.
            _desktopMonitor = null;
            _minimizeFallbackWindow = IntPtr.Zero;
            _minimizeTransitionUntil = 0;
        }
        if (_contextForeground != hwnd)
        {
            if (!explicitlyActivated && (previousMinimized
                || (_minimizeFallbackWindow != IntPtr.Zero && now < _minimizeTransitionUntil)))
            {
                // Windows may activate an unrelated window on another display
                // after minimizing. Keep the desktop of the minimized window
                // until the user clicks a desktop/window or changes foreground.
                if (previousMinimized)
                {
                    _desktopMonitor = _contextForegroundMonitor ?? _placementMonitorDevice;
                    _minimizeTransitionUntil = now + 800;
                }
                _minimizeFallbackWindow = hwnd;
            }
            else if (!desktop)
            {
                _desktopMonitor = null;
                _minimizeFallbackWindow = IntPtr.Zero;
                _minimizeTransitionUntil = 0;
            }
        }
        if (desktop)
            _desktopMonitor ??= _contextForegroundMonitor ?? _placementMonitorDevice ?? screen.DeviceName;
        if (desktop || hwnd == _minimizeFallbackWindow)
            screen = PopupWindowHelper.GetSortedScreens().FirstOrDefault(item => item.DeviceName == _desktopMonitor) ?? screen;
        _contextForeground = hwnd;
        _contextForegroundMonitor = desktop ? screen.DeviceName : foregroundMonitor;
        ObserveForegroundMonitor(hwnd, screen, now);
    }

    private void ObserveMonitorPointerActivation(IntPtr hwnd, Forms.Screen screen, bool desktop)
    {
        if (hwnd == new System.Windows.Interop.WindowInteropHelper(this).Handle
            || Forms.Control.FromHandle(hwnd) is Forms.ToolStripDropDown) return;
        _desktopMonitor = desktop ? screen.DeviceName : null;
        _minimizeFallbackWindow = IntPtr.Zero;
        _minimizeTransitionUntil = 0;
        _explicitForeground = hwnd;
        CancelPendingForegroundMonitor();
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
        if (!IsVisible || _mouseInWindow || _isExpanded || _contextMenuOpen || _isDragArmed || _isDragging
            || _suppressHoverExpand || _awaitingSessionRestore || _sessionRestoreInProgress)
        {
            CancelPendingForegroundMonitor();
            return;
        }
        if (_observedForeground != hwnd || _observedForegroundMonitor != screen.DeviceName)
        {
            _observedForeground = hwnd;
            _observedForegroundMonitor = screen.DeviceName;
            _pendingForegroundMonitor = screen.DeviceName;
            _foregroundStableSince = now;
        }
        if (_pendingForegroundMonitor is null || now - _foregroundStableSince < 400) return;
        _pendingForegroundMonitor = null;
        if (_placementMonitorDevice != screen.DeviceName) PositionWindowOnScreen(screen);
    }

    private void CancelPendingForegroundMonitor()
    {
        _observedForeground = IntPtr.Zero;
        _observedForegroundMonitor = null;
        _pendingForegroundMonitor = null;
    }

    private void PauseForKeyboardFocusSwitch()
    {
        CancelPendingForegroundMonitor();
        // An intentional keyboard switch must also release the automatic focus
        // chosen by Windows after a minimize, even if its HWND is selected again.
        _contextForeground = IntPtr.Zero;
        _minimizeFallbackWindow = IntPtr.Zero;
        _minimizeTransitionUntil = 0;
    }

    private void ResetActiveMonitorAfterDrag()
    {
        var foreground = GetForegroundMonitor(PopupWindowHelper.GetSortedScreens());
        _observedForeground = foreground?.Window ?? IntPtr.Zero;
        _observedForegroundMonitor = foreground?.Screen.DeviceName;
        _pendingForegroundMonitor = null;
        _contextForeground = _observedForeground;
        _contextForegroundMonitor = _observedForegroundMonitor;
        _minimizeFallbackWindow = IntPtr.Zero;
        _minimizeTransitionUntil = 0;
        _desktopMonitor = null;
    }
}
