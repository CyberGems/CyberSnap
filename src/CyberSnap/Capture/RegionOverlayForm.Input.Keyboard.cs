using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    // ProcessCmdKey always receives ESC (OnKeyDown sometimes doesn't)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            if (_emojiPickerOpen)
            {
                _emojiPickerOpen = false;
                HideEmojiSearchBox();
                RefreshToolbar();
                Invalidate();
                return true;
            }
            Cancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (_emojiPickerOpen)
            {
                _emojiPickerOpen = false;
                HideEmojiSearchBox();
                RefreshToolbar();
                Invalidate();
                return;
            }
            Cancel();
            return;
        }

        // Emoji picker keyboard input (no TextBox needed — avoids focus issues)
        if (_emojiPickerOpen)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            if (e.KeyCode == Keys.Back && _emojiSearch.Length > 0)
            {
                _emojiSearch = _emojiSearch[..^1];
                _emojiScrollOffset = 0;
                _emojiHovered = -1;
                InvalidateEmojiCache();
                QueueEmojiWarmup();
                UpdateToolbarSurfaceOnly();
                return;
            }
            if (e.KeyCode == Keys.Left)
            {
                _emojiHovered = Math.Max(0, _emojiHovered - 1);
                UpdateToolbarSurfaceOnly();
                return;
            }
            if (e.KeyCode == Keys.Right)
            {
                var filtered = GetFilteredEmojiPalette();
                _emojiHovered = Math.Min(filtered.Length - 1, _emojiHovered + 1);
                UpdateToolbarSurfaceOnly();
                return;
            }
            if (e.KeyCode == Keys.Up)
            {
                _emojiHovered = Math.Max(0, _emojiHovered - EmojiPickerColumns);
                UpdateToolbarSurfaceOnly();
                return;
            }
            if (e.KeyCode == Keys.Down)
            {
                var filtered = GetFilteredEmojiPalette();
                _emojiHovered = Math.Min(filtered.Length - 1, _emojiHovered + EmojiPickerColumns);
                UpdateToolbarSurfaceOnly();
                return;
            }
            if (e.KeyCode == Keys.Enter && _emojiHovered >= 0)
            {
                var filtered = GetFilteredEmojiPalette();
                if (_emojiHovered < filtered.Length)
                {
                    _selectedEmoji = filtered[_emojiHovered].emoji;
                    _isPlacingEmoji = true;
                    _emojiPickerOpen = false;
                    RefreshToolbar();
                    Invalidate();
                }
                return;
            }
            // Printable characters are handled by OnKeyPress
            return;
        }

        // Undo must work in all states (emoji placing, typing, etc.)
        if (e.KeyCode == Keys.Z && e.Control && _editUndoStack.Count > 0)
        {
            UndoLastEdit();
            return;
        }

        if ((e.KeyCode == Keys.Y && e.Control || e.KeyCode == Keys.Z && e.Control && e.Shift) && _editRedoStack.Count > 0)
        {
            RedoLastEdit();
            return;
        }

        // Emoji placing: Tab re-opens picker
        if (_mode == CaptureMode.Emoji && _isPlacingEmoji)
        {
            if (e.KeyCode == Keys.Tab) { _emojiPickerOpen = true; _isPlacingEmoji = false; QueueEmojiWarmup(); RefreshToolbar(); }
            return;
        }

        // Stroke width shortcuts: [ decrease, ] increase
        if (e.KeyCode == Keys.OemOpenBrackets)
        {
            int idx = Array.IndexOf(StrokeWidths, _strokeWidth);
            if (idx > 0) { StrokeWidth = StrokeWidths[idx - 1]; }
            return;
        }
        if (e.KeyCode == Keys.OemCloseBrackets)
        {
            int idx = Array.IndexOf(StrokeWidths, _strokeWidth);
            if (idx < StrokeWidths.Length - 1) { StrokeWidth = StrokeWidths[idx + 1]; }
            return;
        }

        if (_fontPickerOpen) return;
        if (_isTyping) return;
        if (TryHandleAnnotationToolHotkey(e.KeyCode))
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            RefreshToolbar();
            Invalidate();
            return;
        }

        // Delete selected annotation
        if (e.KeyCode == Keys.Delete && _mode == CaptureMode.Move && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count)
        {
            DeleteAnnotationAt(_selectedAnnotationIndex);
            return;
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_emojiPickerOpen && !char.IsControl(e.KeyChar))
        {
            _emojiSearch += e.KeyChar;
            _emojiScrollOffset = 0;
            _emojiHovered = -1;
            InvalidateEmojiCache();
            QueueEmojiWarmup();
            UpdateToolbarSurfaceOnly();
            e.Handled = true;
            return;
        }
        base.OnKeyPress(e);
    }

    private bool TryHandleAnnotationToolHotkey(Keys keyCode)
    {
        var settings = Services.SettingsService.LoadStatic();
        if (settings is null)
            return false;

        uint mod = 0;
        if ((ModifierKeys & Keys.Control) != 0) mod |= Native.User32.MOD_CONTROL;
        if ((ModifierKeys & Keys.Alt) != 0) mod |= Native.User32.MOD_ALT;
        if ((ModifierKeys & Keys.Shift) != 0) mod |= Native.User32.MOD_SHIFT;
        uint vk = unchecked((uint)(keyCode & Keys.KeyCode));

        var toolId = settings.FindAnnotationToolId(mod, vk, _visibleTools.Where(t => t.Group == 1).Select(t => t.Id));
        if (toolId is null)
            return false;

        var tool = _visibleTools.FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (tool?.Mode is not { })
            return false;

        SetTool(tool);
        return true;
    }
}
