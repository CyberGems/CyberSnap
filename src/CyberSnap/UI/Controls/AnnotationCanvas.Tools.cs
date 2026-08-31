using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
using CyberSnap.Services;
using CyberSnap.UI.Editor;

namespace CyberSnap.UI.Controls;

public sealed partial class AnnotationCanvas
{
    // ── In-progress tool state ─────────────────────────────────────────────

    private bool _isDragging;
    private Point _dragStartImg;
    private Point _dragLastImg;
    private List<Point>? _currentStroke;

    private bool _isMarqueeSelecting;
    private Point _marqueeStartImg;
    private Point _marqueeEndImg;

    // Last cursor position in image space (for the Emoji placement ghost).
    private Point _hoverImg;
    private bool _hoverImgValid;

    // Last cursor position in client/screen space and whether the pointer is over the
    // canvas — drives the floating tool-color/stroke chip that follows the cursor.
    private Point _cursorClient;
    private bool _cursorOnCanvas;

    /// <summary>Raised when the Emoji tool is clicked with no emoji chosen yet, so the
    /// host can open its picker.</summary>
    public event EventHandler? EmojiPlacementRequested;

    // Crop rectangle pending confirmation (image-space)
    private Rectangle _cropRect = Rectangle.Empty;
    private bool _cropDragging;
    private bool _cropHasRect;
    private int _activeCropHandle = -1;
    private Point _cropDragStartImg;
    private Rectangle _cropDragStartRect;

    // Canvas resize via the cyan edge handles on the image bounds (screen-space).
    // Same L-corner / mid-bar chrome as crop, but dragging grows or shrinks the canvas.
    private const float ResizeHitRadius = 10f;
    private static readonly Color ResizeAccent = Color.FromArgb(255, 0, 255, 255);
    private bool _resizeDragging;
    private bool _hasPendingResize;
    private Rectangle _pendingResizeRect;
    private Rectangle _resizeStartRect;
    private int _activeResizeHandle = -1;          // 0..7, same indexing as crop handles
    private Point _resizeStartImg;                 // image-space mouse at drag start
    private Size _resizePreviewSize;               // pending new size while dragging / staged

    // Inline text editor
    private TextBox? _inlineTextBox;
    private Point _inlineTextOrigin;
    // (re-edit state lives in TextToolbar partial: _textEditIndex / _textEditOriginal)
    private bool _inlineTextSelecting;
    private int _inlineTextSelectionAnchor;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HasPendingCrop => _activeTool == CanvasTool.Crop && _cropHasRect;

    /// <summary>
    /// True when a crop is pending AND the user has actually shrunk it below the full image,
    /// so confirming it would change the picture. A pending rect that still covers the whole
    /// image (the default when entering Crop) counts as "not adjusted".
    /// </summary>
    private bool HasAdjustedPendingCrop
    {
        get
        {
            if (!_cropHasRect || _cropRect.Width < 2 || _cropRect.Height < 2)
                return false;
            var full = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            return Rectangle.Intersect(_cropRect, full) != full;
        }
    }

