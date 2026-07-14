using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    // All text input is handled by off-screen TextBox controls

    private void CommitText() => CommitOrCancelInlineText(commit: true);

    private TextAnnotation BuildCurrentTextAnnotation() =>
        new(
            _textPos,
            TextAnnotationPainter.NormalizeNewlines(_textBuffer),
            _textFontSize,
            _toolColor,
            _textBold,
            _textItalic,
            _textStroke,
            _textShadow,
            _textBackground,
            _textFontFamily,
            _textAlign,
            _textMaxWidth);

    /// <summary>
    /// Ends inline text editing. Commits when <paramref name="commit"/> is true and there is
    /// non-whitespace text. Escape should call with <c>commit: false</c> (cancel — never auto-commit).
    /// Re-edits use a single <see cref="Models.Commands.ReplaceAnnotationCommand"/> for clean undo.
    /// </summary>
    private void CommitOrCancelInlineText(bool commit)
    {
        if (!_isTyping) return;

        if (_textBox != null && _textBox.Visible)
            _textBuffer = _textBox.Text;

        bool hasText = !string.IsNullOrWhiteSpace(_textBuffer);
        int editIndex = _textEditStackIndex;
        var original = _textEditOriginal;

        // Clear skip so the stack annotation is visible again after we're done.
        if (_renderSkipIndex == editIndex)
            _renderSkipIndex = -1;

        if (commit && hasText)
        {
            var neu = BuildCurrentTextAnnotation();
            if (editIndex >= 0 && original is not null && editIndex < _undoStack.Count)
            {
                if (!Equals(original, neu))
                    PushEditCommand(new Models.Commands.ReplaceAnnotationCommand(editIndex, original, neu));
            }
            else if (editIndex < 0)
            {
                AddAnnotation(neu);
            }
        }
        else if (commit && !hasText && editIndex >= 0 && original is not null)
        {
            // User cleared all text and confirmed → delete the original.
            PushEditCommand(new Models.Commands.DeleteAnnotationCommand(editIndex, original));
        }
        // cancel (commit:false): leave original in place (never removed from stack)

        _isTyping = false;
        _textEditStackIndex = -1;
        _textEditOriginal = null;
        _hoveredTextBtn = -1;
        HideToolbarTooltip();
        SetSnapGuides(false, false);
        _textBuffer = "";
        InvalidateActiveTextLayout();
        _fontPickerOpen = false;
        HideFontSearchBox();
        HideTextBox();
        PersistCaptureTextStyle();
        MarkCommittedAnnotationsDirty();
        RefreshOverlayUiChrome();
        Invalidate();
    }

    private void PersistCaptureTextStyle()
    {
        if (System.Windows.Application.Current is App app)
        {
            app.PersistEditorTextStyle(
                _textFontSize, _textFontFamily, _textBold, _textItalic,
                _textStroke, _textShadow, _textBackground, (int)_textAlign);
        }
    }

    private void LoadTextStyleFromSettings()
    {
        try
        {
            var s = Services.SettingsService.LoadStatic();
            if (s is null) return;
            _textFontSize = Math.Clamp(s.EditorTextFontSize, 10f, 120f);
            if (!string.IsNullOrWhiteSpace(s.EditorTextFontFamily))
                _textFontFamily = s.EditorTextFontFamily;
            _textBold = s.EditorTextBold;
            _textItalic = s.EditorTextItalic;
            _textStroke = s.EditorTextStroke;
            _textShadow = s.EditorTextShadow;
            _textBackground = s.EditorTextBackground;
            _textAlign = (TextHAlign)Math.Clamp(s.EditorTextAlignment, 0, 2);
        }
        catch { /* keep defaults */ }
    }

    private static RectangleF MeasureTextRect(
        Point pos, string text, float fontSize, string fontFamily,
        bool bold, bool italic, bool background = false,
        float maxWidth = 0, TextHAlign align = TextHAlign.Left) =>
        TextAnnotationPainter.Measure(pos, text, fontSize, fontFamily, bold, italic, background, maxWidth, align);

    private RectangleF GetActiveTextRect()
    {
        if (!_isTyping) return RectangleF.Empty;
        if (_activeTextLayoutDirty)
        {
            _activeTextRectCache = MeasureTextRect(
                _textPos, _textBuffer, _textFontSize, _textFontFamily,
                _textBold, _textItalic, _textBackground, _textMaxWidth, _textAlign);
            _activeTextMeasureWidth = _activeTextRectCache.Width;
            _activeTextHandleCache[0] = WindowsHandleRenderer.CenteredAt(new PointF(_activeTextRectCache.X, _activeTextRectCache.Y));
            _activeTextHandleCache[1] = WindowsHandleRenderer.CenteredAt(new PointF(_activeTextRectCache.Right, _activeTextRectCache.Y));
            _activeTextHandleCache[2] = WindowsHandleRenderer.CenteredAt(new PointF(_activeTextRectCache.X, _activeTextRectCache.Bottom));
            _activeTextHandleCache[3] = WindowsHandleRenderer.CenteredAt(new PointF(_activeTextRectCache.Right, _activeTextRectCache.Bottom));
            _activeTextLayoutDirty = false;
        }
        return _activeTextRectCache;
    }

    private int GetTextHandle(Point p)
    {
        if (!_isTyping) return -1;
        _ = GetActiveTextRect();
        for (int i = 0; i < _activeTextHandleCache.Length; i++)
        {
            var h = Rectangle.Round(_activeTextHandleCache[i]);
            h.Inflate((WindowsHandleRenderer.HitSize - h.Width) / 2, (WindowsHandleRenderer.HitSize - h.Height) / 2);
            if (h.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>Hit-tests committed text annotations. Returns index into <c>_undoStack</c>, or −1.</summary>
    private int HitTestText(Point p, bool includeRenderSkipped = false)
    {
        for (int i = _undoStack.Count - 1; i >= 0; i--)
        {
            if (!includeRenderSkipped && i == _renderSkipIndex) continue;
            if (_undoStack[i] is not TextAnnotation ta) continue;
            var rect = TextAnnotationPainter.Measure(ta);
            // Inflate slightly so double-clicks near edges still land
            rect.Inflate(4, 4);
            if (rect.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Hit-test for double-click re-edit. Includes the annotation currently being drag-moved
    /// (skipped in paint) so the second click of a Pick double-click still finds it.
    /// </summary>
    private int HitTestTextForDoubleClick(Point p) => HitTestText(p, includeRenderSkipped: true);

    private int GetTextCharIndexAt(Point p)
    {
        if (!_isTyping) return 0;
        return TextAnnotationPainter.GetCharIndexAt(
            _textPos, p, _textBuffer ?? "", _textFontSize, _textFontFamily,
            _textBold, _textItalic, _textMaxWidth, _textAlign);
    }

    /// <summary>Selects the word (or contiguous non-whitespace run) around <paramref name="index"/>.</summary>
    private static void SelectWordAtCapture(TextBox box, int index)
    {
        string t = box.Text ?? "";
        if (t.Length == 0)
        {
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            return;
        }

        index = Math.Clamp(index, 0, t.Length);
        if (index >= t.Length || char.IsWhiteSpace(t[index]))
        {
            if (index > 0 && !char.IsWhiteSpace(t[index - 1]))
                index--;
            else
            {
                box.SelectionStart = index;
                box.SelectionLength = 0;
                return;
            }
        }

        int start = index;
        int end = index;
        while (start > 0 && IsCaptureWordChar(t[start - 1])) start--;
        while (end < t.Length && IsCaptureWordChar(t[end])) end++;
        box.SelectionStart = start;
        box.SelectionLength = Math.Max(0, end - start);
    }

    private static bool IsCaptureWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '\'';

    /// <summary>Starts re-editing a committed text annotation without deleting it from the undo stack.</summary>
    private void BeginReEditText(int stackIndex)
    {
        if (stackIndex < 0 || stackIndex >= _undoStack.Count) return;
        if (_undoStack[stackIndex] is not TextAnnotation ta) return;

        // Commit any other in-progress text first
        if (_isTyping)
            CommitOrCancelInlineText(commit: true);

        _textEditStackIndex = stackIndex;
        _textEditOriginal = ta;
        _renderSkipIndex = stackIndex; // hide original while editing (no Delete command)
        _mode = CaptureMode.Text;
        _isTyping = true;
        _textPos = ta.Pos;
        _textBuffer = ta.Text;
        _textFontSize = ta.FontSize;
        _toolColor = ta.Color;
        _textBold = ta.Bold;
        _textItalic = ta.Italic;
        _textStroke = ta.Stroke;
        _textShadow = ta.Shadow;
        _textBackground = ta.Background;
        _textFontFamily = ta.FontFamily;
        _textAlign = ta.Alignment;
        _textMaxWidth = ta.MaxWidth;
        // Drop object selection so only the live text frame is shown
        _selectedAnnotationIndex = -1;
        _multiSelectedIndices.Clear();
        InvalidateActiveTextLayout();
        ShowTextBox();
        MarkCommittedAnnotationsDirty();
        // Reposition ToolbarForm only if needed (font picker, etc.) — text chrome
        // no longer expands it, so this should not jump the main dock.
        RefreshOverlayUiChrome();
        Invalidate();
    }

    private void ToggleColorPicker()
    {
        _emojiPickerOpen = false;
        _fontPickerOpen = false;
        HideEmojiSearchBox();
        HideFontSearchBox();
        _isPlacingEmoji = false;
        _colorPickerOpen = !_colorPickerOpen;
        HideToolbarTooltip();
        Invalidate(InflateForRepaint(GetColorPickerBounds(), 12));
        RefreshToolbar();
    }

    private bool HandleColorPickerClick(Point p)
    {
        if (!_colorPickerRect.Contains(p)) return false;

        for (int i = 0; i < ToolColors.Length; i++)
        {
            if (!GetColorPickerSwatchRect(i).Contains(p))
                continue;

            SetToolColor(ToolColors[i]);
            ToolColorChanged?.Invoke(ToolColors[i]);
            _activeToolId = _visibleTools.FirstOrDefault(t => t.Mode == _mode)?.Id ?? _activeToolId;
            _colorPickerOpen = false;
            Invalidate(InflateForRepaint(GetColorPickerBounds(), 12));
            RefreshToolbar();
            return true;
        }

        return true; // absorb clicks inside the popup even between swatches
    }

    private bool HandleFontPickerClick(Point p)
    {
        if (!_fontPickerRect.Contains(p)) return false;

        int itemH = 34, pad = 10, searchBarH = 34;
        const int starColW = 22;
        int listY = _fontPickerRect.Y + pad + searchBarH + pad;
        int relY = p.Y - listY;
        var entries = GetFontListEntries();
        int visibleCount = 10;
        int maxScroll = Math.Max(0, entries.Length - visibleCount);
        int trackH = visibleCount * itemH - 8;
        int trackX = _fontPickerRect.Right - pad - 4;
        int trackY = listY + 4;
        var trackRect = new Rectangle(trackX - 4, trackY, 12, trackH);
        if (trackRect.Contains(p) && entries.Length > visibleCount)
        {
            int thumbH = Math.Max(12, trackH * visibleCount / entries.Length);
            int thumbTravel = Math.Max(1, trackH - thumbH);
            int target = p.Y - trackY - (thumbH / 2);
            target = Math.Clamp(target, 0, thumbTravel);
            _fontPickerScroll = (int)Math.Round((double)target / thumbTravel * maxScroll);
            RefreshToolbar();
            return true;
        }

        int idx = _fontPickerScroll + relY / itemH;

        if (relY >= 0 && idx >= 0 && idx < entries.Length)
        {
            // Click on star column (right side) → toggle favorite (keep picker open)
            int rowTop = listY + (idx - _fontPickerScroll) * itemH;
            var starHit = new Rectangle(
                _fontPickerRect.Right - pad - starColW - 4, rowTop, starColW + 8, itemH);
            if (starHit.Contains(p))
            {
                ToggleFavoriteFontAndPersist(entries[idx].Name);
                RefreshToolbar();
                Invalidate(InflateForRepaint(GetFontPickerBounds(), 12));
                return true;
            }

            var oldTextRect = Rectangle.Round(GetActiveTextRect());
            var oldToolbarRect = Rectangle.Round(GetTextToolbarBounds());
            var oldPickerRect = InflateForRepaint(GetFontPickerBounds(), 12);
            _textFontFamily = entries[idx].Name;
            _fontPickerOpen = false;
            _fontSearch = "";
            InvalidateFontListCache();
            // Promote to recents immediately so the next open shows it near the top.
            PersistCaptureTextStyle();
            InvalidateActiveTextLayout();
            UpdateTextBoxStyle(); SyncTextBoxSize();
            var newTextRect = Rectangle.Round(GetActiveTextRect());
            var newToolbarRect = Rectangle.Round(GetTextToolbarBounds());
            RefreshOverlayUiChrome();
            Invalidate(Rectangle.Union(
                Rectangle.Union(InflateForRepaint(oldTextRect, 16), InflateForRepaint(newTextRect, 16)),
                Rectangle.Union(Rectangle.Union(InflateForRepaint(oldToolbarRect, 16), InflateForRepaint(newToolbarRect, 16)), oldPickerRect)));
            RefreshToolbar();
            return true;
        }
        return true; // absorb click inside picker
    }

    private bool IsPointInFontPickerSearch(Point p)
    {
        if (!_fontPickerRect.Contains(p)) return false;
        int searchBarH = 34, pad = 10;
        int searchBottom = _fontPickerRect.Y + pad + searchBarH;
        return p.Y < searchBottom;
    }

    private bool IsPointInFontPickerScrollbar(Point p)
    {
        if (!_fontPickerRect.Contains(p)) return false;
        var fonts = GetFilteredFonts();
        int visibleCount = 10;
        if (fonts.Length <= visibleCount) return false;

        int itemH = 34, pad = 10, searchBarH = 34;
        int listY = _fontPickerRect.Y + pad + searchBarH + pad;
        int trackH = visibleCount * itemH - 8;
        int trackX = _fontPickerRect.Right - pad - 4;
        int trackY = listY + 4;
        var trackRect = new Rectangle(trackX - 4, trackY, 12, trackH);
        return trackRect.Contains(p);
    }

    private bool IsPointInFontPickerList(Point p)
    {
        if (!_fontPickerRect.Contains(p)) return false;
        int itemH = 34, pad = 10, searchBarH = 34;
        int listY = _fontPickerRect.Y + pad + searchBarH + pad;
        int relY = p.Y - listY;
        int idx = _fontPickerScroll + relY / itemH;
        return relY >= 0 && idx >= 0 && idx < GetFilteredFonts().Length;
    }

    private bool IsPointInEmojiPickerSearch(Point p)
    {
        if (!_emojiPickerRect.Contains(p)) return false;
        int pad = EmojiPickerPadding, searchBarH = EmojiPickerSearchBarHeight;
        int searchBottom = _emojiPickerRect.Y + pad + searchBarH + pad;
        return p.Y < searchBottom;
    }

    private bool IsPointInEmojiPickerItem(Point p)
    {
        if (!_emojiPickerRect.Contains(p)) return false;

        var filtered = GetFilteredEmojiPalette();
        int cols = EmojiPickerColumns, emojiSize = EmojiPickerIconSize, pad = EmojiPickerPadding;
        int searchBarH = EmojiPickerSearchBarHeight;
        int gridY = _emojiPickerRect.Y + pad + searchBarH + pad;
        int relX = p.X - _emojiPickerRect.X - pad;
        int relY = p.Y - gridY;
        int col = relX / (emojiSize + pad);
        int row = relY / (emojiSize + pad);
        int idx = (_emojiScrollOffset + row) * cols + col;
        return col >= 0 && col < cols && row >= 0 && idx >= 0 && idx < filtered.Length;
    }

    private bool IsPointInColorPickerSwatch(Point p)
    {
        if (!_colorPickerRect.Contains(p)) return false;
        for (int i = 0; i < ToolColors.Length; i++)
            if (GetColorPickerSwatchRect(i).Contains(p))
                return true;
        return false;
    }

    private bool HandleEmojiPickerClick(Point p)
    {
        if (!_emojiPickerRect.Contains(p)) return false;

        var filtered = GetFilteredEmojiPalette();

        int cols = EmojiPickerColumns, emojiSize = EmojiPickerIconSize, pad = EmojiPickerPadding;
        int searchBarH = EmojiPickerSearchBarHeight;
        int gridY = _emojiPickerRect.Y + pad + searchBarH + pad;

        // Check if clicking in search bar area (just keep focus, absorb click)
        if (p.Y < gridY) return true;

        int relX = p.X - _emojiPickerRect.X - pad;
        int relY = p.Y - gridY;
        int col = relX / (emojiSize + pad);
        int row = relY / (emojiSize + pad);
        int idx = (_emojiScrollOffset + row) * cols + col;

        if (col >= 0 && col < cols && row >= 0 && idx < filtered.Length)
        {
            _selectedEmoji = filtered[idx].emoji;
            _isPlacingEmoji = true;
            _emojiPickerOpen = false;
            _fontPickerOpen = false;
            HideEmojiSearchBox();
            Invalidate(InflateForRepaint(GetEmojiPickerBounds(), 12));
            RefreshToolbar();
            return true;
        }
        return true; // absorb click inside picker
    }
}
