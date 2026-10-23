using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    private sealed record ToolbarKeyTarget(string Id, Rectangle Bounds);
    private string? _toolbarKeyboardFocusId;
    private Point _toolbarKeyboardPointerOrigin;
    private bool _activatingToolbarWithKeyboard;
    private bool _toolbarSpaceHeld;

    private List<ToolbarKeyTarget> GetToolbarKeyboardTargets()
    {
        var targets = new List<ToolbarKeyTarget>();
        if (!IsToolbarInteractive()) return targets;
        for (int i = 0; i < _toolbarButtons.Length; i++)
        {
            var bounds = _toolbarButtons[i];
            var center = KeyboardTargetCenter(bounds);
            if (bounds.Width <= 0 || bounds.Height <= 0 || GetToolbarButtonAt(center) != i) continue;
            if (IsRetractableAnnotationToolbarButton(i) && !_annotationRetractRevealRect.Contains(center)) continue;
            if (ShowAnnotationChrome && _toolbarToolIds[i] is "undo" or "eraser"
                && (_historyUtilitiesRevealAmt < .999f || !_annotationHistoryRevealRect.Contains(center))) continue;
            if (_toolbarToolIds[i] == "undo" && _editUndoStack.Count == 0) continue;
            targets.Add(new("bar:" + _toolbarToolIds[i], bounds));
        }
        if (!_menuActivatorRect.IsEmpty) targets.Add(new("menu", _menuActivatorRect));
        if (_altCapturePopupOpen)
        {
            EnsureAltPopupSlotsLaidOut();
            foreach (var slot in _altPopupSlots)
                if (!slot.Container.IsEmpty) targets.Add(new("alt:" + slot.ToolId, slot.Container));
        }
        if (_isConfirmingSelection && !_confirmDocksHiddenForFrameManip)
        {
            LayoutConfirmChromeRects();
            for (int i = 0; i < _confirmChromeRects.Length; i++)
            {
                var bounds = _confirmChromeRects[i];
                if (bounds.Width > 0 && bounds.Height > 0 && !IsConfirmChromeDisabled(_confirmChromeKinds[i]))
                    targets.Add(new("confirm:" + _confirmChromeKinds[i], bounds));
            }
            if (!_confirmOptionsPillRect.IsEmpty) targets.Add(new("confirm:options", _confirmOptionsPillRect));
        }
        return targets;
    }

    private static Point KeyboardTargetCenter(Rectangle bounds) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private bool TryHandleToolbarNavigation(Keys keyData)
    {
        if (_isTyping || _emojiPickerOpen || _isSelecting || _isDraggingToolbar || _isDraggingConfirm
            || _isConfirmDragging || _confirmHandleDragIndex >= 0
            || (_toolbarContextMenu?.Visible ?? false) || (_confirmContextMenu?.Visible ?? false)
            || ActiveControl is TextBoxBase { ContainsFocus: true }) return false;

        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        // Emoji placement already uses Tab to reopen its picker.
        if (key == Keys.Tab && _mode == Models.CaptureMode.Emoji && _isPlacingEmoji) return false;
        if (key == Keys.Tab && modifiers is Keys.None or Keys.Shift)
        {
            var targets = GetToolbarKeyboardTargets();
            if (targets.Count == 0) return false;
            int index = targets.FindIndex(t => t.Id == _toolbarKeyboardFocusId);
            int next = index < 0 ? (modifiers == Keys.Shift ? targets.Count - 1 : 0)
                : (index + (modifiers == Keys.Shift ? -1 : 1) + targets.Count) % targets.Count;
            _toolbarKeyboardFocusId = targets[next].Id;
            _toolbarKeyboardPointerOrigin = Cursor.Position;
            CancelAnnotationToolsCollapse();
            CancelConfirmModesCollapse();
            _annotationToolsHoverDelayTimer.Stop();
            _confirmModesHoverDelayTimer.Stop();
            HideToolbarTooltip();
            UpdateToolbarSurfaceOnly();
            Invalidate();
            ShowToolbarKeyboardTooltip(targets[next]);
            return true;
        }
        if (key != Keys.Space || modifiers != Keys.None || _toolbarKeyboardFocusId is null) return false;
        var target = GetToolbarKeyboardTargets().FirstOrDefault(t => t.Id == _toolbarKeyboardFocusId);
        if (target is null) { ClearToolbarKeyboardFocus(); return false; }
        if (_toolbarSpaceHeld) return true;
        _toolbarSpaceHeld = true;
        _activatingToolbarWithKeyboard = true;
        try
        {
            // Reuse the real click path, including merge buttons, confirmation guards,
            // popup toggles and undo, without moving the user's pointer.
            var point = KeyboardTargetCenter(target.Bounds);
            var click = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
            OnMouseDown(click);
            if (!IsDisposed && !Disposing) OnMouseUp(click);
        }
        finally { _activatingToolbarWithKeyboard = false; }
        return true;
    }

    private void ClearToolbarKeyboardFocus()
    {
        if (_toolbarKeyboardFocusId is null) return;
        _toolbarKeyboardFocusId = null;
        HideToolbarTooltip();
        UpdateToolbarSurfaceOnly();
        Invalidate();
    }

    private bool HasToolbarKeyboardFocus()
    {
        if (_toolbarKeyboardFocusId is null) return false;
        if (GetToolbarKeyboardTargets().Any(t => t.Id == _toolbarKeyboardFocusId)) return true;
        ClearToolbarKeyboardFocus();
        return false;
    }

    private void ShowToolbarKeyboardTooltip(ToolbarKeyTarget target)
    {
        string? text = null;
        var settings = Services.SettingsService.LoadStatic();
        if (target.Id.StartsWith("bar:"))
        {
            int index = Array.IndexOf(_toolbarToolIds, target.Id[4..]);
            text = GetToolbarTooltipText(index);
        }
        else if (target.Id.StartsWith("alt:"))
        {
            string id = target.Id[4..];
            var tool = Models.ToolDef.AllTools.FirstOrDefault(t => t.Id == id);
            text = tool is null ? BuildAltActionTooltip(id, settings) : BuildToolTooltip(tool, settings, false);
        }
        else if (target.Id == "menu")
            text = Services.LocalizationService.Translate("More options") + "\n"
                + Services.LocalizationService.Translate("Tab and Shift+Tab browse buttons. Space activates; Enter confirms the capture.");
        if (string.IsNullOrWhiteSpace(text)) return;
        var anchor = target.Bounds;
        anchor.Offset(_virtualBounds.Location);
        _toolbarToolTip ??= new WindowsToolTip { EmphasizeTitle = true };
        _toolbarToolTip.ShowNear(this, text, anchor, GetToolbarToolTipPlacement());
        _tooltipVisible = true;
        _tooltipShowTime = DateTime.UtcNow;
    }

    private void PaintToolbarKeyboardFocus(Graphics graphics, bool confirmation)
    {
        if (_toolbarKeyboardFocusId is null) return;
        var target = GetToolbarKeyboardTargets().FirstOrDefault(t => t.Id == _toolbarKeyboardFocusId);
        if (target is null || target.Id.StartsWith("confirm:") != confirmation) return;
        var bounds = Rectangle.Inflate(target.Bounds, -2, -2);
        using var pen = new Pen(UiChrome.AccentColor, UiChrome.ScaleFloat(1.5f)) { DashStyle = DashStyle.Dot };
        using var path = WindowsDockRenderer.RoundedRect(bounds, UiChrome.ScaleFloat(5));
        graphics.DrawPath(pen, path);
    }
}
