using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        // Logo / brand toggles the guide — handle before generic dismiss so click is not a no-op.
        if (e.Button == MouseButtons.Left
            && (_logoRect.Contains(e.Location) || _brandRect.Contains(e.Location)))
        {
            HideToolbarTooltip();
            ShowQuickStartGuide();
            return;
        }

        // Closing the guide by clicking outside must never start a capture / selection.
        // Clicks on toolbar chrome still go through so tools and the ▼ menu keep working.
        bool guideWasOpen = _quickStartGuide != null && _quickStartGuide.Visible;
        if (guideWasOpen)
        {
            DismissQuickStartGuide();
            if (!IsPointInOverlayUi(e.Location))
                return;
        }

        if (e.Button == MouseButtons.Right)
        {
            if (_isConfirmingSelection)
            {
                ShowConfirmContextMenu(e.Location);
                return;
            }
            int rightClickBtn = GetToolbarButtonAt(e.Location);
            if (rightClickBtn >= 0 || _toolbarRect.Contains(e.Location))
            {
                HideToolbarTooltip();
                ShowToolbarContextMenu(rightClickBtn, e.Location);
                return;
            }
            int hit = HitTestAnnotation(e.Location);
            if (hit >= 0)
            {
                if (!_multiSelectedIndices.Contains(hit))
                {
                    _selectedAnnotationIndex = hit;
                    _multiSelectedIndices.Clear();
                    // Force immediate repaint so the selection frame is visible
                    // before the context menu appears.
                    Invalidate();
                    Update();
                }
                ShowAnnotationContextMenu(e.Location);
                return;
            }
            if (_isConfirmingSelection)
            {
                // Gentle exit while confirming: show a contextual Close instead of an abrupt cancel.
                ShowConfirmContextMenu(e.Location);
                return;
            }
            ShowEmptyAreaContextMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        // Confirm mode: toolbar / pickers stay interactive (annotation chrome is visible).
        // Handles and action pills take priority over drawing; interior clicks with an
        // annotation tool draw instead of dragging the crop.
        if (_isConfirmingSelection)
        {
            if (_menuActivatorRect.Contains(e.Location)
                || _logoRect.Contains(e.Location)
                || _brandRect.Contains(e.Location)
                || GetToolbarButtonAt(e.Location) >= 0
                || _toolbarRect.Contains(e.Location)
                || (_colorPickerOpen && _colorPickerRect.Contains(e.Location))
                || (_fontPickerOpen && _fontPickerRect.Contains(e.Location))
                || (_emojiPickerOpen && _emojiPickerRect.Contains(e.Location)))
            {
                // Fall through to the normal toolbar / picker handlers below.
            }
            else
            {
                int ch = HitTestConfirmHandle(e.Location);
                if (ch >= 0)
                {
                    _confirmHandleDragIndex = ch;
                    _isConfirmDragging = false;
                    _confirmDragStart = e.Location;
                    _confirmDragStartRect = _confirmRect;
                    return;
                }
                int confirmBtnHit = HitTestConfirmButton(e.Location);
                if (confirmBtnHit >= 0)
                {
                    StartConfirmPress(confirmBtnHit);
                    return;
                }
                if (_confirmRect.Contains(e.Location))
                {
                    // Annotation / drawing tools: treat the locked region like a canvas.
                    if (ToolDef.IsAnnotationTool(_mode))
                    {
                        // Fall through to annotation handlers below (do not drag the crop).
                    }
                    else if (e.Clicks >= 2)
                    {
                        // Double-click with a capture tool commits the primary destination.
                        _isConfirmDragging = false;
                        _confirmHandleDragIndex = -1;
                        CommitPrimaryConfirmAction();
                        return;
                    }
                    else
                    {
                        // Capture tool (or no tool): drag to reposition the crop.
                        _isConfirmDragging = true;
                        _confirmHandleDragIndex = -1;
                        _confirmDragStart = e.Location;
                        _confirmDragOffset = new Point(e.Location.X - _confirmRect.X, e.Location.Y - _confirmRect.Y);
                        _confirmDragStartRect = _confirmRect;
                        return;
                    }
                }
                else
                {
                    // Left-click outside the confirm UI: ignore (Esc / right-click still cancel).
                    return;
                }
            }
        }

        if (_menuActivatorRect.Contains(e.Location))
        {
            HideToolbarTooltip();
            ShowToolbarContextMenu(-1, e.Location);
            return;
        }
        if (_colorPickerOpen && _colorPickerRect.Contains(e.Location))
        {
            if (HandleColorPickerClick(e.Location))
                return;
        }
        if (_fontPickerOpen && _fontPickerRect.Contains(e.Location))
        {
            if (HandleFontPickerClick(e.Location))
                return;
        }
        if (_emojiPickerOpen && _emojiPickerRect.Contains(e.Location))
        {
            if (HandleEmojiPickerClick(e.Location))
                return;
        }

        if (_altCapturePopupOpen && _altCaptureButtonRect.Contains(e.Location))
        {
            var settings = Services.SettingsService.LoadStatic();
            var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;
            var targetMode = (defaultMode == CaptureMode.Center) ? CaptureMode.Rectangle : CaptureMode.Center;

            // Notify settings service and save the preference
            DefaultCaptureModeChanged?.Invoke(targetMode);

            var targetTool = ToolDef.AllTools.FirstOrDefault(t => t.Mode == targetMode);
            if (targetTool != null)
            {
                SetTool(targetTool);
            }

            // Immediately rebuild the toolbar tools to swap the buttons visually on screen
            CalcToolbar();

            _altCapturePopupOpen = false;
            InvalidateToolbarArea();
            return;
        }

        if (_altCapturePopupOpen && GetToolbarButtonAt(e.Location) != _mergedCaptureButtonIndex)
        {
            _altCapturePopupOpen = false;
            InvalidateToolbarArea();
        }

        int btn = GetToolbarButtonAt(e.Location);
        if (btn >= 0)
        {
            if (btn == CloseButtonIndex) { Cancel(); return; }     // close (Cancel)
            if (btn == StrokeWidthButtonIndex) { CycleStrokeWidth(); return; } // stroke width
            if (btn == ColorButtonIndex) { ToggleColorPicker(); return; } // color dot
            if (btn == PositionButtonIndex)
            {
                _isDraggingToolbar = true;
                _toolbarDragStartMouse = e.Location;
                _toolbarDragStartOffset = _toolbarCustomOffset;
                _hasMovedToolbarByDrag = false;
                return;
            }
            if (btn < _mainBarTools.Length)
            {
                if (btn == _mergedCaptureButtonIndex)
                {
                    _isMouseDownOnCaptureBtn = true;
                    _mouseDownStartTime = DateTime.UtcNow;
                    HideToolbarTooltip();
                    DismissQuickStartGuide();
                }
                else
                {
                    if (_mainBarTools[btn].Mode.HasValue)
                        SetTool(_mainBarTools[btn]);
                }
            }
            else if (btn >= CloseButtonIndex + 1 && btn < BtnCount)
            {
                int flyoutIdx = btn - (CloseButtonIndex + 1);
                if (flyoutIdx >= 0 && flyoutIdx < _flyoutTools.Length && _flyoutTools[flyoutIdx].Mode.HasValue)
                    SetTool(_flyoutTools[flyoutIdx]);
            }
            return;
        }
        else if (_toolbarRect.Contains(e.Location))
        {
            _isDraggingToolbar = true;
            _toolbarDragStartMouse = e.Location;
            _toolbarDragStartOffset = _toolbarCustomOffset;
            _hasMovedToolbarByDrag = false;
            return;
        }

        // Color picker popup: check if clicked a swatch
        if (_colorPickerOpen)
        {
            if (HandleColorPickerClick(e.Location))
                return;
            _colorPickerOpen = false;
            Invalidate(InflateForRepaint(GetColorPickerBounds(), 12));
        }

        // Font picker popup
        if (_fontPickerOpen)
        {
            if (HandleFontPickerClick(e.Location))
                return;
            _fontPickerOpen = false;
            HideFontSearchBox();
            Invalidate(InflateForRepaint(GetFontPickerBounds(), 12));
        }

        // Emoji picker popup: check if clicked an emoji
        if (_emojiPickerOpen)
        {
            if (HandleEmojiPickerClick(e.Location))
                return;
            // Clicked outside picker
            _emojiPickerOpen = false;
            HideEmojiSearchBox();
            Invalidate(InflateForRepaint(GetEmojiPickerBounds(), 12));
        }

        // Emoji placing: click to stamp
        if (_mode == CaptureMode.Emoji && _isPlacingEmoji && _selectedEmoji != null)
        {
            HideToolBanner();
            var pos = new Point(e.Location.X - (int)(_emojiPlaceSize / 2), e.Location.Y - (int)(_emojiPlaceSize / 2));
            AddAnnotation(new EmojiAnnotation(pos, _selectedEmoji, _emojiPlaceSize));
            SuppressHoverBoxForLastPlaced();
            Invalidate(InflateForRepaint(GetEmojiPreviewRect(e.Location)));
            return;
        }

        // If typing text: check toolbar buttons, resize handles, drag, or commit
        if (_isTyping)
        {
            // Text formatting toolbar buttons
            if (_textBoldBtnRect.Contains(e.Location))
            {
                _textBold = !_textBold;
                InvalidateActiveTextLayout();
                UpdateTextBoxStyle(); SyncTextBoxSize();
                Invalidate();
                return;
            }
            if (_textItalicBtnRect.Contains(e.Location))
            {
                _textItalic = !_textItalic;
                InvalidateActiveTextLayout();
                UpdateTextBoxStyle(); SyncTextBoxSize();
                Invalidate();
                return;
            }
            if (_textStrokeBtnRect.Contains(e.Location))
            {
                _textStroke = !_textStroke;
                Invalidate();
                return;
            }
            if (_textShadowBtnRect.Contains(e.Location))
            {
                _textShadow = !_textShadow;
                Invalidate();
                return;
            }
            if (_textBackgroundBtnRect.Contains(e.Location))
            {
                _textBackground = !_textBackground;
                InvalidateActiveTextLayout();
                UpdateTextBoxStyle(); SyncTextBoxSize();
                Invalidate();
                return;
            }
            if (_textFontBtnRect.Contains(e.Location))
            {
                _fontPickerOpen = !_fontPickerOpen;
                _fontPickerScroll = 0; _fontSearch = ""; InvalidateFontListCache();
                if (_fontPickerOpen) ShowFontSearchBox(); else HideFontSearchBox();
                Invalidate();
                RefreshToolbar();
                return;
            }
            if (_textSizeMinusBtnRect.Contains(e.Location))
            {
                AdjustTextFontSize(-TextSizeStep);
                return;
            }
            if (_textSizePlusBtnRect.Contains(e.Location))
            {
                AdjustTextFontSize(+TextSizeStep);
                return;
            }
            if (_textAlignLeftBtnRect.Contains(e.Location))
            {
                _textAlign = TextHAlign.Left;
                InvalidateActiveTextLayout();
                Invalidate();
                return;
            }
            if (_textAlignCenterBtnRect.Contains(e.Location))
            {
                _textAlign = TextHAlign.Center;
                InvalidateActiveTextLayout();
                Invalidate();
                return;
            }
            if (_textAlignRightBtnRect.Contains(e.Location))
            {
                _textAlign = TextHAlign.Right;
                InvalidateActiveTextLayout();
                Invalidate();
                return;
            }
            if (_textGripRect.Contains(e.Location))
            {
                // Drag the grip to move the toolbar and text together
                _textDragging = true;
                _lastTextDragLocation = Point.Empty;
                _lastTextDragFrameUtc = default;
                _textDragOffset = new Point(e.Location.X - _textPos.X, e.Location.Y - _textPos.Y);
                ClearCrosshairGuides();
                Invalidate();
                return;
            }
            // Absorb click on the toolbar background
            if (_textToolbarRect.Contains(e.Location))
                return;

            int handle = GetTextHandle(e.Location);
            if (handle >= 0)
            {
                _textResizeHandle = handle;
                _textResizing = true;
                _textResizeStart = e.Location;
                _textResizeStartFontSize = _textFontSize;
                _lastTextDragLocation = Point.Empty;
                _lastTextDragFrameUtc = default;
                Invalidate();
                return;
            }
            // Click inside the text frame: place caret (and allow drag-select).
            // Double-click: select the word under the cursor.
            // Move the block via the grip handle — not by clicking the glyphs.
            var textBox = GetActiveTextRect();
            if (textBox.Contains(e.Location))
            {
                int idx = GetTextCharIndexAt(e.Location);
                if (_textBox != null)
                {
                    _textBuffer = _textBox.Text ?? "";
                    if (e.Clicks >= 2)
                    {
                        SelectWordAtCapture(_textBox, idx);
                        _textSelecting = false;
                        _textBox.Focus();
                    }
                    else
                    {
                        _textBox.SelectionStart = idx;
                        _textBox.SelectionLength = 0;
                        _textSelectionAnchor = idx;
                        _textSelecting = true;
                        _textBox.Focus();
                        Capture = true;
                    }
                }
                ClearCrosshairGuides();
                Invalidate(InflateForRepaint(Rectangle.Round(textBox), 20));
                return;
            }
            // Clicked outside -- commit
            CommitText();
            return;
        }

        // In Text mode, check if clicking on an existing committed text to re-edit
        if (_mode == CaptureMode.Text)
        {
            int hitIdx = HitTestText(e.Location);
            if (hitIdx >= 0)
            {
                BeginReEditText(hitIdx);
                // Place caret at the click (same as while already editing)
                int caretIdx = GetTextCharIndexAt(e.Location);
                if (_textBox != null)
                {
                    _textBox.SelectionStart = caretIdx;
                    _textBox.SelectionLength = 0;
                    _textSelectionAnchor = caretIdx;
                    _textBox.Focus();
                }
                return;
            }
        }

        if (IsDrawingOrMoveMode(_mode) && _mode != CaptureMode.Move)
        {
            int handle = -1;
            int clickedIdx = -1;
            if (_selectedAnnotationIndex >= 0)
            {
                handle = GetSelectHandle(e.Location, _selectedAnnotationIndex);
                if (handle >= 0) clickedIdx = _selectedAnnotationIndex;
            }

            // Control hit on the hovered item only (handles may sit outside the stroke).
            if (handle < 0 && _moveHoverIndex >= 0 && _moveHoverIndex != _suppressHoverBoxIndex)
            {
                handle = GetSelectHandle(e.Location, _moveHoverIndex);
                if (handle >= 0) clickedIdx = _moveHoverIndex;
            }

            // No control hit → select only when the click lands on the object's actual drawn
            // pixels (its surface), never on the empty interior of its wrap box.
            if (handle < 0)
            {
                int surfIdx = HitTestAnnotationSurface(e.Location);
                if (surfIdx == _suppressHoverBoxIndex) surfIdx = -1;
                if (surfIdx >= 0) clickedIdx = surfIdx;
            }

            // Click on empty area with a drawing tool: clear any active selection before
            // starting to draw (same as Move mode behaviour). Emoji and Magnifier are
            // excluded because they place objects on click, not draw shapes.
            // Text mode returns early: deselect only, no new text instance.
            if (clickedIdx < 0 && _selectedAnnotationIndex >= 0
                && _mode != CaptureMode.Magnifier && _mode != CaptureMode.Emoji)
            {
                _selectedAnnotationIndex = -1;
                _multiSelectedIndices.Clear();
                Invalidate();
                if (_mode == CaptureMode.Text) return;
            }

            if (clickedIdx >= 0)
            {
                _selectedAnnotationIndex = clickedIdx;
                if (handle >= 0 && handle != 8)
                {
                    _isSelectResizing = true;
                    _selectResizeHandle = handle;
                    _selectDragStart = e.Location;
                    _selectHandleBounds = GetAnnotationBounds(_undoStack[clickedIdx]);
                    _selectResizeOriginalAnnotation = _undoStack[clickedIdx];
                    _selectPreviewAnnotation = _selectResizeOriginalAnnotation;
                    _renderSkipIndex = clickedIdx;
                    MarkCommittedAnnotationsDirty();
                    ClearCrosshairGuides();
                    Invalidate();
                }
                else
                {
                    _isSelectDragging = true;
                    var bounds = GetAnnotationBounds(_undoStack[clickedIdx]);
                    _selectPreviewAnnotation = _undoStack[clickedIdx];
                    _selectDragStart = e.Location;
                    _selectDragOffset = new Point(e.Location.X - bounds.X, e.Location.Y - bounds.Y);
                    _renderSkipIndex = clickedIdx;
                    MarkCommittedAnnotationsDirty();
                    ClearCrosshairGuides();
                    Invalidate();
                }
                return;
            }
        }

        // Select tool: check resize handles first, then hit-test annotations
        if (_mode == CaptureMode.Move)
        {
            // Double-click text to re-edit MUST run before select-drag. Starting a drag on the
            // first click of a double-click suppresses OnMouseDoubleClick, so detect here.
            // Also: the first click of a double-click sets _renderSkipIndex for drag, which would
            // make HitTestText miss the same annotation — temporarily clear it for the hit-test.
            if (IsCaptureTextDoubleClick(e))
            {
                int textHit = HitTestTextForDoubleClick(e.Location);
                if (textHit >= 0)
                {
                    // Cancel any select-drag started by the first click of this double-click.
                    _isSelectDragging = false;
                    _isSelectResizing = false;
                    _selectPreviewAnnotation = null;
                    _selectResizeOriginalAnnotation = null;
                    if (_renderSkipIndex >= 0)
                    {
                        _renderSkipIndex = -1;
                        MarkCommittedAnnotationsDirty();
                    }
                    BeginReEditText(textHit);
                    return;
                }
            }

            // Check resize/move handles on either the already-selected or the currently hovered annotation
            int handle = -1;
            int clickedIdx = -1;
            if (_selectedAnnotationIndex >= 0)
            {
                handle = GetSelectHandle(e.Location, _selectedAnnotationIndex);
                if (handle >= 0) clickedIdx = _selectedAnnotationIndex;
            }
            if (handle < 0)
            {
                for (int i = _undoStack.Count - 1; i >= 0; i--)
                {
                    int h = GetSelectHandle(e.Location, i);
                    if (h >= 0)
                    {
                        handle = h;
                        clickedIdx = i;
                        break;
                    }
                }
            }

            if (clickedIdx >= 0 && handle >= 0)
            {
                // Move/resize of an existing annotation — clear selection-count / help banners.
                HideToolBanner();
                _selectedAnnotationIndex = clickedIdx;
                if (handle != 8)
                {
                    _isSelectResizing = true;
                    _selectResizeHandle = handle;
                    _selectDragStart = e.Location;
                    _selectHandleBounds = GetAnnotationBounds(_undoStack[clickedIdx]);
                    _selectResizeOriginalAnnotation = _undoStack[clickedIdx];
                    _selectPreviewAnnotation = _selectResizeOriginalAnnotation;
                    _renderSkipIndex = clickedIdx;
                    MarkCommittedAnnotationsDirty();
                    ClearCrosshairGuides();
                    Invalidate();
                }
                else
                {
                    _isSelectDragging = true;
                    var bounds = GetAnnotationBounds(_undoStack[clickedIdx]);
                    _selectPreviewAnnotation = _undoStack[clickedIdx];
                    _selectDragStart = e.Location;
                    _selectDragOffset = new Point(e.Location.X - bounds.X, e.Location.Y - bounds.Y);
                    _renderSkipIndex = clickedIdx;
                    MarkCommittedAnnotationsDirty();
                    ClearCrosshairGuides();
                    Invalidate();
                }
                return;
            }

            int hit = HitTestAnnotationSurface(e.Location);
            if (hit >= 0)
            {
                HideToolBanner();
                if (_multiSelectedIndices.Count > 1 && _multiSelectedIndices.Contains(hit))
                {
                    _isSelectDragging = true;
                    _multiDragStart = e.Location;
                    _multiDragOriginals = _multiSelectedIndices
                        .Where(i => i >= 0 && i < _undoStack.Count)
                        .Select(i => (i, _undoStack[i]))
                        .ToList();
                    _selectedAnnotationIndex = hit;
                    ClearCrosshairGuides();
                    Invalidate();
                }
                else
                {
                    _multiSelectedIndices.Clear();
                    _multiDragOriginals = null;
                    _selectedAnnotationIndex = hit;
                    _isSelectDragging = true;
                    var bounds = GetAnnotationBounds(_undoStack[hit]);
                    _selectPreviewAnnotation = _undoStack[hit];
                    _selectDragStart = e.Location;
                    _selectDragOffset = new Point(e.Location.X - bounds.X, e.Location.Y - bounds.Y);
                    _renderSkipIndex = hit;
                    MarkCommittedAnnotationsDirty();
                    ClearCrosshairGuides();
                    Invalidate();
                }
            }
            else
            {
                _selectedAnnotationIndex = -1;
                _multiSelectedIndices.Clear();
                _isMarqueeSelecting = true;
                _marqueeStart = e.Location;
                _marqueeEnd = e.Location;
                HideToolBanner();
                Invalidate();
            }
            return;
        }

        if (_mode == CaptureMode.ColorPicker)
        {
            HideToolBanner();
            ColorPicked?.Invoke(_hexStr);
            return;
        }

        _hasDragged = false;
        // Any tool action (drag or click) dismisses the help banner so it never sits over the work.
        // Short animated fade with region-scoped invalidate; switch to HideToolBannerImmediate if glitchy.
        HideToolBanner();
        switch (_mode)
        {
            case CaptureMode.Rectangle:
            case CaptureMode.ScrollCapture:
            case CaptureMode.Center:
            case CaptureMode.Ocr:
            case CaptureMode.Scan:
            case CaptureMode.Sticker:
            case CaptureMode.Upscale:
                HideToolbarForCaptureTool();
                if (_windowDetectionMode == WindowDetectionMode.Off)
                {
                    _autoDetectRect = Rectangle.Empty;
                    _autoDetectActive = false;
                }
                else
                {
                    _autoDetectRect = WindowDetector.GetDetectionRectAtPoint(
                        e.Location, _virtualBounds, _windowDetectionMode);
                    _autoDetectActive = _autoDetectRect.Width > 0 && _autoDetectRect.Height > 0;
                }
                _isSelecting = true;
                _selectionStart = _selectionEnd = e.Location;
                _selectionRect = Rectangle.Empty;
                _hasSelection = false;
                ResetCaptureMagnifierDragPlacement();
                CloseSelectionAdorner();
                // Full invalidate: dimming must appear on every monitor immediately.
                // Partial invalidation around the selection only repaints the drag monitor.
                Invalidate();
                break;
            case CaptureMode.Text:
                HideToolbarForCaptureTool();
                // New text instance (not a re-edit)
                if (_isTyping)
                    CommitOrCancelInlineText(commit: true);
                _textEditStackIndex = -1;
                _textEditOriginal = null;
                _isTyping = true;
                _textPos = e.Location;
                _textBuffer = "";
                InvalidateActiveTextLayout();
                ShowTextBox();
                RefreshOverlayUiChrome();
                Invalidate(InflateForRepaint(Rectangle.Round(MeasureTextRect(
                    _textPos, "", _textFontSize, _textFontFamily, _textBold, _textItalic, _textBackground, _textMaxWidth, _textAlign))));
                break;
            case CaptureMode.Highlight:
                HideToolbarForCaptureTool();
                _isHighlighting = true;
                _highlightStart = e.Location;
                break;
            case CaptureMode.RectShape:
                HideToolbarForCaptureTool();
                _isRectShapeDragging = true;
                _shapeStart = e.Location;
                break;
            case CaptureMode.CircleShape:
                HideToolbarForCaptureTool();
                _isCircleShapeDragging = true;
                _shapeStart = e.Location;
                break;
            case CaptureMode.StepNumber:
                HideToolbarForCaptureTool();
                // AddAnnotation → PushEditCommand → RefreshNextStepNumber already advances
                // _nextStepNumber to max+1. A manual ++ here double-counts (1, 3, 5, …).
                AddAnnotation(new StepNumberAnnotation(e.Location, _nextStepNumber, _toolColor));
                SuppressHoverBoxForLastPlaced();
                Invalidate(InflateForRepaint(new Rectangle(e.Location.X - 16, e.Location.Y - 16, 32, 32)));
                break;
            case CaptureMode.Magnifier:
                HideToolbarForCaptureTool();
                // Place a persistent magnifier at click point
                int srcSz = 50;
                int sx2 = Math.Clamp(e.Location.X - srcSz / 2, 0, _bmpW - srcSz);
                int sy2 = Math.Clamp(e.Location.Y - srcSz / 2, 0, _bmpH - srcSz);
                AddAnnotation(new MagnifierAnnotation(e.Location, new Rectangle(sx2, sy2, srcSz, srcSz)));
                SuppressHoverBoxForLastPlaced();
                Invalidate(InflateForRepaint(GetMagnifierPreviewRect(e.Location)));
                break;
            case CaptureMode.Draw:
                HideToolbarForCaptureTool();
                _isSelecting = true;
                _currentStroke = new List<Point> { e.Location };
                break;
            case CaptureMode.Line:
                HideToolbarForCaptureTool();
                _isLineDragging = true;
                _lineStart = e.Location;
                break;
            case CaptureMode.Ruler:
                // Ruler is an annotation tool — don't hide the toolbar while measuring.
                // Clear any ruler selected via right-click so its frame doesn't linger over the new drag.
                if (_selectedAnnotationIndex >= 0)
                {
                    _selectedAnnotationIndex = -1;
                    Invalidate();
                }
                _isRulerDragging = true;
                _rulerStart = e.Location;
                break;
            case CaptureMode.Arrow:
                HideToolbarForCaptureTool();
                _isArrowDragging = true;
                _arrowStart = e.Location;
                break;
            case CaptureMode.CurvedArrow:
                HideToolbarForCaptureTool();
                _isCurvedArrowDragging = true;
                _currentCurvedArrow = new List<Point> { e.Location };
                break;
            case CaptureMode.Blur:
                HideToolbarForCaptureTool();
                _isBlurring = true;
                _blurStart = e.Location;
                break;
            case CaptureMode.Eraser:
                HideToolbarForCaptureTool();
                TryEraseAnnotationAt(e.Location);
                break;
        }
    }

}
