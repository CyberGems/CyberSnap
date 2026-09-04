using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using CyberSnap.Native;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>
/// Opens the Windows tray-icon settings page and locates the notification area
/// so first-run UI can point at it. Apps cannot pin a NotifyIcon themselves.
/// </summary>
internal static class WindowsNotificationArea
{
    private const string TaskbarSettingsUri = "ms-settings:taskbar";

    /// <summary>
    /// Win10 Taskbar settings has no ms-settings URI for the nested icon list.
    /// After opening the parent page we invoke this hyperlink via UI Automation.
    /// </summary>
    private static readonly string[] IconListLinkFragments =
    {
        "icons appear on the taskbar",
        "iconos que aparecer"
    };

    private static readonly string[] IconListPageFragments =
    {
        "Always show all icons in the notification area",
        "Mostrar siempre todos los iconos"
    };

    private static readonly string[] SettingsWindowTitles =
    {
        "Settings",
        "Configuración"
    };

    public enum TaskbarEdge
    {
        Bottom,
        Top,
        Left,
        Right
    }

    public static void OpenIconSettings()
    {
        try
        {
            // Modern Settings app (same launch pattern as Hotkeys). The legacy
            // CLSID applet is hosted by explorer.exe and can crash Explorer.
            // There is no ms-settings URI for the nested icon-list page, so we
            // open Taskbar settings and invoke the hyperlink on that page.
            TryStartUri(TaskbarSettingsUri);
            _ = Task.Run(TryOpenIconListPage);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.open-icon-settings", ex.Message, ex);
        }
    }

    private static void TryOpenIconListPage()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var window = FindSettingsWindow();
                if (window != null)
                {
                    if (HasNamedFragment(window, IconListPageFragments))
                        return;
                    if (TryInvokeIconListLink(window))
                        return;
                }

                Thread.Sleep(200);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.open-icon-list", ex.Message, ex);
        }
    }

    private static AutomationElement? FindSettingsWindow()
    {
        var root = AutomationElement.RootElement;
        foreach (var title in SettingsWindowTitles)
        {
            var window = root.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, title));
            if (window != null)
                return window;
        }

        return null;
    }

    private static bool TryInvokeIconListLink(AutomationElement window)
    {
        var links = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));

        foreach (AutomationElement link in links)
        {
            var name = link.Current.Name;
            if (string.IsNullOrWhiteSpace(name) || !ContainsAny(name, IconListLinkFragments))
                continue;
            if (link.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                && pattern is InvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }

        return false;
    }

    private static bool HasNamedFragment(AutomationElement window, string[] fragments)
    {
        var nodes = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement node in nodes)
        {
            var name = node.Current.Name;
            if (!string.IsNullOrWhiteSpace(name) && ContainsAny(name, fragments))
                return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryStartUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.open-icon-settings", $"{uri}: {ex.Message}", ex);
            return false;
        }
    }

    public static bool TryGetNotifyIconRect(NotifyIcon icon, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        try
        {
            if (TryGetNotifyIconIdentity(icon, out var hwnd, out var id)
                && TryGetIconRect(hwnd, id, out rect)
                && rect.Width > 0 && rect.Height > 0)
                return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.icon-rect", ex.Message, ex);
        }

        return TryGetNotificationAreaRect(out rect);
    }

    public static TaskbarEdge GetTaskbarEdge(Rectangle fallbackAnchor)
    {
        try
        {
            var data = new Shell32.APPBARDATA { cbSize = Marshal.SizeOf<Shell32.APPBARDATA>() };
            if (Shell32.SHAppBarMessage(Shell32.ABM_GETTASKBARPOS, ref data) != IntPtr.Zero)
            {
                return data.uEdge switch
                {
                    Shell32.ABE_TOP => TaskbarEdge.Top,
                    Shell32.ABE_LEFT => TaskbarEdge.Left,
                    Shell32.ABE_RIGHT => TaskbarEdge.Right,
                    _ => TaskbarEdge.Bottom
                };
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.taskbar-edge", ex.Message, ex);
        }

        var screen = Screen.FromPoint(new Point(fallbackAnchor.X, fallbackAnchor.Y));
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        if (work.Top > bounds.Top + 2) return TaskbarEdge.Top;
        if (work.Left > bounds.Left + 2) return TaskbarEdge.Left;
        if (work.Right < bounds.Right - 2) return TaskbarEdge.Right;
        return TaskbarEdge.Bottom;
    }

    private static bool TryGetIconRect(IntPtr hwnd, uint id, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var identifier = new Shell32.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<Shell32.NOTIFYICONIDENTIFIER>(),
            hWnd = hwnd,
            uID = id,
            guidItem = Guid.Empty
        };

        if (Shell32.Shell_NotifyIconGetRect(ref identifier, out var native) != 0)
            return false;

        rect = native.ToRectangle();
        return rect.Width > 0 && rect.Height > 0;
    }

    private static bool TryGetNotifyIconIdentity(NotifyIcon icon, out IntPtr hwnd, out uint id)
    {
        hwnd = IntPtr.Zero;
        id = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(NotifyIcon);

        object? window = GetField(icon, type, flags, "_window", "window");
        if (window is NativeWindow native)
            hwnd = native.Handle;
        else if (window != null)
        {
            var handleProp = window.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (handleProp?.GetValue(window) is IntPtr handle)
                hwnd = handle;
        }

        object? idValue = GetField(icon, type, flags, "_id", "id");
        if (idValue is int intId)
            id = (uint)intId;
        else if (idValue is uint uintId)
            id = uintId;

        return hwnd != IntPtr.Zero;
    }

    private static object? GetField(object target, Type type, BindingFlags flags, params string[] names)
    {
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(target);
        }

        return null;
    }

    private static bool TryGetNotificationAreaRect(out Rectangle rect)
    {
        rect = Rectangle.Empty;
        try
        {
            var tray = User32.FindWindowW("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                var notify = User32.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify != IntPtr.Zero && User32.GetWindowRect(notify, out var native) && native.Width > 0)
                {
                    rect = native.ToRectangle();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.notify-wnd", ex.Message, ex);
        }

        try
        {
            var data = new Shell32.APPBARDATA { cbSize = Marshal.SizeOf<Shell32.APPBARDATA>() };
            if (Shell32.SHAppBarMessage(Shell32.ABM_GETTASKBARPOS, ref data) != IntPtr.Zero)
            {
                var bar = data.rc.ToRectangle();
                rect = data.uEdge switch
                {
                    Shell32.ABE_LEFT => new Rectangle(bar.Left, bar.Bottom - 72, bar.Width, 72),
                    Shell32.ABE_RIGHT => new Rectangle(bar.Left, bar.Bottom - 72, bar.Width, 72),
                    Shell32.ABE_TOP => new Rectangle(bar.Right - 72, bar.Top, 72, bar.Height),
                    _ => new Rectangle(bar.Right - 72, bar.Top, 72, bar.Height)
                };
                return rect.Width > 0 && rect.Height > 0;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.taskbar-pos", ex.Message, ex);
        }

        var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        var work = screen.WorkingArea;
        rect = new Rectangle(work.Right - 48, work.Bottom - 8, 24, 8);
        return true;
    }
}
