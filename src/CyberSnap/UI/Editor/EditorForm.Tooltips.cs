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

    private System.Windows.Forms.Timer? _tooltipTimer;
    private readonly System.Collections.Generic.List<(Control Control, Func<string?> TextProvider, bool Above)> _tooltipControls = new();

    private void RegisterHoverTooltip(Control anchor, Func<string?> textProvider, bool above = true)
    {
        _tooltipControls.Add((anchor, textProvider, above));
        EnsureTooltipTimerStarted();
    }

    private void EnsureTooltipTimerStarted()
    {
        if (_tooltipTimer is not null) return;
        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _tooltipTimer.Tick += TooltipTimer_Tick;
        _tooltipTimer.Start();
    }

    private void TooltipTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed || !Visible || !ContainsFocus)
        {
            if (_hoverAnchor is not null)
            {
                HideHoverTooltip(_hoverAnchor);
            }
            return;
        }

        var screenPos = Cursor.Position;
        Control? hovered = null;
        Func<string?>? provider = null;
        bool above = true;

        foreach (var item in _tooltipControls)
        {
            if (item.Control.IsHandleCreated && item.Control.Visible)
            {
                var rect = item.Control.RectangleToScreen(item.Control.ClientRectangle);
                if (rect.Contains(screenPos))
                {
                    hovered = item.Control;
                    provider = item.TextProvider;
                    above = item.Above;
                    break;
                }
            }
        }

        if (hovered is not null)
        {
            if (!ReferenceEquals(_hoverAnchor, hovered))
            {
                ShowHoverTooltip(hovered, provider!(), above);
            }
        }
        else
        {
            if (_hoverAnchor is not null)
            {
                HideHoverTooltip(_hoverAnchor);
            }
        }
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
