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
    private bool _showTooltips = true;

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

    // The cursor must dwell on a control for this many timer ticks (~100ms each) before its
    // tooltip appears, so brushing the pointer across tools no longer triggers them instantly.
    private const int TooltipDwellTicks = 5;
    private Control? _pendingHover;
    private int _pendingHoverTicks;

    // Canvas resize handles aren't Controls, so they get their own (slower) dwell tracking.
    private const int ResizeTooltipDwellTicks = 9; // ~900ms — deliberately lazier than tools
    private int _resizeTipHandle = -1;
    private int _resizeTipDwell;
    private bool _resizeTipShown;

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public void SetShowTooltips(bool show)
    {
        if (_showTooltips == show) return;
        _showTooltips = show;
        if (!show)
            DismissVisibleHoverTooltips();
    }

    private void TooltipTimer_Tick(object? sender, EventArgs e)
    {
        // Never change tooltip visibility while a mouse button is held. Showing a tooltip
        // window mid-click — e.g. right after a click activates the editor (the emoji picker
        // was the foreground window) and the cursor sits on a tooltip-bearing tool button —
        // steals the pressed button's mouse capture, so the click never completes and the
        // user needs a second click to actually switch tools.
        if (Control.MouseButtons != MouseButtons.None)
        {
            HideResizeTip();
            return;
        }

        if (!_showTooltips)
        {
            DismissVisibleHoverTooltips();
            return;
        }

        if (WindowState == FormWindowState.Minimized)
        {
            DismissVisibleHoverTooltips();
            return;
        }

        var fg = GetForegroundWindow();
        // Keep the tooltip visible while it is the foreground window (TopMost quirk); otherwise
        // the timer would hide and re-show it every tick. Minimize/restore is handled explicitly.
        bool isWindowActive = fg == Handle
            || (_hoverToolTip is { Visible: true } tip && fg == tip.Handle);
        bool isAnyMenuOpen = (_burgerMenu != null && _burgerMenu.Visible)
            || (_canvasMenu != null && _canvasMenu.Visible)
            || (_imageMenu != null && _imageMenu.Visible)
            || (_emojiPicker != null && _emojiPicker.Visible)
            || (_shareMenu != null && _shareMenu.Visible)
            || (_exportMenu != null && _exportMenu.Visible);

        if (IsDisposed || !Visible || !isWindowActive || isAnyMenuOpen)
        {
            HideResizeTip();
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
            HideResizeTip();
            if (ReferenceEquals(_hoverAnchor, hovered))
            {
                // Already showing for this control — nothing left to dwell on.
                _pendingHover = null;
                _pendingHoverTicks = 0;
            }
            else
            {
                // Moved onto a different control: drop any stale tooltip right away, then wait for
                // the cursor to dwell here a few ticks before surfacing the new one.
                if (_hoverAnchor is not null)
                    HideHoverTooltip(_hoverAnchor);

                if (ReferenceEquals(_pendingHover, hovered))
                {
                    if (++_pendingHoverTicks >= TooltipDwellTicks)
                    {
                        ShowHoverTooltip(hovered, provider!(), above);
                        _pendingHover = null;
                        _pendingHoverTicks = 0;
                    }
                }
                else
                {
                    _pendingHover = hovered;
                    _pendingHoverTicks = 0;
                }
            }
        }
        else
        {
            if (_hoverAnchor is not null)
            {
                HideHoverTooltip(_hoverAnchor);
            }
            _pendingHover = null;
            _pendingHoverTicks = 0;
            UpdateResizeTip(screenPos);
        }
    }

    // Slow, dwell-gated tooltip for the canvas resize handles (which are painted, not Controls).
    private void UpdateResizeTip(Point screenPos)
    {
        if (_canvas is null || _canvas.IsResizingCanvas)
        {
            HideResizeTip();
            return;
        }

        var client = _canvas.PointToClient(screenPos);
        int handle = _canvas.HitTestResizeHandlePublic(client);
        if (handle < 0)
        {
            HideResizeTip();
            return;
        }

        if (handle != _resizeTipHandle)
        {
            // Moved onto a different handle: reset dwell and drop any visible bubble.
            HideResizeTip();
            _resizeTipHandle = handle;
            _resizeTipDwell = 0;
            return;
        }

        if (_resizeTipShown) return;
        if (++_resizeTipDwell < ResizeTooltipDwellTicks) return;

        var rect = _canvas.GetResizeHandleClientRect(handle);
        if (rect.IsEmpty) return;
        var screenRect = _canvas.RectangleToScreen(rect);
        string text = LocalizationService.Translate(
            _canvas.ResizeHandlesScaleContent ? "Drag to scale the image" : "Drag to resize the canvas");
        _hoverToolTip ??= new WindowsToolTip();
        _hoverToolTip.ShowNear(this, text, screenRect, above: true);
        _resizeTipShown = true;
    }

    private void HideResizeTip()
    {
        _resizeTipHandle = -1;
        _resizeTipDwell = 0;
        if (_resizeTipShown)
        {
            _resizeTipShown = false;
            try { _hoverToolTip?.Hide(); } catch { }
        }
    }

    private void ShowHoverTooltip(Control anchor, string? text, bool above)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(text) || !anchor.IsHandleCreated)
            return;

        _hoverToolTip ??= new WindowsToolTip();
        _hoverAnchor = anchor;
        var bounds = anchor.RectangleToScreen(anchor.ClientRectangle);

        if (anchor == _titleFileNameLabel)
        {
            using var titleFont = new Font("Consolas", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
            int textWidth = TextRenderer.MeasureText(_titleFileNameText ?? "", titleFont).Width;
            int totalWidth = 14 + 8 + textWidth; // ledAuraSize (14) + gap (8) + textWidth
            int startX = (anchor.Width - totalWidth) / 2;
            if (startX < 4) startX = 4;
            var localBounds = new Rectangle(startX, 0, totalWidth, anchor.Height);
            bounds = anchor.RectangleToScreen(localBounds);
        }

        _hoverToolTip.ShowNear(this, text!, bounds, above);
    }

    private void HideHoverTooltip(Control anchor)
    {
        if (!ReferenceEquals(_hoverAnchor, anchor))
            return;
        _hoverAnchor = null;
        try { _hoverToolTip?.Hide(); } catch { }
    }

    /// <summary>
    /// Hides any visible hover tooltip and resets dwell state (keyboard, minimize, etc.).
    /// </summary>
    private void DismissVisibleHoverTooltips()
    {
        _pendingHover = null;
        _pendingHoverTicks = 0;
        HideResizeTip();
        if (_hoverAnchor is { } anchor)
            HideHoverTooltip(anchor);
        else if (_hoverToolTip is { Visible: true, IsDisposed: false })
        {
            try { _hoverToolTip.Hide(); } catch { }
        }
    }

    // Convenience: appends a keyboard-shortcut hint (language-neutral) to a localized label,
    // mirroring how the capture toolbar surfaces hotkeys next to a tool's name.
    private static string WithShortcut(string textKey, string shortcut)
        => $"{LocalizationService.Translate(textKey)}  ({shortcut})";
}
