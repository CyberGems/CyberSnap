using System.Windows.Forms;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

internal static class EditorViewHotkeyHelper
{
    public static bool IsNumpadAlias(uint assignedVk, uint pressedVk)
    {
        if (assignedVk >= 0x30 && assignedVk <= 0x39 && pressedVk == assignedVk + 0x30)
            return true;
        if (assignedVk == 0xBB && pressedVk == 0x6B)
            return true;
        if (assignedVk == 0xBD && pressedVk == 0x6D)
            return true;
        return false;
    }

    public static bool MatchesViewHotkey(AppSettings settings, string viewId, uint mod, uint vk)
    {
        var (hMod, hKey) = settings.GetEditorViewHotkey(viewId);
        if (hMod != mod || hKey == 0)
            return false;
        return vk == hKey || (mod == 0 && IsNumpadAlias(hKey, vk));
    }

    public static bool IsAnyViewHotkey(Keys keyData)
    {
        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return false;

        uint mod = 0;
        if ((keyData & Keys.Control) != 0) mod |= Native.User32.MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mod |= Native.User32.MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mod |= Native.User32.MOD_SHIFT;
        uint vk = unchecked((uint)(keyData & Keys.KeyCode));

        foreach (var (id, _, _) in EditorViewHotkeyDef.Shortcuts)
        {
            if (MatchesViewHotkey(settings, id, mod, vk))
                return true;
        }

        return false;
    }

    public static bool TryHandleViewHotkeys(AnnotationCanvas canvas, KeyEventArgs e)
    {
        if (e.Control || e.Alt)
            return false;

        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return false;

        uint mod = e.Shift ? Native.User32.MOD_SHIFT : 0u;
        uint vk = unchecked((uint)e.KeyValue);

        if (MatchesViewHotkey(settings, "editorZoomIn", mod, vk))
        {
            canvas.DismissWelcomeOverlay();
            canvas.ZoomBy(1.15, new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2));
            return true;
        }

        if (MatchesViewHotkey(settings, "editorZoomOut", mod, vk))
        {
            canvas.DismissWelcomeOverlay();
            canvas.ZoomBy(1.0 / 1.15, new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2));
            return true;
        }

        if (MatchesViewHotkey(settings, "editorZoomReset", mod, vk))
        {
            canvas.DismissWelcomeOverlay();
            canvas.ZoomReset();
            return true;
        }

        if (MatchesViewHotkey(settings, "editorZoomFit", mod, vk))
        {
            canvas.DismissWelcomeOverlay();
            canvas.ZoomFit();
            return true;
        }

        return false;
    }
}
