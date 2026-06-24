using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Models;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
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
            Cancel();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        // Hide the first-time capture banner on any user interaction
        HideCaptureBanner();

        // Region confirmation mode: handles and buttons take priority
        if (_isConfirmingSelection)
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
            if (confirmBtnHit == 0)
            {
                StartConfirmPress(0); // squash animation, then CommitConfirmedSelection
                return;
            }
            if (confirmBtnHit == 1)
            {
                StartConfirmPress(1); // squash animation, then ExitConfirmMode
                return;
            }
            if (_confirmRect.Contains(e.Location))
            {
                // Start dragging the confirmed region
                _isConfirmDragging = true;
                _confirmHandleDragIndex = -1;
                _confirmDragStart = e.Location;
                _confirmDragOffset = new Point(e.Location.X - _confirmRect.X, e.Location.Y - _confirmRect.Y);
                _confirmDragStartRect = _confirmRect;
                return;
            }
            // Left-click outside the confirm UI: ignore it, so a stray click never captures
            // and the pending selection is kept. Esc and right-click still cancel/exit.
            return;
        }

        if (_logoRect.Contains(e.Location))
        {
            HideToolbarTooltip();
            ShowQuickStartGuide();
            return;
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
            if (btn == PositionButtonIndex) { ToggleToolbarPosition(); return; } // toggle position
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
            // Left-click on empty toolbar space is a no-op; right-click still shows the context menu.
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
                _fontPickerScroll = 0; _fontSearch = ""; _filteredFonts = null;
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
            // Check if clicking inside the text box -- start dragging to move
            var textBox = GetActiveTextRect();
            if (textBox.Contains(e.Location))
            {
                _textDragging = true;
                _lastTextDragLocation = Point.Empty;
                _lastTextDragFrameUtc = default;
                _textDragOffset = new Point(e.Location.X - _textPos.X, e.Location.Y - _textPos.Y);
                ClearCrosshairGuides();
                Invalidate();
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
                var ta = GetTextAnnotations()[hitIdx];
                var oldTextRect = InflateForRepaint(Rectangle.Round(MeasureTextRect(ta.Pos, ta.Text, ta.FontSize, ta.FontFamily, ta.Bold, ta.Italic, ta.Background)));
                RemoveAnnotation(ta);
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
                InvalidateActiveTextLayout();
                ShowTextBox();
                _textDragging = true;
                _lastTextDragLocation = Point.Empty;
                _lastTextDragFrameUtc = default;
                _textDragOffset = new Point(e.Location.X - _textPos.X, e.Location.Y - _textPos.Y);
                RefreshOverlayUiChrome();
                Invalidate();
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

            int activeHoverIdx = _moveHoverIndex;
            if (activeHoverIdx < 0)
            {
                activeHoverIdx = HitTestAnnotation(e.Location);
            }
            // Don't let a click grab the annotation we just placed (cursor is still on it);
            // that would turn a second placement into an accidental move.
            if (activeHoverIdx == _suppressHoverBoxIndex)
                activeHoverIdx = -1;

            if (handle < 0 && activeHoverIdx >= 0)
            {
                handle = GetSelectHandle(e.Location, activeHoverIdx);
                if (handle >= 0) clickedIdx = activeHoverIdx;
            }
            if (handle < 0 && activeHoverIdx >= 0)
            {
                clickedIdx = activeHoverIdx;
            }

            if (clickedIdx >= 0)
            {
                var targetTool = ToolDef.AllTools.FirstOrDefault(t => t.Mode == CaptureMode.Move);
                if (targetTool != null)
                {
                    SetTool(targetTool);
                    CalcToolbar();
                    InvalidateToolbarArea();
                }

                _selectedAnnotationIndex = clickedIdx;
                if (handle >= 0)
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
            // Check resize handles on already-selected annotation
            int handle = GetSelectHandle(e.Location);
            if (handle >= 0 && _selectedAnnotationIndex >= 0)
            {
                _isSelectResizing = true;
                _selectResizeHandle = handle;
                _selectDragStart = e.Location;
                _selectHandleBounds = GetAnnotationBounds(_undoStack[_selectedAnnotationIndex]);
                _selectResizeOriginalAnnotation = _undoStack[_selectedAnnotationIndex];
                _selectPreviewAnnotation = _selectResizeOriginalAnnotation;
                _renderSkipIndex = _selectedAnnotationIndex;
                MarkCommittedAnnotationsDirty();
                ClearCrosshairGuides();
                Invalidate();
                return;
            }

            int hit = HitTestAnnotation(e.Location);
            if (hit >= 0)
            {
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
            ColorPicked?.Invoke(_hexStr);
            return;
        }

        _hasDragged = false;
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
                HideToolBanner();
                var previousSelectionRect = _selectionRect;
                var previousAutoDetectRect = _autoDetectRect;
                bool previousSelectionVisible = _hasSelection;
                bool previousAutoDetectVisible = _autoDetectActive;
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
                if (previousSelectionVisible || previousAutoDetectVisible)
                    Invalidate(Rectangle.Union(
                        InflateForRepaint(previousSelectionRect),
                        InflateForRepaint(previousAutoDetectRect)));
                break;
            case CaptureMode.Text:
                HideToolbarForCaptureTool();
                _isTyping = true;
                _textPos = e.Location;
                _textBuffer = "";
                InvalidateActiveTextLayout();
                ShowTextBox();
                RefreshOverlayUiChrome();
                Invalidate(InflateForRepaint(Rectangle.Round(MeasureTextRect(_textPos, "", _textFontSize, _textFontFamily, _textBold, _textItalic, _textBackground))));
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
                // Dismiss the instruction banner so its fade animation can't fire repaints mid-drag.
                HideToolBanner();
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
