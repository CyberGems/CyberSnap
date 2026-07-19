using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
using CyberSnap.Services;

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
    public CaptureDockSide CaptureDockSide { get; set; } = CaptureDockSide.Bottom;

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
            _visibleTools = ToolDef.AllTools.Where(t => enabledIds.Contains(t.Id)).ToArray();
        }

        _mainBarTools = _visibleTools.Where(t => !flyoutIds.Contains(t.Id)).ToArray();
        _flyoutTools = _visibleTools.Where(t => flyoutIds.Contains(t.Id)).ToArray();
        RefreshToolbar();
    }

    private static string[] GetSystemFonts() => TextAnnotationPainter.GetSystemFonts();

    private int _pinnedFontCount;
    private TextAnnotationPainter.FontListEntry[]? _fontListEntries;

    private (List<string> recents, List<string> favorites) LoadFontListsFromSettings()
    {
        try
        {
            var s = Services.SettingsService.LoadStatic();
            return (
                TextAnnotationPainter.ParseRecentFonts(s?.EditorTextRecentFonts),
                TextAnnotationPainter.ParseFavoriteFonts(s?.EditorTextFavoriteFonts));
        }
        catch
        {
            return (new List<string>(), new List<string>());
        }
    }

    private List<string> GetRecentFonts() => LoadFontListsFromSettings().recents;
    private List<string> GetFavoriteFonts() => LoadFontListsFromSettings().favorites;

    private TextAnnotationPainter.FontListEntry[] GetFontListEntries()
    {
        if (_fontListEntries != null) return _fontListEntries;
        var (recents, favorites) = LoadFontListsFromSettings();
        _fontListEntries = TextAnnotationPainter.GetOrderedFontEntries(
            _fontSearch, favorites, recents, out _pinnedFontCount);
        return _fontListEntries;
    }

    private string[] GetFilteredFonts()
    {
        if (_filteredFonts != null) return _filteredFonts;
        _filteredFonts = GetFontListEntries().Select(e => e.Name).ToArray();
        return _filteredFonts;
    }

    private void InvalidateFontListCache()
    {
        _filteredFonts = null;
        _fontListEntries = null;
    }

    private void ToggleFavoriteFontAndPersist(string family)
    {
        var favorites = GetFavoriteFonts();
        favorites = TextAnnotationPainter.ToggleFavoriteFont(favorites, family);
        var serialized = TextAnnotationPainter.SerializeFavoriteFonts(favorites);
        if (System.Windows.Application.Current is App app)
            app.PersistEditorTextFavoriteFonts(serialized);
        InvalidateFontListCache();
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

        // ToolbarForm only hosts the main dock + its popups (color/emoji/font).
        // The inline text chrome (text frame + formatting toolbar) is painted on the
        // overlay itself. Including it here made ToolbarForm jump/resize to cover
        // mid-screen text every time typing started — a visible glitch.
        Add(InflateIfNeeded(_toolbarRect, Helpers.UiChrome.ScaleInt(12)));
        Add(InflateIfNeeded(GetColorPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
        Add(InflateIfNeeded(GetEmojiPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
        // Font picker is painted on ToolbarForm near the text; expand only while open.
        if (_fontPickerOpen)
            Add(InflateIfNeeded(GetFontPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
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
    {
        // Prefer the active drag end; otherwise the last known cursor (confirm mode,
        // hover after release) so dimension pills stay parked on the nearest corner.
        if (_selectionEnd != Point.Empty)
            return _selectionEnd;
        if (_lastCursorPos != Point.Empty)
            return _lastCursorPos;
        return Point.Empty;
    }

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

    /// <summary>
    /// Capture-selection tools dim the world outside the active hole from the moment the
    /// overlay opens (idle / auto-detect / drag). Confirm mode always dims outside the locked
    /// region — even after the last annotation tool is restored on the bar for in-region edits.
    /// Pure annotation tools (no confirm session) never dim, so live previews stay fluid.
    /// </summary>
    private bool ShouldDimOutsideSelection()
    {
        // Confirm locks the region: keep the veil until Confirm / Retry / Cancel, regardless of
        // which annotation tool is active for editing inside the hole.
        if (_isConfirmingSelection)
            return true;

        if (!IsSelectionCaptureMode())
            return false;

        // Wait until the first auto-detect seed finishes so dim + hole appear together.
        if (!_selectionDimPrimed)
            return false;

        return true;
    }

    /// <summary>
    /// Snapshot windows and seed the auto-detect hole under the cursor before first show/paint.
    /// Safe to call from the constructor (no HWND required).
    /// </summary>
    private void PrimeSelectionDimFromCursor()
    {
        if (_selectionDimPrimed)
            return;

        try
        {
            if (_windowDetectionMode != WindowDetectionMode.Off && IsSelectionCaptureMode())
            {
                WindowDetector.SnapshotWindows(_virtualBounds);
                SeedAutoDetectUnderCursor();
            }
        }
        catch
        {
            // Best-effort: still prime so dim can appear even if enum fails.
        }
        finally
        {
            _selectionDimPrimed = true;
        }
    }

    private void SeedAutoDetectUnderCursor()
    {
        if (!IsSelectionCaptureMode() || _isSelecting || _isConfirmingSelection)
            return;
        if (_windowDetectionMode == WindowDetectionMode.Off)
            return;

        // Prefer manual conversion so this works before Handle creation.
        var screen = Cursor.Position;
        var clientPt = new Point(screen.X - _virtualBounds.X, screen.Y - _virtualBounds.Y);

        if (IsHandleCreated && IsPointInOverlayUi(PointToClient(screen)))
        {
            _autoDetectRect = Rectangle.Empty;
            _autoDetectActive = false;
            _lastAutoDetectRect = Rectangle.Empty;
            return;
        }

        var detected = WindowDetector.GetDetectionRectAtPoint(
            clientPt, _virtualBounds, _windowDetectionMode);
        _autoDetectRect = detected;
        _autoDetectActive = detected.Width > 0 && detected.Height > 0;
        _lastAutoDetectRect = _autoDetectActive ? detected : Rectangle.Empty;
    }

    /// <summary>
    /// Bright region excluded from the dim overlay. Empty = full virtual-desktop dim
    /// (no window under cursor yet, or drag not started).
    /// </summary>
    private Rectangle GetSelectionDimHole()
    {
        if (_isConfirmingSelection && _confirmRect.Width > 0 && _confirmRect.Height > 0)
            return _confirmRect;

        if (_isSelecting && _selectionRect.Width > 2 && _selectionRect.Height > 2)
            return _selectionRect;

        if (!_isSelecting && !_isConfirmingSelection
            && _autoDetectActive
            && _autoDetectRect.Width > 0 && _autoDetectRect.Height > 0)
            return _autoDetectRect;

        return Rectangle.Empty;
    }

    // Softer veil on top of the desaturated outside so color loss stays readable.
    private static readonly Color SelectionDimColor = Color.FromArgb(72, 0, 0, 0);

    private static readonly ColorMatrix DesaturateColorMatrix = new(new[]
    {
        new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
        new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
        new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
        new[] { 0f, 0f, 0f, 1f, 0f },
        new[] { 0f, 0f, 0f, 0f, 1f },
    });

    /// <summary>
    /// One-time grayscale bake of the frozen screenshot. Cost is O(pixels) once per overlay
    /// (typically a few–tens of ms on multi-monitor 4K); per-frame paint is then a plain blit.
    /// </summary>
    private void EnsureDesaturatedScreenshot()
    {
        if (_desaturatedScreenshot is not null || _screenshot is null)
            return;

        try
        {
            int w = _screenshot.Width;
            int h = _screenshot.Height;
            _desaturatedScreenshot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(_desaturatedScreenshot);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(DesaturateColorMatrix);
            g.DrawImage(
                _screenshot,
                new Rectangle(0, 0, w, h),
                0, 0, w, h,
                GraphicsUnit.Pixel,
                attrs);
        }
        catch
        {
            try { _desaturatedScreenshot?.Dispose(); } catch { }
            _desaturatedScreenshot = null;
        }
    }

    /// <summary>
    /// When the dim hole changes, both the old and new holes must repaint (re-dim / un-dim).
    /// Spans all monitors when the hole jumps between them.
    /// </summary>
    private void InvalidateDimHoleChange(Rectangle oldHole, Rectangle newHole)
    {
        if (oldHole == newHole)
            return;

        // Empty ↔ non-empty: the rest of the virtual desktop's veil interpretation changes;
        // full invalidate keeps every monitor in sync.
        if (oldHole.IsEmpty || newHole.IsEmpty)
        {
            Invalidate();
            Update();
            return;
        }

        var dirty = Rectangle.Union(
            InflateForRepaint(oldHole, 20),
            InflateForRepaint(newHole, 20));
        if (dirty.IsEmpty)
        {
            Invalidate();
            Update();
            return;
        }

        Invalidate(dirty);
        Update();
    }

    private void InvalidateAutoDetectChrome(Rectangle oldDetect, Rectangle newDetect)
    {
        if (!IsSelectionCaptureMode() || _isSelecting || _hasSelection)
            return;

        // Dim hole tracks auto-detect while idle — must refresh both holes across monitors.
        InvalidateDimHoleChange(oldDetect, newDetect);
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

    private const int MaxAnnotations = 200;

    private void AddAnnotation(Annotation annotation)
    {
        if (_undoStack.Count >= MaxAnnotations)
        {
            ShowToolBanner(
                string.Format(LocalizationService.Translate("Maximum annotations reached ({0})"), MaxAnnotations));
            return;
        }
        PushEditCommand(new AddAnnotationCommand(annotation));
    }

    /// <summary>Returns the bounding rectangle for any annotation type, for hit-testing.</summary>
    private Rectangle GetAnnotationBounds(Annotation a) => a switch
    {
        ArrowAnnotation arr => RectFromPoints(arr.From, arr.To, 8),
        CurvedArrowAnnotation ca => BoundsOfPoints(ca.Points, 8),
        LineAnnotation ln => RectFromPoints(ln.From, ln.To, 6),
        // Tight wrapper around the line + the label's *actual* rect. GetSelectionBounds used a fixed
        // ~600×360 box, so even a tiny ruler got a huge selection frame regardless of its real size.
        RulerAnnotation ru => RulerRenderer.GetLivePreviewBounds(ru.From, ru.To, ClientRectangle),
        DrawStroke ds => BoundsOfPoints(ds.Points, 4),
        BlurRect br => br.Rect,
        HighlightAnnotation hl => hl.Rect,
        RectShapeAnnotation rs => rs.Rect,
        CircleShapeAnnotation cs => cs.Rect,
        EraserFill ef => ef.Rect,
        StepNumberAnnotation sn => new Rectangle(sn.Pos.X - 14, sn.Pos.Y - 14, 28, 28),
        EmojiAnnotation em => new Rectangle(em.Pos.X, em.Pos.Y, (int)(em.Size * 1.4f) + 4, (int)(em.Size * 1.4f) + 4),
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

    private static Rectangle GetTextBounds(TextAnnotation ta) =>
        Rectangle.Round(TextAnnotationPainter.Measure(ta));

    /// <summary>Hit-tests all annotations in reverse order (top-most first). Returns index or -1.</summary>
    private int HitTestAnnotation(Point p)
    {
        for (int i = _undoStack.Count - 1; i >= 0; i--)
        {
            var bounds = GetAnnotationBounds(_undoStack[i]);
            var hoverBounds = Rectangle.Inflate(bounds, 32, 32);
            if (hoverBounds.Contains(p))
                return i;
        }
        return -1;
    }

    private void UpdateMoveHoverIndex(Point p)
    {
        int hitIdx = HitTestAnnotationSurface(p);

        // Keep hover active while the cursor stays inside the wrap box so corner/edge
        // handles remain reachable after moving off the stroke.
        if (hitIdx < 0 && _moveHoverIndex >= 0 && _moveHoverIndex < _undoStack.Count)
        {
            var bounds = GetAnnotationBounds(_undoStack[_moveHoverIndex]);
            if (Rectangle.Inflate(bounds, 32, 32).Contains(p))
                hitIdx = _moveHoverIndex;
        }

        if (_suppressHoverBoxIndex >= 0)
        {
            if (hitIdx == _suppressHoverBoxIndex) hitIdx = -1;
            else _suppressHoverBoxIndex = -1;
        }
        if (hitIdx == _moveHoverIndex) return;

        if (_moveHoverIndex >= 0 && _moveHoverIndex < _undoStack.Count)
            Invalidate(Rectangle.Inflate(GetAnnotationBounds(_undoStack[_moveHoverIndex]), 40, 40));
        _moveHoverIndex = hitIdx;
        if (hitIdx >= 0 && hitIdx < _undoStack.Count)
            Invalidate(Rectangle.Inflate(GetAnnotationBounds(_undoStack[hitIdx]), 40, 40));
    }

    private int HitTestAnnotationSurface(Point p)
    {
        for (int i = _undoStack.Count - 1; i >= 0; i--)
        {
            if (IsOverAnnotationSurface(_undoStack[i], p))
                return i;
        }
        return -1;
    }

    private const int SurfaceOutlineTolerance = 6;

    private bool IsOverAnnotationSurface(Annotation a, Point pt)
    {
        return a switch
        {
            CircleShapeAnnotation cs => IsOnEllipseOutline(cs.Rect, cs.StrokeWidth, pt),
            RectShapeAnnotation rs   => IsOnRectOutline(rs.Rect, rs.StrokeWidth, pt),
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

        return Inside(band) && !Inside(-band);
    }

    private static bool IsOnRectOutline(Rectangle rect, float strokeWidth, Point pt)
    {
        rect = NormalizeRect(rect);
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        int band = (int)(strokeWidth / 2f + SurfaceOutlineTolerance);
        if (!InflateRect(rect, band, band).Contains(pt)) return false;
        var inner = InflateRect(rect, -band, -band);
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(pt);
    }

    private static Rectangle NormalizeRect(Rectangle r)
    {
        int x = Math.Min(r.X, r.X + r.Width);
        int y = Math.Min(r.Y, r.Y + r.Height);
        return new Rectangle(x, y, Math.Abs(r.Width), Math.Abs(r.Height));
    }

    private static Rectangle InflateRect(Rectangle r, int x, int y)
    {
        var copy = r;
        copy.Inflate(x, y);
        return copy;
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
            TextAnnotation ta => GetTextBounds(ta).Contains(pt),
            StepNumberAnnotation sn => Distance(sn.Pos, pt) <= tol * 3,
            EmojiAnnotation em => InflateRect(GetAnnotationBounds(em), tol, tol).Contains(pt),
            MagnifierAnnotation mg => Distance(mg.Pos, pt) <= tol * 4,
            _ => false,
        };
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

    private bool IsDraggingAnyAnnotation()
    {
        return _isSelecting || _isCurvedArrowDragging || _isHighlighting || 
               _isRectShapeDragging || _isCircleShapeDragging || _isBlurring || 
               _isArrowDragging || _isLineDragging || _isRulerDragging;
    }

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
            CaptureMode.Ruler => true,
            _ => false
        };
    }

    /// <summary>Returns the handle index (0=TL,1=TR,2=BL,3=BR,4=T,5=L,6=R,7=B) at point, or -1.</summary>
    private int GetSelectHandle(Point p)
    {
        return GetSelectHandle(p, _selectedAnnotationIndex);
    }

    /// <summary>Whether an annotation supports resizing. Fixed-size badges (step numbers) can
    /// only be repositioned, so they expose a move-only control box (no resize handles).</summary>
    private static bool IsResizable(Annotation a) => a is not StepNumberAnnotation;

    private int GetSelectHandle(Point p, int annotationIndex)
    {
        if (annotationIndex < 0 || annotationIndex >= _undoStack.Count)
            return -1;
        // Non-resizable items never report a resize handle, so a drag on them is always a move.
        if (!IsResizable(_undoStack[annotationIndex]))
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

        // Handle 8: center move knob — circular hit area sized to cover the 4-way arrow glyph.
        var center = new Point(selRect.X + selRect.Width / 2, selRect.Y + selRect.Height / 2);
        const int centerHitRadius = 14;
        int cdx = p.X - center.X;
        int cdy = p.Y - center.Y;
        if (cdx * cdx + cdy * cdy <= centerHitRadius * centerHitRadius)
            return 8;

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

    private void DeleteMultiSelectedAnnotations()
    {
        var items = _multiSelectedIndices
            .Where(i => i >= 0 && i < _undoStack.Count)
            .Select(i => (i, _undoStack[i]))
            .ToList();
        if (items.Count == 0) return;

        int count = items.Count;
        PushEditCommand(new DeleteMultipleAnnotationsCommand(items));
        _selectedAnnotationIndex = -1;
        _multiSelectedIndices.Clear();
        var msg = string.Format(LocalizationService.Translate("{0} objects deleted"), count);
        ShowToolBanner(msg);
        Invalidate();
    }

    private void ResetSelectedAnnotationState()
    {
        _selectedAnnotationIndex = -1;
        _multiSelectedIndices.Clear();
        _selectPreviewAnnotation = null;
        _selectResizeOriginalAnnotation = null;
        _renderSkipIndex = -1;
        _isSelectDragging = false;
        _isSelectResizing = false;
        _selectResizeHandle = -1;
    }

    private void SelectAll()
    {
        if (_undoStack.Count == 0) return;
        _multiSelectedIndices.Clear();
        for (int i = 0; i < _undoStack.Count; i++)
            _multiSelectedIndices.Add(i);
        _selectedAnnotationIndex = _undoStack.Count - 1;
        var msg = string.Format(LocalizationService.Translate("{0} objects selected"), _multiSelectedIndices.Count);
        ShowToolBanner(msg, persistent: true);
        Invalidate();
    }

    /// <summary>Duplicates the current selection (single or multi) as a single undo-able
    /// operation. Clones are offset by (20,20) client-space pixels, clamped to stay inside
    /// the overlay's client area. The selection moves to the new clones.</summary>
    private void DuplicateSelection()
    {
        var indices = _multiSelectedIndices.Count > 0
            ? _multiSelectedIndices.Where(i => i >= 0 && i < _undoStack.Count).OrderBy(i => i).ToList()
            : (_selectedAnnotationIndex >= 0
                ? new List<int> { _selectedAnnotationIndex }
                : new List<int>());
        if (indices.Count == 0) return;

        var originals = indices.Select(i => _undoStack[i]).ToList();

        // Union bounds of the originals in client space, clamped so the offset clone stays visible.
        Rectangle union = Rectangle.Empty;
        foreach (var a in originals)
        {
            var b = GetAnnotationBounds(a);
            union = union.IsEmpty ? b : Rectangle.Union(union, b);
        }
        var limit = ClientRectangle;
        int dx = 20, dy = 20;
        if (!union.IsEmpty)
        {
            int newX = Math.Clamp(union.X + dx, limit.X, Math.Max(limit.X, limit.Right - union.Width));
            int newY = Math.Clamp(union.Y + dy, limit.Y, Math.Max(limit.Y, limit.Bottom - union.Height));
            dx = newX - union.X;
            dy = newY - union.Y;
        }

        var clones = originals.Select(a => AnnotationTransforms.Translate(a, dx, dy)).ToList();
        int insertStart = _undoStack.Count;
        PushEditCommand(new AddMultipleAnnotationsCommand(clones));

        int added = _undoStack.Count - insertStart;
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
        Invalidate();
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

    // Capture tool active when the region was locked — restored on Retry so annotation tools
    // (e.g. Eraser) activated for in-confirm editing do not stick after ExitConfirmMode.
    private CaptureMode _modeBeforeConfirm = CaptureMode.Rectangle;
    private string? _toolIdBeforeConfirm;

    private void EnterConfirmMode(Rectangle rect, Point? releaseAnchor = null)
    {
        _isConfirmingSelection = true;
        _confirmRect = rect;
        _confirmHandleDragIndex = -1;
        // Remember where the drag ended as a fraction of the selection, so the Confirm/Retry
        // buttons appear near the release point (not forced to the center of a large area).
        if (releaseAnchor is { } a && rect.Width > 0 && rect.Height > 0)
        {
            _confirmButtonAnchorFracX = Math.Clamp((a.X - rect.Left) / (float)rect.Width, 0f, 1f);
            _confirmButtonAnchorFracY = Math.Clamp((a.Y - rect.Top) / (float)rect.Height, 0f, 1f);
        }
        else
        {
            _confirmButtonAnchorFracX = 0.5f;
            _confirmButtonAnchorFracY = 1f;
        }
        RecomputeConfirmButtonWidth();
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        // Keep the release point as the readout anchor until the cursor moves again.
        if (releaseAnchor is { } releasePt)
            _lastCursorPos = releasePt;
        _selectionEnd = Point.Empty;
        try { CloseCaptureMagnifier(); } catch { }
        EnsureToolbarReady();
        RefreshToolbar();
        // Snapshot the capture tool *before* restoring the last annotation tool for confirm-mode editing.
        _modeBeforeConfirm = _mode;
        _toolIdBeforeConfirm = _activeToolId;
        // Restore the last annotation tool on the bar, but never flash its help banner here —
        // the user just finished selecting a region and should only see Confirm/Retry chrome.
        TryRestoreLastAnnotationTool();
        HideToolBannerImmediate();
        _shinePhase[0] = 0f;
        _shinePhase[1] = 0.5f;
        _shinePhase[2] = 0.25f;
        _shineMain[0] = _shineMain[1] = _shineMain[2] = 1f;
        _shineDup[0] = _shineDup[1] = _shineDup[2] = 0f;
        if (!UI.Motion.Disabled) _confirmShineTimer.Start();
        Invalidate();
    }

    /// <summary>
    /// After the region is locked for annotation, restore the last Group-1 tool the user used
    /// (if it is still enabled on the bar). Capture-only users never set the preference.
    /// </summary>
    private void TryRestoreLastAnnotationTool()
    {
        var settings = Services.SettingsService.LoadStatic();
        var lastId = settings?.LastAnnotationToolId;
        if (string.IsNullOrWhiteSpace(lastId))
            return;

        var tool = ToolDef.AllTools.FirstOrDefault(t =>
            t.Group == 1 && string.Equals(t.Id, lastId, StringComparison.OrdinalIgnoreCase));
        if (tool is null || tool.Mode is null)
            return;

        // Only restore if the tool is currently visible on the annotation bar.
        if (!_flyoutTools.Any(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase))
            && !_mainBarTools.Any(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase)))
            return;

        SetTool(tool, showHelpBanner: false);
    }

    private void ExitConfirmMode()
    {
        _isConfirmingSelection = false;
        _confirmRect = Rectangle.Empty;
        _confirmHandleDragIndex = -1;
        _hoveredConfirmButton = -1;
        ResetConfirmPress();
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        _selectionEnd = Point.Empty;
        // Retry = re-select area: put the original capture tool back (not the annotation tool
        // restored for confirm-mode editing, which made Eraser stick after Retry).
        SetMode(_modeBeforeConfirm, _toolIdBeforeConfirm, showHelpBanner: false);
        HideToolBannerImmediate();
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
        ResetConfirmPress();
        InvokeRegionSelected(rect);
    }

    private void InvokeRegionSelected(Rectangle rect)
    {
        if (_mode == CaptureMode.Ocr) OcrRegionSelected?.Invoke(rect);
        else if (_mode == CaptureMode.Scan) ScanRegionSelected?.Invoke(rect);
        else if (_mode == CaptureMode.Sticker) StickerRegionSelected?.Invoke(rect);
        else if (_mode == CaptureMode.Upscale) UpscaleRegionSelected?.Invoke(rect);
        else if (_mode == CaptureMode.ScrollCapture) ScrollRegionSelected?.Invoke(rect);
        else RegionSelected?.Invoke(rect);
    }

    private Rectangle GetInstantCaptureRect()
    {
        if (_windowDetectionMode != WindowDetectionMode.Off)
        {
            var cursor = PointToClient(Cursor.Position);
            var detected = WindowDetector.GetDetectionRectAtPoint(
                cursor, _virtualBounds, _windowDetectionMode);
            if (detected.Width > 0 && detected.Height > 0)
                return detected;
        }

        if (_autoDetectRect.Width > 0 && _autoDetectRect.Height > 0)
            return _autoDetectRect;

        return new Rectangle(0, 0, _screenshot.Width, _screenshot.Height);
    }

    private void CommitCaptureRect(Rectangle rect, bool directCapture = false)
    {
        _autoDetectRect = Rectangle.Empty;
        _autoDetectActive = false;
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        _selectionEnd = Point.Empty;

        if (_mode == CaptureMode.Center && (rect.Width <= 2 || rect.Height <= 2))
        {
            Invalidate();
            return;
        }

        if (!directCapture && ConfirmRegionBeforeCapture && _mode != CaptureMode.ScrollCapture)
            EnterConfirmMode(rect);
        else
            InvokeRegionSelected(rect);
    }

    private bool TryCommitCaptureViaEnter()
    {
        if (_quickStartGuide != null && _quickStartGuide.Visible)
        {
            DismissQuickStartGuide();
            return true;
        }

        if (_isConfirmingSelection)
        {
            CommitConfirmedSelection();
            return true;
        }

        if (_emojiPickerOpen || _fontPickerOpen || _isTyping)
            return false;
        if (_toolbarContextMenu?.Visible == true || _confirmContextMenu?.Visible == true)
            return false;
        if (!IsSelectionCaptureMode())
            return false;

        if (_isSelecting)
        {
            _isSelecting = false;
            ResetEvasion();
            CloseSelectionAdorner();
            if (_selectionRect.Width > 2 && _selectionRect.Height > 2)
                CommitCaptureRect(_selectionRect, directCapture: true);
            else
            {
                _hasSelection = false;
                Invalidate();
            }
            return true;
        }

        if (_hasSelection && _selectionRect.Width > 2 && _selectionRect.Height > 2)
        {
            CommitCaptureRect(_selectionRect, directCapture: true);
            return true;
        }

        CommitCaptureRect(GetInstantCaptureRect(), directCapture: true);
        return true;
    }

    private static readonly int ConfirmHandleSize = 16;
    private static readonly int ConfirmButtonHeight = 34;
    private static readonly int ConfirmButtonGap = 14;

    // Measured width for the confirm/cancel buttons; recomputed on entering confirm
    // mode so the localized label (e.g. "Confirmar") always fits on a single line.
    private int _confirmButtonWidth = UiChrome.ScaleInt(96);

    // Where the drag ended, as a 0..1 fraction inside the selection rect. Drives the
    // Confirm/Retry button placement so they sit near the release point and follow that
    // proportional spot when the selection is moved or resized in confirm mode.
    private float _confirmButtonAnchorFracX = 0.5f;
    private float _confirmButtonAnchorFracY = 1f;

    private Rectangle[] GetConfirmHandleRects()
    {
        int hs = UiChrome.ScaleInt(ConfirmHandleSize);
        int h2 = hs / 2;
        var r = _confirmRect;
        int midX = r.Left + r.Width / 2;
        int midY = r.Top + r.Height / 2;
        return new[]
        {
            new Rectangle(r.Left - h2, r.Top - h2, hs, hs),      // 0 TL
            new Rectangle(r.Right - h2, r.Top - h2, hs, hs),     // 1 TR
            new Rectangle(r.Left - h2, r.Bottom - h2, hs, hs),   // 2 BL
            new Rectangle(r.Right - h2, r.Bottom - h2, hs, hs),  // 3 BR
            new Rectangle(midX - h2, r.Top - h2, hs, hs),        // 4 Top
            new Rectangle(r.Left - h2, midY - h2, hs, hs),       // 5 Left
            new Rectangle(r.Right - h2, midY - h2, hs, hs),      // 6 Right
            new Rectangle(midX - h2, r.Bottom - h2, hs, hs),     // 7 Bottom
        };
    }

    private void ConfirmAndCancelCapture()
    {
        if (_undoStack.Count > 0)
        {
            var isSpanish = string.Equals(
                Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
                "es", StringComparison.OrdinalIgnoreCase);

            var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 220);
            menu.Font = UiChrome.ChromeFont(11.0f);
            _confirmContextMenu = menu;
            menu.Closed += (s, e) => {
                _confirmContextMenu = null;
            };

            var titleLabel = new ToolStripLabel(isSpanish ? "Se perderán las anotaciones" : "Annotations will be lost")
            {
                ForeColor = UiChrome.SurfaceTextMuted,
                Font = UiChrome.ChromeFont(9f),
                Padding = new System.Windows.Forms.Padding(10, 8, 0, 2),
                AutoSize = true,
            };
            menu.Items.Add(titleLabel);
            menu.Items.Add(new ToolStripSeparator());

            var yesLabel = isSpanish ? "Confirmar cancelación" : "Confirm cancellation";
            var yesItem = WindowsMenuRenderer.Item(yesLabel, iconId: "close", danger: true, iconSize: 24);
            yesItem.Click += (_, _) => Cancel();
            menu.Items.Add(yesItem);

            var noLabel = isSpanish ? "Continuar selección" : "Continue selection";
            var noItem = WindowsMenuRenderer.Item(noLabel, iconId: "check", iconSize: 24);
            noItem.Click += (_, _) => menu.Close();
            menu.Items.Add(noItem);

            WindowsMenuRenderer.NormalizeItemWidths(menu, 220, itemHeight: 44);
            menu.Show(System.Windows.Forms.Cursor.Position);
        }
        else
        {
            Cancel();
        }
    }

    private const float ConfirmPressDurationMs = 160f;

    /// <summary>
    /// Begins the click "squash" animation for a confirm/cancel button, then runs the real
    /// action (commit or cancel) when it finishes. Runs the action immediately when motion
    /// is disabled or a press is already playing.
    /// </summary>
    private void StartConfirmPress(int button)
    {
        if (_pressedConfirmButton >= 0) return; // a press is already playing
        if (UI.Motion.Disabled)
        {
            RunConfirmAction(button);
            return;
        }
        _pressedConfirmButton = button;
        _pendingConfirmAction = button;
        _confirmPressAmt = 0f;
        _pressAnimStart = DateTime.UtcNow;
        _confirmPressTimer.Start();
        var (confirmBtn, cancelBtn, closeBtn) = GetConfirmButtonRects();
        var targetBtn = button switch { 0 => confirmBtn, 1 => cancelBtn, _ => closeBtn };
        Invalidate(InflateForRepaint(targetBtn, 24));
    }

    private void ConfirmPressTick()
    {
        float elapsed = (float)(DateTime.UtcNow - _pressAnimStart).TotalMilliseconds;
        float phase = Math.Min(1f, elapsed / ConfirmPressDurationMs);
        _confirmPressAmt = (float)Math.Sin(phase * Math.PI); // 0 → 1 → 0 squash-and-release

        int button = _pressedConfirmButton;
        if (button >= 0)
        {
            var (confirmBtn, cancelBtn, closeBtn) = GetConfirmButtonRects();
            var targetBtn = button switch { 0 => confirmBtn, 1 => cancelBtn, _ => closeBtn };
            Invalidate(InflateForRepaint(targetBtn, 24));
        }

        if (phase >= 1f)
        {
            _confirmPressTimer.Stop();
            _confirmPressAmt = 0f;
            _pressedConfirmButton = -1;
            int action = _pendingConfirmAction;
            _pendingConfirmAction = -1;
            RunConfirmAction(action);
        }
    }

    private void RunConfirmAction(int button)
    {
        if (button == 0) CommitConfirmedSelection();
        else if (button == 1) ExitConfirmMode();
        else if (button == 2) ConfirmAndCancelCapture();
    }

    private void ResetConfirmPress()
    {
        _confirmPressTimer.Stop();
        _pressedConfirmButton = -1;
        _pendingConfirmAction = -1;
        _confirmPressAmt = 0f;
        _confirmShineTimer.Stop();
    }

    /// <summary>
    /// Advances the glint that travels around the confirm/cancel borders. A perpetual
    /// loop while confirming, gated by the "disable animations" setting (UI.Motion.Disabled).
    /// </summary>
    private void ConfirmShineTick()
    {
        if (UI.Motion.Disabled || !_isConfirmingSelection)
        {
            _confirmShineTimer.Stop();
            return;
        }
        // Per button: advance the comet (2× speed while hovered) and ease its intensities.
        // The hovered button gains a second comet (dup→1); the other's comet fades (main→0).
        int hov = _hoveredConfirmButton; // -1 none, 0 confirm, 1 retry, 2 cancel
        float baseDelta = (float)(UiChrome.FrameIntervalMs / 2600.0); // full lap ~2.6s
        for (int i = 0; i < 3; i++)
        {
            _shinePhase[i] += baseDelta * (hov == i ? 2f : 1f);
            if (_shinePhase[i] >= 1f) _shinePhase[i] -= 1f;

            float targetMain = (hov >= 0 && hov != i) ? 0f : 1f;
            float targetDup = (hov == i) ? 1f : 0f;
            _shineMain[i] += (targetMain - _shineMain[i]) * 0.22f;
            _shineDup[i] += (targetDup - _shineDup[i]) * 0.22f;
        }

        var (confirmBtn, cancelBtn, closeBtn) = GetConfirmButtonRects();
        Invalidate(InflateForRepaint(confirmBtn, 24));
        Invalidate(InflateForRepaint(cancelBtn, 24));
        Invalidate(InflateForRepaint(closeBtn, 24));
    }

    // Label font for the Confirm / Retry pills. Centralized so the width measurement in
    // RecomputeConfirmButtonWidth() and the drawing in DrawConfirmActionPill() always use the
    // exact same font.
    //
    // GDI+ does NOT throw when a font family is missing — it silently falls back to
    // Microsoft Sans Serif. The app's default "Segoe UI Variable Text" is a Windows 11-only
    // family, so on Windows 10 these labels were rendering in Microsoft Sans Serif (cramped
    // spacing, dated/soft glyphs). We resolve a family that is actually installed instead of
    // trusting the name blindly.
    private const string ConfirmButtonFontFamily = "Segoe UI";
    private const FontStyle ConfirmButtonFontStyle = FontStyle.Bold;

    private static Font CreateConfirmButtonFont()
    {
        float size = UiChrome.ScaleFloat(11f);
        try
        {
            // new FontFamily(name) throws if the family is not installed (unlike new Font()).
            using (new FontFamily(ConfirmButtonFontFamily)) { }
            return new Font(ConfirmButtonFontFamily, size, ConfirmButtonFontStyle);
        }
        catch
        {
            return new Font(FontFamily.GenericSansSerif, size, ConfirmButtonFontStyle);
        }
    }

    private void RecomputeConfirmButtonWidth()
    {
        int min = UiChrome.ScaleInt(112);
        // The label + icon are drawn as one centered group: [text][gap][icon]. Size the button
        // so that group fits with symmetric side padding. These proportions MUST match the
        // layout math in DrawConfirmActionPill().
        float h = UiChrome.ScaleInt(ConfirmButtonHeight);
        float iconSize = h * 0.58f;
        float gap = h * 0.22f;
        int sidePad = UiChrome.ScaleInt(16);
        try
        {
            using var font = CreateConfirmButtonFont();
            using var g = CreateGraphics();
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            };
            var bounds = new SizeF(10000f, h);
            // Only the Confirm button carries a text label now (Retry is icon-only).
            float w = g.MeasureString(LocalizationService.Translate("Ready").ToUpperInvariant(), font, bounds, sf).Width;
            _confirmButtonWidth = Math.Max(min, (int)Math.Ceiling(w + gap + iconSize + sidePad * 2));
        }
        catch
        {
            _confirmButtonWidth = min;
        }
    }

    private Rectangle GetConfirmButtonMonitorClientBounds(Point anchorClient)
    {
        var screenPoint = new Point(_virtualBounds.X + anchorClient.X, _virtualBounds.Y + anchorClient.Y);
        var monitorBounds = Rectangle.Intersect(Screen.FromPoint(screenPoint).Bounds, _virtualBounds);
        if (monitorBounds.IsEmpty)
            return new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);

        var clientBounds = new Rectangle(
            monitorBounds.X - _virtualBounds.X,
            monitorBounds.Y - _virtualBounds.Y,
            monitorBounds.Width,
            monitorBounds.Height);
        clientBounds.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        return clientBounds.IsEmpty ? new Rectangle(0, 0, ClientSize.Width, ClientSize.Height) : clientBounds;
    }

    private (Rectangle confirm, Rectangle cancel, Rectangle close) GetConfirmButtonRects()
    {
        int bw = _confirmButtonWidth;
        int bh = UiChrome.ScaleInt(ConfirmButtonHeight);
        int gap = UiChrome.ScaleInt(ConfirmButtonGap);
        var r = _confirmRect;

        int retryW = bh;     // Retry is icon-only (square, full height)
        int cancelW = bh;    // Cancel is also an icon-only square, matching Retry

        int offset = UiChrome.ScaleInt(12);
        int margin = UiChrome.ScaleInt(10);
        // Prefer not covering the exact release point; a few px of air when parking inside.
        int cursorGap = UiChrome.ScaleInt(10);

        // Horizontal: put the Confirm button directly under the point where the drag ended (its
        // proportional position inside the selection). Retry sits to its left, then Cancel, so the
        // primary action lands under the cursor and large selections don't force the cursor back
        // to the middle.  Order (left→right): [Cancel] [Retry] [Confirm].
        float anchorX = r.Left + _confirmButtonAnchorFracX * r.Width;
        float anchorY = r.Top + _confirmButtonAnchorFracY * r.Height;
        var monitor = GetConfirmButtonMonitorClientBounds(new Point((int)Math.Round(anchorX), (int)Math.Round(anchorY)));
        int minX = monitor.Left + margin;
        int maxX = monitor.Right - margin;
        int minY = monitor.Top + margin;
        int maxY = monitor.Bottom - margin;
        int maxTop = Math.Max(minY, maxY - bh);

        // Vertical placement priority (near the release/cursor point always wins):
        //  1. Outside on the side nearest the release, if that side fits on the monitor.
        //  2. Inside the selection next to the release (right-edge Listo anchor).
        //  3. Outside on the far side only if inside cannot host the cluster either.
        //
        // Important: do NOT flip to the far outside edge just because it "fits". That is what
        // put Listo at the top of a tall crop when the user released at the bottom-right
        // (see artifacts photo of the Grok TUI selection).
        int outsideBelow = r.Bottom + offset;
        int outsideAbove = r.Top - bh - offset;
        bool belowFits = outsideBelow >= minY && outsideBelow + bh <= maxY;
        bool aboveFits = outsideAbove >= minY && outsideAbove + bh <= maxY;

        // Which outside side is nearer the release point?
        float distBelow = Math.Abs((outsideBelow + bh * 0.5f) - anchorY);
        float distAbove = Math.Abs((outsideAbove + bh * 0.5f) - anchorY);
        bool preferBelow = distBelow <= distAbove;

        int y;
        bool insidePlacement = false;

        int insidePad = Math.Max(offset, UiChrome.ScaleInt(ConfirmHandleSize));
        int insideMin = r.Top + insidePad;
        int insideMax = r.Bottom - insidePad - bh;
        bool canPlaceInside = insideMax >= insideMin;
        int insideY = 0;
        if (canPlaceInside)
        {
            // Prefer just above the release (common when releasing near the bottom edge),
            // then just below — never straddle the selection frame through Listo's middle.
            int aboveCursor = Math.Clamp((int)Math.Round(anchorY - bh - cursorGap), insideMin, insideMax);
            int belowCursor = Math.Clamp((int)Math.Round(anchorY + cursorGap), insideMin, insideMax);
            float bestInside = Math.Abs(aboveCursor + bh * 0.5f - anchorY);
            insideY = aboveCursor;
            float distBelowCursor = Math.Abs(belowCursor + bh * 0.5f - anchorY);
            if (distBelowCursor < bestInside)
                insideY = belowCursor;
        }

        int? preferredOutside = preferBelow
            ? (belowFits ? outsideBelow : null)
            : (aboveFits ? outsideAbove : null);
        int? farOutside = preferBelow
            ? (aboveFits ? outsideAbove : null)
            : (belowFits ? outsideBelow : null);

        if (preferredOutside is int preferredY)
        {
            // Nearest outside side has room — park there (close to the cursor).
            y = preferredY;
        }
        else if (canPlaceInside)
        {
            // Preferred outside is blocked (e.g. released at the bottom of the monitor).
            // Stay next to the cursor inside the crop instead of jumping to the far top/bottom.
            y = insideY;
            insidePlacement = true;
        }
        else if (farOutside is int farY)
        {
            // Cluster cannot fit inside a short selection — last resort far outside.
            y = farY;
        }
        else
        {
            y = Math.Clamp((int)Math.Round(anchorY - bh / 2f), minY, maxTop);
            // Treat monitor-only clamp near the release as "inside-like" for horizontal anchor.
            if (y >= r.Top && y + bh <= r.Bottom)
                insidePlacement = true;
        }

        y = Math.Clamp(y, minY, maxTop);

        // Horizontal:
        //  - Outside the crop: center Listo on the release X (existing behaviour).
        //  - Inside the crop: right-edge of Listo at the release X — same as when the cluster
        //    is forced against the screen edge — so the pill sits fully to the left of the
        //    cursor and is not cut through the middle by the selection frame.
        int confirmX;
        if (insidePlacement)
        {
            confirmX = (int)Math.Round(anchorX - bw); // right edge of Listo at release point
        }
        else
        {
            int confirmXCenter = (int)Math.Round(anchorX - bw / 2f);
            int clusterLeftCenter = confirmXCenter - gap - retryW - gap - cancelW;
            int clusterRightCenter = confirmXCenter + bw;
            if (clusterRightCenter > maxX)
                confirmX = (int)Math.Round(anchorX - bw); // right edge of Listo at release point
            else if (clusterLeftCenter < minX)
                confirmX = (int)Math.Round(anchorX); // left edge of Listo at release point
            else
                confirmX = confirmXCenter;
        }

        int retryX = confirmX - gap - retryW;
        int closeX = retryX - gap - cancelW;

        // Shift the whole cluster back inside the current monitor if it still spills.
        int clusterLeft = closeX;
        int clusterRight = confirmX + bw;
        if (clusterLeft < minX)
        {
            int s = minX - clusterLeft; confirmX += s; retryX += s; closeX += s;
        }
        else if (clusterRight > maxX)
        {
            int s = maxX - clusterRight; confirmX += s; retryX += s; closeX += s;
        }

        return (
            new Rectangle(confirmX, y, bw, bh),
            new Rectangle(retryX, y, retryW, bh),
            new Rectangle(closeX, y, cancelW, bh)
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
        var (confirm, cancel, close) = GetConfirmButtonRects();
        if (confirm.Contains(p)) return 0;
        if (cancel.Contains(p)) return 1;
        if (close.Contains(p)) return 2;
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
