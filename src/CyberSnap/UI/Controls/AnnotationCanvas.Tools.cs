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

    // Canvas resize via the square handles floating outside the image (screen-space).
    // How far outside the image edge the square handles float, in screen pixels. Kept well
    // clear of the crop L-handles (which sit on the image edge) so the two don't get confused.
    private const float ResizeHandleOffset = 28f;
    private const float ResizeHandleSize = 11f;   // side length of each square handle (screen px)
    private const float ResizeHitRadius = 9f;
    // Amber/gold — a warm hue that can't be confused with the bright cyan crop handles, and
    // reads clearly against the dark canvas. Distinguishes the canvas-resize squares at a glance.
    private static readonly Color ResizeAccent = Color.FromArgb(255, 255, 176, 32);
    private bool _resizeDragging;
    private int _activeResizeHandle = -1;          // 0..7, same indexing as crop handles
    private Point _resizeStartImg;                 // image-space mouse at drag start
    private Size _resizeStartSize;                 // bitmap size at drag start
    private Size _resizePreviewSize;               // pending new size while dragging
    private RectangleF _resizePreviewRect;         // screen-space outline shown while dragging

    // Inline text editor
    private TextBox? _inlineTextBox;
    private Point _inlineTextOrigin;

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
        ZoomFit();
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

    private void CancelInProgressTool()
    {
        if (_isDragging || _currentStroke is not null)
        {
            _isDragging = false;
            _currentStroke = null;
            Invalidate();
        }
        if (_selectedAnnotationIndex >= 0 && !_isDragging && _preSpaceTool == null)
        {
            _selectedAnnotationIndex = -1;
            _selectOriginalAnnotation = null;
            Invalidate();
        }
        _isSelectResizing = false;
        _selectResizeHandle = -1;
        _selectResizeOriginalAnnotation = null;
        _isMarqueeSelecting = false;
        ClearMultiSelection();
        _multiDragOriginals = null;
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
        if (_isTempMoveFromPan)
        {
            _isTempMoveFromPan = false;
            _activeTool = CanvasTool.Pan;
        }
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
            CanvasTool.Text => Cursors.IBeam,
            CanvasTool.Eraser => CursorFactory.EraserCursor,
            // The step badge ghost is centered on the cursor and acts as the pointer itself,
            // so hide the OS crosshair (it would otherwise sit on top of the number).
            CanvasTool.StepNumber => CursorFactory.HiddenCursor,
            _ => CursorFactory.PrecisionCursor,
        };
    }

    // ── Mouse routing ──────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // While editing text, the floating toolbar gets first dibs on left clicks so
        // toggling format doesn't steal focus from the text box or commit the text.
        if (e.Button == MouseButtons.Left && HandleTextToolbarMouseDown(e.Location))
        {
            _inlineTextBox?.Focus();
            return;
        }

        Focus();

        // Square resize handles float outside the image; they take priority over the crop
        // handles (which sit on the image edge) and over starting a stroke in the margin.
        if (e.Button == MouseButtons.Left && EditorShowResizeHandles && _baseBitmap != null &&
            _activeTool != CanvasTool.Crop && _preSpaceTool == null)
        {
            int hit = HitTestResizeHandle(e.Location);
            if (hit >= 0)
            {
                _resizeDragging = true;
                _userPanned = true; // Dismiss the welcome banner on first resize drag.
                _activeResizeHandle = hit;
                _resizeStartImg = ScreenToImage(e.Location);
                _resizeStartSize = new Size(_baseBitmap.Width, _baseBitmap.Height);
                _resizePreviewSize = _resizeStartSize;
                _resizePreviewRect = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
                Capture = true;
                return;
            }
        }

        if (e.Button == MouseButtons.Left && EditorAutoCropControls && _cropHasRect && _activeTool != CanvasTool.Crop)
        {
            var screenPt = e.Location;
            var cropScreen = ImageToScreenRect(_cropRect);
            var handles = GetCropHandlePositionsScreen(cropScreen);
            int hitHandle = -1;
            const float hitRadius = 9f;
            for (int i = 0; i < handles.Length; i++)
            {
                var h = handles[i];
                if (Math.Abs(screenPt.X - h.X) <= hitRadius && Math.Abs(screenPt.Y - h.Y) <= hitRadius)
                {
                    hitHandle = i;
                    break;
                }
            }

            if (hitHandle >= 0 && hitHandle <= 7)
            {
                ActiveTool = CanvasTool.Crop;
            }
        }

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
            _viewFitsWindow = false;
            _panStart = e.Location;
            _panStartOffset = _pan;
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (IsDefaultBlank)
        {
            IsDefaultBlank = false;
            Invalidate();
        }

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
            
            int activeHoverIdx = _moveHoverIndex;
            if (activeHoverIdx < 0)
            {
                activeHoverIdx = HitTestAnnotation(img);
            }
            // Don't let a click grab the annotation we just placed (cursor is still on it);
            // that would turn a second placement into an accidental move.
            if (activeHoverIdx == _suppressHoverIndex)
                activeHoverIdx = -1;

            if (handle < 0 && activeHoverIdx >= 0)
            {
                handle = GetSelectHandle(e.Location, activeHoverIdx);
                if (handle >= 0) clickedIdx = activeHoverIdx;
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

            if (clickedIdx >= 0)
            {
                if (_activeTool == CanvasTool.Pan)
                {
                    _isTempMoveFromPan = true;
                }
                ActiveTool = CanvasTool.Move;
                // Ctrl+Click: toggle multi-selection
                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    ToggleMultiSelect(clickedIdx);
                    Invalidate();
                    return;
                }
                ClearMultiSelection();
                _selectedAnnotationIndex = clickedIdx;
                if (handle >= 0 && handle != 8)
                {
                    // A resize handle (corners/edges) → resize.
                    _isSelectResizing = true;
                    _selectResizeHandle = handle;
                    _selectDragStartImg = img;
                    _selectHandleBounds = Rectangle.Round(GetAnnotationVisualBounds(_annotations[clickedIdx]));
                    _selectResizeOriginalAnnotation = _annotations[clickedIdx];
                    _isDragging = true;
                }
                else
                {
                    // Center move knob or plain body click → select and immediately start moving it from its surface!
                    _selectOriginalAnnotation = _annotations[clickedIdx];
                    _selectDragStartImg = img;
                    _isDragging = true;
                }
            }
        }

        switch (_activeTool)
        {
            case CanvasTool.Draw:
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
                _dragStartImg = img;
                _dragLastImg = img;
                _isDragging = true;
                Invalidate();
                break;
            case CanvasTool.Eraser:
                TryEraseAnnotationAt(img);
                break;
            case CanvasTool.CurvedArrow:
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
                if (handle >= 0 && handle != 8 && targetIdx >= 0)
                {
                    ClearMultiSelection();
                    _selectedAnnotationIndex = targetIdx;
                    _isSelectResizing = true;
                    _selectResizeHandle = handle;
                    _selectDragStartImg = img;
                    _selectHandleBounds = Rectangle.Round(GetAnnotationVisualBounds(_annotations[targetIdx]));
                    _selectResizeOriginalAnnotation = _annotations[targetIdx];
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
                        ClearMultiSelection();
                        _selectedAnnotationIndex = targetIdx;
                        _selectOriginalAnnotation = _annotations[targetIdx];
                        _selectDragStartImg = img;
                        _isDragging = true;
                    }
                }
                else
                {
                    // Click on empty space: clear everything.
                    ClearMultiSelection();
                    _selectedAnnotationIndex = -1;
                    _selectOriginalAnnotation = null;

                    _isMarqueeSelecting = true;
                    _marqueeStartImg = img;
                    _marqueeEndImg = img;
                    Capture = true;
                }
                Invalidate();
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
                    else if (cropScreen.Contains(screenPt))
                    {
                        _activeCropHandle = 8; // Move
                        _cropDragging = true;
                        _cropDragStartImg = img;
                        _cropDragStartRect = _cropRect;
                    }
                    else
                    {
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
            case CanvasTool.Text:
                BeginInlineText(img);
                break;
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
        base.OnMouseMove(e);

        // Track the pointer so the floating tool chip can follow it. Repaint on plain
        // hover (the draw/shape tools otherwise only update the cursor here) so the chip
        // keeps up; redundant Invalidate calls within one message coalesce into one paint.
        _cursorClient = e.Location;
        _cursorOnCanvas = true;

        UpdateResizeHandlesHover();

        if (!_isDragging && !_isPanning && !_cropDragging && !_resizeDragging && _preSpaceTool == null
            && ToolShowsCursorChip(_activeTool))
            Invalidate();

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

        // Dragging the inline text box by its toolbar grip
        if (_textGripDragging && _inlineTextBox is not null)
        {
            int nx = e.X - _textGripDragOffset.X;
            int ny = e.Y - _textGripDragOffset.Y;
            _inlineTextOrigin = ScreenToImage(new Point(nx, ny));
            Invalidate();
            return;
        }

        // Hovering the floating text toolbar (skip normal tool hover when over it)
        if (_inlineTextBox is not null && UpdateTextToolbarHover(e.Location))
            return;

        if (!_isDragging && !_cropDragging && (_activeTool == CanvasTool.Pan || _activeTool == CanvasTool.Move))
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
        else if (!_isDragging && !_cropDragging && !_resizeDragging)
        {
            if (EditorShowResizeHandles && _baseBitmap != null && _preSpaceTool == null)
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
            if (EditorAutoCropControls && _cropHasRect && _preSpaceTool == null)
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

                if (hitHandle >= 0 && hitHandle <= 7)
                {
                    Cursor = hitHandle switch
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
                        0 or 3 => Cursors.SizeNWSE,
                        1 or 2 => Cursors.SizeNESW,
                        4 or 7 => Cursors.SizeNS,
                        5 or 6 => Cursors.SizeWE,
                        8       => Cursors.SizeAll,  // center move knob
                        _       => Cursors.Default
                    };
                    return;
                }

                // Hand cursor only over the object's actual drawn pixels (its surface), never
                // over the empty interior of its wrap box. Controls are already handled above.
                int hoverIdx = _moveHoverIndex >= 0 ? _moveHoverIndex : _selectedAnnotationIndex;
                if (hoverIdx >= 0 && hoverIdx < _annotations.Count
                    && IsOverAnnotationSurface(_annotations[hoverIdx], ScreenToImage(e.Location)))
                {
                    Cursor = Cursors.Hand;
                    return;
                }
            }
            UpdateCursor();
        }

        if (!_isDragging && !_cropDragging) return;

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

        switch (_activeTool)
        {
            case CanvasTool.Move when _isSelectResizing && _selectedAnnotationIndex >= 0 && _selectResizeOriginalAnnotation is not null:
                int rdx = img.X - _selectDragStartImg.X;
                int rdy = img.Y - _selectDragStartImg.Y;
                var ob = _selectHandleBounds;
                Rectangle nb = _selectResizeHandle switch
                {
                    0 => Rectangle.FromLTRB(ob.Left + rdx, ob.Top + rdy, ob.Right, ob.Bottom),  // TL
                    1 => Rectangle.FromLTRB(ob.Left, ob.Top + rdy, ob.Right + rdx, ob.Bottom),  // TR
                    2 => Rectangle.FromLTRB(ob.Left + rdx, ob.Top, ob.Right, ob.Bottom + rdy),  // BL
                    3 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right + rdx, ob.Bottom + rdy),  // BR
                    4 => Rectangle.FromLTRB(ob.Left, ob.Top + rdy, ob.Right, ob.Bottom),       // Top
                    5 => Rectangle.FromLTRB(ob.Left + rdx, ob.Top, ob.Right, ob.Bottom),       // Left
                    6 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right + rdx, ob.Bottom),       // Right
                    7 => Rectangle.FromLTRB(ob.Left, ob.Top, ob.Right, ob.Bottom + rdy),       // Bottom
                    _ => ob
                };
                if (nb.Width > 5 && nb.Height > 5)
                {
                    _annotations[_selectedAnnotationIndex] = AnnotationTransforms.Scale(_selectResizeOriginalAnnotation, ob, nb);
                }
                Invalidate();
                break;
            case CanvasTool.Move when _multiDragOriginals is not null && _multiSelectedIndices.Count > 1:
                int mdx = img.X - _multiDragStartImg.X;
                int mdy = img.Y - _multiDragStartImg.Y;
                foreach (var (mi, orig) in _multiDragOriginals)
                {
                    if (mi >= 0 && mi < _annotations.Count)
                        _annotations[mi] = AnnotationTransforms.Translate(orig, mdx, mdy);
                }
                Invalidate();
                break;
            case CanvasTool.Move when _selectedAnnotationIndex >= 0 && _selectOriginalAnnotation is not null:
                int dx = img.X - _selectDragStartImg.X;
                int dy = img.Y - _selectDragStartImg.Y;
                _annotations[_selectedAnnotationIndex] = AnnotationTransforms.Translate(_selectOriginalAnnotation, dx, dy);
                Invalidate();
                break;
            case CanvasTool.Draw:
            case CanvasTool.CurvedArrow:
                if (_currentStroke is not null && (img != _currentStroke[^1]))
                {
                    _currentStroke.Add(img);
                    Invalidate();
                }
                break;
            case CanvasTool.Arrow:
            case CanvasTool.Line:
            case CanvasTool.Rect:
            case CanvasTool.Circle:
            case CanvasTool.Highlight:
            case CanvasTool.Blur:
                _dragLastImg = img;
                Invalidate();
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

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

        if (_textGripDragging)
        {
            _textGripDragging = false;
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
            int handle = _activeResizeHandle;
            var size = _resizePreviewSize;
            bool changed = size.Width != _resizeStartSize.Width || size.Height != _resizeStartSize.Height;

            // Show confirmation while the preview is still visible (don't clear
            // _resizeDragging yet so the dashed outline and size badge stay on screen).
            if (changed && ConfirmResizeByHandle != null && !ConfirmResizeByHandle(size.Width, size.Height))
            {
                // User cancelled — tear down the drag state and restore the original view.
                _resizeDragging = false;
                _activeResizeHandle = -1;
                Capture = false;
                OnStateChanged();
                Invalidate();
                return;
            }

            _resizeDragging = false;
            _activeResizeHandle = -1;
            Capture = false;

            if (changed)
            {
                // Default: drag extends/trims the canvas area, anchoring the content at the
                // edge opposite the dragged handle. With ResizeHandlesScaleContent on, the
                // content is resampled instead.
                ResizeCanvas(size.Width, size.Height, ResizeHandlesScaleContent, AnchorForHandle(handle));
            }
            else
            {
                OnStateChanged();
                Invalidate();
            }
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

        if (!_isDragging) return;
        _isDragging = false;

        switch (_activeTool)
        {
            case CanvasTool.Move when _isSelectResizing && _selectedAnnotationIndex >= 0 && _selectResizeOriginalAnnotation is not null:
                var scaled = _annotations[_selectedAnnotationIndex];
                if (!Equals(_selectResizeOriginalAnnotation, scaled))
                {
                    Push(new ReplaceAnnotationCommand(_selectedAnnotationIndex, _selectResizeOriginalAnnotation, scaled));
                }
                _isSelectResizing = false;
                _selectResizeHandle = -1;
                _selectResizeOriginalAnnotation = null;
                Invalidate();
                break;
            case CanvasTool.Move when _multiDragOriginals is not null && _multiSelectedIndices.Count > 1:
                int mtdx = 0, mtdy = 0;
                // Compute delta from any of the originals.
                if (_multiDragOriginals.Count > 0)
                {
                    var (firstIdx, firstOrig) = _multiDragOriginals[0];
                    if (firstIdx >= 0 && firstIdx < _annotations.Count)
                        (mtdx, mtdy) = ComputeTranslationDelta(firstOrig, _annotations[firstIdx]);
                }
                if (mtdx != 0 || mtdy != 0)
                {
                    Push(new TransformMultipleAnnotationsCommand(_multiDragOriginals, mtdx, mtdy));
                }
                _multiDragOriginals = null;
                break;
            case CanvasTool.Move when _selectedAnnotationIndex >= 0 && _selectOriginalAnnotation is not null:
                var moved = _annotations[_selectedAnnotationIndex];
                int tdx = 0, tdy = 0;
                // Compute the actual translation by diffing the moved annotation against the original.
                // For rect-based annotations we diff the Rect location; for point-based we diff the first point.
                (tdx, tdy) = ComputeTranslationDelta(_selectOriginalAnnotation, moved);
                if (tdx != 0 || tdy != 0)
                {
                    Push(new TransformAnnotationCommand(_selectOriginalAnnotation, _selectedAnnotationIndex, tdx, tdy));
                }
                _selectOriginalAnnotation = null; // command now owns the original reference
                break;
            case CanvasTool.Draw when _currentStroke is { Count: >= 2 }:
                Push(new AddAnnotationCommand(new DrawStroke(_currentStroke, ToolColor, StrokeWidth)));
                _currentStroke = null;
                break;
            case CanvasTool.Draw:
                _currentStroke = null;
                Invalidate();
                break;
            case CanvasTool.Arrow when _dragStartImg != _dragLastImg:
                Push(new AddAnnotationCommand(new ArrowAnnotation(_dragStartImg, _dragLastImg, ToolColor, StrokeWidth)));
                break;
            case CanvasTool.Line when _dragStartImg != _dragLastImg:
                Push(new AddAnnotationCommand(new LineAnnotation(_dragStartImg, _dragLastImg, ToolColor, StrokeWidth)));
                break;
            case CanvasTool.Rect:
                var rect = NormRect(_dragStartImg, _dragLastImg);
                if (rect.Width >= 4 && rect.Height >= 4)
                    Push(new AddAnnotationCommand(new RectShapeAnnotation(rect, ToolColor, StrokeWidth)));
                Invalidate();
                break;
            case CanvasTool.Circle:
                var crect = NormRect(_dragStartImg, _dragLastImg);
                if (crect.Width >= 4 && crect.Height >= 4)
                    Push(new AddAnnotationCommand(new CircleShapeAnnotation(crect, ToolColor, StrokeWidth)));
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
                Push(new AddAnnotationCommand(new CurvedArrowAnnotation(_currentStroke, ToolColor, StrokeWidth)));
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

        if (_isTempMoveFromPan)
        {
            _isTempMoveFromPan = false;
            ActiveTool = CanvasTool.Pan;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
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

    /// <summary>Suppresses the hover/control box for the annotation just placed, until the
    /// cursor leaves it (so the box appears only on a deliberate re-hover).</summary>
    private void SuppressHoverForLastPlaced()
    {
        _suppressHoverIndex = _annotations.Count - 1;
        _moveHoverIndex = -1;
    }

    private void UpdateMoveHover(Point img)
    {
        int hitIdx = HitTestAnnotation(img);
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

        // While editing text, the wheel adjusts the font size instead of zooming.
        if (_inlineTextBox is not null)
        {
            AdjustTextFontSize(e.Delta > 0 ? 2f : -2f);
            return;
        }

        // With the Emoji tool active, the wheel sizes the emoji to be placed (matches
        // the capture overlay); zoom is unaffected for every other tool.
        if (_activeTool == CanvasTool.Emoji)
        {
            EmojiPlaceSize = _emojiPlaceSize + (e.Delta > 0 ? 4f : -4f);
            ShowToolBanner($"Emoji size: {(int)_emojiPlaceSize}px");
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

        if (e.KeyCode == Keys.Space && _preSpaceTool != null)
        {
            _isPanning = false;

            ActiveTool = _preSpaceTool.Value;
            _preSpaceTool = null;

            e.Handled = true;
        }
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
            _isSelectResizing = false;
            _selectResizeHandle = -1;

            ActiveTool = CanvasTool.Pan;

            if (Control.MouseButtons == MouseButtons.Left)
            {
                _isPanning = true;
                _userPanned = true;
                _viewFitsWindow = false;
                _panStart = PointToClient(Cursor.Position);
                _panStartOffset = _pan;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Space && _inlineTextBox is null)
        {
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
        if (e.KeyCode == Keys.Enter && _activeTool == CanvasTool.Crop && _cropHasRect)
        {
            TryConfirmCrop();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            // Esc mirrors the right-click escape: cancel any in-progress action and
            // deselect the active tool back to neutral Pan. When already neutral, just
            // clear any stray in-progress/crop state.
            if (!TryDeselectTool())
            {
                CancelInProgressTool();
                CancelCropPending();
            }
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
        if (e.KeyCode is Keys.Oemplus or Keys.Add)
        {
            ZoomBy(1.15, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.OemMinus or Keys.Subtract)
        {
            ZoomBy(1.0 / 1.15, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)
        {
            ZoomReset();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.F2)
        {
            ZoomFit();
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

        switch (_activeTool)
        {
            case CanvasTool.Draw when _currentStroke is { Count: >= 2 }:
                SketchRenderer.DrawFreehandStroke(g, _currentStroke, ToolColor, GetScaledStrokeWidth(StrokeWidth), AnnotationStrokeShadow);
                break;
            case CanvasTool.Arrow:
                SketchRenderer.DrawArrow(g, _dragStartImg, _dragLastImg, ToolColor,
                    _dragStartImg.GetHashCode(), strokeShadow: AnnotationStrokeShadow, strokeWidth: GetScaledStrokeWidth(StrokeWidth));
                break;
            case CanvasTool.Line:
                SketchRenderer.DrawLine(g, _dragStartImg, _dragLastImg, ToolColor,
                    _dragStartImg.GetHashCode(), AnnotationStrokeShadow, GetScaledStrokeWidth(StrokeWidth));
                break;
            case CanvasTool.Rect:
                var rect = NormRect(_dragStartImg, _dragLastImg);
                if (rect.Width > 0 && rect.Height > 0)
                    SketchRenderer.DrawRectShape(g, rect, ToolColor, AnnotationStrokeShadow, GetScaledStrokeWidth(StrokeWidth));
                break;
            case CanvasTool.Circle:
                var crect = NormRect(_dragStartImg, _dragLastImg);
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

            using var font = new Font("Segoe UI Variable Text", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
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
        bool showDefaultControls = EditorAutoCropControls && _cropHasRect;
        if (_activeTool != CanvasTool.Crop && !showDefaultControls) return;
        if (!_cropDragging && !_cropHasRect) return;
        if (_cropRect.Width <= 0 || _cropRect.Height <= 0) return;

        var imgRect = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        var cropScreen = ImageToScreenRect(_cropRect);

        if (_activeTool == CanvasTool.Crop)
        {
            using (var dark = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            using (var region = new Region(imgRect))
            {
                region.Exclude(cropScreen);
                g.FillRegion(dark, region);
            }
        }

        if (_activeTool == CanvasTool.Crop)
        {
            using (var shadowPen = new Pen(Color.FromArgb(120, 0, 0, 0), 1.5f))
            using (var borderPen = new Pen(Color.FromArgb(255, 0, 255, 255), 1.5f) { DashStyle = DashStyle.Dash })
            {
                g.DrawRectangle(shadowPen, cropScreen.X + 1f, cropScreen.Y + 1f, cropScreen.Width, cropScreen.Height);
                g.DrawRectangle(borderPen, cropScreen.X, cropScreen.Y, cropScreen.Width, cropScreen.Height);
            }
        }

        if (_cropHasRect && _preSpaceTool == null)
            DrawCropHandles(g, cropScreen);
    }

    private static void DrawCropHandles(Graphics g, RectangleF rect)
    {
        // Modern premium crop handles: L-shaped corners and pill-shaped edge bars.
        var accent = Color.FromArgb(255, 0, 255, 255);
        var shadow = Color.FromArgb(100, 0, 0, 0);

        using var thickPen = new Pen(accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shadowPen = new Pen(shadow, 5.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Corner line length
        const float len = 12f;
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

    // ── Canvas resize handles (square, float outside the image) ─────────────

    /// <summary>The 8 resize handle centers in screen space, laid out on a rectangle inflated
    /// outward from the image edge. Indexing matches the crop handles: 0=TL,1=TR,2=BL,3=BR,
    /// 4=Top,5=Right,6=Bottom,7=Left.</summary>
    private PointF[] GetResizeHandlePositionsScreen()
    {
        var img = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        var r = RectangleF.Inflate(img, ResizeHandleOffset, ResizeHandleOffset);
        return new PointF[]
        {
            new(r.Left, r.Top),
            new(r.Right, r.Top),
            new(r.Left, r.Bottom),
            new(r.Right, r.Bottom),
            new(r.Left + r.Width / 2f, r.Top),
            new(r.Right, r.Top + r.Height / 2f),
            new(r.Left + r.Width / 2f, r.Bottom),
            new(r.Left, r.Top + r.Height / 2f),
        };
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
        int startW = _resizeStartSize.Width;
        int startH = _resizeStartSize.Height;

        bool affectsW = _activeResizeHandle is 0 or 1 or 2 or 3 or 5 or 7;
        bool affectsH = _activeResizeHandle is 0 or 1 or 2 or 3 or 4 or 6;
        bool growLeft = _activeResizeHandle is 0 or 2 or 7;   // left edge handles invert X
        bool growTop = _activeResizeHandle is 0 or 1 or 4;     // top edge handles invert Y

        int newW = startW + (affectsW ? (growLeft ? -dx : dx) : 0);
        int newH = startH + (affectsH ? (growTop ? -dy : dy) : 0);

        bool isCorner = _activeResizeHandle is 0 or 1 or 2 or 3;
        // In scale mode, corners keep aspect ratio unless Shift is held (the axis with the
        // larger drag wins). In canvas-extend mode, corners resize each axis freely, since
        // you're adding/removing margin rather than scaling the picture. Shift inverts either.
        bool keepAspect = ResizeHandlesScaleContent ^ ModifierKeys.HasFlag(Keys.Shift);
        if (isCorner && keepAspect && startW > 0 && startH > 0)
        {
            double s = Math.Abs(dx) >= Math.Abs(dy)
                ? (double)newW / startW
                : (double)newH / startH;
            newW = (int)Math.Round(startW * s);
            newH = (int)Math.Round(startH * s);
        }

        newW = Math.Clamp(newW, MinCanvasSize, MaxCanvasSize);
        newH = Math.Clamp(newH, MinCanvasSize, MaxCanvasSize);
        _resizePreviewSize = new Size(newW, newH);

        // Build the preview outline anchored at the fixed corner/edge (opposite the dragged one).
        var imgScreen = ImageToScreenRect(new RectangleF(0, 0, startW, startH));
        float sw = (float)(newW * _zoom);
        float sh = (float)(newH * _zoom);
        float left = growLeft ? imgScreen.Right - sw : imgScreen.Left;
        float top = growTop ? imgScreen.Bottom - sh : imgScreen.Top;
        if (!affectsW) { left = imgScreen.Left; sw = imgScreen.Width; }
        if (!affectsH) { top = imgScreen.Top; sh = imgScreen.Height; }
        if (isCorner)
        {
            left = growLeft ? imgScreen.Right - sw : imgScreen.Left;
            top = growTop ? imgScreen.Bottom - sh : imgScreen.Top;
        }
        _resizePreviewRect = new RectangleF(left, top, sw, sh);

        OnStateChanged(); // refresh the live status-bar hint with the pending size
        Invalidate();
    }

    /// <summary>The content anchor for a canvas-extend drag: the edge/corner opposite the
    /// dragged handle stays put while the canvas grows or shrinks toward the handle.</summary>
    private static Models.Commands.AnchorPosition AnchorForHandle(int handle) => handle switch
    {
        0 => Models.Commands.AnchorPosition.BottomRight, // dragged TL → anchor BR
        1 => Models.Commands.AnchorPosition.BottomLeft,  // dragged TR → anchor BL
        2 => Models.Commands.AnchorPosition.TopRight,    // dragged BL → anchor TR
        3 => Models.Commands.AnchorPosition.TopLeft,     // dragged BR → anchor TL
        4 => Models.Commands.AnchorPosition.Bottom,      // dragged Top → anchor Bottom
        5 => Models.Commands.AnchorPosition.Left,        // dragged Right → anchor Left
        6 => Models.Commands.AnchorPosition.Top,         // dragged Bottom → anchor Top
        7 => Models.Commands.AnchorPosition.Right,       // dragged Left → anchor Right
        _ => Models.Commands.AnchorPosition.Center,
    };

    private void RenderResizeHandles(Graphics g)
    {
        if (!EditorShowResizeHandles || _baseBitmap == null) return;
        if (_activeTool == CanvasTool.Crop || _preSpaceTool != null) return;
        if (_resizeHandlesOpacity <= 0f && !_resizeDragging) return;

        // While dragging, draw the pending-size outline plus a size badge.
        if (_resizeDragging)
        {
            using var previewShadow = new Pen(Color.FromArgb(120, 0, 0, 0), 2.5f) { DashStyle = DashStyle.Dash };
            using var previewPen = new Pen(Color.FromArgb(128, ResizeAccent.R, ResizeAccent.G, ResizeAccent.B), 1.6f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(previewShadow, _resizePreviewRect.X + 1f, _resizePreviewRect.Y + 1f, _resizePreviewRect.Width, _resizePreviewRect.Height);
            g.DrawRectangle(previewPen, _resizePreviewRect.X, _resizePreviewRect.Y, _resizePreviewRect.Width, _resizePreviewRect.Height);
            DrawResizeSizeBadge(g, _resizePreviewRect, $"{_resizePreviewSize.Width} × {_resizePreviewSize.Height}");
        }

        var handles = GetResizeHandlePositionsScreen();
        int alphaFill = (int)(128 * _resizeHandlesOpacity);
        int alphaShadow = (int)(110 * _resizeHandlesOpacity);
        if (alphaFill <= 0) return;

        var fill = Color.FromArgb(alphaFill, ResizeAccent.R, ResizeAccent.G, ResizeAccent.B);
        var shadow = Color.FromArgb(alphaShadow, 0, 0, 0);
        float half = ResizeHandleSize / 2f;

        using var fillBrush = new SolidBrush(fill);
        using var shadowBrush = new SolidBrush(shadow);
        foreach (var h in handles)
        {
            var rect = new RectangleF(h.X - half, h.Y - half, ResizeHandleSize, ResizeHandleSize);
            using (var sp = RoundedRect(new RectangleF(rect.X + 1f, rect.Y + 1f, rect.Width, rect.Height), 2.5f))
                g.FillPath(shadowBrush, sp);
            using (var fp = RoundedRect(rect, 2.5f))
                g.FillPath(fillBrush, fp);
        }
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

    // ── Inline text editor ─────────────────────────────────────────────────

    private void BeginInlineText(Point imgOrigin)
    {
        CommitOrCancelInlineText(commit: true);

        _inlineTextOrigin = imgOrigin;

        _inlineTextBox = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = ToolColor,
            Location = new Point(-100, -100),
            Size = new Size(1, 1),
            TabStop = false,
        };
        _inlineTextBox.KeyDown += InlineTextBox_KeyDown;
        _inlineTextBox.TextChanged += (_, _) => Invalidate();
        Controls.Add(_inlineTextBox);
        UpdateInlineTextBoxStyle(); // apply current bold/italic/font/size to the editor box
        _inlineTextBox.Focus();
        Invalidate();               // show the floating toolbar
    }

    private void InlineTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            CommitOrCancelInlineText(commit: false);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            CommitOrCancelInlineText(commit: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void CommitOrCancelInlineText(bool commit)
    {
        if (_inlineTextBox is null) return;
        var text = _inlineTextBox.Text;
        var origin = _inlineTextOrigin;

        Controls.Remove(_inlineTextBox);
        _inlineTextBox.Dispose();
        _inlineTextBox = null;

        if (commit && !string.IsNullOrWhiteSpace(text))
        {
            Push(new AddAnnotationCommand(new TextAnnotation(
                Pos: origin,
                Text: text,
                FontSize: _textFontSize,
                Color: ToolColor,
                Bold: _textBold,
                Italic: _textItalic,
                Stroke: _textStroke,
                Shadow: _textShadow,
                Background: _textBackground,
                FontFamily: _textFontFamily)));
        }
        _fontDropdownOpen = false;
        _hoveredTextBtn = -1;
        _textGripDragging = false;
        Focus();
        Invalidate();
    }

    // ── Hit-testing & selection helpers ────────────────────────────────────

    /// <summary>Finds the top-most annotation whose visual bounds contain <paramref name="pt"/></summary>
    private int HitTestAnnotation(Point pt)
    {
        const int tolerance = 10;
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
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
        OnStateChanged();
        Invalidate();
    }

    /// <summary>Toggles an annotation index in/out of the multi-selection set.
    /// If only a single item was selected before, it's promoted into the multi-set first.</summary>
    private void ToggleMultiSelect(int index)
    {
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

    private static bool HitTestSingle(Annotation a, Point pt, int tol)
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
            RulerAnnotation ru => DistanceToSegment(pt, ru.From, ru.To) <= tol * 2,
            CurvedArrowAnnotation ca => ca.Points.Any(p => Distance(p, pt) <= tol * 2),
            DrawStroke ds => ds.Points.Any(p => Distance(p, pt) <= tol),
            TextAnnotation ta => MeasureInlineTextRect(ta.Pos, ta.Text, ta.FontSize, ta.FontFamily, ta.Bold, ta.Italic, ta.Background).Contains(pt),
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
            CircleShapeAnnotation cs => IsOnEllipseOutline(cs.Rect, GetScaledStrokeWidth(cs.StrokeWidth), pt),
            RectShapeAnnotation rs   => IsOnRectOutline(rs.Rect, GetScaledStrokeWidth(rs.StrokeWidth), pt),
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

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_activeTool == CanvasTool.Crop && _cropHasRect)
        {
            var screenPt = PointToClient(Cursor.Position);
            var imgPt = ScreenToImage(screenPt);
            if (_cropRect.Contains(imgPt))
            {
                TryConfirmCrop();
            }
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

        if (cropScreen.Contains(screenPt))
            return Cursors.SizeAll;

        return CursorFactory.PrecisionCursor;
    }

    private bool IsDrawingOrMoveTool(CanvasTool tool)
    {
        return tool != CanvasTool.Crop && tool != CanvasTool.Eraser && (tool != CanvasTool.Pan || !PanModeLockObjects);
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
            RectShapeAnnotation rs => rs.Rect,
            CircleShapeAnnotation cs => cs.Rect,
            EraserFill ef => ef.Rect,
            ArrowAnnotation arr => GetSegmentBounds(arr.From, arr.To, GetScaledStrokeWidth(arr.StrokeWidth)),
            LineAnnotation ln => GetSegmentBounds(ln.From, ln.To, GetScaledStrokeWidth(ln.StrokeWidth)),
            RulerAnnotation ru => GetSegmentBounds(ru.From, ru.To, 6f), // Ruler stroke width ~6px
            CurvedArrowAnnotation ca => GetPointsBounds(ca.Points, GetScaledStrokeWidth(ca.StrokeWidth)),
            DrawStroke ds => GetPointsBounds(ds.Points, GetScaledStrokeWidth(ds.StrokeWidth)),
            TextAnnotation ta => MeasureInlineTextRect(ta.Pos, ta.Text, ta.FontSize, ta.FontFamily, ta.Bold, ta.Italic, ta.Background),
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
        var bounds = GetAnnotationVisualBounds(selected);
        var screenRect = Rectangle.Round(ImageToScreenRect(bounds));
        var selRect = Rectangle.Inflate(screenRect, 4, 4);
        // Non-resizable items expose only the center move knob (handle 8), never a resize handle.
        if (IsResizable(selected))
        {
            var handles = new[] {
                new Point(selRect.X, selRect.Y),                           // 0: TL
                new Point(selRect.Right - 1, selRect.Y),                   // 1: TR
                new Point(selRect.X, selRect.Bottom - 1),                  // 2: BL
                new Point(selRect.Right - 1, selRect.Bottom - 1),          // 3: BR
                new Point(selRect.X + selRect.Width / 2, selRect.Y),       // 4: Top
                new Point(selRect.X, selRect.Y + selRect.Height / 2),      // 5: Left
                new Point(selRect.Right - 1, selRect.Y + selRect.Height / 2),// 6: Right
                new Point(selRect.X + selRect.Width / 2, selRect.Bottom - 1)// 7: Bottom
            };
            for (int i = 0; i < 8; i++)
            {
                var hr = WindowsHandleRenderer.HitRect(handles[i]);
                if (hr.Contains(screenPt)) return i;
            }
        }

        // Handle 8: center move knob — circular hit area sized to cover the 4-way arrow glyph.
        var center = new Point(selRect.X + selRect.Width / 2, selRect.Y + selRect.Height / 2);
        const int centerHitRadius = 14;
        int cdx = screenPt.X - center.X;
        int cdy = screenPt.Y - center.Y;
        if (cdx * cdx + cdy * cdy <= centerHitRadius * centerHitRadius)
            return 8;

        return -1;
    }
}
