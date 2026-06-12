using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    public CaptureMode CurrentMode => _mode;
    public void SetShowToolNumberBadges(bool show)
    {
        _showToolNumberBadges = show;
        RefreshToolbar();
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowCrosshairGuides { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AnnotationStrokeShadow { get; set; } = true;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float StrokeWidth
    {
        get => _strokeWidth;
        set
        {
            if (Math.Abs(_strokeWidth - value) < 0.01f) return;
            _strokeWidth = value;
            StrokeWidthChanged?.Invoke(value);
            RefreshToolbar();
        }
    }

    public event Action<float>? StrokeWidthChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DetectWindows { get; set; } = true;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowCaptureMagnifier { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public CaptureDockSide CaptureDockSide { get; set; } = CaptureDockSide.Top;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double UiScale
    {
        get => Helpers.UiChrome.UiScale;
        set
        {
            Helpers.UiChrome.SetUiScale(value);
            RefreshToolbar();
        }
    }

    private bool IsVerticalDock => CaptureDockSide is CaptureDockSide.Left or CaptureDockSide.Right;
    private bool IsBottomDock => CaptureDockSide == CaptureDockSide.Bottom;
    private bool IsTopDock => CaptureDockSide == CaptureDockSide.Top;
    private bool IsLeftDock => CaptureDockSide == CaptureDockSide.Left;
    private bool IsRightDock => CaptureDockSide == CaptureDockSide.Right;

    public void SetEnabledTools(List<string>? enabledIds)
    {
        var flyoutIds = ToolDef.FlyoutToolIds();
        if (enabledIds == null)
        {
            var defaultEnabled = ToolDef.DefaultEnabledIds();
            _visibleTools = ToolDef.AllTools.Where(t => defaultEnabled.Contains(t.Id)).ToArray();
        }
        else
        {
            if (enabledIds.Contains("record") && !enabledIds.Contains("recordGif"))
            {
                enabledIds.Add("recordGif");
            }
            _visibleTools = ToolDef.AllTools.Where(t => enabledIds.Contains(t.Id)).ToArray();
        }

        _mainBarTools = _visibleTools.Where(t => !flyoutIds.Contains(t.Id)).ToArray();
        _flyoutTools = _visibleTools.Where(t => flyoutIds.Contains(t.Id)).ToArray();
        RefreshToolbar();
    }

    // All system fonts, cached once
    private static string[]? _allSystemFonts;
    private static string[] GetSystemFonts()
    {
        if (_allSystemFonts != null) return _allSystemFonts;
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        _allSystemFonts = fonts.Families
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return _allSystemFonts;
    }

    private string[] GetFilteredFonts()
    {
        if (_filteredFonts != null) return _filteredFonts;
        var all = GetSystemFonts();
        if (string.IsNullOrEmpty(_fontSearch))
        {
            _filteredFonts = all;
            return _filteredFonts;
        }
        var terms = _fontSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _filteredFonts = all.Where(f =>
        {
            foreach (var term in terms)
                if (f.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            return true;
        }).ToArray();
        return _filteredFonts;
    }

    private Rectangle GetOverlayUiBounds()
    {
        Rectangle bounds = Rectangle.Empty;
        static Rectangle InflateIfNeeded(Rectangle r, int pad)
        {
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            r.Inflate(pad, pad);
            return r;
        }

        void Add(Rectangle r)
        {
            if (r.IsEmpty) return;
            bounds = bounds.IsEmpty ? r : Rectangle.Union(bounds, r);
        }

        Add(InflateIfNeeded(_toolbarRect, Helpers.UiChrome.ScaleInt(12)));
        Add(InflateForRepaint(Rectangle.Round(GetTextToolbarBounds())));
        Add(InflateForRepaint(Rectangle.Round(GetActiveTextRect())));
        Add(InflateIfNeeded(GetColorPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
        Add(InflateIfNeeded(GetFontPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
        Add(InflateIfNeeded(GetEmojiPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
        if (_altCapturePopupOpen)
        {
            Add(InflateIfNeeded(_altCaptureButtonRect, Helpers.UiChrome.ScaleInt(12)));
        }
        return bounds;
    }

    private bool IsPointInOverlayUi(Point p)
    {
        if (IsPointInToolbarChrome(p)) return true;
        if (_emojiPickerOpen && _emojiPickerRect.Contains(p)) return true;
        if (_fontPickerOpen && _fontPickerRect.Contains(p)) return true;
        if (_colorPickerOpen && _colorPickerRect.Contains(p)) return true;
        if (_altCapturePopupOpen && _altCaptureButtonRect.Contains(p)) return true;
        return false;
    }

    private bool IsPointInToolbarChrome(Point p)
    {
        if (!IsToolbarInteractive())
            return false;

        var tbBounds = _toolbarRect;
        tbBounds.Inflate(Helpers.UiChrome.ScaleInt(8), Helpers.UiChrome.ScaleInt(8));
        if (IsVerticalDock)
            tbBounds.Width += Helpers.UiChrome.ScaleInt(10);
        else
            tbBounds.Height += Helpers.UiChrome.ScaleInt(10);
        return tbBounds.Contains(p);
    }

    private Rectangle PositionPopupFromAnchor(Rectangle anchor, int width, int height, int gap = -1)
    {
        if (gap < 0)
            gap = Helpers.UiChrome.ScaledPopupGap;
        var clampBounds = GetToolbarAnchorClientBounds();
        int x;
        int y;

        if (IsVerticalDock)
        {
            x = IsRightDock ? anchor.X - width - gap : anchor.Right + gap;
            y = anchor.Y + (anchor.Height / 2) - (height / 2);
            var margin = Helpers.UiChrome.ScaleInt(8);
            y = Math.Clamp(y, clampBounds.Top + margin, Math.Max(clampBounds.Top + margin, clampBounds.Bottom - height - margin));
            x = Math.Clamp(x, clampBounds.Left + margin, Math.Max(clampBounds.Left + margin, clampBounds.Right - width - margin));
        }
        else
        {
            x = anchor.X + (anchor.Width / 2) - (width / 2);
            y = IsBottomDock ? anchor.Y - height - gap : anchor.Bottom + gap;
            var margin = Helpers.UiChrome.ScaleInt(8);
            x = Math.Clamp(x, clampBounds.Left + margin, Math.Max(clampBounds.Left + margin, clampBounds.Right - width - margin));
            y = Math.Clamp(y, clampBounds.Top + margin, Math.Max(clampBounds.Top + margin, clampBounds.Bottom - height - margin));
        }

        return new Rectangle(x, y, width, height);
    }

    private PointF GetTooltipOrigin(Rectangle anchor, SizeF size, float gap = -1f)
    {
        if (gap < 0)
            gap = Helpers.UiChrome.ScaleFloat(6f);
        float x;
        float y;

        if (IsVerticalDock)
        {
            x = IsRightDock ? anchor.X - size.Width - gap : anchor.Right + gap;
            y = anchor.Y + (anchor.Height / 2f) - (size.Height / 2f);
            y = Math.Clamp(y, 4f, Math.Max(4f, Height - size.Height - 4f));
        }
        else
        {
            x = anchor.X + (anchor.Width / 2f) - (size.Width / 2f);
            y = IsBottomDock ? anchor.Y - size.Height - gap : anchor.Bottom + gap;
            x = Math.Clamp(x, 4f, Math.Max(4f, Width - size.Width - 4f));
            y = Math.Clamp(y, 4f, Math.Max(4f, Height - size.Height - 4f));
        }

        return new PointF(x, y);
    }

    private bool ShouldShowCaptureMagnifierAt(Point p)
        => ShowCaptureMagnifier
           && ToolDef.IsCaptureTool(_mode)
           && !_isConfirmingSelection
           && !IsPointInOverlayUi(p);

    private Point GetReadoutCursorPoint()
        => _selectionEnd != Point.Empty ? _selectionEnd : _lastCursorPos;

    private Rectangle GetSelectionOverlayBounds(Rectangle rect, bool isOcr, bool isScan)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return Rectangle.Empty;

        var dirty = rect;
        dirty.Inflate(8, 8);

        var readoutBounds = SelectionSizeReadout.GetBounds(
            GetReadoutCursorPoint(),
            rect,
            _readoutFont,
            ClientRectangle);
        if (!readoutBounds.IsEmpty)
            dirty = Rectangle.Union(dirty, InflateForRepaint(readoutBounds, 8));

        return dirty;
    }

    private Region GetSelectionOverlayRegion(Rectangle rect, bool isOcr, bool isScan)
    {
        var region = new Region();
        region.MakeEmpty();

        if (rect.Width <= 0 || rect.Height <= 0)
            return region;

        const int borderPad = 10;
        region.Union(new Rectangle(rect.Left - borderPad, rect.Top - borderPad, rect.Width + borderPad * 2, borderPad * 2));
        region.Union(new Rectangle(rect.Left - borderPad, rect.Bottom - borderPad, rect.Width + borderPad * 2, borderPad * 2));
        region.Union(new Rectangle(rect.Left - borderPad, rect.Top - borderPad, borderPad * 2, rect.Height + borderPad * 2));
        region.Union(new Rectangle(rect.Right - borderPad, rect.Top - borderPad, borderPad * 2, rect.Height + borderPad * 2));

        var readoutBounds = SelectionSizeReadout.GetBounds(
            GetReadoutCursorPoint(),
            rect,
            _readoutFont,
            ClientRectangle);
        if (!readoutBounds.IsEmpty)
            region.Union(InflateForRepaint(readoutBounds, 8));

        return region;
    }

    private void InvalidateSelectionOverlay(Rectangle oldRect, bool oldOcr, bool oldScan, Rectangle newRect, bool newOcr, bool newScan)
    {
        using var region = GetSelectionOverlayRegion(oldRect, oldOcr, oldScan);
        using var next = GetSelectionOverlayRegion(newRect, newOcr, newScan);
        region.Union(next);
        Invalidate(region);
    }

    private bool IsSelectionCaptureMode()
        => _mode is CaptureMode.Rectangle or CaptureMode.Center or CaptureMode.Ocr or CaptureMode.Scan or CaptureMode.Sticker or CaptureMode.Upscale or CaptureMode.ScrollCapture;

    private void InvalidateAutoDetectChrome(Rectangle oldDetect, Rectangle newDetect)
    {
        if (!IsSelectionCaptureMode() || _isSelecting || _hasSelection)
            return;

        if (oldDetect.IsEmpty != newDetect.IsEmpty)
        {
            Invalidate();
            Update();
            return;
        }

        var oldDirty = InflateForRepaint(oldDetect);
        var newDirty = InflateForRepaint(newDetect);
        if (!oldDirty.IsEmpty && !newDirty.IsEmpty)
        {
            Invalidate(Rectangle.Union(oldDirty, newDirty));
            Update();
        }
        else if (!oldDirty.IsEmpty)
            Invalidate(oldDirty);
        else if (!newDirty.IsEmpty)
            Invalidate(newDirty);
    }

    private void UpdateAutoDetectRect(Point location)
    {
        if (_windowDetectionMode == WindowDetectionMode.Off)
        {
            var previousDetect = _autoDetectRect;
            _autoDetectRect = Rectangle.Empty;
            _autoDetectActive = false;
            InvalidateAutoDetectChrome(previousDetect, Rectangle.Empty);
            return;
        }

        var oldDetect = _autoDetectRect;
        var detected = WindowDetector.GetDetectionRectAtPoint(
            location, _virtualBounds, _windowDetectionMode);
        _autoDetectRect = detected;
        _autoDetectActive = detected.Width > 0 && detected.Height > 0;

        if (oldDetect == detected)
            return;

        InvalidateAutoDetectChrome(oldDetect, detected);
    }

    private void MarkCommittedAnnotationsDirty()
    {
        _committedAnnotationsDirty = true;
    }

    private IEditorContext OverlayEditContext =>
        _overlayEditorContext ??= new OverlayEditorContext(this);

    private void PushEditCommand(IEditCommand command)
    {
        command.Apply(OverlayEditContext);
        _editUndoStack.Add(command);
        ClearRedoEditHistory();
        RefreshNextStepNumber();
        MarkCommittedAnnotationsDirty();
    }

    private void ClearRedoEditHistory()
    {
        foreach (var command in _editRedoStack)
            command.Dispose();
        _editRedoStack.Clear();
    }

    private void ClearEditHistory()
    {
        foreach (var command in _editUndoStack)
            command.Dispose();
        foreach (var command in _editRedoStack)
            command.Dispose();
        _editUndoStack.Clear();
        _editRedoStack.Clear();
    }

    private void RefreshNextStepNumber()
    {
        var maxStep = _undoStack.OfType<StepNumberAnnotation>().Select(step => step.Number).DefaultIfEmpty(0).Max();
        _nextStepNumber = maxStep + 1;
    }

    private void AddAnnotation(Annotation annotation)
    {
        PushEditCommand(new AddAnnotationCommand(annotation));
    }

    /// <summary>Returns the bounding rectangle for any annotation type, for hit-testing.</summary>
    private Rectangle GetAnnotationBounds(Annotation a) => a switch
    {
        ArrowAnnotation arr => RectFromPoints(arr.From, arr.To, 8),
        CurvedArrowAnnotation ca => BoundsOfPoints(ca.Points, 8),
        LineAnnotation ln => RectFromPoints(ln.From, ln.To, 6),
        RulerAnnotation ru => GetRulerPaintBounds(ru.From, ru.To),
        DrawStroke ds => BoundsOfPoints(ds.Points, 4),
        BlurRect br => br.Rect,
        HighlightAnnotation hl => hl.Rect,
        RectShapeAnnotation rs => rs.Rect,
        CircleShapeAnnotation cs => cs.Rect,
        EraserFill ef => ef.Rect,
        StepNumberAnnotation sn => new Rectangle(sn.Pos.X - 14, sn.Pos.Y - 14, 28, 28),
        EmojiAnnotation em => new Rectangle(em.Pos.X, em.Pos.Y, (int)em.Size, (int)em.Size),
        MagnifierAnnotation mg => GetMagnifierVisualBounds(mg),
        TextAnnotation ta => GetTextBounds(ta),
        _ => Rectangle.Empty
    };

    private static Rectangle RectFromPoints(Point a, Point b, int pad)
    {
        int x = Math.Min(a.X, b.X) - pad;
        int y = Math.Min(a.Y, b.Y) - pad;
        int w = Math.Abs(b.X - a.X) + pad * 2;
        int h = Math.Abs(b.Y - a.Y) + pad * 2;
        return new Rectangle(x, y, w, h);
    }

    private static Rectangle BoundsOfPoints(List<Point> pts, int pad)
    {
        if (pts.Count == 0) return Rectangle.Empty;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var p in pts) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        return new Rectangle(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
    }

    private static Rectangle GetTextBounds(TextAnnotation ta)
    {
        using var font = new Font(ta.FontFamily, ta.FontSize,
            (ta.Bold ? FontStyle.Bold : 0) | (ta.Italic ? FontStyle.Italic : 0));
        var sz = System.Windows.Forms.TextRenderer.MeasureText(ta.Text, font);
        int padX = ta.Background ? 16 : 10;
        int padY = ta.Background ? 12 : 6;
        return new Rectangle(ta.Pos.X - (padX / 2), ta.Pos.Y - (padY / 2), sz.Width + padX, sz.Height + padY);
    }

    /// <summary>Hit-tests all annotations in reverse order (top-most first). Returns index or -1.</summary>
    private int HitTestAnnotation(Point p)
    {
        for (int i = _undoStack.Count - 1; i >= 0; i--)
        {
            var bounds = GetAnnotationBounds(_undoStack[i]);
            if (bounds.Contains(p))
                return i;
        }
        return -1;
    }

    /// <summary>Moves an annotation by a delta. Returns a new annotation with updated position.</summary>
    private static Annotation MoveAnnotation(Annotation a, int dx, int dy) => a switch
    {
        ArrowAnnotation arr => arr with { From = Offset(arr.From, dx, dy), To = Offset(arr.To, dx, dy) },
        CurvedArrowAnnotation ca => ca with { Points = ca.Points.Select(p => Offset(p, dx, dy)).ToList() },
        LineAnnotation ln => ln with { From = Offset(ln.From, dx, dy), To = Offset(ln.To, dx, dy) },
        RulerAnnotation ru => ru with { From = Offset(ru.From, dx, dy), To = Offset(ru.To, dx, dy) },
        DrawStroke ds => ds with { Points = ds.Points.Select(p => Offset(p, dx, dy)).ToList() },
        BlurRect br => br with { Rect = OffsetRect(br.Rect, dx, dy) },
        HighlightAnnotation hl => hl with { Rect = OffsetRect(hl.Rect, dx, dy) },
        RectShapeAnnotation rs => rs with { Rect = OffsetRect(rs.Rect, dx, dy) },
        CircleShapeAnnotation cs => cs with { Rect = OffsetRect(cs.Rect, dx, dy) },
        EraserFill ef => ef with { Rect = OffsetRect(ef.Rect, dx, dy) },
        StepNumberAnnotation sn => sn with { Pos = Offset(sn.Pos, dx, dy) },
        EmojiAnnotation em => em with { Pos = Offset(em.Pos, dx, dy) },
        MagnifierAnnotation mg => mg with { Pos = Offset(mg.Pos, dx, dy) },
        TextAnnotation ta => ta with { Pos = Offset(ta.Pos, dx, dy) },
        _ => a
    };

    private static Point Offset(Point p, int dx, int dy) => new(p.X + dx, p.Y + dy);
    private static Rectangle OffsetRect(Rectangle r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    private bool IsDrawingOrMoveMode(CaptureMode mode)
    {
        return mode switch
        {
            CaptureMode.Move => true,
            CaptureMode.Draw => true,
            CaptureMode.Highlight => true,
            CaptureMode.RectShape => true,
            CaptureMode.CircleShape => true,
            CaptureMode.Arrow => true,
            CaptureMode.CurvedArrow => true,
            CaptureMode.Text => true,
            CaptureMode.StepNumber => true,
            CaptureMode.Blur => true,
            CaptureMode.Magnifier => true,
            CaptureMode.Emoji => true,
            CaptureMode.Line => true,
            _ => false
        };
    }

    /// <summary>Returns the handle index (0=TL,1=TR,2=BL,3=BR,4=T,5=L,6=R,7=B) at point, or -1.</summary>
    private int GetSelectHandle(Point p)
    {
        return GetSelectHandle(p, _selectedAnnotationIndex);
    }

    private int GetSelectHandle(Point p, int annotationIndex)
    {
        if (annotationIndex < 0 || annotationIndex >= _undoStack.Count)
            return -1;
        var bounds = GetAnnotationBounds(_undoStack[annotationIndex]);
        var selRect = Rectangle.Inflate(bounds, 4, 4);
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
            if (hr.Contains(p)) return i;
        }
        return -1;
    }

    private static Annotation ScaleAnnotation(Annotation a, Rectangle oldBounds, Rectangle newBounds)
    {
        return AnnotationTransforms.Scale(a, oldBounds, newBounds);
    }

    private bool RemoveAnnotation(Annotation annotation)
    {
        var index = _undoStack.LastIndexOf(annotation);
        return DeleteAnnotationAt(index, invalidate: false);
    }

    private void CommitSelectTransform()
    {
        if (_selectedAnnotationIndex >= 0 &&
            _selectedAnnotationIndex < _undoStack.Count &&
            _selectPreviewAnnotation is not null)
        {
            var original = _undoStack[_selectedAnnotationIndex];
            if (!Equals(original, _selectPreviewAnnotation))
                PushEditCommand(new ReplaceAnnotationCommand(_selectedAnnotationIndex, original, _selectPreviewAnnotation));
        }

        _selectPreviewAnnotation = null;
        if (_renderSkipIndex >= 0)
        {
            _renderSkipIndex = -1;
            MarkCommittedAnnotationsDirty();
        }
    }

    private bool DeleteAnnotationAt(int index, bool invalidate = true)
    {
        if (index < 0 || index >= _undoStack.Count)
            return false;

        var annotation = _undoStack[index];
        var bounds = InflateForRepaint(GetAnnotationBounds(annotation), 28);
        PushEditCommand(new DeleteAnnotationCommand(index, annotation));
        ResetSelectedAnnotationState();
        if (invalidate)
            Invalidate(bounds);
        return true;
    }

    private bool TryEraseAnnotationAt(Point point)
    {
        _eraserHoverIndex = -1;
        var hit = HitTestAnnotation(point);
        return DeleteAnnotationAt(hit);
    }

    private bool UndoLastEdit()
    {
        if (_editUndoStack.Count == 0)
            return false;

        var command = _editUndoStack[^1];
        _editUndoStack.RemoveAt(_editUndoStack.Count - 1);
        command.Revert(OverlayEditContext);
        _editRedoStack.Add(command);
        ResetSelectedAnnotationState();
        RefreshNextStepNumber();
        MarkCommittedAnnotationsDirty();
        Invalidate();
        return true;
    }

    private bool RedoLastEdit()
    {
        if (_editRedoStack.Count == 0)
            return false;

        var command = _editRedoStack[^1];
        _editRedoStack.RemoveAt(_editRedoStack.Count - 1);
        command.Apply(OverlayEditContext);
        _editUndoStack.Add(command);
        ResetSelectedAnnotationState();
        RefreshNextStepNumber();
        MarkCommittedAnnotationsDirty();
        Invalidate();
        return true;
    }

    private void ResetSelectedAnnotationState()
    {
        _selectedAnnotationIndex = -1;
        _selectPreviewAnnotation = null;
        _selectResizeOriginalAnnotation = null;
        _renderSkipIndex = -1;
        _isSelectDragging = false;
        _isSelectResizing = false;
        _selectResizeHandle = -1;
    }

    private sealed class OverlayEditorContext : IEditorContext
    {
        private readonly RegionOverlayForm _owner;

        public OverlayEditorContext(RegionOverlayForm owner)
        {
            _owner = owner;
        }

        public Bitmap BaseBitmap
        {
            get => _owner._screenshot;
            set => throw new NotSupportedException("The capture overlay edit context only supports annotation commands.");
        }

        public List<Annotation> Annotations => _owner._undoStack;

        public void Invalidate() => _owner.MarkCommittedAnnotationsDirty();
    }

    private Bitmap GetCommittedAnnotationsBitmap()
    {
        if (!_committedAnnotationsDirty && _committedAnnotationsBitmap is not null)
            return _committedAnnotationsBitmap;

        _committedAnnotationsBitmap?.Dispose();
        var bitmap = new Bitmap(_bmpW, _bmpH, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(_screenshot, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            RenderAnnotationsTo(g);
        }

        _committedAnnotationsBitmap = bitmap;
        _committedAnnotationsDirty = false;
        return bitmap;
    }

    // ── Region confirmation mode (handles + Confirm/Cancel buttons) ──

    private void EnterConfirmMode(Rectangle rect)
    {
        _isConfirmingSelection = true;
        _confirmRect = rect;
        _confirmHandleDragIndex = -1;
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        _selectionEnd = Point.Empty;
        try { CloseCaptureMagnifier(); } catch { }
        EnsureToolbarReady();
        RefreshToolbar();
        Invalidate();
    }

    private void ExitConfirmMode()
    {
        _isConfirmingSelection = false;
        _confirmRect = Rectangle.Empty;
        _confirmHandleDragIndex = -1;
        _hoveredConfirmButton = -1;
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        _selectionEnd = Point.Empty;
        EnsureToolbarReady();
        RefreshToolbar();
        Invalidate();
    }

    private void CommitConfirmedSelection()
    {
        var rect = _confirmRect;
        _isConfirmingSelection = false;
        _confirmRect = Rectangle.Empty;
        _confirmHandleDragIndex = -1;
        _hoveredConfirmButton = -1;
        bool isOcr = _mode == CaptureMode.Ocr;
        bool isScan = _mode == CaptureMode.Scan;
        bool isSticker = _mode == CaptureMode.Sticker;
        bool isUpscale = _mode == CaptureMode.Upscale;
        bool isScroll = _mode == CaptureMode.ScrollCapture;
        if (isOcr) OcrRegionSelected?.Invoke(rect);
        else if (isScan) ScanRegionSelected?.Invoke(rect);
        else if (isSticker) StickerRegionSelected?.Invoke(rect);
        else if (isUpscale) UpscaleRegionSelected?.Invoke(rect);
        else if (isScroll) ScrollRegionSelected?.Invoke(rect);
        else RegionSelected?.Invoke(rect);
    }

    private static readonly int ConfirmHandleSize = 10;
    private static readonly int ConfirmButtonWidth = 90;
    private static readonly int ConfirmButtonHeight = 32;
    private static readonly int ConfirmButtonGap = 8;

    private Rectangle[] GetConfirmHandleRects()
    {
        int hs = UiChrome.ScaleInt(ConfirmHandleSize);
        int h2 = hs / 2;
        var r = _confirmRect;
        return new[]
        {
            new Rectangle(r.Left - h2, r.Top - h2, hs, hs),      // 0 TL
            new Rectangle(r.Right - h2, r.Top - h2, hs, hs),     // 1 TR
            new Rectangle(r.Left - h2, r.Bottom - h2, hs, hs),   // 2 BL
            new Rectangle(r.Right - h2, r.Bottom - h2, hs, hs),  // 3 BR
        };
    }

    private (Rectangle confirm, Rectangle cancel) GetConfirmButtonRects()
    {
        int bw = UiChrome.ScaleInt(ConfirmButtonWidth);
        int bh = UiChrome.ScaleInt(ConfirmButtonHeight);
        int gap = UiChrome.ScaleInt(ConfirmButtonGap);
        var r = _confirmRect;
        int totalW = bw * 2 + gap;
        int x = r.Left + (r.Width - totalW) / 2;
        int y = r.Bottom + UiChrome.ScaleInt(12);
        // Clamp y so buttons stay inside client area
        if (y + bh > ClientSize.Height - 10)
            y = Math.Max(10, r.Top - bh - UiChrome.ScaleInt(12));
        return (
            new Rectangle(x, y, bw, bh),
            new Rectangle(x + bw + gap, y, bw, bh)
        );
    }

    private int HitTestConfirmHandle(Point p)
    {
        var handles = GetConfirmHandleRects();
        for (int i = 0; i < handles.Length; i++)
            if (handles[i].Contains(p)) return i;
        return -1;
    }

    private int HitTestConfirmButton(Point p)
    {
        var (confirm, cancel) = GetConfirmButtonRects();
        if (confirm.Contains(p)) return 0;
        if (cancel.Contains(p)) return 1;
        return -1;
    }

    private Rectangle GetToolbarAnchorClientBounds()
    {
        var bounds = _toolbarAnchorArea.IsEmpty
            ? new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)
            : new Rectangle(
                _toolbarAnchorArea.X - _virtualBounds.X,
                _toolbarAnchorArea.Y - _virtualBounds.Y,
                _toolbarAnchorArea.Width,
                _toolbarAnchorArea.Height);

        bounds.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        return bounds.IsEmpty ? new Rectangle(0, 0, ClientSize.Width, ClientSize.Height) : bounds;
    }
}
