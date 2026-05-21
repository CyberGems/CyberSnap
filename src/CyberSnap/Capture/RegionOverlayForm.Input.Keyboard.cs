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
        if (e.KeyCode == Keys.Z && e.Control && _undoStack.Count > 0)
        {
            var last = RemoveLastAnnotation();
            _redoStack.Add(last);
            // Update step counter when undoing a step number
            if (last is StepNumberAnnotation)
            {
                var remaining = _undoStack.OfType<StepNumberAnnotation>().LastOrDefault();
                _nextStepNumber = remaining != null ? remaining.Number + 1 : 1;
            }
            Invalidate(InflateForRepaint(GetAnnotationBounds(last)));
            return;
        }

        if ((e.KeyCode == Keys.Y && e.Control || e.KeyCode == Keys.Z && e.Control && e.Shift) && _redoStack.Count > 0)
        {
            var annotation = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            RestoreAnnotation(annotation);
            if (annotation is StepNumberAnnotation step)
                _nextStepNumber = Math.Max(_nextStepNumber, step.Number + 1);
            Invalidate(InflateForRepaint(GetAnnotationBounds(annotation)));
            return;
        }

        // Emoji placing: Tab re-opens picker
        if (_mode == CaptureMode.Emoji && _isPlacingEmoji)
        {
            if (e.KeyCode == Keys.Tab) { _emojiPickerOpen = true; _isPlacingEmoji = false; QueueEmojiWarmup(); RefreshToolbar(); }
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
        if (e.KeyCode == Keys.Delete && _mode == CaptureMode.Select && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count)
        {
            var bounds = InflateForRepaint(GetAnnotationBounds(_undoStack[_selectedAnnotationIndex]));
            _undoStack.RemoveAt(_selectedAnnotationIndex);
            _redoStack.Clear();
            MarkCommittedAnnotationsDirty();
            _selectedAnnotationIndex = -1;
            _selectPreviewAnnotation = null;
            _renderSkipIndex = -1;
            _isSelectDragging = false;
            _isSelectResizing = false;
            Invalidate(bounds);
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
