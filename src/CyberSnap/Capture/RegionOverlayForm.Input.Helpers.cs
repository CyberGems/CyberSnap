using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    private (string emoji, string name)[]? _filteredEmojis;
    private string _lastEmojiSearch = "";

    private (string emoji, string name)[] GetFilteredEmojiPalette()
    {
        if (_filteredEmojis != null && _lastEmojiSearch == _emojiSearch)
            return _filteredEmojis;
        _lastEmojiSearch = _emojiSearch;
        _filteredEmojis = string.IsNullOrEmpty(_emojiSearch)
            ? EmojiPalette
            : EmojiPalette.Where(em => em.name.Contains(_emojiSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        return _filteredEmojis;
    }

    private void InvalidateEmojiCache()
    {
        _filteredEmojis = null;
    }

    private static Rectangle InflateForRepaint(Rectangle rect, int pad = 8)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return Rectangle.Empty;
        rect.Inflate(pad, pad);
        return rect;
    }

    private int GetToolbarButtonAt(Point p)
    {
        if (!IsToolbarInteractive())
            return -1;

        for (int i = 0; i < _toolbarButtons.Length; i++)
            if (_toolbarButtons[i].Contains(p)) return i;
        return -1;
    }

    private Rectangle GetSquareSelectionRect(Point start, Point current)
    {
        int dx = current.X - start.X;
        int dy = current.Y - start.Y;
        int size = Math.Max(Math.Abs(dx), Math.Abs(dy));

        var b = _selectionMonitorClientBounds;
        if (!b.IsEmpty && b.Width > 0 && b.Height > 0)
        {
            int maxW = dx >= 0 ? b.Right - 1 - start.X : start.X - b.Left;
            int maxH = dy >= 0 ? b.Bottom - 1 - start.Y : start.Y - b.Top;
            int maxSize = Math.Min(maxW, maxH);
            size = Math.Min(size, maxSize);
        }

        int x2 = start.X + Math.Sign(dx == 0 ? 1 : dx) * size;
        int y2 = start.Y + Math.Sign(dy == 0 ? 1 : dy) * size;
        return NormRect(start, new Point(x2, y2));
    }

    private Rectangle GetCenterSelectionRect(Point center, Point current)
    {
        int halfW = Math.Abs(current.X - center.X);
        int halfH = Math.Abs(current.Y - center.Y);

        var aspectRatio = (ModifierKeys & Keys.Shift) != 0
            ? CenterSelectionAspectRatio.Square
            : _centerSelectionAspectRatio;

        if (aspectRatio != CenterSelectionAspectRatio.Free)
        {
            if (halfW == 0 && halfH == 0)
                return Rectangle.Empty;

            ApplyCenterAspectRatio(aspectRatio, ref halfW, ref halfH);
        }

        var b = _selectionMonitorClientBounds;
        int maxHalfW = b.IsEmpty ? Math.Max(0, Math.Min(center.X, _bmpW - center.X)) : Math.Max(0, Math.Min(center.X - b.Left, b.Right - center.X));
        int maxHalfH = b.IsEmpty ? Math.Max(0, Math.Min(center.Y, _bmpH - center.Y)) : Math.Max(0, Math.Min(center.Y - b.Top, b.Bottom - center.Y));
        double scale = Math.Min(1d, Math.Min(maxHalfW / Math.Max(1d, halfW), maxHalfH / Math.Max(1d, halfH)));
        halfW = Math.Max(0, (int)Math.Floor(halfW * scale));
        halfH = Math.Max(0, (int)Math.Floor(halfH * scale));

        return new Rectangle(center.X - halfW, center.Y - halfH, halfW * 2, halfH * 2);
    }

    private static void ApplyCenterAspectRatio(CenterSelectionAspectRatio aspectRatio, ref int halfW, ref int halfH)
    {
        var ratio = aspectRatio switch
        {
            CenterSelectionAspectRatio.Square => 1d,
            CenterSelectionAspectRatio.Widescreen16x9 => 16d / 9d,
            CenterSelectionAspectRatio.Classic4x3 => 4d / 3d,
            CenterSelectionAspectRatio.Photo3x2 => 3d / 2d,
            CenterSelectionAspectRatio.Portrait9x16 => 9d / 16d,
            _ => 0d
        };
        if (ratio <= 0)
            return;

        if (halfW / (double)Math.Max(1, halfH) >= ratio)
            halfH = Math.Max(1, (int)Math.Round(halfW / ratio));
        else
            halfW = Math.Max(1, (int)Math.Round(halfH * ratio));
    }

    private void SetTool(ToolDef tool, bool showHelpBanner = true)
    {
        if (tool.Mode is { } mode)
            SetMode(mode, tool.Id, showHelpBanner);
    }

    public void SetToolColor(Color color)
    {
        _toolColor = color;
        for (int i = 0; i < ToolColors.Length; i++)
        {
            if (ToolColors[i].ToArgb() == color.ToArgb())
            {
                _toolColorIndex = i;
                ToolColorChanged?.Invoke(color);
                return;
            }
        }
        ToolColorChanged?.Invoke(color);
    }

    /// <summary>Maps a tool id to the Streamline/Fluent icon id the capture toolbar renders, so the
    /// tool banner shows the exact same vector icon. Mirrors the id mappings in the toolbar paint path
    /// (<see cref="DrawIcon"/> and the toolbar build in RegionOverlayForm.cs).</summary>
    private static string ToolbarIconIdFor(string toolId) => toolId switch
    {
        "crop" => "rect",
        "rect" => "captureRect",
        "scroll" => "scrollCapture",
        var id => id,
    };

    private void SetMode(CaptureMode m, string? toolId = null, bool showHelpBanner = true)
    {
        if (_isTyping) CommitText();
        bool wasEmoji = _mode == CaptureMode.Emoji && _emojiPickerOpen;
        bool wasSelectionMode = IsSelectionCaptureMode();
        _colorPickerOpen = false;
        _fontPickerOpen = false;
        HideFontSearchBox();
        _emojiHovered = -1;
        _eraserHoverIndex = -1;

        // Reset and invalidate selection/hover states before switching tools to prevent trails/ghosts
        if (_selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count)
        {
            var bounds = GetAnnotationBounds(_undoStack[_selectedAnnotationIndex]);
            Invalidate(Rectangle.Inflate(bounds, 16, 16));
            _selectedAnnotationIndex = -1;
        }
        _multiSelectedIndices.Clear();
        if (_moveHoverIndex >= 0 && _moveHoverIndex < _undoStack.Count)
        {
            var bounds = GetAnnotationBounds(_undoStack[_moveHoverIndex]);
            Invalidate(Rectangle.Inflate(bounds, 16, 16));
        }
        _moveHoverIndex = -1;

        _mode = m;
        _activeToolId = toolId ?? _visibleTools.FirstOrDefault(t => t.Mode == m)?.Id;

        // Remember annotation tools so the next confirm session can restore them.
        // Skip placement tools — restoring Magnifier auto-selected a live ghost that looked
        // like the capture pixel magnifier and left paint trails while hovering.
        if (ToolDef.IsAnnotationTool(m)
            && !string.IsNullOrEmpty(_activeToolId)
            && m is not CaptureMode.Magnifier and not CaptureMode.Emoji and not CaptureMode.StepNumber)
            LastAnnotationToolChanged?.Invoke(_activeToolId);

        _hasSelection = false;
        _hasDragged = false;
        _selectionRect = Rectangle.Empty;
        _lastSelectionRect = Rectangle.Empty;
        _isSelecting = false;
        ResetEvasion();
        _isBlurring = false;
        _isHighlighting = false;
        _isRectShapeDragging = false;
        _isCircleShapeDragging = false;
        _isArrowDragging = false;
        _isLineDragging = false;
        _isRulerDragging = false;
        _isCurvedArrowDragging = false;
        _isPlacingMagnifier = false;
        if (_isSelectDragging || _isSelectResizing || _renderSkipIndex >= 0)
        {
            _isSelectDragging = false;
            _isSelectResizing = false;
            _selectPreviewAnnotation = null;
            _selectResizeOriginalAnnotation = null;
            _renderSkipIndex = -1;
            MarkCommittedAnnotationsDirty();
        }
        _autoDetectActive = false;
        _autoDetectRect = Rectangle.Empty;
        _lastAutoDetectRect = Rectangle.Empty;

        if (m == CaptureMode.ColorPicker)
        {
            _pickerTimer.Stop();
            _pickerReady = false;
            Cursor = CursorFactory.EyedropperCursor;
        }
        else
        {
            _pickerTimer.Stop();
            CloseMagWindow();
        }

        // Emoji mode: toggle picker if already in emoji mode
        if (m == CaptureMode.Emoji)
        {
            try
            {
                if (wasEmoji)
                {
                    _emojiPickerOpen = false;
                    _isPlacingEmoji = false;
                    _emojiWarmupPending = false;
                    _emojiWarmupIndex = 0;
                    HideEmojiSearchBox();
                    RefreshToolbar();
                    return;
                }
                _emojiPickerOpen = true;
                _isPlacingEmoji = false;
                _selectedEmoji = null;
                _emojiSearch = "";
                _emojiScrollOffset = 0;
                int cols = EmojiPickerColumns, emojiSize = EmojiPickerIconSize, pad = EmojiPickerPadding, visibleRows = EmojiPickerVisibleRows;
                int searchBarH = EmojiPickerSearchBarHeight;
                int pw = cols * (emojiSize + pad) + pad;
                int ph = searchBarH + pad + visibleRows * (emojiSize + pad) + pad;
                _emojiPickerRect = PositionPopupFromAnchor(_toolbarRect, pw, ph);
                EnsureToolbarReady();
                ShowEmojiSearchBox();
                QueueEmojiWarmup();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("overlay.emoji-mode", ex);
                _emojiPickerOpen = false;
                HideEmojiSearchBox();
            }
        }
        else
        {
            _emojiPickerOpen = false;
            _isPlacingEmoji = false;
            _emojiWarmupPending = false;
            _emojiWarmupIndex = 0;
            HideEmojiSearchBox();
        }

        if (m == CaptureMode.Magnifier)
            _isPlacingMagnifier = true;

        // Show a mode-specific help banner so the user knows what to do.
        // Tool name + icon in theme label color, action description + suffix in theme accent.
        var suffix = " · " + LocalizationService.Translate("Right-click or Esc to cancel");
        var tool = ToolDef.AllTools.FirstOrDefault(t => t.Mode == m);
        // Render the SAME vector icon the toolbar uses, by id — not the font char (which has no
        // glyph in the banner's text font and shows up as a generic box).
        var iconId = tool != null ? ToolbarIconIdFor(tool.Id) : null;
        if (iconId != null && !Helpers.FluentIcons.HasIcon(iconId)) iconId = null;
        var toolName = tool != null ? LocalizationService.Translate(tool.Label) : "";
        // Recording banners use a shorter, area-focused label instead of the full "Screen Recorder (…)"
        // tool name, so they read "Grabación MP4: Clic y arrastra para seleccionar área · …".
        if (m == CaptureMode.Record)
            toolName = LocalizationService.Translate("MP4 Recording");
        else if (m == CaptureMode.RecordGif)
            toolName = LocalizationService.Translate("GIF Recording");

        string? action = null;
        if (m == CaptureMode.Rectangle)
            action = LocalizationService.Translate("Click & drag to capture");
        else if (m == CaptureMode.Center)
            action = LocalizationService.Translate("Click for centered capture");
        else if (m == CaptureMode.Ocr)
            action = LocalizationService.Translate("Select text area to recognize");
        else if (m == CaptureMode.Scan)
            action = LocalizationService.Translate("Select QR or barcode to scan");
        else if (m == CaptureMode.ScrollCapture)
            action = LocalizationService.Translate("Select scrolling area");
        else if (m == CaptureMode.Ruler)
            action = LocalizationService.Translate("Click & drag to measure");
        else if (m == CaptureMode.ColorPicker)
            action = LocalizationService.Translate("Click a pixel to pick its color");
        else if (m == CaptureMode.Record)
            action = LocalizationService.Translate("Click & drag to select area");
        else if (m == CaptureMode.RecordGif)
            action = LocalizationService.Translate("Click & drag to select area");
        else if (m == CaptureMode.Move)
            action = string.Format(
                LocalizationService.Translate("Click to select · Drag to move · Double-click {0} to select all"),
                LocalizationService.Translate("Pick"));
        else if (m == CaptureMode.Eraser)
            action = LocalizationService.Translate("Click or drag to erase objects");
        else if (m == CaptureMode.Highlight)
            action = LocalizationService.Translate("Click & drag to highlight");
        else if (m == CaptureMode.Text)
            action = LocalizationService.Translate("Click to place text");
        else if (m == CaptureMode.Arrow)
            action = LocalizationService.Translate("Click & drag to draw arrow");
        else if (m == CaptureMode.Line)
            action = LocalizationService.Translate("Click & drag to draw line");
        else if (m == CaptureMode.Draw)
            action = LocalizationService.Translate("Click & drag to draw");
        else if (m == CaptureMode.CurvedArrow)
            action = LocalizationService.Translate("Click & drag to draw curved arrow");
        else if (m == CaptureMode.CircleShape)
            action = LocalizationService.Translate("Click & drag to draw circle");
        else if (m == CaptureMode.RectShape)
            action = LocalizationService.Translate("Click & drag to draw rectangle");
        else if (m == CaptureMode.StepNumber)
            action = LocalizationService.Translate("Click to place step number");
        else if (m == CaptureMode.Magnifier)
            action = LocalizationService.Translate("Click to place magnifier");
        else if (m == CaptureMode.Blur)
            action = LocalizationService.Translate("Click & drag to blur");
        else if (m == CaptureMode.Emoji)
            action = LocalizationService.Translate("Click to pick emoji");

        // Silent restores (e.g. last annotation tool after locking a region) must not flash a help banner.
        if (showHelpBanner && action != null && !string.IsNullOrEmpty(toolName))
        {
            var label = toolName + ": ";
            var segments = new BannerSegment[]
            {
                new(label, StandaloneToolBanner.LabelColor),
                new(action + suffix, null), // null = theme accent
            };
            ShowToolBanner(segments, persistent: false, iconId: iconId);
        }
        else
        {
            HideToolBanner();
        }

        if (m == CaptureMode.Record)
        {
            RecordingRequested?.Invoke(Models.RecordingFormat.MP4);
            return;
        }

        if (m == CaptureMode.RecordGif)
        {
            RecordingRequested?.Invoke(Models.RecordingFormat.GIF);
            return;
        }

        Invalidate(Rectangle.Union(InflateForRepaint(GetEmojiPickerBounds(), 12), InflateForRepaint(GetColorPickerBounds(), 12)));
        // Capture-selection modes dim the full virtual desktop; entering or leaving them must
        // refresh every monitor so the veil does not stick on a secondary screen.
        if (wasSelectionMode || IsSelectionCaptureMode())
            Invalidate();
        RefreshToolbar();
    }

    private void SwitchModeFromHotkey(CaptureMode mode)
    {
        SetMode(mode);
        Focus();
        Invalidate();
        UpdateToolbarSurfaceOnly();
    }

    private static bool IsOverlaySwitchableMode(CaptureMode mode) =>
        ToolDef.AllTools.Any(tool => tool.Mode == mode);

    private Point GetRulerEnd(Point current) => GetConstrainedLineEnd(_rulerStart, current);

    private Point GetConstrainedLineEnd(Point start, Point current) =>
        (ModifierKeys & Keys.Shift) != 0
            ? LineSnapHelper.SnapEndTo45Degrees(start, current)
            : current;

    private Point GetConstrainedDrawPoint(Point current)
    {
        if ((ModifierKeys & Keys.Shift) == 0 || _currentStroke is not { Count: > 0 })
            return current;

        var start = _currentStroke[0];
        int dx = current.X - start.X;
        int dy = current.Y - start.Y;
        return Math.Abs(dx) >= Math.Abs(dy)
            ? new Point(current.X, start.Y)
            : new Point(start.X, current.Y);
    }

    private Rectangle GetShapeRect(Point current)
    {
        if ((ModifierKeys & Keys.Shift) == 0)
            return NormRect(_shapeStart, current);

        int dx = current.X - _shapeStart.X;
        int dy = current.Y - _shapeStart.Y;
        int size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        int x2 = _shapeStart.X + Math.Sign(dx == 0 ? 1 : dx) * size;
        int y2 = _shapeStart.Y + Math.Sign(dy == 0 ? 1 : dy) * size;
        return NormRect(_shapeStart, new Point(x2, y2));
    }

    private Rectangle GetMagnifierPreviewRect(Point cursor)
    {
        if (cursor == Point.Empty)
            return Rectangle.Empty;
        const int srcSize = 50;
        // Include both placement sides (px/py may flip left of cursor when near the right edge).
        var src = new Rectangle(cursor.X - srcSize / 2, cursor.Y - srcSize / 2, srcSize, srcSize);
        var a = GetMagnifierPaintBounds(cursor, src, ClientSize);
        // Conservative dual-side union so a flip mid-drag never leaves a smear.
        int zoom = 3;
        int dst = srcSize * zoom;
        var b = new Rectangle(cursor.X - 20 - dst - 12, cursor.Y - 20 - dst - 12, (dst + 40) * 2, (dst + 40) * 2);
        return Rectangle.Union(a, b);
    }

    private Rectangle GetEmojiPreviewRect(Point cursor)
    {
        if (cursor == Point.Empty)
            return Rectangle.Empty;
        int size = (int)Math.Ceiling(_emojiPlaceSize);
        int x = cursor.X - size / 2;
        int y = cursor.Y - size / 2;
        return new Rectangle(x - 8, y - 8, size + 16, size + 16);
    }

    /// <summary>Conservative bounds for the step-number placement ghost centered at the cursor
    /// (wide enough for multi-digit badges plus the drop shadow).</summary>
    private static Rectangle GetStepPreviewRect(Point cursor)
    {
        if (cursor == Point.Empty)
            return Rectangle.Empty;
        return new Rectangle(cursor.X - 24, cursor.Y - 18, 48, 38);
    }

    private Rectangle GetGlobalSnapGuideBounds()
    {
        if (!_snapGuideXVisible && !_snapGuideYVisible)
            return Rectangle.Empty;

        var bounds = Rectangle.Empty;
        int cx = ClientSize.Width / 2;
        int cy = ClientSize.Height / 2;

        if (_snapGuideXVisible)
            bounds = Rectangle.Union(bounds, new Rectangle(cx - 3, 0, 6, ClientSize.Height));
        if (_snapGuideYVisible)
            bounds = Rectangle.Union(bounds, new Rectangle(0, cy - 3, ClientSize.Width, 6));

        return InflateForRepaint(bounds, 6);
    }

    private void SetSnapGuides(bool showVertical, bool showHorizontal)
    {
        if (_snapGuideXVisible == showVertical && _snapGuideYVisible == showHorizontal)
            return;

        var oldBounds = GetGlobalSnapGuideBounds();
        _snapGuideXVisible = showVertical;
        _snapGuideYVisible = showHorizontal;
        var newBounds = GetGlobalSnapGuideBounds();

        if (!oldBounds.IsEmpty && !newBounds.IsEmpty)
            Invalidate(Rectangle.Union(oldBounds, newBounds));
        else if (!oldBounds.IsEmpty)
            Invalidate(oldBounds);
        else if (!newBounds.IsEmpty)
            Invalidate(newBounds);
    }

    private Point SnapPointToGlobalCenter(Rectangle boundsAtDesiredPosition, Point desiredPoint)
    {
        int centerX = ClientSize.Width / 2;
        int centerY = ClientSize.Height / 2;
        int boundsCenterX = boundsAtDesiredPosition.Left + boundsAtDesiredPosition.Width / 2;
        int boundsCenterY = boundsAtDesiredPosition.Top + boundsAtDesiredPosition.Height / 2;
        int snapX = centerX - boundsCenterX;
        int snapY = centerY - boundsCenterY;

        bool snappedX = Math.Abs(snapX) <= GlobalCenterSnapThreshold;
        bool snappedY = Math.Abs(snapY) <= GlobalCenterSnapThreshold;
        SetSnapGuides(snappedX, snappedY);

        return new Point(
            desiredPoint.X + (snappedX ? snapX : 0),
            desiredPoint.Y + (snappedY ? snapY : 0));
    }

    private Point SnapTextPositionToGlobalCenter(Point desiredTextPos)
    {
        var snappedBounds = Rectangle.Round(MeasureTextRect(desiredTextPos, _textBuffer, _textFontSize, _textFontFamily, _textBold, _textItalic, _textBackground));
        return SnapPointToGlobalCenter(snappedBounds, desiredTextPos);
    }

    private Point SnapAnnotationDeltaToGlobalCenter(Rectangle originalBounds, Point desiredDelta)
    {
        var movedBounds = OffsetRect(originalBounds, desiredDelta.X, desiredDelta.Y);
        return SnapPointToGlobalCenter(movedBounds, desiredDelta);
    }

    private Rectangle GetColorPickerBounds()
    {
        int pw = ColorPickerColumns * (ColorPickerSwatchSize + ColorPickerPadding) + ColorPickerPadding;
        int ph = ColorPickerRows * (ColorPickerSwatchSize + ColorPickerPadding) + ColorPickerPadding;
        var colorBtn = _toolbarButtons.Length > ColorButtonIndex ? _toolbarButtons[ColorButtonIndex] : Rectangle.Empty;
        return PositionPopupFromAnchor(colorBtn, pw, ph);
    }

    private Rectangle GetColorPickerSwatchRect(int index)
    {
        if (_colorPickerRect.IsEmpty || index < 0 || index >= ToolColors.Length)
            return Rectangle.Empty;

        int col = index % ColorPickerColumns;
        int row = index / ColorPickerColumns;
        int x = _colorPickerRect.X + ColorPickerPadding + col * (ColorPickerSwatchSize + ColorPickerPadding);
        int y = _colorPickerRect.Y + ColorPickerPadding + row * (ColorPickerSwatchSize + ColorPickerPadding);
        return new Rectangle(x, y, ColorPickerSwatchSize, ColorPickerSwatchSize);
    }

    private Rectangle GetEmojiPickerBounds()
    {
        int cols = EmojiPickerColumns, emojiSize = EmojiPickerIconSize, pad = EmojiPickerPadding;
        int searchBarH = EmojiPickerSearchBarHeight;
        int visibleRows = EmojiPickerVisibleRows;
        int pw = cols * (emojiSize + pad) + pad;
        int ph = searchBarH + pad + visibleRows * (emojiSize + pad) + pad;
        return PositionPopupFromAnchor(_toolbarRect, pw, ph);
    }

    private Rectangle GetFontPickerBounds()
    {
        // Keep in sync with PaintFontPicker layout constants.
        int pad = 10, visibleCount = 10, itemH = 34, searchBarH = 34;
        int pw = 300, ph = searchBarH + pad + visibleCount * itemH + pad * 2;
        int px, py;
        if (_isTyping)
        {
            px = _textPos.X;
            py = _textPos.Y - ph - 10;
            if (py < 10) py = _textPos.Y + 40;
        }
        else
        {
            var popupRect = PositionPopupFromAnchor(_toolbarRect, pw, ph);
            px = popupRect.X;
            py = popupRect.Y;
        }
        return new Rectangle(px, py, pw, ph);
    }
}
