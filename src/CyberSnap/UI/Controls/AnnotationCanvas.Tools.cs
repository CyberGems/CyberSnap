using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;

namespace CyberSnap.UI.Controls;

public sealed partial class AnnotationCanvas
{
    // ── In-progress tool state ─────────────────────────────────────────────

    private bool _isDragging;
    private Point _dragStartImg;
    private Point _dragLastImg;
    private List<Point>? _currentStroke;

    // Last cursor position in image space (for the Emoji placement ghost).
    private Point _hoverImg;
    private bool _hoverImgValid;

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
        if (_selectedAnnotationIndex >= 0 && !_isDragging)
        {
            _selectedAnnotationIndex = -1;
            _selectOriginalAnnotation = null;
            Invalidate();
        }
        if (_eraserHoverIndex >= 0)
        {
            _eraserHoverIndex = -1;
            Invalidate();
        }
        CommitOrCancelInlineText(commit: false);
    }

    private void UpdateCursor()
    {
        Cursor = _activeTool switch
        {
            CanvasTool.Pan => Cursors.Hand,
            CanvasTool.Select => Cursors.Default,
            CanvasTool.Crop => Cursors.Cross,
            CanvasTool.Text => Cursors.IBeam,
            CanvasTool.Eraser => CursorFactory.EraserCursor,
            _ => Cursors.Cross,
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

        if (e.Button == MouseButtons.Middle ||
            (e.Button == MouseButtons.Left && _activeTool == CanvasTool.Pan))
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
            case CanvasTool.Select:
                var hitIdx = HitTestAnnotation(img);
                if (hitIdx >= 0)
                {
                    _selectedAnnotationIndex = hitIdx;
                    _selectOriginalAnnotation = _annotations[hitIdx];
                    _selectDragStartImg = img;
                    _isDragging = true;
                }
                else
                {
                    _selectedAnnotationIndex = -1;
                    _selectOriginalAnnotation = null;
                }
                Invalidate();
                break;
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
                int next = _annotations.OfType<StepNumberAnnotation>()
                    .Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
                Push(new AddAnnotationCommand(new StepNumberAnnotation(img, next, ToolColor)));
                break;
            case CanvasTool.Magnifier:
                Push(new AddAnnotationCommand(new MagnifierAnnotation(img, GetMagnifierSrcRect(img))));
                break;
            case CanvasTool.Emoji:
                if (!string.IsNullOrEmpty(_selectedEmoji))
                {
                    var emojiPos = new Point(img.X - (int)(_emojiPlaceSize / 2), img.Y - (int)(_emojiPlaceSize / 2));
                    Push(new AddAnnotationCommand(new EmojiAnnotation(emojiPos, _selectedEmoji, _emojiPlaceSize)));
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

        if (!_isDragging && !_cropDragging && (_activeTool == CanvasTool.Pan || _activeTool == CanvasTool.Select))
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
                    _ => Cursors.Cross
                };
            }
            else
            {
                Cursor = GetCropCursor(e.Location);
            }
        }
        else if (!_isDragging && !_cropDragging)
        {
            if (EditorAutoCropControls && _cropHasRect)
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
            UpdateCursor();
        }

        if (!_isDragging && !_cropDragging) return;

        var img = ScreenToImage(e.Location);

        if (_cropDragging)
        {
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
            case CanvasTool.Select when _selectedAnnotationIndex >= 0 && _selectOriginalAnnotation is not null:
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
            case CanvasTool.Select when _selectedAnnotationIndex >= 0 && _selectOriginalAnnotation is not null:
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
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_eraserHoverIndex >= 0)
        {
            _eraserHoverIndex = -1;
            Invalidate();
        }
        if (_hoverImgValid)
        {
            _hoverImgValid = false;
            Invalidate();
        }
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

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
        if (e.KeyCode == Keys.Delete && _selectedAnnotationIndex >= 0)
        {
            DeleteAnnotationAt(_selectedAnnotationIndex);
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

    private void RenderToolPreview(Graphics g)
    {
        // Emoji ghost follows the cursor (click-to-place, so there is no drag).
        if (_activeTool == CanvasTool.Emoji && !string.IsNullOrEmpty(_selectedEmoji) && _hoverImgValid)
        {
            var ghostPos = new Point(_hoverImg.X - (int)(_emojiPlaceSize / 2), _hoverImg.Y - (int)(_emojiPlaceSize / 2));
            PaintEmoji(g, ghostPos, _selectedEmoji, _emojiPlaceSize, 0.6f);
        }

        // Magnifier lens preview follows the cursor before the click places it.
        if (_activeTool == CanvasTool.Magnifier && _hoverImgValid)
            PaintMagnifier(g, _hoverImg, GetMagnifierSrcRect(_hoverImg), 0.65f);

        if (!_isDragging) return;

        switch (_activeTool)
        {
            case CanvasTool.Draw when _currentStroke is { Count: >= 2 }:
                SketchRenderer.DrawFreehandStroke(g, _currentStroke, ToolColor, StrokeWidth, AnnotationStrokeShadow);
                break;
            case CanvasTool.Arrow:
                SketchRenderer.DrawArrow(g, _dragStartImg, _dragLastImg, ToolColor,
                    _dragStartImg.GetHashCode(), strokeShadow: AnnotationStrokeShadow, strokeWidth: StrokeWidth);
                break;
            case CanvasTool.Line:
                SketchRenderer.DrawLine(g, _dragStartImg, _dragLastImg, ToolColor,
                    _dragStartImg.GetHashCode(), AnnotationStrokeShadow, StrokeWidth);
                break;
            case CanvasTool.Rect:
                var rect = NormRect(_dragStartImg, _dragLastImg);
                if (rect.Width > 0 && rect.Height > 0)
                    SketchRenderer.DrawRectShape(g, rect, ToolColor, AnnotationStrokeShadow, StrokeWidth);
                break;
            case CanvasTool.Circle:
                var crect = NormRect(_dragStartImg, _dragLastImg);
                if (crect.Width > 0 && crect.Height > 0)
                    SketchRenderer.DrawCircleShape(g, crect, ToolColor, AnnotationStrokeShadow, StrokeWidth);
                break;
            case CanvasTool.CurvedArrow when _currentStroke is { Count: >= 2 }:
                SketchRenderer.DrawCurvedArrow(g, _currentStroke, ToolColor, _currentStroke.Count * 7919, AnnotationStrokeShadow, StrokeWidth);
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

        if (_cropHasRect)
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
            EmojiAnnotation em => Distance(em.Pos, pt) <= tol * 3,
            MagnifierAnnotation mg => Distance(mg.Pos, pt) <= tol * 4,
            _ => false,
        };
    }

    private static Rectangle InflateRect(Rectangle r, int x, int y) =>
        Rectangle.Inflate(r, x, y);

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
        if (!_cropHasRect) return Cursors.Cross;

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
                    _ => Cursors.Cross
                };
            }
        }

        if (cropScreen.Contains(screenPt))
            return Cursors.SizeAll;

        return Cursors.Cross;
    }
}
