using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // Text under the cursor always wins — including Move/Pick (select-all).
        // Note: often this path never runs after Move starts a drag on the first click;
        // the timed check in OnMouseDown is the reliable path.
        int hitIdx = HitTestText(e.Location);
        if (hitIdx >= 0)
        {
            // Cancel any select-drag started by the first click of the double-click.
            _isSelectDragging = false;
            _isSelectResizing = false;
            _selectPreviewAnnotation = null;
            if (_renderSkipIndex >= 0 && _textEditStackIndex < 0)
            {
                _renderSkipIndex = -1;
                MarkCommittedAnnotationsDirty();
            }
            BeginReEditText(hitIdx);
            return;
        }

        if (_mode == CaptureMode.Move)
            SelectAll();
    }

    /// <summary>Timed double-click detection for text re-edit under Pick/Move.</summary>
    private bool IsCaptureTextDoubleClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return false;
        if (e.Clicks >= 2) return true;

        int now = Environment.TickCount;
        var size = SystemInformation.DoubleClickSize;
        bool isDouble = _lastTextDblClickTick != 0
            && unchecked(now - _lastTextDblClickTick) <= SystemInformation.DoubleClickTime
            && Math.Abs(e.Location.X - _lastTextDblClickLocation.X) <= size.Width
            && Math.Abs(e.Location.Y - _lastTextDblClickLocation.Y) <= size.Height;

        _lastTextDblClickTick = now;
        _lastTextDblClickLocation = e.Location;
        return isDouble;
    }


    private void ClampToolbarToScreen()
    {
        var screenPoint = new Point(
            _virtualBounds.X + _toolbarRect.X + _toolbarRect.Width / 2,
            _virtualBounds.Y + _toolbarRect.Y + _toolbarRect.Height / 2);
        var screen = Screen.FromPoint(screenPoint);
        var work = screen.WorkingArea;

        int minX = work.Left - _virtualBounds.X + UiChrome.ScaleInt(8);
        int maxX = work.Right - _virtualBounds.X - _toolbarRect.Width - UiChrome.ScaleInt(8);
        int minY = work.Top - _virtualBounds.Y + UiChrome.ScaleInt(8);
        int maxY = work.Bottom - _virtualBounds.Y - _toolbarRect.Height - UiChrome.ScaleInt(8);

        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;

        int clampedX = Math.Clamp(_toolbarRect.X, minX, maxX);
        int clampedY = Math.Clamp(_toolbarRect.Y, minY, maxY);

        int clampDx = clampedX - _toolbarRect.X;
        int clampDy = clampedY - _toolbarRect.Y;

        if (clampDx != 0 || clampDy != 0)
        {
            _toolbarCustomOffset = new Point(_toolbarCustomOffset.X + clampDx, _toolbarCustomOffset.Y + clampDy);
            CalcToolbar();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isDraggingToolbar)
        {
            if (_tooltipVisible)
                HideToolbarTooltip();

            if (!Cursor.Equals(Cursors.Hand))
                Cursor = Cursors.Hand;

            int dx = e.Location.X - _toolbarDragStartMouse.X;
            int dy = e.Location.Y - _toolbarDragStartMouse.Y;
            if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)
                _hasMovedToolbarByDrag = true;

            if (_hasMovedToolbarByDrag)
            {
                _toolbarCustomOffset = new Point(_toolbarDragStartOffset.X + dx, _toolbarDragStartOffset.Y + dy);
                CalcToolbar();
                ClampToolbarToScreen();
                PositionToolbarForm();
                UpdateToolbarSurfaceOnly();
            }
            return;
        }

        if (_isConfirmingSelection)
        {
            _prevCursorPos = _lastCursorPos;
            _lastCursorPos = e.Location;

            // Never keep the capture magnifier alive over confirm chrome or while resizing/moving.
            if (_captureMagnifierForm is { Visible: true })
                HideCaptureMagnifier();

            // 1. Confirm-mode handle resize
            if (_confirmHandleDragIndex >= 0)
            {
                ClearCrosshairGuides();
                SetSnapGuides(false, false);
                InvalidateConfirmChromeLayout();
                int dx = e.Location.X - _confirmDragStart.X;
                int dy = e.Location.Y - _confirmDragStart.Y;
                var ob = _confirmDragStartRect;
                var b = _selectionMonitorClientBounds;
                int minX = b.IsEmpty ? 0 : b.Left;
                int maxX = b.IsEmpty ? _bmpW - 1 : b.Right - 1;
                int minY = b.IsEmpty ? 0 : b.Top;
                int maxY = b.IsEmpty ? _bmpH - 1 : b.Bottom - 1;

                Rectangle nb;
                switch (_confirmHandleDragIndex)
                {
                    case 0: // TL
                        nb = Rectangle.FromLTRB(
                            Math.Clamp(ob.Left + dx, minX, ob.Right - 5),
                            Math.Clamp(ob.Top + dy, minY, ob.Bottom - 5),
                            ob.Right,
                            ob.Bottom);
                        break;
                    case 1: // TR
                        nb = Rectangle.FromLTRB(
                            ob.Left,
                            Math.Clamp(ob.Top + dy, minY, ob.Bottom - 5),
                            Math.Clamp(ob.Right + dx, ob.Left + 5, maxX),
                            ob.Bottom);
                        break;
                    case 2: // BL
                        nb = Rectangle.FromLTRB(
                            Math.Clamp(ob.Left + dx, minX, ob.Right - 5),
                            ob.Top,
                            ob.Right,
                            Math.Clamp(ob.Bottom + dy, ob.Top + 5, maxY));
                        break;
                    case 3: // BR
                        nb = Rectangle.FromLTRB(
                            ob.Left,
                            ob.Top,
                            Math.Clamp(ob.Right + dx, ob.Left + 5, maxX),
                            Math.Clamp(ob.Bottom + dy, ob.Top + 5, maxY));
                        break;
                    case 4: // Top
                        nb = Rectangle.FromLTRB(
                            ob.Left,
                            Math.Clamp(ob.Top + dy, minY, ob.Bottom - 5),
                            ob.Right,
                            ob.Bottom);
                        break;
                    case 5: // Left
                        nb = Rectangle.FromLTRB(
                            Math.Clamp(ob.Left + dx, minX, ob.Right - 5),
                            ob.Top,
                            ob.Right,
                            ob.Bottom);
                        break;
                    case 6: // Right
                        nb = Rectangle.FromLTRB(
                            ob.Left,
                            ob.Top,
                            Math.Clamp(ob.Right + dx, ob.Left + 5, maxX),
                            ob.Bottom);
                        break;
                    case 7: // Bottom
                        nb = Rectangle.FromLTRB(
                            ob.Left,
                            ob.Top,
                            ob.Right,
                            Math.Clamp(ob.Bottom + dy, ob.Top + 5, maxY));
                        break;
                    default:
                        nb = ob;
                        break;
                }

                if (nb.Width > 5 && nb.Height > 5)
                {
                    // Same lightweight path as move: freeze docks, only repaint the hole/frame.
                    var oldRect = _confirmRect;
                    var oldPill = _confirmSizeReadoutRect;
                    _confirmRect = nb;
                    RefreshConfirmSizeReadoutRect();
                    InvalidateConfirmHoleMove(oldRect, _confirmRect, oldPill, _confirmSizeReadoutRect);
                }
                return;
            }

            // 2. Confirm-mode region drag (move entire selection without resizing)
            if (_isConfirmDragging)
            {
                ClearCrosshairGuides();
                SetSnapGuides(false, false);
                int newX = e.Location.X - _confirmDragOffset.X;
                int newY = e.Location.Y - _confirmDragOffset.Y;
                // Cheap path: single dirty union + translate chrome/toolbar (no per-pixel rebuild).
                ApplyConfirmRectDrag(new Rectangle(newX, newY, _confirmRect.Width, _confirmRect.Height));
                return;
            }

            // 2b. Outside-frame reselect (no annotations): promote armed click to a new rubber-band.
            if (_outsideReselectArmed && !HasConfirmAnnotations())
            {
                int odx = e.Location.X - _outsideReselectDown.X;
                int ody = e.Location.Y - _outsideReselectDown.Y;
                if (!_outsideReselectMoved && (Math.Abs(odx) > 3 || Math.Abs(ody) > 3))
                {
                    _outsideReselectMoved = true;
                    ExitConfirmMode(showToolbar: false);
                    StartAreaSelectionFromPoint(_outsideReselectDown);
                    // Fall through past confirm hover into normal selection mouse-move.
                }
                else if (!_outsideReselectMoved)
                {
                    Cursor = CursorFactory.PrecisionCursor;
                    return;
                }
            }

            // Still in confirm? (may have just exited via outside reselect promote)
            if (_isConfirmingSelection)
            {
                // 3. Confirm-mode hover takes priority
                int prevHoveredConfirm = _hoveredConfirmButton;
                _hoveredConfirmButton = -1;

                System.Windows.Forms.Cursor confirmTarget = Cursors.Default;
                int ch = HitTestConfirmHandle(e.Location);
                int btnHit = ch >= 0 ? -1 : HitTestConfirmButton(e.Location);
                bool sizePillHover = ch < 0 && btnHit < 0 && HitTestConfirmSizeReadout(e.Location);
                bool gripHover = ch < 0 && btnHit < 0 && !sizePillHover
                    && ((ShowAnnotationChrome && !_annotationGripRect.IsEmpty && _annotationGripRect.Contains(e.Location))
                        || (!_confirmGripRect.IsEmpty && _confirmGripRect.Contains(e.Location)));

                if (sizePillHover != _hoveredConfirmSizeReadout)
                {
                    _hoveredConfirmSizeReadout = sizePillHover;
                    if (!_confirmSizeReadoutRect.IsEmpty)
                        Invalidate(InflateForRepaint(_confirmSizeReadoutRect, UiChrome.ScaleInt(8)));
                }
                if (ch >= 0)
                    confirmTarget = ch switch
                    {
                        0 or 3 => Cursors.SizeNWSE,
                        1 or 2 => Cursors.SizeNESW,
                        4 or 7 => Cursors.SizeNS,
                        5 or 6 => Cursors.SizeWE,
                        _ => Cursors.Default
                    };
                else if (btnHit >= 0)
                {
                    confirmTarget = Cursors.Hand;
                    _hoveredConfirmButton = btnHit;
                }
                else if (sizePillHover)
                {
                    confirmTarget = Cursors.Hand;
                    if (!Cursor.Equals(confirmTarget))
                        Cursor = confirmTarget;
                    return;
                }
                else if (gripHover)
                {
                    confirmTarget = Cursors.Hand;
                }
                else if (IsOutsideLockedCaptureFrame(e.Location))
                {
                    confirmTarget = HasConfirmAnnotations() ? Cursors.Default : CursorFactory.PrecisionCursor;
                    if (!Cursor.Equals(confirmTarget))
                        Cursor = confirmTarget;
                    return;
                }
                else if (ToolDef.IsAnnotationTool(_mode))
                {
                    if (_hoveredConfirmButton != prevHoveredConfirm)
                    {
                        OnConfirmHoverChanged(prevHoveredConfirm);
                        HideToolbarTooltip();
                        _tooltipDismissed = false;
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }
                    // Fall through — annotation mouse-move handlers below.
                }
                else if (_toolbarRect.Contains(e.Location)
                    || _menuActivatorRect.Contains(e.Location)
                    || IsPointInToolbarChrome(e.Location))
                {
                    if (_hoveredConfirmButton != prevHoveredConfirm)
                    {
                        OnConfirmHoverChanged(prevHoveredConfirm);
                        HideToolbarTooltip();
                        _tooltipDismissed = false;
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }
                    // Fall through for toolbar hover tracking below.
                }
                else if (_confirmRect.Contains(e.Location)
                         && (!ToolDef.IsAnnotationTool(_mode)
                             || (_mode == CaptureMode.Move
                                 && HitTestAnnotation(e.Location) < 0
                                 && HitTestAnnotationSurface(e.Location) < 0)))
                {
                    confirmTarget = Cursors.Hand;
                    if (!Cursor.Equals(confirmTarget)) Cursor = confirmTarget;

                    if (_hoveredConfirmButton != prevHoveredConfirm)
                    {
                        OnConfirmHoverChanged(prevHoveredConfirm);
                        HideToolbarTooltip();
                        _tooltipDismissed = false;
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }

                    return;
                }
                else if (ToolDef.IsAnnotationTool(_mode))
                {
                    if (_hoveredConfirmButton != prevHoveredConfirm)
                    {
                        OnConfirmHoverChanged(prevHoveredConfirm);
                        HideToolbarTooltip();
                        _tooltipDismissed = false;
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    confirmTarget = CursorFactory.PrecisionCursor;
                    if (!Cursor.Equals(confirmTarget)) Cursor = confirmTarget;

                    if (_hoveredConfirmButton != prevHoveredConfirm)
                    {
                        OnConfirmHoverChanged(prevHoveredConfirm);
                        HideToolbarTooltip();
                        _tooltipDismissed = false;
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }

                    return;
                }

                if (ch >= 0 || btnHit >= 0 || gripHover)
                {
                    if (!Cursor.Equals(confirmTarget)) Cursor = confirmTarget;

                    if (_hoveredConfirmButton != prevHoveredConfirm)
                    {
                        OnConfirmHoverChanged(prevHoveredConfirm);
                        HideToolbarTooltip();
                        _tooltipDismissed = false;
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }

                    return;
                }
            }
        }

        if (_isMarqueeSelecting)
        {
            _marqueeEnd = e.Location;
            var marqueeRect = NormRect(_marqueeStart, _marqueeEnd);
            _multiSelectedIndices.Clear();
            _selectedAnnotationIndex = -1;

            if (marqueeRect.Width >= 2 && marqueeRect.Height >= 2)
            {
                for (int i = 0; i < _undoStack.Count; i++)
                {
                    var bounds = GetAnnotationBounds(_undoStack[i]);
                    if (bounds != Rectangle.Empty && marqueeRect.IntersectsWith(bounds))
                    {
                        _multiSelectedIndices.Add(i);
                    }
                }

                if (_multiSelectedIndices.Count == 1)
                {
                    _selectedAnnotationIndex = _multiSelectedIndices.First();
                    _multiSelectedIndices.Clear();
                    HideToolBanner();
                }
                else if (_multiSelectedIndices.Count > 1)
                {
                    _selectedAnnotationIndex = _multiSelectedIndices.Max();
                    var msg = string.Format(LocalizationService.Translate("{0} objects selected"), _multiSelectedIndices.Count);
                    ShowToolBanner(msg, persistent: true);
                }
                else
                {
                    HideToolBanner();
                }
            }
            else
            {
                HideToolBanner();
            }

            Invalidate();
            return;
        }

        bool needsRepaint = false;
        bool toolbarDirty = false;

        Point anchorPoint = _isSelecting && !_selectionMonitorClientBounds.IsEmpty
            ? ClampPointToSelectionMonitor(e.Location)
            : e.Location;

        if (UpdateToolbarAnchorForClientPoint(anchorPoint))
            toolbarDirty = true;

        if (_textSelecting && _isTyping && _textBox != null)
        {
            // Keep buffer in sync for caret/selection measure
            _textBuffer = _textBox.Text ?? "";
            int idx = GetTextCharIndexAt(e.Location);
            int start = Math.Min(_textSelectionAnchor, idx);
            int end = Math.Max(_textSelectionAnchor, idx);
            _textBox.SelectionStart = start;
            _textBox.SelectionLength = Math.Max(0, end - start);
            Invalidate(InflateForRepaint(Rectangle.Round(GetActiveTextRect()), 24));
            return;
        }

        if (_textDragging && _isTyping)
        {
            if (!Cursor.Equals(Cursors.Hand))
                Cursor = Cursors.Hand;
            var now = DateTime.UtcNow;
            if (_lastTextDragLocation != Point.Empty &&
                Math.Abs(e.Location.X - _lastTextDragLocation.X) < 2 &&
                Math.Abs(e.Location.Y - _lastTextDragLocation.Y) < 2)
                return;

            if (_lastTextDragFrameUtc != default &&
                (now - _lastTextDragFrameUtc).TotalMilliseconds < UiChrome.FrameIntervalMs)
                return;

            _lastTextDragLocation = e.Location;
            _lastTextDragFrameUtc = now;
            ClearCrosshairGuides();
            SetSnapGuides(false, false);
            var oldRect = Rectangle.Round(GetActiveTextRect());
            var oldToolbarRect = Rectangle.Round(GetTextToolbarBounds());
            var desiredTextPos = new Point(e.Location.X - _textDragOffset.X, e.Location.Y - _textDragOffset.Y);
            var snappedTextPos = SnapTextPositionToGlobalCenter(desiredTextPos);
            _textPos = snappedTextPos;
            InvalidateActiveTextLayout();
            var newRect = Rectangle.Round(GetActiveTextRect());
            var newToolbarRect = Rectangle.Round(GetTextToolbarBounds());
            InvalidateLiveTransform(
                Rectangle.Union(oldRect, oldToolbarRect),
                Rectangle.Union(newRect, newToolbarRect));
            return;
        }

        // Text resize drag - each handle pulls in its own direction
        if (_textResizing && _isTyping)
        {
            ClearCrosshairGuides();
            SetSnapGuides(false, false);
            var oldRect = Rectangle.Round(GetActiveTextRect());
            var oldToolbarRect = Rectangle.Round(GetTextToolbarBounds());
            float totalDx = e.Location.X - _textResizeStart.X;
            float totalDy = e.Location.Y - _textResizeStart.Y;
            float delta = _textResizeHandle switch
            {
                0 => (-totalDx - totalDy) * 0.15f,
                1 => (totalDx - totalDy) * 0.15f,
                2 => (-totalDx + totalDy) * 0.15f,
                3 => (totalDx + totalDy) * 0.15f,
                _ => 0
            };
            _textFontSize = Math.Clamp(_textResizeStartFontSize + delta, 10f, 120f);
            InvalidateActiveTextLayout();
            var newRect = Rectangle.Round(GetActiveTextRect());
            var newToolbarRect = Rectangle.Round(GetTextToolbarBounds());
            InvalidateLiveTransform(
                Rectangle.Union(oldRect, oldToolbarRect),
                Rectangle.Union(newRect, newToolbarRect));
            return;
        }

        int btn = GetToolbarButtonAt(e.Location);
        if (btn != _hoveredButton)
        {
            _hoveredButton = btn;
            toolbarDirty = true;
            HideToolbarTooltip();
            _tooltipDismissed = false;
            _hoverButtonStartTime = DateTime.UtcNow;
        }

        bool hovBrand = IsPointInBrandClickArea(e.Location);
        bool hovBrandDrag = !_brandRect.IsEmpty && _brandRect.Contains(e.Location) && !hovBrand;
        bool hovActivator = _menuActivatorRect.Contains(e.Location);
        if (hovBrand != _hoveredBrand || hovBrandDrag != _hoveredBrandDragArea || hovActivator != _hoveredMenuActivator)
        {
            _hoveredBrand = hovBrand;
            _hoveredBrandDragArea = hovBrandDrag;
            _hoveredMenuActivator = hovActivator;
            toolbarDirty = true;
            // Reset tooltip so brand / drag / ▼ can show their own hints after the delay.
            HideToolbarTooltip();
            _tooltipDismissed = false;
            _hoverButtonStartTime = DateTime.UtcNow;
        }

        int prevAltSlot = _hoveredAltSlotIndex;
        _hoveredAltSlotIndex = GetAltPopupSlotAt(e.Location);
        _hoveredAltCaptureBtn = _hoveredAltSlotIndex >= 0;
        if (_hoveredAltSlotIndex != prevAltSlot)
        {
            toolbarDirty = true;
            HideToolbarTooltip();
            _tooltipDismissed = false;
            _hoverButtonStartTime = DateTime.UtcNow;
        }

        if (_altCapturePopupOpen)
        {
            bool nearToolbar = _toolbarRect.Contains(e.Location) || IsPointInOverlayUi(e.Location);
            bool nearAltBtn = _altCaptureButtonRect.Contains(e.Location) || _hoveredAltSlotIndex >= 0;
            if (!nearToolbar && !nearAltBtn)
            {
                int distToolbar = DistToRect(e.Location, _toolbarRect);
                int distAlt = DistToRect(e.Location, _altCaptureButtonRect);
                if (Math.Min(distToolbar, distAlt) > 40)
                {
                    CloseAltToolPopup(invalidate: false);
                    toolbarDirty = true;
                }
            }
        }

        // Text toolbar button hover tracking
        int prevTextBtn = _hoveredTextBtn;
        _hoveredTextBtn = -1;
        if (_isTyping)
        {
            if (_textBoldBtnRect.Contains(e.Location)) _hoveredTextBtn = 0;
            else if (_textItalicBtnRect.Contains(e.Location)) _hoveredTextBtn = 1;
            else if (_textStrokeBtnRect.Contains(e.Location)) _hoveredTextBtn = 2;
            else if (_textShadowBtnRect.Contains(e.Location)) _hoveredTextBtn = 3;
            else if (_textBackgroundBtnRect.Contains(e.Location)) _hoveredTextBtn = 4;
            else if (_textFontBtnRect.Contains(e.Location)) _hoveredTextBtn = 5;
            else if (_textSizeMinusBtnRect.Contains(e.Location)) _hoveredTextBtn = 6;
            else if (_textSizePlusBtnRect.Contains(e.Location)) _hoveredTextBtn = 7;
            else if (_textGripRect.Contains(e.Location)) _hoveredTextBtn = 8;
            else if (_textAlignLeftBtnRect.Contains(e.Location)) _hoveredTextBtn = 9;
            else if (_textAlignCenterBtnRect.Contains(e.Location)) _hoveredTextBtn = 10;
            else if (_textAlignRightBtnRect.Contains(e.Location)) _hoveredTextBtn = 11;
        }
        if (_hoveredTextBtn != prevTextBtn)
        {
            _textBtnTooltip = _hoveredTextBtn switch
            {
                0 => "Bold", 1 => "Italic", 2 => "Stroke", 3 => "Shadow", 4 => "Background",
                5 => _textFontFamily, 6 => "Decrease size", 7 => "Increase size", 8 => "Move",
                9 => "Align left", 10 => "Align center", 11 => "Align right",
                _ => ""
            };
            UpdateTextToolbarTooltip();
            needsRepaint = true;
        }



        // Select tool resize
        if (_isSelectResizing && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count && _selectResizeOriginalAnnotation is not null)
        {
            ClearCrosshairGuides();
            SetSnapGuides(false, false);
            var oldBounds = GetAnnotationBounds(_selectPreviewAnnotation ?? _undoStack[_selectedAnnotationIndex]);
            int dx = e.Location.X - _selectDragStart.X;
            int dy = e.Location.Y - _selectDragStart.Y;
            var ob = _selectHandleBounds;
            Rectangle nb = _selectResizeHandle switch
            {
                0 => Rectangle.FromLTRB(ob.Left + dx, ob.Top + dy, ob.Right, ob.Bottom),  // TL
                1 => Rectangle.FromLTRB(ob.Left, ob.Top + dy, ob.Right + dx, ob.Bottom),  // TR
                2 => Rectangle.FromLTRB(ob.Left + dx, ob.Top, ob.Right, ob.Bottom + dy),  // BL
                3 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right + dx, ob.Bottom + dy),  // BR
                4 => Rectangle.FromLTRB(ob.Left, ob.Top + dy, ob.Right, ob.Bottom),       // Top
                5 => Rectangle.FromLTRB(ob.Left + dx, ob.Top, ob.Right, ob.Bottom),       // Left
                6 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right + dx, ob.Bottom),       // Right
                7 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right, ob.Bottom + dy),       // Bottom
                _ => ob
            };
            if (nb.Width > 5 && nb.Height > 5)
            {
                var scaled = ScaleAnnotation(_selectResizeOriginalAnnotation, ob, nb);
                _selectPreviewAnnotation = scaled;
                var newBounds = GetAnnotationBounds(scaled);
                InvalidateLiveTransform(oldBounds, newBounds);
            }
            return;
        }

        // Select tool move drag
        if (_isSelectDragging && _multiDragOriginals is not null && _multiSelectedIndices.Count > 1)
        {
            ClearCrosshairGuides();
            int dx = e.Location.X - _multiDragStart.X;
            int dy = e.Location.Y - _multiDragStart.Y;
            if (Math.Abs(dx) > 0 || Math.Abs(dy) > 0)
            {
                foreach (var (mi, orig) in _multiDragOriginals)
                {
                    if (mi >= 0 && mi < _undoStack.Count)
                    {
                        _undoStack[mi] = MoveAnnotation(orig, dx, dy);
                    }
                }
                MarkCommittedAnnotationsDirty();
                Invalidate();
            }
            return;
        }

        if (_isSelectDragging && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count)
        {
            ClearCrosshairGuides();
            var current = _selectPreviewAnnotation ?? _undoStack[_selectedAnnotationIndex];
            var currentBounds = GetAnnotationBounds(current);
            var desiredTopLeft = new Point(e.Location.X - _selectDragOffset.X, e.Location.Y - _selectDragOffset.Y);
            var snappedTopLeft = SnapPointToGlobalCenter(
                new Rectangle(desiredTopLeft, currentBounds.Size),
                desiredTopLeft);
            int dx = snappedTopLeft.X - currentBounds.X;
            int dy = snappedTopLeft.Y - currentBounds.Y;
            if (Math.Abs(dx) > 0 || Math.Abs(dy) > 0)
            {
                var moved = MoveAnnotation(current, dx, dy);
                _selectPreviewAnnotation = moved;
                InvalidateLiveTransform(currentBounds, GetAnnotationBounds(moved));
            }
            else
                SetSnapGuides(false, false);
            return;
        }

        // Cursor: show appropriate cursor for context
        System.Windows.Forms.Cursor target = Cursors.Default;
        if (_fontPickerOpen && _fontPickerRect.Contains(e.Location))
        {
            if (IsPointInFontPickerSearch(e.Location))
                target = Cursors.IBeam;
            else if (IsPointInFontPickerScrollbar(e.Location) || IsPointInFontPickerList(e.Location))
                target = Cursors.Hand;
            else
                target = Cursors.Default;
        }
        else if (_emojiPickerOpen && _emojiPickerRect.Contains(e.Location))
        {
            if (IsPointInEmojiPickerSearch(e.Location))
                target = Cursors.IBeam;
            else if (IsPointInEmojiPickerItem(e.Location))
                target = Cursors.Hand;
            else
                target = Cursors.Default;
        }
        else if (_colorPickerOpen && _colorPickerRect.Contains(e.Location))
            target = IsPointInColorPickerSwatch(e.Location) ? Cursors.Hand : Cursors.Default;
        else if (_altCapturePopupOpen && (_altCaptureButtonRect.Contains(e.Location) || _hoveredAltSlotIndex >= 0))
            target = _hoveredAltSlotIndex >= 0 ? Cursors.Hand : Cursors.Default;

        else if (TryGetToolbarHoverCursor(e.Location) is { } toolbarCursor)
            target = toolbarCursor;
        else if (_isTyping && _hoveredTextBtn == 8)
            target = Cursors.Hand;
        else if (_isTyping && _hoveredTextBtn >= 0)
            target = Cursors.Hand;
        else if (_isTyping && _textToolbarRect.Contains(e.Location))
            target = Cursors.Default;
        else if (_isTyping)
        {
            int h = GetTextHandle(e.Location);
            if (h >= 0) target = h is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            // Over the glyphs: I-beam for caret/selection. Move is only via the grip (handled above).
            else if (GetActiveTextRect().Contains(e.Location)) target = Cursors.IBeam;
            else target = Cursors.Default;
        }
        else if (IsDrawingOrMoveMode(_mode))
        {
            bool handled = false;
            if (!IsDraggingAnyAnnotation())
            {
                int sh = -1;
                if (_selectedAnnotationIndex >= 0)
                {
                    sh = GetSelectHandle(e.Location, _selectedAnnotationIndex);
                }
                if (sh < 0 && _moveHoverIndex >= 0)
                {
                    sh = GetSelectHandle(e.Location, _moveHoverIndex);
                }

                if (sh >= 0)
                {
                    target = sh switch
                    {
                        0 or 3 => Cursors.SizeNWSE,
                        1 or 2 => Cursors.SizeNESW,
                        4 or 7 => Cursors.SizeNS,
                        5 or 6 => Cursors.SizeWE,
                        8      => Cursors.SizeAll,  // center move knob
                        _      => Cursors.Default
                    };
                    handled = true;
                }
                else
                {
                    UpdateMoveHoverIndex(e.Location);
                }

                if (!handled)
                {
                    int hoverIdx = _moveHoverIndex >= 0 ? _moveHoverIndex : _selectedAnnotationIndex;
                    if (hoverIdx >= 0 && hoverIdx < _undoStack.Count
                        && IsOverAnnotationSurface(_undoStack[hoverIdx], e.Location))
                    {
                        target = Cursors.SizeAll;
                        handled = true;
                    }
                }
            }

            if (!handled)
            {

                if (_mode == CaptureMode.Text && !_isTyping)
                    target = Cursors.IBeam;
                else if (_mode == CaptureMode.Move)
                    target = Cursors.Default;
                else if (_mode == CaptureMode.StepNumber && !IsPointInOverlayUi(e.Location))
                    // The step badge ghost is centered on the cursor and acts as the pointer,
                    // so hide the crosshair (it would sit on top of the number).
                    target = _blankCursor;
                else if (_mode == CaptureMode.ColorPicker)
                    target = CursorFactory.EyedropperCursor;
                else
                    target = CursorFactory.PrecisionCursor;
            }
        }
        else if (_mode == CaptureMode.Eraser)
        {
            int h = HitTestAnnotation(e.Location);
            if (h != _eraserHoverIndex)
            {
                if (_eraserHoverIndex >= 0 && _eraserHoverIndex < _undoStack.Count)
                    Invalidate(Rectangle.Inflate(GetAnnotationBounds(_undoStack[_eraserHoverIndex]), 6, 6));
                _eraserHoverIndex = h;
                if (h >= 0 && h < _undoStack.Count)
                    Invalidate(Rectangle.Inflate(GetAnnotationBounds(_undoStack[h]), 6, 6));
            }
            target = CursorFactory.EraserCursor;
        }
        else if (_mode == CaptureMode.ColorPicker)
            target = CursorFactory.EyedropperCursor;
        else
            target = CursorFactory.PrecisionCursor;

        if (!Cursor.Equals(target)) Cursor = target;

        Point oldCursor;
        if (_isConfirmingSelection && ToolDef.IsAnnotationTool(_mode))
        {
            // Confirm block already advanced _lastCursorPos; keep _prevCursorPos as the
            // previous point so the tool-cursor chip invalidates correctly while moving.
            oldCursor = _prevCursorPos == Point.Empty ? e.Location : _prevCursorPos;
        }
        else
        {
            _prevCursorPos = _lastCursorPos;
            var prevCursor = _lastCursorPos;
            oldCursor = prevCursor == Point.Empty ? e.Location : prevCursor;
            _lastCursorPos = e.Location;
        }

        if (_mode == CaptureMode.ColorPicker)
        {
            UpdateColorPicker(e.Location);
            return;
        }

        if (ShowCaptureMagnifier && ToolDef.IsCaptureTool(_mode) && !_isSelecting && ShouldShowCaptureMagnifierAt(e.Location))
            UpdateCaptureMagnifier(e.Location);
        else if (_captureMagnifierForm is { Visible: true }
                 && (!ShowCaptureMagnifier
                     || !ToolDef.IsCaptureTool(_mode)
                     || _isConfirmingSelection
                     || IsPointInOverlayUi(e.Location)
                     || !ShouldShowCaptureMagnifierAt(e.Location)))
            HideCaptureMagnifier();



        switch (_mode)
        {
            case CaptureMode.Rectangle when !_isSelecting:
            case CaptureMode.ScrollCapture when !_isSelecting:
            case CaptureMode.Center when !_isSelecting:
            case CaptureMode.Ocr when !_isSelecting:
            case CaptureMode.Scan when !_isSelecting:
            case CaptureMode.Sticker when !_isSelecting:
            case CaptureMode.Upscale when !_isSelecting:
                if (_mode == CaptureMode.Center)
                {
                    var oldDetect = _autoDetectRect;
                    _autoDetectRect = Rectangle.Empty;
                    _autoDetectActive = false;
                    _autoDetectTimer.Stop();
                    InvalidateAutoDetectChrome(oldDetect, Rectangle.Empty);
                }
                else if (IsPointInOverlayUi(e.Location))
                {
                    var oldDetect = _autoDetectRect;
                    _autoDetectRect = Rectangle.Empty;
                    _autoDetectActive = false;
                    _autoDetectTimer.Stop();
                    InvalidateAutoDetectChrome(oldDetect, Rectangle.Empty);
                }
                else
                {
                    UpdateAutoDetectRect(e.Location);
                }
                break;
            case CaptureMode.Rectangle when _isSelecting:
            case CaptureMode.ScrollCapture when _isSelecting:
            case CaptureMode.Center when _isSelecting:
            case CaptureMode.Ocr when _isSelecting:
            case CaptureMode.Scan when _isSelecting:
            case CaptureMode.Sticker when _isSelecting:
            case CaptureMode.Upscale when _isSelecting:
                _autoDetectActive = false;
                _autoDetectTimer.Stop();
                CheckEvasion(e.Location);
                var oldSelectionRect = _selectionRect;
                var oldSelectionCursor = _selectionEnd;
                // Do not allow the rubber-band past the monitor where the drag started.
                var nextSelectionEnd = ClampPointToSelectionMonitor(e.Location);
                var nextSelectionRect = _mode == CaptureMode.Center
                    ? GetCenterSelectionRect(_selectionStart, nextSelectionEnd)
                    : _mode == CaptureMode.Rectangle && (ModifierKeys & Keys.Shift) != 0
                    ? GetSquareSelectionRect(_selectionStart, nextSelectionEnd)
                    : NormRect(_selectionStart, nextSelectionEnd);
                nextSelectionRect = ClampRectToSelectionMonitor(nextSelectionRect);
                if (nextSelectionEnd == oldSelectionCursor && nextSelectionRect == oldSelectionRect)
                {
                    if (ShowCrosshairGuides)
                        UpdateCrosshairGuides(nextSelectionEnd);
                    return;
                }
                _selectionEnd = nextSelectionEnd;
                _selectionRect = nextSelectionRect;
                if (_selectionRect.Width > 3 || _selectionRect.Height > 3) _hasDragged = true;
                _hasSelection = _selectionRect.Width > 2 && _selectionRect.Height > 2;
                InvalidateSelectionChromeThrottled(oldSelectionRect, oldSelectionCursor, _selectionRect, _selectionEnd);
                // Capture pixel magnifier stays available during drag (idle path also updates it).
                if (ShowCaptureMagnifier && ShouldShowCaptureMagnifierAt(e.Location))
                    UpdateCaptureMagnifier(e.Location);
                else if (_captureMagnifierForm is { Visible: true })
                    HideCaptureMagnifier();
                break;
            case CaptureMode.Highlight when _isHighlighting:
                InvalidateLivePreview(NormRect(_highlightStart, oldCursor), NormRect(_highlightStart, e.Location), 18);
                break;
            case CaptureMode.RectShape when _isRectShapeDragging:
                InvalidateLivePreview(GetShapeRect(oldCursor), GetShapeRect(e.Location), 18);
                break;
            case CaptureMode.CircleShape when _isCircleShapeDragging:
                InvalidateLivePreview(GetShapeRect(oldCursor), GetShapeRect(e.Location), 18);
                break;
            case CaptureMode.Line when _isLineDragging:
            {
                var oldEnd = GetConstrainedLineEnd(_lineStart, oldCursor);
                var newEnd = GetConstrainedLineEnd(_lineStart, e.Location);
                InvalidateLivePreview(RectFromPoints(_lineStart, oldEnd, 1), RectFromPoints(_lineStart, newEnd, 1), 18);
                break;
            }
            case CaptureMode.Ruler when _isRulerDragging:
                // Invalidate the precise paint extent (line + the label's *actual* rect) at both the
                // old and new positions. Tight bounds clear the old label without ghosting, yet keep
                // the per-frame repaint small so the overlay's dimming blend stays fluid while dragging.
                InvalidateLivePreview(RulerRenderer.GetLivePreviewBounds(_rulerStart, GetRulerEnd(oldCursor), ClientRectangle), RulerRenderer.GetLivePreviewBounds(_rulerStart, GetRulerEnd(e.Location), ClientRectangle), 0);
                // Force the small dirty region to paint now. The overlay's mouse-move handler is heavy
                // (toolbar/hover bookkeeping) and floods the queue, starving low-priority WM_PAINT — the
                // line then lags and catches up in jumps. A synchronous Update keeps it glued to the cursor.
                Update();
                break;
            case CaptureMode.Arrow when _isArrowDragging:
            {
                var oldEnd = GetConstrainedLineEnd(_arrowStart, oldCursor);
                var newEnd = GetConstrainedLineEnd(_arrowStart, e.Location);
                InvalidateLivePreview(RectFromPoints(_arrowStart, oldEnd, 1), RectFromPoints(_arrowStart, newEnd, 1), 32);
                break;
            }
            case CaptureMode.Blur when _isBlurring:
                InvalidateLivePreview(NormRect(_blurStart, oldCursor), NormRect(_blurStart, e.Location), 18);
                break;
            case CaptureMode.Emoji when _isPlacingEmoji:
                InvalidateLivePreview(GetEmojiPreviewRect(oldCursor), GetEmojiPreviewRect(e.Location), 10);
                break;
            case CaptureMode.Magnifier when _isPlacingMagnifier:
                // Ghost lens is large (~150px + flip offset). Always clear old+new with a wide pad.
                // When entering chrome, still invalidate old position so the ghost disappears cleanly.
                InvalidateLivePreview(GetMagnifierPreviewRect(oldCursor), GetMagnifierPreviewRect(e.Location), 32);
                break;
            case CaptureMode.StepNumber:
                InvalidateLivePreview(GetStepPreviewRect(oldCursor), GetStepPreviewRect(e.Location), 10);
                break;
            case CaptureMode.Draw when _isSelecting:
                if (_currentStroke is { Count: > 0 })
                {
                    var oldDirty = GetDrawPreviewBounds();
                    if ((ModifierKeys & Keys.Shift) != 0)
                    {
                        var start = _currentStroke[0];
                        var constrained = GetConstrainedDrawPoint(e.Location);
                        _currentStroke.Clear();
                        _currentStroke.Add(start);
                        _currentStroke.Add(constrained);
                    }
                    else
                    {
                        _currentStroke.Add(e.Location);
                    }
                    InvalidateLivePreview(oldDirty, GetDrawPreviewBounds(), 18);
                }
                break;
            case CaptureMode.CurvedArrow when _isCurvedArrowDragging:
                var oldCurveDirty = _currentCurvedArrow is { Count: > 0 }
                    ? BoundsOfPoints(_currentCurvedArrow, 16)
                    : Rectangle.Empty;
                _currentCurvedArrow?.Add(e.Location);
                var newCurveDirty = _currentCurvedArrow is { Count: > 0 }
                    ? BoundsOfPoints(_currentCurvedArrow, 16)
                    : Rectangle.Empty;
                InvalidateLivePreview(oldCurveDirty, newCurveDirty, 18);
                break;
        }

        // Font picker hover
        if (_fontPickerOpen)
        {
            int itemH = 34, pad = 10, searchBarH = 34;
            int listY = _fontPickerRect.Y + pad + searchBarH + pad;
            int relY = e.Location.Y - listY;
            int idx = _fontPickerScroll + relY / itemH;
            int newHover = (relY >= 0 && idx < GetFilteredFonts().Length) ? idx : -1;
            if (newHover != _fontPickerHovered) { _fontPickerHovered = newHover; toolbarDirty = true; }
        }

        // Emoji picker hover
        if (_emojiPickerOpen)
        {
            var filtered = GetFilteredEmojiPalette();
            int cols = EmojiPickerColumns, emojiSize = EmojiPickerIconSize, pad = EmojiPickerPadding;
            int searchBarH = EmojiPickerSearchBarHeight;
            int gridY = _emojiPickerRect.Y + pad + searchBarH + pad;
            int relX = e.Location.X - _emojiPickerRect.X - pad;
            int relY = e.Location.Y - gridY;
            int col = relX / (emojiSize + pad);
            int row = relY / (emojiSize + pad);
            int idx = (_emojiScrollOffset + row) * cols + col;
            int newHover = (col >= 0 && col < cols && relY >= 0 && idx < filtered.Length) ? idx : -1;
            if (newHover != _emojiHovered) { _emojiHovered = newHover; toolbarDirty = true; }
        }

        if (_textSelecting || _textDragging || _textResizing || _isSelectDragging || _isSelectResizing)
            ClearCrosshairGuides();
        else
            UpdateCrosshairGuides(_lastCursorPos);

        if (!_isSelecting && !_isMarqueeSelecting && !_isConfirmDragging && !_textResizing && !_textDragging && !_isSelectDragging && !_isSelectResizing && ToolShowsCursorChip(_mode))
        {
            bool chipOk = _moveHoverIndex < 0 && _selectedAnnotationIndex < 0 && _eraserHoverIndex < 0
                && !IsPointInOverlayUi(e.Location);
            if (_isConfirmingSelection)
            {
                chipOk = chipOk
                    && ToolDef.IsAnnotationTool(_mode)
                    && _confirmRect.Contains(e.Location)
                    && HitTestConfirmHandle(e.Location) < 0
                    && HitTestConfirmButton(e.Location) < 0;
            }

            var oldChip = GetCursorChipRect(oldCursor);
            var newChip = chipOk ? GetCursorChipRect(_lastCursorPos) : Rectangle.Empty;
            // Always clear the previous chip when hiding (leaving the region / hitting chrome),
            // otherwise ghosts linger outside the selection.
            if (chipOk || !oldChip.IsEmpty)
                InvalidateLivePreview(oldChip, newChip, 8);
        }

        if (needsRepaint)
            Invalidate();

        if (toolbarDirty)
            RefreshToolbar();
    }

    private void InvalidateLiveTransform(Rectangle oldBounds, Rectangle newBounds)
    {
        var oldDirty = InflateForRepaint(oldBounds, 28);
        var newDirty = InflateForRepaint(newBounds, 28);

        if (!oldDirty.IsEmpty && !newDirty.IsEmpty)
            Invalidate(Rectangle.Union(oldDirty, newDirty));
        else if (!oldDirty.IsEmpty)
            Invalidate(oldDirty);
        else if (!newDirty.IsEmpty)
            Invalidate(newDirty);
        else
            Invalidate();
    }

    private void InvalidateLivePreview(Rectangle oldBounds, Rectangle newBounds, int pad)
    {
        var oldDirty = InflateForRepaint(oldBounds, pad);
        var newDirty = InflateForRepaint(newBounds, pad);

        // Smear-proofing: always re-invalidate whatever the previous frame painted,
        // so any pixels a tool drew outside its declared bounds still get cleared.
        var prevPaint = _lastLivePreviewPaintExtent;
        _lastLivePreviewPaintExtent = Rectangle.Empty;

        Rectangle union = Rectangle.Empty;
        static Rectangle Add(Rectangle u, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return u;
            if (u.Width <= 0 || u.Height <= 0) return r;
            return Rectangle.Union(u, r);
        }
        union = Add(union, oldDirty);
        union = Add(union, newDirty);
        union = Add(union, prevPaint);

        if (union.Width > 0 && union.Height > 0)
            Invalidate(union);
    }

    private Rectangle GetDrawPreviewBounds()
    {
        if (_currentStroke is not { Count: > 0 })
            return Rectangle.Empty;

        return (ModifierKeys & Keys.Shift) != 0 && _currentStroke.Count >= 2
            ? RectFromPoints(_currentStroke[0], _currentStroke[^1], 8)
            : BoundsOfPoints(_currentStroke, 8);
    }

    private static float GetPathLength(List<Point> points)
    {
        float length = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            float dx = points[i].X - points[i - 1].X;
            float dy = points[i].Y - points[i - 1].Y;
            length += MathF.Sqrt(dx * dx + dy * dy);
        }
        return length;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_isDraggingToolbar)
        {
            _isDraggingToolbar = false;
            if (!_hasMovedToolbarByDrag)
            {
                int btnHit = GetToolbarButtonAt(e.Location);
                if (btnHit == PositionButtonIndex)
                {
                    ToggleToolbarPosition();
                }
            }
            return;
        }

        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            Invalidate();
            return;
        }

        SetSnapGuides(false, false);

        if (_isMouseDownOnCaptureBtn)
        {
            _isMouseDownOnCaptureBtn = false;
            var holdTime = (DateTime.UtcNow - _mouseDownStartTime).TotalMilliseconds;
            _mouseDownStartTime = DateTime.MinValue;
            int heldBtn = _mergedHoldButtonIndex;

            // Hold-and-slide: release over an alt slot selects that tool.
            if (_altCapturePopupOpen || holdTime >= 300)
            {
                EnsureAltPopupSlotsLaidOut();
                int altSlot = GetAltPopupSlotAt(e.Location);
                if (altSlot >= 0 && altSlot < _altPopupSlots.Count)
                {
                    SelectAltPopupTool(_altPopupSlots[altSlot].ToolId);
                    return;
                }
            }

            if (holdTime < 300)
            {
                // Short press activates the primary tool shown on the merged slot.
                if (heldBtn == _mergedCaptureButtonIndex
                    && heldBtn >= 0 && heldBtn < _mainBarTools.Length)
                {
                    var tool = _mainBarTools[heldBtn];
                    if (tool.Mode.HasValue)
                        SetTool(tool);
                }
                else if (heldBtn >= CloseButtonIndex + 1 && heldBtn < BtnCount)
                {
                    int flyoutIdx = heldBtn - (CloseButtonIndex + 1);
                    if (flyoutIdx >= 0 && flyoutIdx < _flyoutTools.Length
                        && _flyoutTools[flyoutIdx].Mode.HasValue)
                    {
                        SetTool(_flyoutTools[flyoutIdx]);
                    }
                }

                CloseAltToolPopup();
            }
            // Long press without landing on an alt: leave popup open for a second click.
            return;
        }

        // Second click on an open alt popup (mouse was not still held from the long-press).
        if (_altCapturePopupOpen && TryHandleAltToolPopupClick(e.Location))
            return;

        // Outside-frame click (no annotations): Retry selection without starting a new rubber-band.
        if (_outsideReselectArmed)
        {
            bool moved = _outsideReselectMoved;
            _outsideReselectArmed = false;
            if (!moved)
            {
                if (_isConfirmingSelection && !HasConfirmAnnotations())
                    ExitConfirmMode();
                return;
            }
            // Drag already promoted to selection — fall through to selection mouse-up below.
        }

        // End confirm-mode handle drag
        if (_isConfirmingSelection && _confirmHandleDragIndex >= 0)
        {
            _confirmHandleDragIndex = -1;
            EndConfirmFrameDragVisuals();
            return;
        }

        // End confirm-mode region drag
        if (_isConfirmingSelection && _isConfirmDragging)
        {
            _isConfirmDragging = false;
            EndConfirmFrameDragVisuals();
            return;
        }

        // End select drag/resize
        if (_isSelectResizing) { CommitSelectTransform(); _isSelectResizing = false; _selectResizeHandle = -1; _selectResizeOriginalAnnotation = null; UpdateCrosshairGuides(_lastCursorPos); Invalidate(); return; }
        if (_isSelectDragging && _multiDragOriginals is not null && _multiSelectedIndices.Count > 1)
        {
            int dx = e.Location.X - _multiDragStart.X;
            int dy = e.Location.Y - _multiDragStart.Y;
            if (dx != 0 || dy != 0)
            {
                PushEditCommand(new TransformMultipleAnnotationsCommand(_multiDragOriginals, dx, dy));
            }
            _isSelectDragging = false;
            _multiDragOriginals = null;
            UpdateCrosshairGuides(_lastCursorPos);
            Invalidate();
            return;
        }
        if (_isSelectDragging) { CommitSelectTransform(); _isSelectDragging = false; UpdateCrosshairGuides(_lastCursorPos); Invalidate(); return; }
        // End text move/resize
        if (_textSelecting)
        {
            _textSelecting = false;
            if (Capture) Capture = false;
            UpdateCrosshairGuides(_lastCursorPos);
            Invalidate(InflateForRepaint(Rectangle.Round(GetActiveTextRect()), 24));
            return;
        }
        if (_textDragging) { _textDragging = false; UpdateCrosshairGuides(_lastCursorPos); RefreshOverlayUiChrome(); return; }
        if (_textResizing) { _textResizing = false; _textResizeHandle = -1; UpdateCrosshairGuides(_lastCursorPos); RefreshOverlayUiChrome(); return; }
        switch (_mode)
        {
            case CaptureMode.Highlight when _isHighlighting:
                _isHighlighting = false;
                var hlRect = NormRect(_highlightStart, e.Location);
                if (hlRect.Width > 2 && hlRect.Height > 2)
                    AddAnnotation(new HighlightAnnotation(hlRect, DefaultHighlightColor));
                Invalidate(InflateForRepaint(hlRect));
                break;
            case CaptureMode.RectShape when _isRectShapeDragging:
                _isRectShapeDragging = false;
                var rectShape = GetShapeRect(e.Location);
                if (rectShape.Width > 2 && rectShape.Height > 2)
                    AddAnnotation(new RectShapeAnnotation(rectShape, _toolColor, _strokeWidth));
                Invalidate(InflateForRepaint(rectShape));
                break;
            case CaptureMode.CircleShape when _isCircleShapeDragging:
                _isCircleShapeDragging = false;
                var circleShape = GetShapeRect(e.Location);
                if (circleShape.Width > 2 && circleShape.Height > 2)
                    AddAnnotation(new CircleShapeAnnotation(circleShape, _toolColor, _strokeWidth));
                Invalidate(InflateForRepaint(circleShape));
                break;
            case CaptureMode.Magnifier:
                // Click already placed it in OnMouseDown, nothing to do on up
                break;
            case CaptureMode.Draw when _isSelecting:
                _isSelecting = false;
                if (_currentStroke is { Count: >= 2 })
                {
                    if ((ModifierKeys & Keys.Shift) != 0)
                    {
                        var start = _currentStroke[0];
                        var constrainedEnd = GetConstrainedDrawPoint(e.Location);
                        _currentStroke.Clear();
                        _currentStroke.Add(start);
                        _currentStroke.Add(constrainedEnd);
                    }
                    AddAnnotation(new DrawStroke(_currentStroke, _toolColor, _strokeWidth));
                    Invalidate(InflateForRepaint(BoundsOfPoints(_currentStroke, 6)));
                }
                _currentStroke = null;
                break;
            case CaptureMode.Line when _isLineDragging:
                _isLineDragging = false;
                var lineEnd = GetConstrainedLineEnd(_lineStart, e.Location);
                float ldx = lineEnd.X - _lineStart.X;
                float ldy = lineEnd.Y - _lineStart.Y;
                if (MathF.Sqrt(ldx * ldx + ldy * ldy) > 5)
                    AddAnnotation(new LineAnnotation(_lineStart, lineEnd, _toolColor, _strokeWidth));
                Invalidate(InflateForRepaint(RectFromPoints(_lineStart, lineEnd, 1)));
                break;
            case CaptureMode.Ruler when _isRulerDragging:
                _isRulerDragging = false;
                var rulerEnd = GetRulerEnd(e.Location);
                float rdx = rulerEnd.X - _rulerStart.X;
                float rdy = rulerEnd.Y - _rulerStart.Y;
                if (MathF.Sqrt(rdx * rdx + rdy * rdy) > 3)
                    AddAnnotation(new RulerAnnotation(_rulerStart, rulerEnd));
                Invalidate(Rectangle.Union(GetRulerPaintBounds(_rulerStart, rulerEnd), _lastLivePreviewPaintExtent.Width > 0 ? _lastLivePreviewPaintExtent : GetRulerPaintBounds(_rulerStart, rulerEnd)));
                _lastLivePreviewPaintExtent = Rectangle.Empty;
                break;
            case CaptureMode.Arrow when _isArrowDragging:
                _isArrowDragging = false;
                var end = GetConstrainedLineEnd(_arrowStart, e.Location);
                float dx = end.X - _arrowStart.X;
                float dy = end.Y - _arrowStart.Y;
                if (MathF.Sqrt(dx * dx + dy * dy) > 5)
                    AddAnnotation(new ArrowAnnotation(_arrowStart, end, _toolColor, _strokeWidth));
                Invalidate(InflateForRepaint(RectFromPoints(_arrowStart, end, 1)));
                break;
            case CaptureMode.CurvedArrow when _isCurvedArrowDragging:
                _isCurvedArrowDragging = false;
                if (_currentCurvedArrow is { Count: >= 2 } && GetPathLength(_currentCurvedArrow) > 5f)
                {
                    AddAnnotation(new CurvedArrowAnnotation(_currentCurvedArrow, _toolColor, _strokeWidth));
                    Invalidate(InflateForRepaint(BoundsOfPoints(_currentCurvedArrow, 10)));
                }
                _currentCurvedArrow = null;
                break;
            case CaptureMode.Blur when _isBlurring:
                _isBlurring = false;
                var blurRect = NormRect(_blurStart, e.Location);
                if (blurRect.Width > 3 && blurRect.Height > 3)
                    AddAnnotation(new BlurRect(blurRect));
                Invalidate(InflateForRepaint(blurRect));
                break;
            case CaptureMode.Rectangle when _isSelecting:
            case CaptureMode.Center when _isSelecting:
            case CaptureMode.Ocr when _isSelecting:
            case CaptureMode.Scan when _isSelecting:
            case CaptureMode.Sticker when _isSelecting:
            case CaptureMode.Upscale when _isSelecting:
            case CaptureMode.ScrollCapture when _isSelecting:
                _isSelecting = false;
                ResetEvasion();
                CloseSelectionAdorner();
                bool isCenter = _mode == CaptureMode.Center;
                bool isOcr = _mode == CaptureMode.Ocr;
                bool isScan = _mode == CaptureMode.Scan;
                bool isSticker = _mode == CaptureMode.Sticker;
                bool isUpscale = _mode == CaptureMode.Upscale;
                bool isScroll = _mode == CaptureMode.ScrollCapture;
                if (isCenter && _selectionRect.Width > 2 && _selectionRect.Height > 2)
                {
                    _autoDetectRect = Rectangle.Empty;
                    _autoDetectActive = false;
                    EnterConfirmMode(_selectionRect, e.Location);
                }
                else if (isCenter)
                {
                    _hasSelection = false;
                    Invalidate();
                }
                else if (!_hasDragged)
                {
                    if (_windowDetectionMode != WindowDetectionMode.Off)
                    {
                        var detectedAtRelease = WindowDetector.GetDetectionRectAtPoint(
                            e.Location, _virtualBounds, _windowDetectionMode);
                        if (detectedAtRelease.Width > 0 && detectedAtRelease.Height > 0)
                            _autoDetectRect = detectedAtRelease;
                    }
                    else
                    {
                        _autoDetectRect = Rectangle.Empty;
                        _autoDetectActive = false;
                    }

                    // Use auto-detected window region if available, else fullscreen
                    var clickRect = (_autoDetectRect.Width > 0 && _autoDetectRect.Height > 0)
                        ? _autoDetectRect
                        : new Rectangle(0, 0, _screenshot.Width, _screenshot.Height);
                    if (isOcr) OcrRegionSelected?.Invoke(clickRect);
                    else if (isScan) ScanRegionSelected?.Invoke(clickRect);
                    else if (isSticker) StickerRegionSelected?.Invoke(clickRect);
                    else if (isUpscale) UpscaleRegionSelected?.Invoke(clickRect);
                    else if (isScroll) ScrollRegionSelected?.Invoke(clickRect);
                    else
                    {
                        // Area / center capture: always lock the region first (annotation + actions).
                        _autoDetectRect = Rectangle.Empty;
                        _autoDetectActive = false;
                        EnterConfirmMode(clickRect, e.Location);
                    }
                }
                else if (_selectionRect.Width > 2 && _selectionRect.Height > 2)
                {
                    _autoDetectRect = Rectangle.Empty;
                    _autoDetectActive = false;
                    // Scroll capture skips confirm — goes straight to the scrolling control bar.
                    // Area capture always confirms (annotation toolbar + action pills).
                    if (!isScroll && (ConfirmRegionBeforeCapture || _mode is CaptureMode.Rectangle or CaptureMode.Center))
                        EnterConfirmMode(_selectionRect, e.Location);
                    else if (isOcr) OcrRegionSelected?.Invoke(_selectionRect);
                    else if (isScan) ScanRegionSelected?.Invoke(_selectionRect);
                    else if (isSticker) StickerRegionSelected?.Invoke(_selectionRect);
                    else if (isUpscale) UpscaleRegionSelected?.Invoke(_selectionRect);
                    else if (isScroll) ScrollRegionSelected?.Invoke(_selectionRect);
                    else RegionSelected?.Invoke(_selectionRect);
                }
                else { _hasSelection = false; Invalidate(); }
                break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // Check if the cursor actually left the form area. Child/overlay windows
        // (toolbar, crosshair guides) trigger spurious mouse-leave events while
        // the cursor is still logically within our bounds.
        var screenPos = System.Windows.Forms.Cursor.Position;
        var clientPos = PointToClient(screenPos);
        bool actuallyLeft = clientPos.X < 0 || clientPos.Y < 0
            || clientPos.X >= ClientSize.Width || clientPos.Y >= ClientSize.Height;

        if (actuallyLeft)
        {
            _eraserHoverIndex = -1;
            _hoveredButton = -1;
            _hoveredBrand = false;
            _hoveredBrandDragArea = false;
            _hoveredMenuActivator = false;
            if (_hoveredTextBtn >= 0)
            {
                _hoveredTextBtn = -1;
                HideToolbarTooltip();
            }
            CloseCaptureMagnifier();
            _autoDetectTimer.Stop();
            ClearCrosshairGuides();
            _prevCursorPos = _lastCursorPos;
            _lastCursorPos = Point.Empty;
            _lastAutoDetectRect = Rectangle.Empty;
            _autoDetectRect = Rectangle.Empty;
            _autoDetectActive = false;
            Invalidate();
            RefreshToolbar();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_fontPickerOpen)
        {
            int visibleCount = 10;
            int maxScroll = Math.Max(0, GetFilteredFonts().Length - visibleCount);
            _fontPickerScroll = Math.Clamp(_fontPickerScroll + (e.Delta > 0 ? -1 : 1), 0, maxScroll);
            RefreshToolbar();
        }
        else if (_emojiPickerOpen)
        {
            var filtered = GetFilteredEmojiPalette();
            int cols = EmojiPickerColumns, visibleRows = EmojiPickerVisibleRows;
            int totalRows = (filtered.Length + cols - 1) / cols;
            int maxScroll = Math.Max(0, totalRows - visibleRows);
            int oldScroll = _emojiScrollOffset;
            _emojiScrollOffset = Math.Clamp(_emojiScrollOffset + (e.Delta > 0 ? -1 : 1), 0, maxScroll);
            if (_emojiScrollOffset != oldScroll)
                QueueEmojiWarmup();
            RefreshToolbar();
        }
        else if (_mode == CaptureMode.Emoji && _isPlacingEmoji)
        {
            // Scroll wheel changes emoji size
            var oldPreview = GetEmojiPreviewRect(_lastCursorPos);
            _emojiPlaceSize = Math.Clamp(_emojiPlaceSize + (e.Delta > 0 ? 4f : -4f), 16f, 128f);
            Invalidate(Rectangle.Union(InflateForRepaint(oldPreview), InflateForRepaint(GetEmojiPreviewRect(_lastCursorPos))));
        }
        else if (_isTyping)
        {
            // Scroll wheel changes the text font size while editing
            AdjustTextFontSize(e.Delta > 0 ? TextSizeStep : -TextSizeStep);
        }
        base.OnMouseWheel(e);
    }

    private const float TextSizeStep = 2f;

    // Adjusts the active text font size, mirroring the corner-handle resize path
    // (clamp + InvalidateActiveTextLayout). Used by the +/- buttons and the wheel.
    private void AdjustTextFontSize(float delta)
    {
        if (!_isTyping) return;
        float next = Math.Clamp(_textFontSize + delta, 10f, 120f);
        if (Math.Abs(next - _textFontSize) < 0.01f) return;
        _textFontSize = next;
        InvalidateActiveTextLayout();
        Invalidate();
    }

    // Shows the hovered text-toolbar button's tooltip, reusing the shared WindowsToolTip.
    // Button rects are in overlay client space; offset by _virtualBounds to reach screen.
    private void UpdateTextToolbarTooltip()
    {
        if (!_isTyping || _hoveredTextBtn < 0 || string.IsNullOrEmpty(_textBtnTooltip))
        {
            HideToolbarTooltip();
            return;
        }

        RectangleF rect = _hoveredTextBtn switch
        {
            0 => _textBoldBtnRect,
            1 => _textItalicBtnRect,
            2 => _textStrokeBtnRect,
            3 => _textShadowBtnRect,
            4 => _textBackgroundBtnRect,
            5 => _textFontBtnRect,
            6 => _textSizeMinusBtnRect,
            7 => _textSizePlusBtnRect,
            8 => _textGripRect,
            9 => _textAlignLeftBtnRect,
            10 => _textAlignCenterBtnRect,
            11 => _textAlignRightBtnRect,
            _ => RectangleF.Empty,
        };
        if (rect.IsEmpty)
        {
            HideToolbarTooltip();
            return;
        }

        _toolbarToolTip ??= new WindowsToolTip();
        var anchor = new Rectangle(
            _virtualBounds.X + (int)rect.X,
            _virtualBounds.Y + (int)rect.Y,
            (int)rect.Width,
            (int)rect.Height);
        _toolbarToolTip.ShowNear(this, _textBtnTooltip, anchor, above: true);
    }
}