    /// <summary>Triggers Apply on the pending crop rectangle. Idempotent.</summary>
    public bool TryConfirmCrop()
    {
        if (!_cropHasRect || _cropRect.Width < 2 || _cropRect.Height < 2)
            return false;

        var clamped = Rectangle.Intersect(_cropRect, new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        if (clamped.Width < 2 || clamped.Height < 2)
            return false;

        ClearCropPending();
        Push(new CropCommand(clamped));
        
        // Calculate new pan to keep the cropped region at the same screen position
        _pan.X += (float)(clamped.X * _zoom);
        _pan.Y += (float)(clamped.Y * _zoom);
        _viewFitsWindow = false;
        _userPanned = true;

        HideToolBanner();

        if (_activeTool == CanvasTool.Crop)
        {
            _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            _cropHasRect = true;
        }

        return true;
    }

    public void CancelCropPending()
    {
        if (!_cropHasRect && !_cropDragging) return;
        ClearCropPending();
        ShowToolBanner(CyberSnap.Services.LocalizationService.Translate("Crop canceled"));
        Invalidate();
        OnStateChanged();
    }

    private void ClearCropPending()
    {
        _cropDragging = false;
        if (EditorAutoCropControls && _baseBitmap is not null)
        {
            _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            _cropHasRect = true;
        }
        else
        {
            _cropRect = Rectangle.Empty;
            _cropHasRect = false;
        }
    }

    /// <summary>Apply or discard a pending crop when leaving the Crop tool for another.</summary>
    /// <returns><c>true</c> when the crop was applied.</returns>
    private bool FinalizeLeavingCrop()
    {
        bool cropApplied = false;
        if (HasAdjustedPendingCrop)
            cropApplied = TryConfirmCrop();
        if (!cropApplied)
            ClearCropPending();
        return cropApplied;
    }

    private bool IsCropOverlayActive =>
        _activeTool == CanvasTool.Crop || _preSpaceTool == CanvasTool.Crop;

    private void CancelInProgressTool()
    {
        if (_isDragging || _currentStroke is not null)
        {
            _isDragging = false;
            _currentStroke = null;
            Invalidate();
        }
        bool selectionCleared = false;
        if (_selectedAnnotationIndex >= 0 && !_isDragging && _preSpaceTool == null)
        {
            _selectedAnnotationIndex = -1;
            _selectOriginalAnnotation = null;
            selectionCleared = true;
            Invalidate();
        }
        _isSelectResizing = false;
        _selectResizeHandle = -1;
        _selectResizeOriginalAnnotation = null;
        _isMarqueeSelecting = false;
        ExitRotateMode(invalidate: false);
        ClearMultiSelection(); // may fire OnStateChanged if multi-selection was active
        _multiDragOriginals = null;
        if (selectionCleared)
            OnStateChanged(); // contextual Pick hints when Esc deselects
        if (_eraserHoverIndex >= 0)
        {
            _eraserHoverIndex = -1;
            Invalidate();
        }
        if (_moveHoverIndex >= 0)
        {
            _moveHoverIndex = -1;
            Invalidate();
        }
        CommitOrCancelInlineText(commit: false);
        FinishTempMoveFromPanIfNeeded();
    }

    private void UpdateCursor()
    {
        // Note: the hand ("click to select") cursor is NOT decided here — it's applied in
        // OnMouseMove only when the pointer is actually over an object's drawn pixels (its
        // surface) or its controls, never over the empty interior of its wrap box. This
        // method just yields the active tool's default cursor.
        Cursor = _activeTool switch
        {
            CanvasTool.Pan => CursorFactory.PanCursor,
            CanvasTool.Move => Cursors.Default,
            CanvasTool.Crop => CursorFactory.PrecisionCursor,
            CanvasTool.CutOut => CursorFactory.PrecisionCursor,
            CanvasTool.Text => Cursors.IBeam,
            CanvasTool.Eraser => CursorFactory.EraserCursor,
            // The step badge ghost is centered on the cursor and acts as the pointer itself,
            // so hide the OS crosshair (it would otherwise sit on top of the number).
            CanvasTool.StepNumber => CursorFactory.HiddenCursor,
            _ => CursorFactory.PrecisionCursor,
        };
    }

    private void FinishTempMoveFromPanIfNeeded()
    {
        if (!_isTempMoveFromPan)
            return;
        _isTempMoveFromPan = false;
        if (_activeTool != CanvasTool.Pan)
            ActiveTool = CanvasTool.Pan;
    }

    /// <summary>True while dragging/resizing an existing annotation (not drawing a new shape).</summary>
    private bool IsManipulatingExistingAnnotation =>
        _selectOriginalAnnotation is not null
        || _isSelectResizing
        || _isRotating
        || _multiDragOriginals is not null;

    private bool ProcessSelectionDragMove(Point img)
    {
        if (_pendingRotateToggle && _selectOriginalAnnotation is not null)
        {
            int pdx = img.X - _selectDragStartImg.X;
            int pdy = img.Y - _selectDragStartImg.Y;
            if (pdx * pdx + pdy * pdy > 16)
                _pendingRotateToggle = false;
        }

        if (_isRotating && _selectedAnnotationIndex >= 0 && _rotateOriginal is not null)
        {
            Cursor = CursorFactory.RotateCursor;
            float cur = MathF.Atan2(img.Y - _rotatePivot.Y, img.X - _rotatePivot.X) * 180f / MathF.PI;
            float delta = AnnotationTransforms.SignedDeltaDegrees(_rotateStartDegrees, cur);
            if ((ModifierKeys & Keys.Shift) != 0)
                delta = MathF.Round(delta / 15f) * 15f;
            _annotations[_selectedAnnotationIndex] = AnnotationTransforms.Rotate(_rotateOriginal, _rotatePivot, delta);
            Invalidate();
            return true;
        }

        if (_isSelectResizing && _selectedAnnotationIndex >= 0 && _selectResizeOriginalAnnotation is not null)
        {
            int rdx = img.X - _selectDragStartImg.X;
            int rdy = img.Y - _selectDragStartImg.Y;
            var ob = _selectHandleBounds;
            Rectangle nb = _selectResizeHandle switch
            {
                0 => Rectangle.FromLTRB(ob.Left + rdx, ob.Top + rdy, ob.Right, ob.Bottom),
                1 => Rectangle.FromLTRB(ob.Left, ob.Top + rdy, ob.Right + rdx, ob.Bottom),
                2 => Rectangle.FromLTRB(ob.Left + rdx, ob.Top, ob.Right, ob.Bottom + rdy),
                3 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right + rdx, ob.Bottom + rdy),
                4 => Rectangle.FromLTRB(ob.Left, ob.Top + rdy, ob.Right, ob.Bottom),
                5 => Rectangle.FromLTRB(ob.Left + rdx, ob.Top, ob.Right, ob.Bottom),
                6 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right + rdx, ob.Bottom),
                7 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right, ob.Bottom + rdy),
                _ => ob
            };
            if (nb.Width > 5 && nb.Height > 5)
                _annotations[_selectedAnnotationIndex] = AnnotationTransforms.Scale(_selectResizeOriginalAnnotation, ob, nb);
            Invalidate();
            return true;
        }

        if (_multiDragOriginals is not null && _multiSelectedIndices.Count > 1)
        {
            int mdx = img.X - _multiDragStartImg.X;
            int mdy = img.Y - _multiDragStartImg.Y;
            foreach (var (mi, orig) in _multiDragOriginals)
            {
                if (mi >= 0 && mi < _annotations.Count)
                    _annotations[mi] = AnnotationTransforms.Translate(orig, mdx, mdy);
            }
            Invalidate();
            return true;
        }

        if (_selectedAnnotationIndex >= 0 && _selectOriginalAnnotation is not null)
        {
            int dx = img.X - _selectDragStartImg.X;
            int dy = img.Y - _selectDragStartImg.Y;
            _annotations[_selectedAnnotationIndex] = AnnotationTransforms.Translate(_selectOriginalAnnotation, dx, dy);
            Invalidate();
            return true;
        }

        return false;
    }

    private bool CommitSelectionDrag()
    {
        if (_isRotating && _selectedAnnotationIndex >= 0 && _rotateOriginal is not null)
        {
            var rotated = _annotations[_selectedAnnotationIndex];
            if (!Equals(_rotateOriginal, rotated))
                Push(new ReplaceAnnotationCommand(_selectedAnnotationIndex, _rotateOriginal, rotated));
            _isRotating = false;
            _rotateOriginal = null;
            Invalidate();
            return true;
        }

        if (_isSelectResizing && _selectedAnnotationIndex >= 0 && _selectResizeOriginalAnnotation is not null)
        {
            var scaled = _annotations[_selectedAnnotationIndex];
            if (!Equals(_selectResizeOriginalAnnotation, scaled))
                Push(new ReplaceAnnotationCommand(_selectedAnnotationIndex, _selectResizeOriginalAnnotation, scaled));
            _isSelectResizing = false;
            _selectResizeHandle = -1;
            _selectResizeOriginalAnnotation = null;
            Invalidate();
            return true;
        }

        if (_multiDragOriginals is not null && _multiSelectedIndices.Count > 1)
        {
            int mtdx = 0, mtdy = 0;
            if (_multiDragOriginals.Count > 0)
            {
                var (firstIdx, firstOrig) = _multiDragOriginals[0];
                if (firstIdx >= 0 && firstIdx < _annotations.Count)
                    (mtdx, mtdy) = ComputeTranslationDelta(firstOrig, _annotations[firstIdx]);
            }
            if (mtdx != 0 || mtdy != 0)
                Push(new TransformMultipleAnnotationsCommand(_multiDragOriginals, mtdx, mtdy));
            _multiDragOriginals = null;
            return true;
        }

        if (_selectedAnnotationIndex >= 0 && _selectOriginalAnnotation is not null)
        {
            var moved = _annotations[_selectedAnnotationIndex];
            var (tdx, tdy) = ComputeTranslationDelta(_selectOriginalAnnotation, moved);
            if (tdx != 0 || tdy != 0)
            {
                _pendingRotateToggle = false;
                Push(new TransformAnnotationCommand(_selectOriginalAnnotation, _selectedAnnotationIndex, tdx, tdy));
            }
            else if (_pendingRotateToggle)
            {
                _pendingRotateToggle = false;
                ArmRotateToggle();
            }
            _selectOriginalAnnotation = null;
            return true;
        }

        return false;
    }

    // ── Mouse routing ──────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // Handle Pick double-click before base.OnMouseDown — base can start marquee/drag on
        // the same click that should select all (capture overlay never has this conflict).
        if (TryHandleSelectAllDoubleClick(e))
            return;

        // Welcome chips are real buttons: consume the click so tools don't start underneath.
        if (e.Button == MouseButtons.Left && TryWelcomeMouseDown(e.Location))
            return;

        base.OnMouseDown(e);

        // Scrollbar overlays consume clicks in their hit zone before any tool.
        if (ScrollbarMouseDown(e)) return;

        // While editing text, the floating toolbar gets first dibs on left clicks so
        // toggling format doesn't steal focus from the text box or commit the text.
        if (e.Button == MouseButtons.Left && _inlineTextBox is not null)
        {
            if (HandleTextToolbarMouseDown(e.Location))
            {
                _inlineTextBox.Focus();
                return;
            }
            int th = HitTestInlineTextHandle(e.Location);
            if (th >= 0)
            {
                _textResizing = true;
                _textResizeHandle = th;
                _textResizeStartScreen = e.Location;
                _textResizeStartFontSize = _textFontSize;
                return;
            }

            // Click inside the live text frame → place caret / start drag-select.
            // Double-click → select the word under the caret (standard text UX).
            var imgForCaret = ScreenToImage(e.Location);
            var liveRect = MeasureInlineTextRect(
                _inlineTextOrigin, _inlineTextBox.Text, _textFontSize, _textFontFamily,
                _textBold, _textItalic, _textBackground, _textMaxWidth, _textAlign);
            if (liveRect.Contains(imgForCaret) || GetInlineTextScreenBounds().Contains(e.Location))
            {
                int caretIdx = TextAnnotationPainter.GetCharIndexAt(
                    _inlineTextOrigin, imgForCaret, _inlineTextBox.Text,
                    _textFontSize, _textFontFamily, _textBold, _textItalic,
                    _textMaxWidth, _textAlign);

                if (e.Clicks >= 2)
                {
                    SelectWordAt(_inlineTextBox, caretIdx);
                    _inlineTextSelecting = false;
                    _inlineTextBox.Focus();
                    Invalidate();
                    return;
                }

                _inlineTextBox.SelectionStart = caretIdx;
                _inlineTextBox.SelectionLength = 0;
                _inlineTextSelectionAnchor = caretIdx;
                _inlineTextSelecting = true;
                _inlineTextBox.Focus();
                Capture = true;
                Invalidate();
                return;
            }

            // Click outside the live frame while editing → commit and stop.
            // Don't open "browse file" or place new text on the same click.
            CommitOrCancelInlineText(commit: true);
            return;
        }

        Focus();

        // Cyan edge handles resize the canvas in both directions. Annotation grips on a
        // selected object win when they overlap the canvas edge.
        if (e.Button == MouseButtons.Left && TryBeginCanvasResize(e.Location))
            return;

        if (e.Button == MouseButtons.Left)
        {
            if (_hoveredHorizontalGuideIndex >= 0)
            {
                _activeDraggedHorizontalGuideIndex = _hoveredHorizontalGuideIndex;
                Capture = true;
                return;
            }
            if (_hoveredVerticalGuideIndex >= 0)
            {
                _activeDraggedVerticalGuideIndex = _hoveredVerticalGuideIndex;
                Capture = true;
                return;
            }
        }

        bool hitAnnotationOrHandle = false;
        if (_activeTool == CanvasTool.Pan && !PanModeLockObjects && e.Button == MouseButtons.Left)
        {
            var imgPt = ScreenToImage(e.Location);
            int hoverIdx = (_moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count)
                ? _moveHoverIndex
                : HitTestAnnotation(imgPt);
            
            if (hoverIdx >= 0 && hoverIdx != _suppressHoverIndex)
            {
                hitAnnotationOrHandle = true;
            }
            else if (_selectedAnnotationIndex >= 0 && GetSelectHandle(e.Location, _selectedAnnotationIndex) >= 0)
            {
                hitAnnotationOrHandle = true;
            }
        }

        if (e.Button == MouseButtons.Middle ||
            (e.Button == MouseButtons.Left && _activeTool == CanvasTool.Pan && (PanModeLockObjects || !hitAnnotationOrHandle)))
        {
            _isPanning = true;
            _userPanned = true;
            DismissWelcomeOverlay();
            _viewFitsWindow = false;
            _panStart = e.Location;
            _panStartOffset = _pan;
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        // Do not reset IsDefaultBlank on mouse down so that double click can be detected.
        // It will be reset when annotations are actually added, or when an image is pasted/opened.

        var img = ScreenToImage(e.Location);

        // Commit any pending text first
        if (_inlineTextBox is not null && _activeTool != CanvasTool.Text)
            CommitOrCancelInlineText(commit: true);

        if (_preSpaceTool == null && IsDrawingOrMoveTool(_activeTool) && _activeTool != CanvasTool.Move)
        {
            int handle = -1;
            int clickedIdx = -1;
            if (_selectedAnnotationIndex >= 0)
            {
                handle = GetSelectHandle(e.Location, _selectedAnnotationIndex);
                if (handle >= 0) clickedIdx = _selectedAnnotationIndex;
            }

            // Control hit on the hovered item only (handles may sit outside the stroke).
            if (handle < 0 && _moveHoverIndex >= 0 && _moveHoverIndex != _suppressHoverIndex)
            {
                handle = GetSelectHandle(e.Location, _moveHoverIndex);
                if (handle >= 0) clickedIdx = _moveHoverIndex;
            }
            // No control hit → select only when the click lands on the object's actual drawn
            // pixels (its surface), never on the empty interior of its wrap box. Clicking the
            // hollow interior falls through below and draws, exactly like clicking blank canvas.
            if (handle < 0)
            {
                int surfIdx = HitTestAnnotationSurface(img);
                if (surfIdx == _suppressHoverIndex) surfIdx = -1;
                if (surfIdx >= 0) clickedIdx = surfIdx;
            }

            // Click on empty area with a drawing tool: clear any active selection before
            // starting to draw (same as Pick/Move behaviour). Emoji and Magnifier are
            // excluded because they place objects on click, not draw shapes.
            // Inline text editing is fully handled above (caret / commit) — never reach here
            // while _inlineTextBox is active.
            if (clickedIdx < 0 && _selectedAnnotationIndex >= 0
                && _activeTool != CanvasTool.Magnifier && _activeTool != CanvasTool.Emoji)
            {
                _selectedAnnotationIndex = -1;
                ClearMultiSelection();
                ExitRotateMode(invalidate: false);
                Invalidate();
            }

            if (clickedIdx >= 0)
            {
                if (_activeTool == CanvasTool.Pan)
                    _isTempMoveFromPan = true;

                // Ctrl+Click: toggle multi-selection
                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    ToggleMultiSelect(clickedIdx);
                    Invalidate();
                    return;
                }
                bool alreadySelected = clickedIdx == _selectedAnnotationIndex;
                ClearMultiSelection();
                if (!alreadySelected)
                    ExitRotateMode(invalidate: false);
                _selectedAnnotationIndex = clickedIdx;
                if (handle >= 0 && handle != 8 && !_isRotateMode)
                {
                    // A resize handle (corners/edges) → resize.
                    _isSelectResizing = true;
                    _selectResizeHandle = handle;
                    _selectDragStartImg = img;
                    _selectHandleBounds = Rectangle.Round(GetAnnotationVisualBounds(_annotations[clickedIdx]));
                    _selectResizeOriginalAnnotation = _annotations[clickedIdx];
                    _isDragging = true;
                }
                else if (handle >= 0 && handle != 8 && _isRotateMode)
                {
                    BeginRotateDrag(clickedIdx, img);
                    _isDragging = true;
                }
                else
                {
                    // Center move knob or plain body click → select and immediately start moving it from its surface!
                    _pendingRotateToggle = alreadySelected && (handle < 0 || handle == 8)
                        && AnnotationTransforms.CanRotate(_annotations[clickedIdx]);
                    _selectOriginalAnnotation = _annotations[clickedIdx];
                    _selectDragStartImg = img;
                    _isDragging = true;
                }
                _currentStroke = null;
                Invalidate();
                OnStateChanged(); // contextual Pick hints when selection changes
                return;
            }
        }

        switch (_activeTool)
        {
            case CanvasTool.Draw:
                ClearMoveHoverHighlight();
                _currentStroke = new List<Point> { img };
                _isDragging = true;
                Invalidate();
                break;
            case CanvasTool.Arrow:
            case CanvasTool.Line:
            case CanvasTool.Rect:
            case CanvasTool.Circle:
            case CanvasTool.Highlight:
            case CanvasTool.Blur:
                ClearMoveHoverHighlight();
                _dragStartImg = img;
                _dragLastImg = img;
                _isDragging = true;
                Invalidate();
                break;
            case CanvasTool.Eraser:
                TryEraseAnnotationAt(img);
                break;
            case CanvasTool.CurvedArrow:
                ClearMoveHoverHighlight();
                _currentStroke = new List<Point> { img };
                _isDragging = true;
                Invalidate();
                break;
            case CanvasTool.Move:
            {
                int handle = -1;
                int targetIdx = -1;

                // Prefer a handle on the already-selected annotation.
                if (_selectedAnnotationIndex >= 0)
                {
                    handle = GetSelectHandle(e.Location, _selectedAnnotationIndex);
                    if (handle >= 0) targetIdx = _selectedAnnotationIndex;
                }

                // Otherwise, check the hovered annotation's controls. We use the bbox-hover
                // index here (not the surface) so the resize handles and the center move knob —
                // which sits in the hollow interior — stay grabbable even over empty space.
                if (handle < 0)
                {
                    int controlIdx = (_moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count)
                        ? _moveHoverIndex
                        : HitTestAnnotation(img);
                    if (controlIdx >= 0)
                    {
                        int hoverHandle = GetSelectHandle(e.Location, controlIdx);
                        if (hoverHandle >= 0)
                        {
                            handle = hoverHandle;
                            targetIdx = controlIdx;
                        }
                    }
                }

                // No control hit → select/move only when the click lands on an object's actual
                // drawn pixels (its surface), never on the empty interior of its wrap box. A
                // miss leaves targetIdx = -1, handled below as an empty-space click (marquee).
                if (handle < 0)
                {
                    targetIdx = HitTestAnnotationSurface(img);
                }

                // Ctrl+Click: toggle multi-selection
                if (ModifierKeys.HasFlag(Keys.Control) && targetIdx >= 0)
                {
                    ToggleMultiSelect(targetIdx);
                    Invalidate();
                    break;
                }

                // A real resize handle (anything but the center knob, handle 8) → resize.
                // Resize always operates on a single annotation, so clear multi-selection.
                if (handle >= 0 && handle != 8 && targetIdx >= 0 && !_isRotateMode)
                {
                    ClearMultiSelection();
                    if (targetIdx != _selectedAnnotationIndex)
                        ExitRotateMode(invalidate: false);
                    _selectedAnnotationIndex = targetIdx;
                    _isSelectResizing = true;
                    _selectResizeHandle = handle;
                    _selectDragStartImg = img;
                    _selectHandleBounds = Rectangle.Round(GetAnnotationVisualBounds(_annotations[targetIdx]));
                    _selectResizeOriginalAnnotation = _annotations[targetIdx];
                    _isDragging = true;
                }
                else if (handle >= 0 && handle != 8 && targetIdx >= 0 && _isRotateMode)
                {
                    ClearMultiSelection();
                    if (targetIdx != _selectedAnnotationIndex)
                        ExitRotateMode(invalidate: false);
                    _selectedAnnotationIndex = targetIdx;
                    BeginRotateDrag(targetIdx, img);
                    _isDragging = true;
                }
                else if (targetIdx >= 0)
                {
                    // If the clicked item (matched by surface or control) is part of a
                    // multi-selection, initiate a group drag.
                    if (_multiSelectedIndices.Count > 1 && _multiSelectedIndices.Contains(targetIdx))
                    {
                        _multiDragStartImg = img;
                        _multiDragOriginals = _multiSelectedIndices
                            .Where(i => i >= 0 && i < _annotations.Count)
                            .Select(i => (i, _annotations[i]))
                            .ToList();
                        _selectedAnnotationIndex = targetIdx;
                        _isDragging = true;
                    }
                    else
                    {
                        // Center move knob or plain body click — select and immediately start moving it from its surface!
                        bool alreadySelected = targetIdx == _selectedAnnotationIndex;
                        ClearMultiSelection();
                        if (!alreadySelected)
                            ExitRotateMode(invalidate: false);
                        _selectedAnnotationIndex = targetIdx;
                        _pendingRotateToggle = alreadySelected && (handle < 0 || handle == 8)
                            && AnnotationTransforms.CanRotate(_annotations[targetIdx]);
                        _selectOriginalAnnotation = _annotations[targetIdx];
                        _selectDragStartImg = img;
                        _isDragging = true;
                    }
                }
                else
                {
                    // Single click on empty space: clear everything, start marquee selection.
                    ClearMultiSelection();
                    _selectedAnnotationIndex = -1;
                    _selectOriginalAnnotation = null;
                    ExitRotateMode(invalidate: false);

                    _isMarqueeSelecting = true;
                    _marqueeStartImg = img;
                    _marqueeEndImg = img;
                }
                Invalidate();
                OnStateChanged(); // contextual Pick hints when selection changes
                break;
            }
            case CanvasTool.Crop:
                if (_cropHasRect)
                {
                    var screenPt = e.Location;
                    var cropScreen = ImageToScreenRect(_cropRect);
                    var handles = GetCropHandlePositionsScreen(cropScreen);
                    int hitHandle = -1;
                    const float hitRadius = 7f;
                    for (int i = 0; i < handles.Length; i++)
                    {
                        var h = handles[i];
                        if (Math.Abs(screenPt.X - h.X) <= hitRadius && Math.Abs(screenPt.Y - h.Y) <= hitRadius)
                        {
                            hitHandle = i;
                            break;
                        }
                    }

                    if (hitHandle >= 0)
                    {
                        _activeCropHandle = hitHandle;
                        _cropDragging = true;
                        _cropDragStartImg = img;
                        _cropDragStartRect = _cropRect;
                    }
                    else if (HasAdjustedPendingCrop && cropScreen.Contains(screenPt))
                    {
                        _activeCropHandle = 8; // Move an already-shrunk crop
                        _cropDragging = true;
                        _cropDragStartImg = img;
                        _cropDragStartRect = _cropRect;
                    }
                    else
                    {
                        // Full-image crop (the default on enter) or click outside a shrunk
                        // rect: drag a new selection from this point. Avoids having to
                        // pinch the eight edge handles inward on a large screenshot.
                        _activeCropHandle = -1;
                        _cropRect = new Rectangle(img.X, img.Y, 0, 0);
                        _dragStartImg = img;
                        _dragLastImg = img;
                        _cropDragging = true;
                        _cropHasRect = false;
                    }
                }
                else
                {
                    _activeCropHandle = -1;
                    _dragStartImg = img;
                    _dragLastImg = img;
                    _cropDragging = true;
                    _cropHasRect = false;
                }
                Invalidate();
                OnStateChanged();
                break;
            case CanvasTool.CutOut:
                BeginCutOutPointer(img, e.Location);
                break;
            case CanvasTool.Text:
            {
                // Click on existing committed text → re-edit; empty canvas → new text.
                // (While already editing, caret/commit are handled earlier and never reach here.)
                int textHit = HitTestTextAnnotation(img);
                if (textHit >= 0 && textHit != _renderSkipAnnotationIndex)
                    BeginReEditText(textHit);
                else
                    BeginInlineText(img);
                break;
            }
            case CanvasTool.StepNumber:
                Push(new AddAnnotationCommand(new StepNumberAnnotation(img, NextStepNumber(), ToolColor)));
                SuppressHoverForLastPlaced();
                break;
            case CanvasTool.Magnifier:
                Push(new AddAnnotationCommand(new MagnifierAnnotation(img, GetMagnifierSrcRect(img))));
                SuppressHoverForLastPlaced();
                break;
            case CanvasTool.Emoji:
                if (!string.IsNullOrEmpty(_selectedEmoji))
                {
                    int bitmapSize = (int)(_emojiPlaceSize * 1.4f) + 4;
                    var emojiPos = new Point(img.X - bitmapSize / 2, img.Y - bitmapSize / 2);
                    Push(new AddAnnotationCommand(new EmojiAnnotation(emojiPos, _selectedEmoji, _emojiPlaceSize)));
                    SuppressHoverForLastPlaced();
                }
                else
                {
                    EmojiPlacementRequested?.Invoke(this, EventArgs.Empty);
                }
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Track the pointer so the floating tool chip can follow it.
        Point oldCursorClient = _cursorClient;
        _cursorClient = e.Location;
        _cursorOnCanvas = true;
        base.OnMouseMove(e);

        if (TryWelcomeMouseMove(e.Location))
            return;

        // Scrollbar drag takes full priority; hover updates always run.
        if (ScrollbarMouseMove(e)) return;

        UpdateResizeHandlesHover();

        if (!_isDragging && !_isPanning && !_cropDragging && !_cutOutDragging && !_resizeDragging && _preSpaceTool == null
            && ToolShowsCursorChip(_activeTool))
        {
            var oldChip = GetCursorChipRect(oldCursorClient);
            var newChip = GetCursorChipRect(_cursorClient);
            if (!oldChip.IsEmpty || !newChip.IsEmpty)
            {
                var dirty = !oldChip.IsEmpty && !newChip.IsEmpty ? Rectangle.Union(oldChip, newChip) : (!oldChip.IsEmpty ? oldChip : newChip);
                dirty.Intersect(ClientRectangle);
                if (dirty.Width > 0 && dirty.Height > 0)
                    Invalidate(dirty);
            }
        }

        if (_resizeDragging)
        {
            UpdateResizeDrag(e.Location);
            return;
        }

        if (_activeDraggedHorizontalGuideIndex >= 0)
        {
            _horizontalGuides[_activeDraggedHorizontalGuideIndex] = ScreenToImage(e.Location).Y;
            Invalidate();
            return;
        }

        if (_activeDraggedVerticalGuideIndex >= 0)
        {
            _verticalGuides[_activeDraggedVerticalGuideIndex] = ScreenToImage(e.Location).X;
            Invalidate();
            return;
        }

        if (_isMarqueeSelecting)
        {
            _marqueeEndImg = ScreenToImage(e.Location);

            var marqueeRect = NormRect(_marqueeStartImg, _marqueeEndImg);
            _multiSelectedIndices.Clear();
            _selectedAnnotationIndex = -1;

            if (marqueeRect.Width >= 2 && marqueeRect.Height >= 2)
            {
                for (int i = 0; i < _annotations.Count; i++)
                {
                    var bounds = Rectangle.Round(GetAnnotationVisualBounds(_annotations[i]));
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
                    ShowToolBanner(msg, sticky: true);
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

        // Drag-select while editing text
        if (_inlineTextSelecting && _inlineTextBox is not null)
        {
            var imgSel = ScreenToImage(e.Location);
            int idx = TextAnnotationPainter.GetCharIndexAt(
                _inlineTextOrigin, imgSel, _inlineTextBox.Text,
                _textFontSize, _textFontFamily, _textBold, _textItalic,
                _textMaxWidth, _textAlign);
            int start = Math.Min(_inlineTextSelectionAnchor, idx);
            int end = Math.Max(_inlineTextSelectionAnchor, idx);
            _inlineTextBox.SelectionStart = start;
            _inlineTextBox.SelectionLength = Math.Max(0, end - start);
            Invalidate();
            return;
        }

        // Dragging the inline text box by its toolbar grip
        if (_textGripDragging && _inlineTextBox is not null)
        {
            Cursor = CursorFactory.GrabbingCursor;
            int nx = e.X - _textGripDragOffset.X;
            int ny = e.Y - _textGripDragOffset.Y;
            _inlineTextOrigin = ScreenToImage(new Point(nx, ny));
            Invalidate();
            return;
        }

        // Corner-handle resize of font size while typing
        if (_textResizing && _inlineTextBox is not null)
        {
            float dx = e.X - _textResizeStartScreen.X;
            float dy = e.Y - _textResizeStartScreen.Y;
            // Outward drag on any corner grows the text
            float delta = (Math.Abs(dx) > Math.Abs(dy) ? dx : dy) * 0.15f;
            if (_textResizeHandle is 0 or 2) delta = -delta; // left corners: drag left grows
            float ns = Math.Clamp(_textResizeStartFontSize + delta, 10f, 120f);
            if (Math.Abs(ns - _textFontSize) >= 0.01f)
            {
                _textFontSize = ns;
                UpdateInlineTextBoxStyle();
                TextFontSizeChanged?.Invoke(ns);
                Invalidate();
            }
            return;
        }

        // Hovering the floating text toolbar (skip normal tool hover when over it)
        if (_inlineTextBox is not null && UpdateTextToolbarHover(e.Location))
            return;

        if (!_isDragging && !_cropDragging && !_cutOutDragging && (_activeTool == CanvasTool.Pan || _activeTool == CanvasTool.Move))
        {
            int hHover = HitTestHorizontalGuide(e.Location);
            int vHover = HitTestVerticalGuide(e.Location);
            if (hHover != _hoveredHorizontalGuideIndex || vHover != _hoveredVerticalGuideIndex)
            {
                _hoveredHorizontalGuideIndex = hHover;
                _hoveredVerticalGuideIndex = vHover;
                Invalidate();
            }

            if (hHover >= 0 || vHover >= 0)
            {
                Cursor = hHover >= 0 ? Cursors.HSplit : Cursors.VSplit;
                if (_moveHoverIndex != -1)
                {
                    _moveHoverIndex = -1;
                    Invalidate();
                }
                return;
            }
        }
        else
        {
            if (_hoveredHorizontalGuideIndex != -1 || _hoveredVerticalGuideIndex != -1)
            {
                _hoveredHorizontalGuideIndex = -1;
                _hoveredVerticalGuideIndex = -1;
                Invalidate();
            }
        }

        if (IsDrawingOrMoveTool(_activeTool) && !_isDragging)
        {
            if (_preSpaceTool == null)
            {
                var imgPt = ScreenToImage(e.Location);
                UpdateMoveHover(imgPt);
            }
            else if (_moveHoverIndex != -1)
            {
                _moveHoverIndex = -1;
                Invalidate();
            }
        }

        if (_isPanning)
        {
            _pan = new PointF(
                _panStartOffset.X + (e.X - _panStart.X),
                _panStartOffset.Y + (e.Y - _panStart.Y));
            NotifyScrollbarActivity();
            Invalidate();
            return;
        }

        if (_activeTool == CanvasTool.Eraser && !_isDragging)
        {
            var imgPt = ScreenToImage(e.Location);
            UpdateEraserHover(imgPt);
            return;
        }

        // Placement ghost for the click-to-place tools (Emoji needs a chosen glyph):
        // track the cursor and repaint so the translucent preview follows it.
        if (!_isDragging &&
            (_activeTool == CanvasTool.Magnifier ||
             _activeTool == CanvasTool.StepNumber ||
             (_activeTool == CanvasTool.Emoji && !string.IsNullOrEmpty(_selectedEmoji))))
        {
            _hoverImg = ScreenToImage(e.Location);
            _hoverImgValid = true;
            Invalidate();
            return;
        }

        if (_activeTool == CanvasTool.Crop)
        {
            if (_cropDragging)
            {
                Cursor = _activeCropHandle switch
                {
                    0 or 3 => Cursors.SizeNWSE,
                    1 or 2 => Cursors.SizeNESW,
                    4 or 6 => Cursors.SizeNS,
                    5 or 7 => Cursors.SizeWE,
                    8 => Cursors.SizeAll,
                    _ => CursorFactory.PrecisionCursor
                };
            }
            else
            {
                Cursor = GetCropCursor(e.Location);
            }
        }
        else if (_activeTool == CanvasTool.CutOut)
        {
            Cursor = GetCutOutCursor(e.Location);
        }
        else if (!_isDragging && !_cropDragging && !_cutOutDragging && !_resizeDragging)
        {
            if (EditorShowResizeHandles && _baseBitmap != null && _preSpaceTool == null
                && !IsOverAnnotationGrip(e.Location))
            {
                int rh = HitTestResizeHandle(e.Location);
                if (rh >= 0)
                {
                    Cursor = rh switch
                    {
                        0 or 3 => Cursors.SizeNWSE,
                        1 or 2 => Cursors.SizeNESW,
                        4 or 6 => Cursors.SizeNS,
                        5 or 7 => Cursors.SizeWE,
                        _ => Cursors.Default
                    };
                    return;
                }
            }
            if (IsDrawingOrMoveTool(_activeTool) && _preSpaceTool == null)
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
                    Cursor = sh switch
                    {
                        >= 0 and <= 3 when _isRotateMode => CursorFactory.RotateCursor,
                        0 or 3 => Cursors.SizeNWSE,
                        1 or 2 => Cursors.SizeNESW,
                        4 or 7 => Cursors.SizeNS,
                        5 or 6 => Cursors.SizeWE,
                        8       => Cursors.SizeAll,
                        _       => Cursors.Default
                    };
                    return;
                }

                int hoverIdx = _moveHoverIndex >= 0 ? _moveHoverIndex : _selectedAnnotationIndex;
                if (hoverIdx >= 0 && hoverIdx < _annotations.Count
                    && IsOverAnnotationSurface(_annotations[hoverIdx], ScreenToImage(e.Location)))
                {
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }
            UpdateCursor();
        }

        if (!_isDragging && !_cropDragging && !_cutOutDragging) return;

        var img = ScreenToImage(e.Location);

        if (_cropDragging)
        {
            if (_baseBitmap is null) return;

            if (_activeCropHandle == -1)
            {
                _cropRect = NormRect(_dragStartImg, img);
                _dragLastImg = img;
            }
            else if (_activeCropHandle == 8)
            {
                int dx = img.X - _cropDragStartImg.X;
                int dy = img.Y - _cropDragStartImg.Y;
                var r = _cropDragStartRect;
                int nx = r.X + dx;
                int ny = r.Y + dy;
                nx = Math.Clamp(nx, 0, _baseBitmap.Width - r.Width);
                ny = Math.Clamp(ny, 0, _baseBitmap.Height - r.Height);
                _cropRect = new Rectangle(nx, ny, r.Width, r.Height);
            }
            else
            {
                int dx = img.X - _cropDragStartImg.X;
                int dy = img.Y - _cropDragStartImg.Y;
                var r = _cropDragStartRect;

                int left = r.Left;
                int right = r.Right;
                int top = r.Top;
                int bottom = r.Bottom;

                const int minSize = 4;

                switch (_activeCropHandle)
                {
                    case 0:
                        left = Math.Min(r.Left + dx, r.Right - minSize);
                        top = Math.Min(r.Top + dy, r.Bottom - minSize);
                        break;
                    case 1:
                        right = Math.Max(r.Right + dx, r.Left + minSize);
                        top = Math.Min(r.Top + dy, r.Bottom - minSize);
                        break;
                    case 2:
                        left = Math.Min(r.Left + dx, r.Right - minSize);
                        bottom = Math.Max(r.Bottom + dy, r.Top + minSize);
                        break;
                    case 3:
                        right = Math.Max(r.Right + dx, r.Left + minSize);
                        bottom = Math.Max(r.Bottom + dy, r.Top + minSize);
                        break;
                    case 4:
                        top = Math.Min(r.Top + dy, r.Bottom - minSize);
                        break;
                    case 5:
                        right = Math.Max(r.Right + dx, r.Left + minSize);
                        break;
                    case 6:
                        bottom = Math.Max(r.Bottom + dy, r.Top + minSize);
                        break;
                    case 7:
                        left = Math.Min(r.Left + dx, r.Right - minSize);
                        break;
                }

                left = Math.Clamp(left, 0, _baseBitmap.Width);
                right = Math.Clamp(right, 0, _baseBitmap.Width);
                top = Math.Clamp(top, 0, _baseBitmap.Height);
                bottom = Math.Clamp(bottom, 0, _baseBitmap.Height);

                _cropRect = new Rectangle(left, top, right - left, bottom - top);
            }
            Invalidate();
            return;
        }

        if (_cutOutDragging)
        {
            UpdateCutOutDrag(img);
            return;
        }

        if (ProcessSelectionDragMove(img))
            return;

        switch (_activeTool)
        {
            case CanvasTool.Draw:
            case CanvasTool.CurvedArrow:
                if (_currentStroke is not null && (img != _currentStroke[^1]))
                {
                    var prevPt = _currentStroke[^1];
                    _currentStroke.Add(img);
                    var dirtySegment = RectFromPoints(prevPt, img, (int)Math.Ceiling(StrokeWidth * 2));
                    InvalidateLivePreview(dirtySegment, dirtySegment, 16);
                }
                break;
            case CanvasTool.Arrow:
            case CanvasTool.Line:
            {
                var oldEnd = GetDragLineEnd(_dragLastImg);
                var newEnd = GetDragLineEnd(img);
                var oldBounds = RectFromPoints(_dragStartImg, oldEnd);
                var newBounds = RectFromPoints(_dragStartImg, newEnd);
                _dragLastImg = img;
                InvalidateLivePreview(oldBounds, newBounds, 32);
                break;
            }
            case CanvasTool.Rect:
            case CanvasTool.Circle:
            {
                var oldBounds = GetDragShapeRect(_dragLastImg);
                var newBounds = GetDragShapeRect(img);
                _dragLastImg = img;
                InvalidateLivePreview(oldBounds, newBounds, 24);
                break;
            }
            case CanvasTool.Highlight:
            case CanvasTool.Blur:
            {
                var oldBounds = NormRect(_dragStartImg, _dragLastImg);
                var newBounds = NormRect(_dragStartImg, img);
                _dragLastImg = img;
                InvalidateLivePreview(oldBounds, newBounds, 18);
                break;
            }
        }
    }

    private void InvalidateLivePreview(Rectangle oldImgBounds, Rectangle newImgBounds, int extraPad = 24)
    {
        float sw = GetScaledStrokeWidth(StrokeWidth);
        int pad = (int)Math.Ceiling(sw + extraPad);

        RectangleF oldScreen = ImageToScreenRect(Rectangle.Inflate(oldImgBounds, pad, pad));
        RectangleF newScreen = ImageToScreenRect(Rectangle.Inflate(newImgBounds, pad, pad));

        Rectangle dirty;
        if (!oldImgBounds.IsEmpty && !newImgBounds.IsEmpty)
            dirty = Rectangle.Round(RectangleF.Union(oldScreen, newScreen));
        else if (!oldImgBounds.IsEmpty)
            dirty = Rectangle.Round(oldScreen);
        else if (!newImgBounds.IsEmpty)
            dirty = Rectangle.Round(newScreen);
        else
            dirty = ClientRectangle;

        dirty.Inflate(6, 6);
        dirty.Intersect(ClientRectangle);
        if (dirty.Width > 0 && dirty.Height > 0)
        {
            Invalidate(dirty);
            Update();
        }
        else
        {
            Invalidate();
        }
    }

    private Rectangle GetCursorChipRect(Point clientPos)
    {
        if (clientPos.IsEmpty || !ToolShowsCursorChip(_activeTool))
            return Rectangle.Empty;

        bool hasStroke = ToolChipHasStroke(_activeTool);
        string label = hasStroke ? string.Format(LocalizationService.Translate("Thickness {0}"), (int)Math.Round(StrokeWidth)) : string.Empty;

        const int glyphSize = 22;
        using var font = UiChrome.ChromeFont(8.5f, FontStyle.Regular);
        using var tempBmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tempBmp);
        SizeF textSize = label.Length > 0 ? g.MeasureString(label, font) : SizeF.Empty;

        const int padX = 7, padY = 5, gap = 6;
        float contentH = Math.Max(glyphSize, textSize.Height);
        int chipW = padX + glyphSize
            + (label.Length > 0 ? gap + (int)Math.Ceiling(textSize.Width) : 0) + padX;
        int chipH = padY + (int)Math.Ceiling(contentH) + padY;

        const int off = 18;
        int x = clientPos.X + off;
        int y = clientPos.Y + off;
        if (x + chipW > ClientSize.Width) x = clientPos.X - off - chipW;
        if (y + chipH > ClientSize.Height) y = clientPos.Y - off - chipH;
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        return Rectangle.Inflate(new Rectangle(x, y, chipW, chipH), 6, 6);
    }

    private static Rectangle RectFromPoints(Point a, Point b, int pad = 0)
    {
        int x = Math.Min(a.X, b.X) - pad;
        int y = Math.Min(a.Y, b.Y) - pad;
        int w = Math.Abs(a.X - b.X) + pad * 2;
        int h = Math.Abs(a.Y - b.Y) + pad * 2;
        return new Rectangle(x, y, w, h);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && TryWelcomeMouseUp(e.Location))
            return;

        base.OnMouseUp(e);

        if (ScrollbarMouseUp(e)) return;

        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            Capture = false;
            Invalidate();
            OnStateChanged();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            if (_activeDraggedHorizontalGuideIndex >= 0)
            {
                int idx = _activeDraggedHorizontalGuideIndex;
                _activeDraggedHorizontalGuideIndex = -1;
                Capture = false;

                Point imgPt = ScreenToImage(e.Location);
                bool offCanvas = e.Y < 0 || e.Y > ClientSize.Height || imgPt.Y < 0 || imgPt.Y > _baseBitmap.Height;
                if (offCanvas)
                {
                    RemoveHorizontalGuideAt(idx);
                    ShowToolBanner(LocalizationService.Translate("Guide removed"));
                }
                Invalidate();
                return;
            }

            if (_activeDraggedVerticalGuideIndex >= 0)
            {
                int idx = _activeDraggedVerticalGuideIndex;
                _activeDraggedVerticalGuideIndex = -1;
                Capture = false;

                Point imgPt = ScreenToImage(e.Location);
                bool offCanvas = e.X < 0 || e.X > ClientSize.Width || imgPt.X < 0 || imgPt.X > _baseBitmap.Width;
                if (offCanvas)
                {
                    RemoveVerticalGuideAt(idx);
                    ShowToolBanner(LocalizationService.Translate("Guide removed"));
                }
                Invalidate();
                return;
            }
        }

        if (_inlineTextSelecting)
        {
            _inlineTextSelecting = false;
            if (Capture) Capture = false;
            Invalidate();
            return;
        }

        if (_textGripDragging)
        {
            _textGripDragging = false;
            return;
        }

        if (_textResizing)
        {
            _textResizing = false;
            _textResizeHandle = -1;
            NotifyTextStyleChanged();
            return;
        }

        if (_isPanning && (e.Button == MouseButtons.Middle ||
            (e.Button == MouseButtons.Left && _activeTool == CanvasTool.Pan)))
        {
            _isPanning = false;
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (_resizeDragging)
        {
            _resizeDragging = false;
            _activeResizeHandle = -1;
            Capture = false;

            bool changed = HasPendingResize;
            if (changed)
            {
                _resizePreviewSize = new Size(_pendingResizeRect.Width, _pendingResizeRect.Height);
                ShowToolBanner(CyberSnap.Services.LocalizationService.Translate("Enter / Double-click to confirm"), sticky: true);
            }
            else
            {
                ClearPendingResizeState();
            }
            OnStateChanged();
            Invalidate();
            return;
        }

        if (_cropDragging)
        {
            bool wasResized = _activeCropHandle >= 0 && _activeCropHandle <= 7;
            _cropDragging = false;
            if (_activeCropHandle == -1)
            {
                _cropRect = NormRect(_dragStartImg, _dragLastImg);
            }
            _cropHasRect = _cropRect.Width >= 4 && _cropRect.Height >= 4;
            bool clickedOutside = !_cropHasRect;
            if (!_cropHasRect)
            {
                if (EditorAutoCropControls && _baseBitmap is not null)
                {
                    _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
                    _cropHasRect = true;
                }
                else
                {
                    _cropRect = Rectangle.Empty;
                }
            }
            _activeCropHandle = -1;
            Invalidate();
            OnStateChanged();
            if (clickedOutside)
            {
                ShowToolBanner(CyberSnap.Services.LocalizationService.Translate("Crop canceled"));
            }
            else if (wasResized && _cropHasRect)
            {
                ShowToolBanner(CyberSnap.Services.LocalizationService.Translate("Enter / Double-click to confirm"), sticky: true);
            }
            return;
        }

        if (_cutOutDragging)
        {
            EndCutOutPointer();
            return;
        }

        if (!_isDragging) return;
        _isDragging = false;

        if (CommitSelectionDrag())
        {
            FinishTempMoveFromPanIfNeeded();
            return;
        }

        switch (_activeTool)
        {
            case CanvasTool.Draw when _currentStroke is { Count: >= 2 }:
                Push(new AddAnnotationCommand(new DrawStroke(_currentStroke, ToolColor, StrokeWidth)));
                _currentStroke = null;
                break;
            case CanvasTool.Draw:
                _currentStroke = null;
                Invalidate();
                break;
            case CanvasTool.Arrow:
            {
                var arrowEnd = GetDragLineEnd(_dragLastImg);
                if (_dragStartImg != arrowEnd)
                    PushAndSelectDrawn(new AddAnnotationCommand(new ArrowAnnotation(_dragStartImg, arrowEnd, ToolColor, StrokeWidth)));
                break;
            }
            case CanvasTool.Line:
            {
                var lineEnd = GetDragLineEnd(_dragLastImg);
                if (_dragStartImg != lineEnd)
                    PushAndSelectDrawn(new AddAnnotationCommand(new LineAnnotation(_dragStartImg, lineEnd, ToolColor, StrokeWidth)));
                break;
            }
            case CanvasTool.Rect:
                var rect = GetDragShapeRect(_dragLastImg);
                if (rect.Width >= 4 && rect.Height >= 4)
                    PushAndSelectDrawn(new AddAnnotationCommand(new RectShapeAnnotation(rect, ToolColor, StrokeWidth)));
                Invalidate();
                break;
            case CanvasTool.Circle:
                var crect = GetDragShapeRect(_dragLastImg);
                if (crect.Width >= 4 && crect.Height >= 4)
                    PushAndSelectDrawn(new AddAnnotationCommand(new CircleShapeAnnotation(crect, ToolColor, StrokeWidth)));
                Invalidate();
                break;
            case CanvasTool.Highlight:
                var hlRect = NormRect(_dragStartImg, _dragLastImg);
                if (hlRect.Width >= 4 && hlRect.Height >= 4)
                    Push(new AddAnnotationCommand(new HighlightAnnotation(hlRect, ToolColor)));
                Invalidate();
                break;
            case CanvasTool.Blur:
                var blurRect = NormRect(_dragStartImg, _dragLastImg);
                if (blurRect.Width >= 4 && blurRect.Height >= 4)
                    Push(new AddAnnotationCommand(new BlurRect(blurRect)));
                Invalidate();
                break;
            case CanvasTool.CurvedArrow when _currentStroke is { Count: >= 2 }:
                PushAndSelectDrawn(new AddAnnotationCommand(new CurvedArrowAnnotation(_currentStroke, ToolColor, StrokeWidth)));
                _currentStroke = null;
                break;
            case CanvasTool.CurvedArrow:
                _currentStroke = null;
                Invalidate();
                break;
            default:
                Invalidate();
                break;
        }

        FinishTempMoveFromPanIfNeeded();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearScrollbarHover();
        if (_welcomeHoverChip >= 0 || _welcomeHoverCard || _welcomePressedChip >= 0)
        {
            _welcomeHoverChip = -1;
            _welcomeHoverCard = false;
            _welcomePressedChip = -1;
            Invalidate();
        }
        if (_cursorOnCanvas)
        {
            _cursorOnCanvas = false;
            if (ToolShowsCursorChip(_activeTool))
                Invalidate();
        }
        if (_eraserHoverIndex >= 0)
        {
            _eraserHoverIndex = -1;
            Invalidate();
        }
        if (_moveHoverIndex >= 0)
        {
            _moveHoverIndex = -1;
            Invalidate();
        }
        if (_hoverImgValid)
        {
            _hoverImgValid = false;
            Invalidate();
        }
        UpdateResizeHandlesHover();
    }

    /// <summary>Commits a drag-drawn shape (circle, rect, line, arrow) and selects it so
    /// handles appear immediately. Stamp-like tools (text, emoji, highlight, blur, magnifier,
    /// step) keep using plain Push so they stay unselected for rapid repeat placement.</summary>
    private void PushAndSelectDrawn(IEditCommand command)
    {
        int countBefore = _annotations.Count;
        Push(command);
        if (_annotations.Count <= countBefore) return;

        _multiSelectedIndices.Clear();
        _multiDragOriginals = null;
        _selectedAnnotationIndex = _annotations.Count - 1;
        RefreshLastUndoAfterSelection();
        OnStateChanged();
        Invalidate();
    }

    /// <summary>Suppresses the hover/control box for the annotation just placed, until the
    /// cursor leaves it (so the box appears only on a deliberate re-hover).</summary>
    private void SuppressHoverForLastPlaced()
    {
        _suppressHoverIndex = _annotations.Count - 1;
        _moveHoverIndex = -1;
    }

    private void UpdateMoveHover(Point img)
    {
        int hitIdx = HitTestAnnotationSurface(img);

        // Keep hover active while the cursor stays inside the wrap box so corner/edge
        // handles remain reachable after moving off the stroke.
        if (hitIdx < 0 && _moveHoverIndex >= 0 && _moveHoverIndex < _annotations.Count
            && HitTestSingle(_annotations[_moveHoverIndex], img, 10))
        {
            hitIdx = _moveHoverIndex;
        }

        if (_suppressHoverIndex >= 0)
        {
            if (hitIdx == _suppressHoverIndex) hitIdx = -1;   // still on the just-placed item: stay inert
            else _suppressHoverIndex = -1;                    // cursor left it: re-enable normal hover
        }
        if (hitIdx == _moveHoverIndex) return;

        var oldIdx = _moveHoverIndex;
        _moveHoverIndex = hitIdx;

        if (oldIdx >= 0 || hitIdx >= 0)
            Invalidate();

        UpdateCursor();
    }

    /// <summary>
    /// Drops hover chrome immediately. Call when a new draw drag starts so another
    /// object's dashed box / move glyph cannot linger over the live preview.
    /// </summary>
    private void ClearMoveHoverHighlight()
    {
        if (_moveHoverIndex < 0)
            return;
        _moveHoverIndex = -1;
        Invalidate();
    }

    private void UpdateEraserHover(Point img)
    {
        int hitIdx = HitTestAnnotation(img);
        if (hitIdx == _eraserHoverIndex) return;

        var oldIdx = _eraserHoverIndex;
        _eraserHoverIndex = hitIdx;

        if (oldIdx >= 0 || hitIdx >= 0)
            Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // Flash scrollbars on any wheel action (zoom or tool-specific).
        NotifyScrollbarActivity();

        // While editing text: scroll the font list if open, otherwise adjust font size.
        if (_inlineTextBox is not null)
        {
            if (_fontDropdownOpen)
            {
                EnsureFontDropdownData();
                int maxScroll = Math.Max(0, _fontDropdownEntries.Length - FontDropdownVisible);
                _fontDropdownScroll = Math.Clamp(_fontDropdownScroll + (e.Delta > 0 ? -1 : 1), 0, maxScroll);
                Invalidate();
                return;
            }
            AdjustTextFontSize(e.Delta > 0 ? 2f : -2f);
            return;
        }

        // With the Emoji tool active, the wheel sizes the emoji to be placed (matches
        // the capture overlay); zoom is unaffected for every other tool.
        if (_activeTool == CanvasTool.Emoji)
        {
            EmojiPlaceSize = _emojiPlaceSize + (e.Delta > 0 ? 4f : -4f);
            ShowToolBanner($"{CyberSnap.Services.LocalizationService.Translate("Emoji size")}: {(int)_emojiPlaceSize}px");
            Invalidate();
            return;
        }

        const double step = 1.15;
        ZoomBy(e.Delta > 0 ? step : 1.0 / step, e.Location);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.KeyCode is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            && _isDragging && _activeTool is CanvasTool.Rect or CanvasTool.Circle or CanvasTool.Line or CanvasTool.Arrow)
        {
            Invalidate();
        }

        if (e.KeyCode == Keys.Space)
            EndSpacePanGesture(e);
    }

    private void EndSpacePanGesture(KeyEventArgs e)
    {
        // Not in a temporary Space-pan session — leave normal Pan-tool drags alone.
        if (_preSpaceTool is null)
            return;

        _isPanning = false;

        var elapsedMs = (DateTime.UtcNow - _spaceKeyDownUtc).TotalMilliseconds;
        if (elapsedMs < EditorToolHotkeyHelper.SpacePanTapThresholdMs
            && EditorToolHotkeyHelper.IsSpaceAssignedAsPanHotkey())
        {
            var sourceTool = _preSpaceTool.Value;
            _preSpaceTool = null;
            if (sourceTool == CanvasTool.Crop)
                FinalizeLeavingCrop();
            if (sourceTool == CanvasTool.CutOut)
                FinalizeLeavingCutOut();
            ShowToolBanner(GetToolName(CanvasTool.Pan));
            UpdateCursor();
            OnStateChanged();
            Invalidate();
        }
        else
        {
            var restore = _preSpaceTool.Value;
            _preSpaceTool = null;
            ActiveTool = restore;
        }

        e.Handled = true;
    }

    public void StartTemporarySpacePan()
    {
        if (_preSpaceTool == null && _activeTool != CanvasTool.Pan)
        {
            _preSpaceTool = _activeTool;

            if (_isDragging || _currentStroke is not null)
            {
                _isDragging = false;
                _currentStroke = null;
            }
            if (_cropDragging)
            {
                _cropDragging = false;
                _activeCropHandle = -1;
            }
            if (_cutOutDragging)
            {
                _cutOutDragging = false;
                _activeCutOutHandle = -1;
            }
            _isSelectResizing = false;
            _selectResizeHandle = -1;

            ActiveTool = CanvasTool.Pan;

            if (Control.MouseButtons == MouseButtons.Left)
            {
                _isPanning = true;
                _userPanned = true;
                DismissWelcomeOverlay();
                _viewFitsWindow = false;
                _panStart = PointToClient(Cursor.Position);
                _panStartOffset = _pan;
            }
        }
    }

    /// <summary>
    /// Host-side Space keydown (ProcessKeyPreview). Avoids SendMessage re-entry that
    /// would skip <see cref="OnKeyDown"/> via ProcessKeyPreview returning true first.
    /// </summary>
    public void HandleSpacePanKeyDown()
    {
        if (_inlineTextBox is not null)
            return;

        // Only stamp the initial press — key-repeat must not reset the tap timer.
        if (_preSpaceTool is null && _activeTool != CanvasTool.Pan)
            _spaceKeyDownUtc = DateTime.UtcNow;
        StartTemporarySpacePan();
    }

    /// <summary>Host-side Space keyup to end temporary pan when the canvas may not be focused.</summary>
    public void HandleSpacePanKeyUp()
    {
        if (_inlineTextBox is not null)
            return;

        var e = new KeyEventArgs(Keys.Space);
        EndSpacePanGesture(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_inlineTextBox is null
            && !EditorToolHotkeyHelper.IsReservedEditorChord(e.KeyData)
            && EditorToolHotkeyHelper.TryActivateTool(this, e.KeyData))
        {
            e.Handled = true;
            return;
        }

        if (e.KeyCode is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            && _isDragging && _activeTool is CanvasTool.Rect or CanvasTool.Circle or CanvasTool.Line or CanvasTool.Arrow)
        {
            Invalidate();
        }

        if (e.KeyCode == Keys.Space && _inlineTextBox is null)
        {
            // Only stamp the initial press — key-repeat would reset the timer and turn
            // a long hold into a false "tap" that commits Pan instead of restoring.
            if (_preSpaceTool is null && _activeTool != CanvasTool.Pan)
                _spaceKeyDownUtc = DateTime.UtcNow;
            StartTemporarySpacePan();
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.Z)
        {
            if (e.Shift) Redo(); else Undo();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Enter && HasPendingResize)
        {
            TryConfirmPendingResize();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Enter && _activeTool == CanvasTool.Crop && _cropHasRect)
        {
            TryConfirmCrop();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Enter && _activeTool == CanvasTool.CutOut && IsValidPendingCutOut)
        {
            TryConfirmCutOut();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            ProcessEscapeKey();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Delete && (_selectedAnnotationIndex >= 0 || _multiSelectedIndices.Count > 0))
        {
            if (_multiSelectedIndices.Count > 1)
            {
                DeleteMultiSelectedAnnotations();
            }
            else if (_selectedAnnotationIndex >= 0)
            {
                DeleteAnnotationAt(_selectedAnnotationIndex);
            }
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.D && e.Control && (_selectedAnnotationIndex >= 0 || _multiSelectedIndices.Count > 0))
        {
            DuplicateSelection();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.A && e.Control)
        {
            SelectAll();
            e.Handled = true;
            return;
        }
        if (EditorViewHotkeyHelper.TryHandleViewHotkeys(this, e))
        {
            e.Handled = true;
            return;
        }
    }

    // ── Tool preview (live, drawn inside the zoom transform) ───────────────

    /// <summary>Next step badge number = one past the highest already placed (1 when none).</summary>
    private int NextStepNumber() =>
        _annotations.OfType<StepNumberAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    private void RenderToolPreview(Graphics g)
    {
        if (_isMarqueeSelecting)
        {
            var marqueeRect = NormRect(_marqueeStartImg, _marqueeEndImg);
            if (marqueeRect.Width > 0 && marqueeRect.Height > 0)
            {
                using (var fillBrush = new SolidBrush(Color.FromArgb(30, 0, 120, 215)))
                using (var borderPen = new Pen(Color.FromArgb(180, 0, 120, 215), 1.5f))
                {
                    borderPen.DashStyle = DashStyle.Dash;
                    g.FillRectangle(fillBrush, marqueeRect);
                    g.DrawRectangle(borderPen, marqueeRect);
                }
            }
        }

        // Emoji ghost follows the cursor (click-to-place, so there is no drag).
        if (_activeTool == CanvasTool.Emoji && !string.IsNullOrEmpty(_selectedEmoji) && _hoverImgValid)
        {
            int bitmapSize = (int)(_emojiPlaceSize * 1.4f) + 4;
            var ghostPos = new Point(_hoverImg.X - bitmapSize / 2, _hoverImg.Y - bitmapSize / 2);
            PaintEmoji(g, ghostPos, _selectedEmoji, _emojiPlaceSize, 0.6f);
        }

        // Magnifier lens preview follows the cursor before the click places it.
        if (_activeTool == CanvasTool.Magnifier && _hoverImgValid)
            PaintMagnifier(g, _hoverImg, GetMagnifierSrcRect(_hoverImg), 0.65f);

        // Step number ghost shows the next badge (and its number) exactly where a click lands.
        // Hidden while hovering an existing badge, where a click moves it instead of placing a new one.
        if (_activeTool == CanvasTool.StepNumber && _hoverImgValid && _moveHoverIndex < 0)
            PaintStepNumber(g, _hoverImg, NextStepNumber(), ToolColor, 0.6f);

        if (!_isDragging) return;

        // Shape-tool drag preview uses _dragStartImg/_dragLastImg from the last draw. When
        // moving/resizing an existing object those coords are stale and would ghost the
        // original shape at its creation position on top of the live annotation.
        if (IsManipulatingExistingAnnotation)
            return;

        switch (_activeTool)
        {
            case CanvasTool.Draw when _currentStroke is { Count: >= 2 }:
                SketchRenderer.DrawFreehandStroke(g, _currentStroke, ToolColor, GetScaledStrokeWidth(StrokeWidth), AnnotationStrokeShadow);
                break;
            case CanvasTool.Arrow:
            {
                var arrowEnd = GetDragLineEnd(_dragLastImg);
                SketchRenderer.DrawArrow(g, _dragStartImg, arrowEnd, ToolColor,
                    _dragStartImg.GetHashCode(), strokeShadow: AnnotationStrokeShadow, strokeWidth: GetScaledStrokeWidth(StrokeWidth));
                break;
            }
            case CanvasTool.Line:
            {
                var lineEnd = GetDragLineEnd(_dragLastImg);
                SketchRenderer.DrawLine(g, _dragStartImg, lineEnd, ToolColor,
                    _dragStartImg.GetHashCode(), AnnotationStrokeShadow, GetScaledStrokeWidth(StrokeWidth));
                break;
            }
            case CanvasTool.Rect:
                var rect = GetDragShapeRect(_dragLastImg);
                if (rect.Width > 0 && rect.Height > 0)
                    SketchRenderer.DrawRectShape(g, rect, ToolColor, AnnotationStrokeShadow, GetScaledStrokeWidth(StrokeWidth));
                break;
            case CanvasTool.Circle:
                var crect = GetDragShapeRect(_dragLastImg);
                if (crect.Width > 0 && crect.Height > 0)
                    SketchRenderer.DrawCircleShape(g, crect, ToolColor, AnnotationStrokeShadow, GetScaledStrokeWidth(StrokeWidth));
                break;
            case CanvasTool.CurvedArrow when _currentStroke is { Count: >= 2 }:
                SketchRenderer.DrawCurvedArrow(g, _currentStroke, ToolColor, _currentStroke.Count * 7919, AnnotationStrokeShadow, GetScaledStrokeWidth(StrokeWidth));
                break;
            case CanvasTool.Highlight:
                var hlPrev = NormRect(_dragStartImg, _dragLastImg);
                if (hlPrev.Width > 0 && hlPrev.Height > 0)
                {
                    using (var path = SketchRenderer.RoundedRect(hlPrev, 5))
                    using (var brush = new SolidBrush(Color.FromArgb(92, ToolColor.R, ToolColor.G, ToolColor.B)))
                        g.FillPath(brush, path);
                }
                break;
            case CanvasTool.Blur:
                var blurPrev = NormRect(_dragStartImg, _dragLastImg);
                if (blurPrev.Width > 2 && blurPrev.Height > 2)
                    PaintBlurRect(g, blurPrev);
                break;
        }
    }

    // ── Cursor tool chip (color + stroke, drawn in screen space) ───────────

    /// <summary>Tools whose color (and possibly stroke) the cursor chip should preview.</summary>
    private static bool ToolShowsCursorChip(CanvasTool t) => t is
        CanvasTool.Draw or CanvasTool.Arrow or CanvasTool.CurvedArrow or
        CanvasTool.Line or CanvasTool.Rect or CanvasTool.Circle or CanvasTool.Highlight;

    /// <summary>Of the chip tools, the ones that actually carry a stroke width to show.</summary>
    private static bool ToolChipHasStroke(CanvasTool t) => t is
        CanvasTool.Draw or CanvasTool.Arrow or CanvasTool.CurvedArrow or
        CanvasTool.Line or CanvasTool.Rect or CanvasTool.Circle;

    /// <summary>
    /// Small chip that floats just off the cursor showing the active drawing tool's color
    /// (and stroke width where it applies), so the user can confirm what they're about to
    /// draw without looking back at the toolbar. Drawn in screen space; suppressed while
    /// dragging (the live stroke preview already conveys this), while editing text, and
    /// while hovering an existing annotation to move/resize it.
    /// </summary>
    private void RenderCursorToolPreview(Graphics g)
    {
        if (!_cursorOnCanvas || _isDragging || _isPanning || _cropDragging) return;
        if (_preSpaceTool != null || _inlineTextBox is not null) return;
        if (!ToolShowsCursorChip(_activeTool)) return;
        // Don't compete with the move/resize affordance when over an annotation.
        if (_moveHoverIndex >= 0 || _selectedAnnotationIndex >= 0) return;

        bool hasStroke = ToolChipHasStroke(_activeTool);
        var color = ToolColor;

        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            // A little figure of the active tool (circle, arrow, …) drawn in the tool color,
            // with its outline thickness scaled from — but not equal to — the real stroke so
            // it stays legible at any width. The exact width is spelled out by the label.
            const int glyphSize = 22;
            float glyphStroke = Math.Clamp(StrokeWidth * 0.5f, 1.8f, 4.5f);
            string label = hasStroke ? string.Format(LocalizationService.Translate("Thickness {0}"), (int)Math.Round(StrokeWidth)) : string.Empty;

            using var font = UiChrome.ChromeFont(8.5f, FontStyle.Regular);
            SizeF textSize = label.Length > 0 ? g.MeasureString(label, font) : SizeF.Empty;

            const int padX = 7, padY = 5, gap = 6;
            float contentH = Math.Max(glyphSize, textSize.Height);
            int chipW = padX + glyphSize
                + (label.Length > 0 ? gap + (int)Math.Ceiling(textSize.Width) : 0) + padX;
            int chipH = padY + (int)Math.Ceiling(contentH) + padY;

            // Float down-right of the pointer, flipping near the right/bottom edges so the
            // chip never spills off-canvas or sits under the cursor hotspot.
            const int off = 18;
            int x = _cursorClient.X + off;
            int y = _cursorClient.Y + off;
            if (x + chipW > ClientSize.Width) x = _cursorClient.X - off - chipW;
            if (y + chipH > ClientSize.Height) y = _cursorClient.Y - off - chipH;
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            var chipRect = new Rectangle(x, y, chipW, chipH);

            using (var shadowPath = EditorPaint.RoundedRect(new Rectangle(chipRect.X + 1, chipRect.Y + 2, chipRect.Width, chipRect.Height), 6))
            using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillPath(shadow, shadowPath);

            using (var path = EditorPaint.RoundedRect(chipRect, 6))
            using (var bg = new SolidBrush(Color.FromArgb(235, EditorColors.BgCard)))
            using (var border = new Pen(Color.FromArgb(120, EditorColors.Accent), 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            var glyphBox = new RectangleF(chipRect.X + padX, chipRect.Y + (chipRect.Height - glyphSize) / 2f, glyphSize, glyphSize);
            DrawToolGlyph(g, _activeTool, glyphBox, color, glyphStroke);

            if (label.Length > 0)
            {
                float tx = glyphBox.Right + gap;
                float ty = chipRect.Y + (chipRect.Height - textSize.Height) / 2f;
                using var tb = new SolidBrush(EditorColors.TextSecondary);
                g.DrawString(label, font, tb, tx, ty);
            }
        }
        finally
        {
            g.SmoothingMode = oldSmoothing;
        }
    }

    /// <summary>Draws a compact figure of <paramref name="tool"/> inside <paramref name="box"/>,
    /// in the tool color — the same shape the tool produces, miniaturized for the cursor chip.</summary>
    private static void DrawToolGlyph(Graphics g, CanvasTool tool, RectangleF box, Color color, float stroke)
    {
        const float m = 3.5f;
        float l = box.Left + m, t = box.Top + m, r = box.Right - m, b = box.Bottom - m;

        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (tool)
        {
            case CanvasTool.Line:
                g.DrawLine(pen, l, b, r, t);
                break;

            case CanvasTool.Arrow:
                g.DrawLine(pen, l, b, r, t);
                DrawGlyphArrowhead(g, pen, new PointF(l, b), new PointF(r, t));
                break;

            case CanvasTool.CurvedArrow:
            {
                var p0 = new PointF(l, b);
                var p1 = new PointF(l + (r - l) * 0.1f, t + (b - t) * 0.35f);
                var p2 = new PointF(r, t);
                g.DrawCurve(pen, new[] { p0, p1, p2 }, 0.6f);
                DrawGlyphArrowhead(g, pen, p1, p2);
                break;
            }

            case CanvasTool.Rect:
                using (var path = EditorPaint.RoundedRect(Rectangle.Round(new RectangleF(l, t, r - l, b - t)), 3))
                    g.DrawPath(pen, path);
                break;

            case CanvasTool.Circle:
                g.DrawEllipse(pen, l, t, r - l, b - t);
                break;

            case CanvasTool.Draw:
            {
                // A small freehand squiggle conveys the pencil/brush.
                var pts = new[]
                {
                    new PointF(l, b - (b - t) * 0.15f),
                    new PointF(l + (r - l) * 0.34f, t),
                    new PointF(l + (r - l) * 0.66f, b),
                    new PointF(r, t + (b - t) * 0.15f),
                };
                g.DrawCurve(pen, pts, 0.6f);
                break;
            }

            case CanvasTool.Highlight:
            {
                // Translucent bar mirrors how the highlighter actually paints.
                using var fill = new SolidBrush(Color.FromArgb(150, color.R, color.G, color.B));
                float barTop = t + (b - t) * 0.18f;
                using var path = EditorPaint.RoundedRect(
                    Rectangle.Round(new RectangleF(l, barTop, r - l, (b - t) * 0.64f)), 2);
                g.FillPath(fill, path);
                break;
            }
        }
    }

    /// <summary>Draws a small arrowhead at <paramref name="to"/>, pointing along from→to.</summary>
    private static void DrawGlyphArrowhead(Graphics g, Pen pen, PointF from, PointF to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.1f) return;

        float nx = dx / len, ny = dy / len;
        float head = Math.Max(5f, len * 0.4f);
        const float ang = 26f * MathF.PI / 180f;

        var basePt = new PointF(to.X - nx * head, to.Y - ny * head);
        var left = RotateAround(basePt, to, -ang);
        var right = RotateAround(basePt, to, ang);
        g.DrawLine(pen, left, to);
        g.DrawLine(pen, right, to);
    }

    private static PointF RotateAround(PointF p, PointF pivot, float radians)
    {
        float s = MathF.Sin(radians), c = MathF.Cos(radians);
        float dx = p.X - pivot.X, dy = p.Y - pivot.Y;
        return new PointF(
            pivot.X + dx * c - dy * s,
            pivot.Y + dx * s + dy * c);
    }

    // ── Crop overlay (drawn outside the zoom transform) ────────────────────

    private void RenderCropOverlay(Graphics g)
    {
        bool cropToolMode = IsCropOverlayActive;
        if (!cropToolMode) return;
        if (!_cropDragging && !_cropHasRect) return;
        if (_cropRect.Width <= 0 || _cropRect.Height <= 0) return;

        var imgRect = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        var cropScreen = ImageToScreenRect(_cropRect);

        using (var dark = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
        using (var region = new Region(imgRect))
        {
            region.Exclude(cropScreen);
            g.FillRegion(dark, region);
        }

        using (var shadowPen = new Pen(Color.FromArgb(120, 0, 0, 0), 1.5f))
        using (var borderPen = new Pen(Color.FromArgb(255, 0, 255, 255), 1.5f) { DashStyle = DashStyle.Dash })
        {
            g.DrawRectangle(shadowPen, cropScreen.X + 1f, cropScreen.Y + 1f, cropScreen.Width, cropScreen.Height);
            g.DrawRectangle(borderPen, cropScreen.X, cropScreen.Y, cropScreen.Width, cropScreen.Height);
        }

        bool showHandles = _cropHasRect && (_preSpaceTool == null || _preSpaceTool == CanvasTool.Crop);
        if (showHandles)
            DrawCropHandles(g, cropScreen);
    }

    private static void DrawCropHandles(Graphics g, RectangleF rect)
    {
        // Modern premium crop handles: L-shaped corners and pill-shaped edge bars.
        var accent = Color.FromArgb(255, 0, 255, 255);
        var shadow = Color.FromArgb(100, 0, 0, 0);

        using var thickPen = new Pen(accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shadowPen = new Pen(shadow, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Corner line length
        const float len = 11f;
        // Offset from the actual crop boundary to float nicely (or aligned perfectly)
        const float offset = 0f; 

        // 1. Draw Corners (L-shapes)
        // Top-Left
        DrawL(g, shadowPen, rect.Left - offset, rect.Top - offset, len, len);
        DrawL(g, thickPen, rect.Left - offset, rect.Top - offset, len, len);

        // Top-Right
        DrawL(g, shadowPen, rect.Right + offset, rect.Top - offset, -len, len);
        DrawL(g, thickPen, rect.Right + offset, rect.Top - offset, -len, len);

        // Bottom-Left
        DrawL(g, shadowPen, rect.Left - offset, rect.Bottom + offset, len, -len);
        DrawL(g, thickPen, rect.Left - offset, rect.Bottom + offset, len, -len);

        // Bottom-Right
        DrawL(g, shadowPen, rect.Right + offset, rect.Bottom + offset, -len, -len);
        DrawL(g, thickPen, rect.Right + offset, rect.Bottom + offset, -len, -len);

        // 2. Draw Mid-edges (Pills/bars)
        float midX = rect.Left + rect.Width / 2f;
        float midY = rect.Top + rect.Height / 2f;
        const float barLen = 14f;

        // Top edge
        g.DrawLine(shadowPen, midX - barLen / 2f, rect.Top, midX + barLen / 2f, rect.Top);
        g.DrawLine(thickPen, midX - barLen / 2f, rect.Top, midX + barLen / 2f, rect.Top);

        // Bottom edge
        g.DrawLine(shadowPen, midX - barLen / 2f, rect.Bottom, midX + barLen / 2f, rect.Bottom);
        g.DrawLine(thickPen, midX - barLen / 2f, rect.Bottom, midX + barLen / 2f, rect.Bottom);

        // Left edge
        g.DrawLine(shadowPen, rect.Left, midY - barLen / 2f, rect.Left, midY + barLen / 2f);
        g.DrawLine(thickPen, rect.Left, midY - barLen / 2f, rect.Left, midY + barLen / 2f);

        // Right edge
        g.DrawLine(shadowPen, rect.Right, midY - barLen / 2f, rect.Right, midY + barLen / 2f);
        g.DrawLine(thickPen, rect.Right, midY - barLen / 2f, rect.Right, midY + barLen / 2f);
    }

    private static void DrawL(Graphics g, Pen pen, float x, float y, float dx, float dy)
    {
        g.DrawLine(pen, x, y, x + dx, y);
        g.DrawLine(pen, x, y, x, y + dy);
    }

    // ── Canvas resize handles (cyan L-corners on the image edge) ─────────────

    /// <summary>The 8 resize handle centers in screen space, on the pending canvas rect
    /// (or the image edge when nothing is staged). Indexing matches crop handles.</summary>
    private PointF[] GetResizeHandlePositionsScreen()
    {
        var r = ImageToScreenRect(CurrentResizeRect());
        return GetCropHandlePositionsScreen(r);
    }

    private Rectangle CurrentResizeRect()
    {
        if ((_resizeDragging || _hasPendingResize) && !_pendingResizeRect.IsEmpty)
            return _pendingResizeRect;
        return new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
    }

    private void ClearPendingResizeState()
    {
        _hasPendingResize = false;
        _pendingResizeRect = Rectangle.Empty;
        _resizePreviewSize = Size.Empty;
        _activeResizeHandle = -1;
    }

    public void CancelPendingResize(bool silent = false)
    {
        bool had = _resizeDragging || HasPendingResize;
        _resizeDragging = false;
        Capture = false;
        ClearPendingResizeState();
        if (!had) return;
        if (!silent)
            ShowToolBanner(CyberSnap.Services.LocalizationService.Translate("Resize canceled"));
        OnStateChanged();
        Invalidate();
    }

    private bool TryConfirmPendingResizeFromScreen(Point screenPt)
    {
        if (!HasPendingResize) return false;
        var pendingScreen = ImageToScreenRect(_pendingResizeRect);
        if (!pendingScreen.Contains(screenPt)) return false;
        return TryConfirmPendingResize();
    }

    public bool TryConfirmPendingResize()
    {
        if (!HasPendingResize) return false;
        var rect = _pendingResizeRect;
        int w = rect.Width;
        int h = rect.Height;
        if (ConfirmResizeByHandle != null && !ConfirmResizeByHandle(w, h))
            return false;

        ClearPendingResizeState();
        ResizeCanvasFromPending(rect, ResizeHandlesScaleContent);
        OnStateChanged();
        Invalidate();
        return true;
    }

    /// <summary>True if an annotation grip (or rotate arrow) is under the pointer, so it
    /// should win over the canvas-edge resize handles.</summary>
    private bool IsOverAnnotationGrip(Point screenPt)
    {
        if (_selectedAnnotationIndex >= 0 && GetSelectHandle(screenPt, _selectedAnnotationIndex) >= 0)
            return true;
        if (_moveHoverIndex >= 0 && _moveHoverIndex != _suppressHoverIndex
            && GetSelectHandle(screenPt, _moveHoverIndex) >= 0)
            return true;
        return false;
    }

    private bool TryBeginCanvasResize(Point screenPt)
    {
        if (!EditorShowResizeHandles || _baseBitmap == null || HideCanvasResizeHandles)
            return false;
        if (IsOverAnnotationGrip(screenPt))
            return false;
        int hit = HitTestResizeHandle(screenPt);
        if (hit < 0)
            return false;

        _resizeDragging = true;
        _userPanned = true;
        DismissWelcomeOverlay();
        _activeResizeHandle = hit;
        _resizeStartImg = ScreenToImage(screenPt);
        _resizeStartRect = CurrentResizeRect();
        _pendingResizeRect = _resizeStartRect;
        _hasPendingResize = true;
        _resizePreviewSize = _resizeStartRect.Size;
        Capture = true;
        return true;
    }

    private int HitTestResizeHandle(Point screenPt)
    {
        var handles = GetResizeHandlePositionsScreen();
        for (int i = 0; i < handles.Length; i++)
        {
            var h = handles[i];
            if (Math.Abs(screenPt.X - h.X) <= ResizeHitRadius && Math.Abs(screenPt.Y - h.Y) <= ResizeHitRadius)
                return i;
        }
        return -1;
    }

    private void UpdateResizeDrag(Point screenPt)
    {
        var img = ScreenToImage(screenPt);
        int dx = img.X - _resizeStartImg.X;
        int dy = img.Y - _resizeStartImg.Y;
        var r = _resizeStartRect;

        int left = r.Left;
        int right = r.Right;
        int top = r.Top;
        int bottom = r.Bottom;

        switch (_activeResizeHandle)
        {
            case 0: left += dx; top += dy; break;
            case 1: right += dx; top += dy; break;
            case 2: left += dx; bottom += dy; break;
            case 3: right += dx; bottom += dy; break;
            case 4: top += dy; break;
            case 5: right += dx; break;
            case 6: bottom += dy; break;
            case 7: left += dx; break;
        }

        int newW = right - left;
        int newH = bottom - top;
        bool isCorner = _activeResizeHandle is 0 or 1 or 2 or 3;
        bool keepAspect = ResizeHandlesScaleContent ^ ModifierKeys.HasFlag(Keys.Shift);
        if (isCorner && keepAspect && r.Width > 0 && r.Height > 0)
        {
            double s = Math.Abs(dx) >= Math.Abs(dy)
                ? (double)newW / r.Width
                : (double)newH / r.Height;
            newW = (int)Math.Round(r.Width * s);
            newH = (int)Math.Round(r.Height * s);
            bool growLeft = _activeResizeHandle is 0 or 2;
            bool growTop = _activeResizeHandle is 0 or 1;
            if (growLeft) left = right - newW; else right = left + newW;
            if (growTop) top = bottom - newH; else bottom = top + newH;
        }

        newW = right - left;
        newH = bottom - top;
        if (newW < MinCanvasSize)
        {
            if (left != r.Left) left = right - MinCanvasSize;
            else right = left + MinCanvasSize;
            newW = MinCanvasSize;
        }
        if (newH < MinCanvasSize)
        {
            if (top != r.Top) top = bottom - MinCanvasSize;
            else bottom = top + MinCanvasSize;
            newH = MinCanvasSize;
        }
        newW = Math.Clamp(newW, MinCanvasSize, MaxCanvasSize);
        newH = Math.Clamp(newH, MinCanvasSize, MaxCanvasSize);
        if (left != r.Left) left = right - newW; else right = left + newW;
        if (top != r.Top) top = bottom - newH; else bottom = top + newH;

        _pendingResizeRect = new Rectangle(left, top, right - left, bottom - top);
        _hasPendingResize = true;
        _resizePreviewSize = new Size(_pendingResizeRect.Width, _pendingResizeRect.Height);

        OnStateChanged();
        Invalidate();
    }

    private void RenderResizeHandles(Graphics g)
    {
        if (!EditorShowResizeHandles || _baseBitmap == null) return;
        if (HideCanvasResizeHandles) return;

        var liveRect = ImageToScreenRect(CurrentResizeRect());
        bool showPreview = _resizeDragging || HasPendingResize;

        if (showPreview)
        {
            using var previewShadow = new Pen(Color.FromArgb(120, 0, 0, 0), 2.5f) { DashStyle = DashStyle.Dash };
            using var previewPen = new Pen(Color.FromArgb(200, ResizeAccent), 1.35f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(previewShadow, liveRect.X + 1f, liveRect.Y + 1f, liveRect.Width, liveRect.Height);
            g.DrawRectangle(previewPen, liveRect.X, liveRect.Y, liveRect.Width, liveRect.Height);
            DrawResizeSizeBadge(g, liveRect, $"{CurrentResizeRect().Width} × {CurrentResizeRect().Height}");
        }

        DrawCropHandles(g, liveRect);
    }

    private static void DrawResizeSizeBadge(Graphics g, RectangleF previewRect, string text)
    {
        using var font = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        var size = g.MeasureString(text, font);
        float pad = 6f;
        float bw = size.Width + pad * 2;
        float bh = size.Height + pad;
        float bx = previewRect.X + previewRect.Width / 2f - bw / 2f;
        float by = previewRect.Y - bh - 4f;
        if (by < 2f) by = previewRect.Y + 4f;
        var badge = new RectangleF(bx, by, bw, bh);
        using (var bg = new SolidBrush(Color.FromArgb(220, 10, 14, 22)))
        using (var path = RoundedRect(badge, 5f))
            g.FillPath(bg, path);
        using var textBrush = new SolidBrush(ResizeAccent);
        g.DrawString(text, font, textBrush, bx + pad, by + pad / 2f);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Rectangle NormRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>Square-bounding drag rect when Shift is held (matches capture overlay).</summary>
    private Rectangle GetDragShapeRect(Point current)
    {
        if (!ModifierKeys.HasFlag(Keys.Shift))
            return NormRect(_dragStartImg, current);

        int dx = current.X - _dragStartImg.X;
        int dy = current.Y - _dragStartImg.Y;
        int size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        int x2 = _dragStartImg.X + Math.Sign(dx == 0 ? 1 : dx) * size;
        int y2 = _dragStartImg.Y + Math.Sign(dy == 0 ? 1 : dy) * size;
        return NormRect(_dragStartImg, new Point(x2, y2));
    }

    /// <summary>45° snap when Shift is held (matches capture overlay).</summary>
    private Point GetDragLineEnd(Point current) =>
        ModifierKeys.HasFlag(Keys.Shift)
            ? LineSnapHelper.SnapEndTo45Degrees(_dragStartImg, current)
            : current;

    // ── Inline text editor ─────────────────────────────────────────────────

    private int HitTestTextAnnotation(Point imgPt)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (i == _renderSkipAnnotationIndex) continue;
            if (_annotations[i] is not TextAnnotation ta) continue;
            if (TextAnnotationPainter.Measure(ta).Contains(imgPt))
                return i;
        }
        return -1;
    }

    /// <summary>Selects the word (or contiguous non-whitespace run) around <paramref name="index"/>.</summary>
    private static void SelectWordAt(TextBox box, int index)
    {
        string t = box.Text ?? "";
        if (t.Length == 0)
        {
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            return;
        }

        index = Math.Clamp(index, 0, t.Length);
        // If caret is at end or on whitespace, nudge left into the previous word when possible.
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
        while (start > 0 && IsWordChar(t[start - 1])) start--;
        while (end < t.Length && IsWordChar(t[end])) end++;
        box.SelectionStart = start;
        box.SelectionLength = Math.Max(0, end - start);
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '\'' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.ConnectorPunctuation;

    private void BeginInlineText(Point imgOrigin, string? initialText = null)
    {
        CommitOrCancelInlineText(commit: true);

        _textEditIndex = -1;
        _textEditOriginal = null;
        _inlineTextOrigin = imgOrigin;
        CreateInlineTextBox(initialText ?? "");
    }

    private void BeginReEditText(int index)
    {
        if (index < 0 || index >= _annotations.Count) return;
        if (_annotations[index] is not TextAnnotation ta) return;

        CommitOrCancelInlineText(commit: true);

        // Drop object-selection chrome so only the live edit frame is shown.
        // Otherwise the cyan selection box stays locked to the original bounds
        // while the dashed inline frame grows with the text.
        _selectedAnnotationIndex = -1;
        ClearMultiSelection();
        ExitRotateMode(invalidate: false);
        _moveHoverIndex = -1;

        _textEditIndex = index;
        _textEditOriginal = ta;
        _inlineTextOrigin = ta.Pos;
        _textFontSize = ta.FontSize;
        _textFontFamily = ta.FontFamily;
        _textBold = ta.Bold;
        _textItalic = ta.Italic;
        _textStroke = ta.Stroke;
        _textShadow = ta.Shadow;
        _textBackground = ta.Background;
        _textAlign = ta.Alignment;
        _textMaxWidth = ta.MaxWidth;
        ToolColor = ta.Color;

        // Hide original while editing (skip in paint)
        _renderSkipAnnotationIndex = index;
        CreateInlineTextBox(ta.Text);
    }

    private int _renderSkipAnnotationIndex = -1;

    private void CreateInlineTextBox(string text)
    {
        _inlineTextBox = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = ToolColor,
            Location = new Point(-100, -100),
            Size = new Size(1, 1),
            TabStop = false,
            Text = text,
        };
        _inlineTextBox.KeyDown += InlineTextBox_KeyDown;
        _inlineTextBox.TextChanged += (_, _) => Invalidate();
        Controls.Add(_inlineTextBox);
        UpdateInlineTextBoxStyle();
        _inlineTextBox.Focus();
        _inlineTextBox.SelectionStart = _inlineTextBox.TextLength;
        _inlineTextBox.SelectionLength = 0;
        Invalidate();
        OnStateChanged(); // refresh status-bar hint for live text editing
    }

    /// <summary>
    /// Escape routing for the editor keyboard forwarder (ProcessKeyPreview / ProcessCmdKey).
    /// Must be called directly — SendMessage re-enters ProcessKeyPreview and never reaches
    /// <see cref="OnKeyDown"/>.
    /// </summary>
    public void ProcessEscapeKey()
    {
        if (TryFinishInlineTextFromEscape())
            return;

        if (_activeTool == CanvasTool.Text)
            return;

        if (_resizeDragging || HasPendingResize)
        {
            CancelPendingResize();
            return;
        }

        if (!TryDeselectTool())
        {
            CancelInProgressTool();
            CancelCropPending();
            CancelCutOutPending();
        }
    }

    private bool TryFinishInlineTextFromEscape()
    {
        if (_inlineTextBox is null) return false;
        // Escape always cancels (never auto-commits).
        CommitOrCancelInlineText(commit: false);
        return true;
    }

    private void InlineTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            ProcessEscapeKey();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            // Enter commits; Shift+Enter inserts a newline.
            CommitOrCancelInlineText(commit: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private TextAnnotation BuildCurrentTextAnnotation(Point origin, string text) =>
        new(
            Pos: origin,
            Text: TextAnnotationPainter.NormalizeNewlines(text),
            FontSize: _textFontSize,
            Color: ToolColor,
            Bold: _textBold,
            Italic: _textItalic,
            Stroke: _textStroke,
            Shadow: _textShadow,
            Background: _textBackground,
            FontFamily: _textFontFamily,
            Alignment: _textAlign,
            MaxWidth: _textMaxWidth);

    private void CommitOrCancelInlineText(bool commit)
    {
        if (_inlineTextBox is null) return;
        var text = _inlineTextBox.Text;
        var origin = _inlineTextOrigin;
        var dirty = GetInlineTextEditingRepaintBounds();
        int editIndex = _textEditIndex;
        var original = _textEditOriginal;

        Controls.Remove(_inlineTextBox);
        _inlineTextBox.Dispose();
        _inlineTextBox = null;

        _renderSkipAnnotationIndex = -1;

        bool hasText = !string.IsNullOrWhiteSpace(text);
        if (commit && hasText)
        {
            var neu = BuildCurrentTextAnnotation(origin, text);
            if (editIndex >= 0 && original is not null && editIndex < _annotations.Count)
            {
                if (!Equals(original, neu))
                    Push(new ReplaceAnnotationCommand(editIndex, original, neu));
            }
            else if (editIndex < 0)
            {
                Push(new AddAnnotationCommand(neu));
            }
        }
        else if (commit && !hasText && editIndex >= 0 && original is not null)
        {
            Push(new DeleteAnnotationCommand(editIndex, original));
            _selectedAnnotationIndex = -1;
            RefreshLastUndoAfterSelection();
        }
        // cancel: original stays (was never removed)

        _textEditIndex = -1;
        _textEditOriginal = null;
        _fontDropdownOpen = false;
        _hoveredTextBtn = -1;
        _textGripDragging = false;
        _textResizing = false;
        _inlineTextSelecting = false;
        NotifyTextStyleChanged();
        Focus();
        if (dirty != Rectangle.Empty)
            Invalidate(dirty);
        else
            Invalidate();
        OnStateChanged(); // restore tool hint after leaving text edit
    }

    private Rectangle GetInlineTextEditingRepaintBounds()
    {
        var textBounds = Rectangle.Round(GetInlineTextScreenBounds());
        if (textBounds.IsEmpty)
            return Rectangle.Empty;

        textBounds.Inflate(24, 24);
        if (!_textToolbarRect.IsEmpty)
        {
            var toolbar = Rectangle.Round(_textToolbarRect);
            toolbar.Inflate(8, 8);
            textBounds = Rectangle.Union(textBounds, toolbar);
        }
        return textBounds;
    }

    // ── Hit-testing & selection helpers ────────────────────────────────────

    /// <summary>Finds the top-most annotation whose visual bounds contain <paramref name="pt"/></summary>
    private int HitTestAnnotation(Point pt)
    {
        const int tolerance = 10;
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (i == _renderSkipAnnotationIndex) continue;
            if (HitTestSingle(_annotations[i], pt, tolerance))
                return i;
        }
        return -1;
    }

    /// <summary>Like <see cref="HitTestAnnotation"/> but matches only the topmost annotation
    /// whose actual drawn pixels (its surface) lie under the point — hollow shapes ignore their
    /// empty interior. Drives click/selection so it agrees with the surface-scoped hand cursor.</summary>
    private int HitTestAnnotationSurface(Point pt)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (i == _renderSkipAnnotationIndex) continue;
            if (IsOverAnnotationSurface(_annotations[i], pt))
                return i;
        }
        return -1;
    }

    private bool TryEraseAnnotationAt(Point pt)
    {
        _eraserHoverIndex = -1;
        var hitIdx = HitTestAnnotation(pt);

        if (hitIdx < 0)
        {
            if (_selectedAnnotationIndex >= 0)
            {
                _selectedAnnotationIndex = -1;
                Invalidate();
            }
            return false;
        }

        DeleteAnnotationAt(hitIdx);
        return true;
    }

    private void DeleteAnnotationAt(int index)
    {
        if (index < 0 || index >= _annotations.Count)
            return;

        var toDelete = _annotations[index];
        Push(new DeleteAnnotationCommand(index, toDelete));
        _selectedAnnotationIndex = -1;
        RefreshLastUndoAfterSelection();
    }

    /// <summary>Deletes all multi-selected annotations as a single undo-able operation.</summary>
    private void DeleteMultiSelectedAnnotations()
    {
        var items = _multiSelectedIndices
            .Where(i => i >= 0 && i < _annotations.Count)
            .Select(i => (i, _annotations[i]))
            .ToList();
        if (items.Count == 0) return;

        int count = items.Count;
        Push(new DeleteMultipleAnnotationsCommand(items));
        _selectedAnnotationIndex = -1;
        _multiSelectedIndices.Clear();
        _multiDragOriginals = null;
        RefreshLastUndoAfterSelection();
        var msg = string.Format(LocalizationService.Translate("{0} objects deleted"), count);
        ShowToolBanner(msg);
        OnStateChanged();
    }

    /// <summary>Duplicates the current selection (single or multi) as a single undo-able
    /// operation. Clones are offset by (20,20) image-space pixels, clamped so they stay
    /// on the canvas (the Add guard rejects off-canvas inserts). The selection moves to
    /// the new clones.</summary>
    private void DuplicateSelection()
    {
        var indices = _multiSelectedIndices.Count > 0
            ? _multiSelectedIndices.Where(i => i >= 0 && i < _annotations.Count).OrderBy(i => i).ToList()
            : (_selectedAnnotationIndex >= 0
                ? new List<int> { _selectedAnnotationIndex }
                : new List<int>());
        if (indices.Count == 0) return;

        var originals = indices.Select(i => _annotations[i]).ToList();

        // Union bounds of the originals in image space, clamped so the offset clone stays on canvas.
        Rectangle union = Rectangle.Empty;
        foreach (var a in originals)
        {
            var b = AnnotationTransforms.GetBounds(a);
            union = union.IsEmpty ? b : Rectangle.Union(union, b);
        }
        var canvasBounds = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
        int dx = 20, dy = 20;
        if (!union.IsEmpty)
        {
            int newX = Math.Clamp(union.X + dx, 0, Math.Max(0, canvasBounds.Width - union.Width));
            int newY = Math.Clamp(union.Y + dy, 0, Math.Max(0, canvasBounds.Height - union.Height));
            dx = newX - union.X;
            dy = newY - union.Y;
        }

        var clones = originals.Select(a => AnnotationTransforms.Translate(a, dx, dy)).ToList();
        int insertStart = _annotations.Count;
        Push(new AddMultipleAnnotationsCommand(clones));

        // Push may reject silently if the batch is entirely off-canvas (shouldn't happen with the
        // clamp above, but guard anyway): only update selection if the clones were actually added.
        int added = _annotations.Count - insertStart;
        if (added <= 0) return;

        _multiSelectedIndices.Clear();
        if (added == 1)
        {
            _selectedAnnotationIndex = insertStart;
        }
        else
        {
            _selectedAnnotationIndex = -1;
            for (int i = 0; i < added; i++)
                _multiSelectedIndices.Add(insertStart + i);
        }
        _multiDragOriginals = null;
        RefreshLastUndoAfterSelection();
        OnStateChanged();
        Invalidate();
    }

    /// <summary>Toggles an annotation index in/out of the multi-selection set.
    /// If only a single item was selected before, it's promoted into the multi-set first.</summary>
    private void ToggleMultiSelect(int index)
    {
        ExitRotateMode(invalidate: false);
        // Promote the existing single selection into the multi-set so it's not lost.
        if (_multiSelectedIndices.Count == 0 && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex != index)
        {
            _multiSelectedIndices.Add(_selectedAnnotationIndex);
        }

        if (_multiSelectedIndices.Contains(index))
        {
            _multiSelectedIndices.Remove(index);
            if (_multiSelectedIndices.Count == 0)
            {
                _selectedAnnotationIndex = -1;
                HideToolBanner();
            }
            else if (_multiSelectedIndices.Count == 1)
            {
                _selectedAnnotationIndex = _multiSelectedIndices.First();
                _multiSelectedIndices.Clear();
                HideToolBanner();
            }
            else
            {
                _selectedAnnotationIndex = _multiSelectedIndices.Max();
                var msg = string.Format(LocalizationService.Translate("{0} objects selected"), _multiSelectedIndices.Count);
                ShowToolBanner(msg, sticky: true);
            }
        }
        else
        {
            _multiSelectedIndices.Add(index);
            _selectedAnnotationIndex = index;
            var msg = string.Format(LocalizationService.Translate("{0} objects selected"), _multiSelectedIndices.Count);
            ShowToolBanner(msg, sticky: true);
        }
        OnStateChanged();
    }

    private bool HitTestSingle(Annotation a, Point pt, int tol)
    {
        return a switch
        {
            BlurRect br => InflateRect(br.Rect, tol, tol).Contains(pt),
            HighlightAnnotation hl => InflateRect(hl.Rect, tol, tol).Contains(pt),
            RectShapeAnnotation rs => InflateRect(rs.Rect, tol, tol).Contains(pt),
            CircleShapeAnnotation cs => InflateRect(cs.Rect, tol, tol).Contains(pt),
            EraserFill ef => InflateRect(ef.Rect, tol, tol).Contains(pt),
            ArrowAnnotation arr => DistanceToSegment(pt, arr.From, arr.To) <= tol * 2,
            LineAnnotation ln => DistanceToSegment(pt, ln.From, ln.To) <= tol * 2,
            RulerAnnotation ru => DistanceToSegment(pt, ru.From, ru.To) <= tol * 2
                || RulerRenderer.GetLabelBounds(ru.From, ru.To, ClientRectangle).Contains(pt),
            CurvedArrowAnnotation ca => ca.Points.Any(p => Distance(p, pt) <= tol * 2),
            DrawStroke ds => ds.Points.Any(p => Distance(p, pt) <= tol),
            TextAnnotation ta => TextAnnotationPainter.Measure(ta).Contains(pt),
            StepNumberAnnotation sn => Distance(sn.Pos, pt) <= tol * 3,
            EmojiAnnotation em => InflateRect(GetAnnotationBounds(em), tol, tol).Contains(pt),
            MagnifierAnnotation mg => Distance(mg.Pos, pt) <= tol * 4,
            _ => false,
        };
    }

    private static Rectangle InflateRect(Rectangle r, int x, int y) =>
        Rectangle.Inflate(r, x, y);

    // Hit tolerance (px) added on each side of a hollow shape's stroke so its thin outline
    // is still comfortable to hover.
    private const int SurfaceOutlineTolerance = 6;

    /// <summary>True only when <paramref name="pt"/> (image space) lies over the annotation's
    /// actually-drawn pixels — its stroke/fill — not merely inside its bounding box. Scopes the
    /// hand cursor to the object's surface: hollow shapes (circle/rect) count only their outline,
    /// not their empty interior. Other types already fill (or closely hug) their bounds, so they
    /// reuse the regular bounding-box hit test.</summary>
    private bool IsOverAnnotationSurface(Annotation a, Point pt)
    {
        return a switch
        {
            CircleShapeAnnotation cs => IsOnEllipseOutlineRotated(cs.Rect, cs.Rotation, GetScaledStrokeWidth(cs.StrokeWidth), pt),
            RectShapeAnnotation rs   => IsOnRectOutlineRotated(rs.Rect, rs.Rotation, GetScaledStrokeWidth(rs.StrokeWidth), pt),
            _                        => HitTestSingle(a, pt, 10),
        };
    }

    private static bool IsOnEllipseOutline(Rectangle rect, float strokeWidth, Point pt)
    {
        rect = NormalizeRect(rect);
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        float band = strokeWidth / 2f + SurfaceOutlineTolerance;
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;

        bool Inside(float expand)
        {
            float rx = rect.Width / 2f + expand;
            float ry = rect.Height / 2f + expand;
            if (rx <= 0 || ry <= 0) return false;
            float nx = (pt.X - cx) / rx;
            float ny = (pt.Y - cy) / ry;
            return nx * nx + ny * ny <= 1f;
        }

        // On the ring = inside the outer (stroke + tolerance) ellipse but outside the inner one.
        return Inside(band) && !Inside(-band);
    }

    private static bool IsOnRectOutline(Rectangle rect, float strokeWidth, Point pt)
    {
        rect = NormalizeRect(rect);
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        int band = (int)(strokeWidth / 2f + SurfaceOutlineTolerance);
        if (!InflateRect(rect, band, band).Contains(pt)) return false;
        var inner = InflateRect(rect, -band, -band);
        // On the border = inside the outer rect but outside the inner (hollow) rect.
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(pt);
    }

    private static bool IsOnRectOutlineRotated(Rectangle rect, float rotation, float strokeWidth, Point pt)
    {
        if (Math.Abs(rotation % 360f) < 0.05f)
            return IsOnRectOutline(rect, strokeWidth, pt);
        var local = AnnotationTransforms.InverseRotatePoint(pt, AnnotationTransforms.CenterOf(rect), rotation);
        return IsOnRectOutline(rect, strokeWidth, Point.Round(local));
    }

    private static bool IsOnEllipseOutlineRotated(Rectangle rect, float rotation, float strokeWidth, Point pt)
    {
        if (Math.Abs(rotation % 360f) < 0.05f)
            return IsOnEllipseOutline(rect, strokeWidth, pt);
        var local = AnnotationTransforms.InverseRotatePoint(pt, AnnotationTransforms.CenterOf(rect), rotation);
        return IsOnEllipseOutline(rect, strokeWidth, Point.Round(local));
    }

    private static Rectangle NormalizeRect(Rectangle r)
    {
        int x = Math.Min(r.X, r.X + r.Width);
        int y = Math.Min(r.Y, r.Y + r.Height);
        return new Rectangle(x, y, Math.Abs(r.Width), Math.Abs(r.Height));
    }

    private static float Distance(Point a, Point b) =>
        (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static float DistanceToSegment(Point p, Point a, Point b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f) return Distance(p, a);
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0f, 1f);
        float projX = a.X + t * dx, projY = a.Y + t * dy;
        return (float)Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
    }

    private static (int dx, int dy) ComputeTranslationDelta(Annotation original, Annotation moved)
    {
        return (original, moved) switch
        {
            (BlurRect o, BlurRect m) => (m.Rect.X - o.Rect.X, m.Rect.Y - o.Rect.Y),
            (HighlightAnnotation o, HighlightAnnotation m) => (m.Rect.X - o.Rect.X, m.Rect.Y - o.Rect.Y),
            (RectShapeAnnotation o, RectShapeAnnotation m) => (m.Rect.X - o.Rect.X, m.Rect.Y - o.Rect.Y),
            (CircleShapeAnnotation o, CircleShapeAnnotation m) => (m.Rect.X - o.Rect.X, m.Rect.Y - o.Rect.Y),
            (EraserFill o, EraserFill m) => (m.Rect.X - o.Rect.X, m.Rect.Y - o.Rect.Y),
            (ArrowAnnotation o, ArrowAnnotation m) => (m.From.X - o.From.X, m.From.Y - o.From.Y),
            (LineAnnotation o, LineAnnotation m) => (m.From.X - o.From.X, m.From.Y - o.From.Y),
            (RulerAnnotation o, RulerAnnotation m) => (m.From.X - o.From.X, m.From.Y - o.From.Y),
            (CurvedArrowAnnotation o, CurvedArrowAnnotation m)
                => o.Points.Count > 0 && m.Points.Count > 0 ? (m.Points[0].X - o.Points[0].X, m.Points[0].Y - o.Points[0].Y) : (0, 0),
            (DrawStroke o, DrawStroke m)
                => o.Points.Count > 0 && m.Points.Count > 0 ? (m.Points[0].X - o.Points[0].X, m.Points[0].Y - o.Points[0].Y) : (0, 0),
            (TextAnnotation o, TextAnnotation m) => (m.Pos.X - o.Pos.X, m.Pos.Y - o.Pos.Y),
            (StepNumberAnnotation o, StepNumberAnnotation m) => (m.Pos.X - o.Pos.X, m.Pos.Y - o.Pos.Y),
            (EmojiAnnotation o, EmojiAnnotation m) => (m.Pos.X - o.Pos.X, m.Pos.Y - o.Pos.Y),
            (MagnifierAnnotation o, MagnifierAnnotation m) => (m.Pos.X - o.Pos.X, m.Pos.Y - o.Pos.Y),
            _ => (0, 0),
        };
    }

    private bool IsPickToolActiveForSelectAll()
        => _activeTool == CanvasTool.Move && _preSpaceTool == null;

    private void CancelSelectAllDoubleClickSideEffects()
    {
        _isMarqueeSelecting = false;
        _isDragging = false;
        _isSelectResizing = false;
        _selectResizeHandle = -1;
        _selectOriginalAnnotation = null;
        _multiDragOriginals = null;
        CancelRotateToggleTimer();
        _pendingRotateToggle = false;
        Capture = false;
    }

    /// <summary>
    /// Manual double-click fallback for Pick. Records timing on every left click; fires
    /// SelectAll when Pick is active — same contract as capture overlay Move mode.
    /// </summary>
    private bool TryHandleSelectAllDoubleClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return false;

        int now = Environment.TickCount;
        var doubleClickSize = SystemInformation.DoubleClickSize;
        bool isDoubleClick = e.Clicks >= 2
            || (_lastClickTick != 0
                && unchecked(now - _lastClickTick) <= SystemInformation.DoubleClickTime
                && Math.Abs(e.Location.X - _lastClickLocation.X) <= doubleClickSize.Width
                && Math.Abs(e.Location.Y - _lastClickLocation.Y) <= doubleClickSize.Height);

        _lastClickTick = now;
        _lastClickLocation = e.Location;

        if (!isDoubleClick || !IsPickToolActiveForSelectAll()) return false;

        CancelRotateToggleTimer();
        _pendingRotateToggle = false;

        // Centralized: text re-edit wins over select-all (also used by WndProc DBLCLK).
        SelectAllFromDoubleClick();
        return true;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // Already editing: word-select is handled in OnMouseDown (e.Clicks >= 2).
        // Swallow the event so EditorForm's blank-canvas "open file" handler never runs.
        if (_inlineTextBox is not null)
        {
            var imgPtEdit = ScreenToImage(e.Location);
            var liveRect = MeasureInlineTextRect(
                _inlineTextOrigin, _inlineTextBox.Text, _textFontSize, _textFontFamily,
                _textBold, _textItalic, _textBackground, _textMaxWidth, _textAlign);
            if (liveRect.Contains(imgPtEdit) || GetInlineTextScreenBounds().Contains(e.Location))
            {
                int caretIdx = TextAnnotationPainter.GetCharIndexAt(
                    _inlineTextOrigin, imgPtEdit, _inlineTextBox.Text,
                    _textFontSize, _textFontFamily, _textBold, _textItalic,
                    _textMaxWidth, _textAlign);
                SelectWordAt(_inlineTextBox, caretIdx);
                _inlineTextBox.Focus();
                Invalidate();
            }
            return;
        }

        // Any tool: double-click text to re-edit. Pick's select-all is handled via
        // SelectAllFromDoubleClick (which itself prefers text when present).
        var imgPt = ScreenToImage(e.Location);
        int textHit = HitTestTextAnnotation(imgPt);
        if (textHit >= 0)
        {
            // Skip if Pick already handled this via MouseDown/WndProc (IsEditingText true above).
            if (_activeTool == CanvasTool.Move)
            {
                // WndProc/MouseDown usually already handled Pick; if we get here, do it once.
                SelectAllFromDoubleClick();
                return;
            }
            ActiveTool = CanvasTool.Text;
            BeginReEditText(textHit);
            return;
        }

        if (HasPendingResize && TryConfirmPendingResizeFromScreen(e.Location))
            return;

        if (_activeTool == CanvasTool.Move)
        {
            SelectAllFromDoubleClick();
            return;
        }

        if (_activeTool == CanvasTool.Crop && _cropHasRect)
        {
            if (_cropRect.Contains(imgPt))
                TryConfirmCrop();
        }

        if (_activeTool == CanvasTool.CutOut && _cutOutHasRect)
        {
            if (_cutOutRect.Contains(imgPt))
                TryConfirmCutOut();
        }
    }

    private PointF[] GetCropHandlePositionsScreen(RectangleF rect)
    {
        return new PointF[]
        {
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Left, rect.Bottom),
            new(rect.Right, rect.Bottom),
            new(rect.Left + rect.Width / 2f, rect.Top),
            new(rect.Right, rect.Top + rect.Height / 2f),
            new(rect.Left + rect.Width / 2f, rect.Bottom),
            new(rect.Left, rect.Top + rect.Height / 2f),
        };
    }

    private Cursor GetCropCursor(Point screenPt)
    {
        if (!_cropHasRect) return CursorFactory.PrecisionCursor;

        var cropScreen = ImageToScreenRect(_cropRect);
        var handles = GetCropHandlePositionsScreen(cropScreen);
        const float hitRadius = 7f;

        for (int i = 0; i < handles.Length; i++)
        {
            var h = handles[i];
            if (Math.Abs(screenPt.X - h.X) <= hitRadius && Math.Abs(screenPt.Y - h.Y) <= hitRadius)
            {
                return i switch
                {
                    0 or 3 => Cursors.SizeNWSE,
                    1 or 2 => Cursors.SizeNESW,
                    4 or 6 => Cursors.SizeNS,
                    5 or 7 => Cursors.SizeWE,
                    _ => CursorFactory.PrecisionCursor
                };
            }
        }

        if (HasAdjustedPendingCrop && cropScreen.Contains(screenPt))
            return Cursors.SizeAll;

        return CursorFactory.PrecisionCursor;
    }

    private bool IsDrawingOrMoveTool(CanvasTool tool)
    {
        return tool != CanvasTool.Crop && tool != CanvasTool.CutOut && tool != CanvasTool.Eraser && (tool != CanvasTool.Pan || !PanModeLockObjects);
    }

    private int GetSelectHandle(Point screenPt)
    {
        return GetSelectHandle(screenPt, _selectedAnnotationIndex);
    }

    /// <summary>Whether an annotation supports resizing. Fixed-size badges (step numbers) can
    /// only be repositioned, so they expose a move-only control box (no resize handles).</summary>
    private static bool IsResizable(Annotation a) => a is not StepNumberAnnotation and not MagnifierAnnotation;

    /// <summary>Returns the screen-space bounding box of an annotation's visual representation,
    /// including its stroke width. Used to draw the selection/hover control box.</summary>
    private RectangleF GetAnnotationVisualBounds(Annotation a)
    {
        return a switch
        {
            BlurRect br => br.Rect,
            HighlightAnnotation hl => hl.Rect,
            RectShapeAnnotation rs => AnnotationTransforms.GetAxisAlignedBounds(rs.Rect, rs.Rotation),
            CircleShapeAnnotation cs => AnnotationTransforms.GetAxisAlignedBounds(cs.Rect, cs.Rotation),
            EraserFill ef => ef.Rect,
            ArrowAnnotation arr => GetSegmentBounds(arr.From, arr.To, GetScaledStrokeWidth(arr.StrokeWidth)),
            LineAnnotation ln => GetSegmentBounds(ln.From, ln.To, GetScaledStrokeWidth(ln.StrokeWidth)),
            RulerAnnotation ru => GetSegmentBounds(ru.From, ru.To, 6f), // Ruler stroke width ~6px
            CurvedArrowAnnotation ca => GetPointsBounds(ca.Points, GetScaledStrokeWidth(ca.StrokeWidth)),
            DrawStroke ds => GetPointsBounds(ds.Points, GetScaledStrokeWidth(ds.StrokeWidth)),
            TextAnnotation ta => TextAnnotationPainter.Measure(ta),
            StepNumberAnnotation sn => new RectangleF(sn.Pos.X - 16, sn.Pos.Y - 16, 32, 32),
            EmojiAnnotation em => new RectangleF(em.Pos.X - em.Size / 2f, em.Pos.Y - em.Size / 2f, em.Size, em.Size),
            MagnifierAnnotation mg => new RectangleF(mg.Pos.X - 60, mg.Pos.Y - 60, 120, 120),
            _ => RectangleF.Empty,
        };
    }

    private RectangleF GetSegmentBounds(Point from, Point to, float strokeWidth)
    {
        float pad = strokeWidth / 2f + 4f; // Extra padding for hit tolerance
        float x = Math.Min(from.X, to.X) - pad;
        float y = Math.Min(from.Y, to.Y) - pad;
        float w = Math.Abs(to.X - from.X) + pad * 2;
        float h = Math.Abs(to.Y - from.Y) + pad * 2;
        return new RectangleF(x, y, w, h);
    }

    private RectangleF GetPointsBounds(List<Point> points, float strokeWidth)
    {
        if (points.Count == 0) return RectangleF.Empty;
        float pad = strokeWidth / 2f + 4f;
        float minX = points.Min(p => p.X) - pad;
        float minY = points.Min(p => p.Y) - pad;
        float maxX = points.Max(p => p.X) + pad;
        float maxY = points.Max(p => p.Y) + pad;
        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    private int GetSelectHandle(Point screenPt, int annotationIndex)
    {
        if (annotationIndex < 0 || annotationIndex >= _annotations.Count)
            return -1;
        var selected = _annotations[annotationIndex];

        if (_isRotateMode
            && (annotationIndex == _selectedAnnotationIndex || _multiSelectedIndices.Contains(annotationIndex))
            && AnnotationTransforms.CanRotate(selected))
        {
            var corners = GetRotateModeCorners(selected, 0f);
            var pivot = new PointF(
                (corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4f,
                (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4f);
            var screenPivot = ImageToScreenF(pivot);
            for (int i = 0; i < 4; i++)
            {
                var sc = ImageToScreenF(corners[i]);
                var arrow = WindowsHandleRenderer.RotateArrowCenter(sc, screenPivot, 1f);
                if (WindowsHandleRenderer.RotateArrowHitRect(arrow, 1f).Contains(screenPt))
                    return i;
            }
            var spivot = Point.Round(screenPivot);
            if (WindowsHandleRenderer.CenterPlusHitRect(spivot).Contains(screenPt))
                return 8;
            return -1;
        }
        var bounds = GetAnnotationVisualBounds(selected);
        var screenRect = Rectangle.Round(ImageToScreenRect(bounds));
        var selRect = Rectangle.Inflate(screenRect, 4, 4);
        bool isActiveSelection = annotationIndex == _selectedAnnotationIndex
            || _multiSelectedIndices.Contains(annotationIndex);
        // Non-resizable items expose only the center move knob (handle 8), never a resize handle.
        // Hover chrome is wrap-only, so resize grips are hit-tested only while selected.
        if (IsResizable(selected) && isActiveSelection)
        {
            // 4 Corners: 0: TL, 1: TR, 2: BL, 3: BR
            var corners = new[] {
                new Point(selRect.X, selRect.Y),
                new Point(selRect.Right - 1, selRect.Y),
                new Point(selRect.X, selRect.Bottom - 1),
                new Point(selRect.Right - 1, selRect.Bottom - 1)
            };
            for (int i = 0; i < 4; i++)
            {
                var hr = WindowsHandleRenderer.HitRect(corners[i]);
                if (hr.Contains(screenPt)) return i;
            }

            // Mid-edges only if screen size >= 56px
            if (screenRect.Width >= 56)
            {
                var topHr = WindowsHandleRenderer.HitRect(new Point(selRect.X + selRect.Width / 2, selRect.Y));
                if (topHr.Contains(screenPt)) return 4; // Top
                var btmHr = WindowsHandleRenderer.HitRect(new Point(selRect.X + selRect.Width / 2, selRect.Bottom - 1));
                if (btmHr.Contains(screenPt)) return 7; // Bottom
            }
            if (screenRect.Height >= 56)
            {
                var leftHr = WindowsHandleRenderer.HitRect(new Point(selRect.X, selRect.Y + selRect.Height / 2));
                if (leftHr.Contains(screenPt)) return 5; // Left
                var rightHr = WindowsHandleRenderer.HitRect(new Point(selRect.Right - 1, selRect.Y + selRect.Height / 2));
                if (rightHr.Contains(screenPt)) return 6; // Right
            }
        }

        if ((annotationIndex == _selectedAnnotationIndex || _multiSelectedIndices.Contains(annotationIndex))
            && WindowsHandleRenderer.FitsCenterPlus(selRect.Width, selRect.Height))
        {
            var center = new Point(selRect.X + selRect.Width / 2, selRect.Y + selRect.Height / 2);
            if (WindowsHandleRenderer.CenterPlusHitRect(center).Contains(screenPt))
                return 8;
        }

        return -1;
    }

    private PointF[] GetRotateModeCorners(Annotation a, float pad) =>
        AnnotationTransforms.GetRotateHandleCorners(a, pad);

    private void BeginRotateDrag(int idx, Point img)
    {
        var a = _annotations[idx];
        _rotatePivot = AnnotationTransforms.PivotOf(a);
        _rotateOriginal = a;
        _rotateStartDegrees = MathF.Atan2(img.Y - _rotatePivot.Y, img.X - _rotatePivot.X) * 180f / MathF.PI;
        _isRotating = true;
        _pendingRotateToggle = false;
        CancelRotateToggleTimer();
    }

    private void ExitRotateMode(bool invalidate = true)
    {
        CancelRotateToggleTimer();
        _pendingRotateToggle = false;
        bool was = _isRotateMode || _isRotating;
        _isRotateMode = false;
        _isRotating = false;
        _rotateOriginal = null;
        if (invalidate && was)
            Invalidate();
    }

    private void ArmRotateToggle()
    {
        CancelRotateToggleTimer();
        if (_selectedAnnotationIndex < 0 || _selectedAnnotationIndex >= _annotations.Count)
            return;
        if (!AnnotationTransforms.CanRotate(_annotations[_selectedAnnotationIndex]))
            return;
        _isRotateMode = !_isRotateMode;
        Invalidate();
    }

    private void CancelRotateToggleTimer()
    {
        if (_rotateToggleTimer is null) return;
        _rotateToggleTimer.Stop();
        _rotateToggleTimer.Dispose();
        _rotateToggleTimer = null;
    }
}
