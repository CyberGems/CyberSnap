using System;
using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI.Editor;

public sealed partial class EditorForm
{
    // Reuses the exact same custom tooltip used by the capture toolbar so the editor
    // hints share its look (rounded cyber surface, soft shadow, themed colors).
    private WindowsToolTip? _hoverToolTip;
    private Control? _hoverAnchor;

    /// <summary>
    /// Attaches a CyberSnap-styled hover tooltip to <paramref name="anchor"/>. The text is
    /// resolved through <see cref="LocalizationService"/> on every hover so it follows the
    /// current language. <paramref name="above"/> places the bubble above the control (used
    /// for the bottom status bar) or below it (top bar).
    /// </summary>
    private void RegisterHoverTooltip(Control anchor, string textKey, bool above = true)
        => RegisterHoverTooltip(anchor, () => LocalizationService.Translate(textKey), above);

    private void RegisterHoverTooltip(Control anchor, Func<string?> textProvider, bool above = true)
    {
        void Enter(object? sender, EventArgs e) => ShowHoverTooltip(anchor, textProvider(), above);
        void Leave(object? sender, EventArgs e)
        {
            // Composite controls (a panel hosting an icon + a label) raise MouseLeave on the
            // parent the instant the cursor steps onto a child. Only dismiss once the cursor
            // has truly left the anchor's bounds so the tooltip does not flicker.
            if (!anchor.RectangleToScreen(anchor.ClientRectangle).Contains(Cursor.Position))
                HideHoverTooltip(anchor);
        }

        HookHoverRecursive(anchor, Enter, Leave);
    }

    private static void HookHoverRecursive(Control root, EventHandler enter, EventHandler leave)
    {
        root.MouseEnter += enter;
        root.MouseLeave += leave;
        foreach (Control child in root.Controls)
            HookHoverRecursive(child, enter, leave);
    }

    private void ShowHoverTooltip(Control anchor, string? text, bool above)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(text) || !anchor.IsHandleCreated)
            return;

        _hoverToolTip ??= new WindowsToolTip();
        _hoverAnchor = anchor;
        var bounds = anchor.RectangleToScreen(anchor.ClientRectangle);
        _hoverToolTip.ShowNear(this, text!, bounds, above);
    }

    private void HideHoverTooltip(Control anchor)
    {
        if (!ReferenceEquals(_hoverAnchor, anchor))
            return;
        _hoverAnchor = null;
        try { _hoverToolTip?.Hide(); } catch { }
    }

    // Convenience: appends a keyboard-shortcut hint (language-neutral) to a localized label,
    // mirroring how the capture toolbar surfaces hotkeys next to a tool's name.
    private static string WithShortcut(string textKey, string shortcut)
        => $"{LocalizationService.Translate(textKey)}  ({shortcut})";
}
