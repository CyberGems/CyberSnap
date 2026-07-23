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

    /// <summary>
    /// Annotation confirm dock is always a vertical column. Capture-phase dock follows user setting.
    /// </summary>
    private bool IsVerticalDock =>
        ShowAnnotationChrome || CaptureDockSide is CaptureDockSide.Left or CaptureDockSide.Right;
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
        // Confirm chrome (wrapper + pills + handles) counts as overlay UI so the
        // capture magnifier never samples / paints over it (avoids trails).
        if (_isConfirmingSelection && IsPointInConfirmChrome(p)) return true;
        return false;
    }

    private bool IsPointInConfirmChrome(Point p)
    {
        if (!_isConfirmingSelection)
            return false;

        LayoutConfirmChromeRects();
        // Generous pad: magnifier must vanish before the cursor "enters" the dock glow.
        if (!_confirmChromeWrapperRect.IsEmpty)
        {
            var wrap = _confirmChromeWrapperRect;
            wrap.Inflate(UiChrome.ScaleInt(28), UiChrome.ScaleInt(28));
            if (wrap.Contains(p)) return true;
        }

        foreach (var r in _confirmChromeRects)
        {
            if (r.Width <= 0) continue;
            var hit = r;
            hit.Inflate(UiChrome.ScaleInt(10), UiChrome.ScaleInt(10));
            if (hit.Contains(p)) return true;
        }

        foreach (var h in GetConfirmHandleRects())
        {
            var hit = h;
            hit.Inflate(UiChrome.ScaleInt(6), UiChrome.ScaleInt(6));
            if (hit.Contains(p)) return true;
        }

        if (!_confirmSizeReadoutRect.IsEmpty)
        {
            var sizeHit = _confirmSizeReadoutRect;
            sizeHit.Inflate(UiChrome.ScaleInt(6), UiChrome.ScaleInt(6));
            if (sizeHit.Contains(p)) return true;
        }

        // Selection frame edge / handle band while confirming — treat as chrome for magnifier.
        if (!_confirmRect.IsEmpty)
        {
            var outer = _confirmRect;
            outer.Inflate(UiChrome.ScaleInt(20), UiChrome.ScaleInt(20));
            var inner = _confirmRect;
            inner.Inflate(-UiChrome.ScaleInt(8), -UiChrome.ScaleInt(8));
            if (outer.Contains(p) && (inner.Width <= 0 || !inner.Contains(p)))
                return true;
        }

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

    /// <summary>
    /// Tight click region around the logo icon and "CyberSnap" text label (+ 3px margin for accessibility)
    /// that opens the Quick Start Guide.
    /// </summary>
    private bool IsPointInBrandClickArea(Point location)
    {
        if (_logoRect.IsEmpty)
            return false;

        // Bounding box of the visual brand logo icon + text label (approx 68px wide)
        int brandWidth = Helpers.UiChrome.ScaleInt(68);
        var contentRect = new Rectangle(
            _logoRect.X - 3,
            _logoRect.Y - 3,
            brandWidth,
            _logoRect.Height + 6);

        return contentRect.Contains(location);
    }

    /// <summary>
    /// Area of the toolbar reserved for dragging: the position move button OR the empty space
    /// around the branding strip (excluding the clickable brand logo/text itself).
    /// Gaps between tool buttons are NOT drag handles.
    /// </summary>
    private bool IsPointInToolbarDragArea(Point location)
    {
        int btn = GetToolbarButtonAt(location);
        if (btn == PositionButtonIndex)
            return true;

        if (!_brandRect.IsEmpty && _brandRect.Contains(location) && !IsPointInBrandClickArea(location))
            return true;

        return false;
    }

    /// <summary>
    /// Cursor over the capture/confirm dock: <see cref="CursorFactory.GrabCursor"/> on designated drag surfaces
    /// (Move button, empty branding area); <see cref="Cursors.Hand"/> on clickable controls including brand;
    /// <see cref="Cursors.Default"/> on all dead/background surfaces.
    /// </summary>
    private Cursor? TryGetToolbarHoverCursor(Point location)
    {
        bool overDock = _toolbarRect.Contains(location) || IsPointInToolbarChrome(location);
        if (!overDock)
            return null;

        if ((ShowAnnotationChrome && !_annotationGripRect.IsEmpty && _annotationGripRect.Contains(location))
            || (!ShowAnnotationChrome && !_captureGripRect.IsEmpty && _captureGripRect.Contains(location)))
        {
            return Cursors.Hand;
        }

        if (_menuActivatorRect.Contains(location))
            return Cursors.Hand;

        // Clickable branding (logo + text + 3px margin) -> Hand cursor for quick-start guide.
        if (IsPointInBrandClickArea(location))
            return Cursors.Hand;

        // Dedicated drag zone (around branding or Move button) -> Hand cursor.
        if (IsPointInToolbarDragArea(location))
            return Cursors.Hand;

        int btn = GetToolbarButtonAt(location);
        if (btn >= 0)
            return Cursors.Hand;

        // All other dead surfaces and gaps between tools on the dock -> Default arrow cursor.
        return Cursors.Default;
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

    /// <summary>
    /// Magnifier only while idle-hovering the capture surface — never while dragging/resizing
    /// a selection, confirming, or over chrome (toolbar / confirm wrapper / pills).
    /// </summary>
    /// <summary>
    /// Capture pixel magnifier (not the annotation Magnifier tool). Shown while hovering the
    /// surface with a capture tool — including during selection drag — but never in confirm mode,
    /// never while resizing/moving the locked region, and never over chrome.
    /// </summary>
    private bool ShouldShowCaptureMagnifierAt(Point p)
        => ShowCaptureMagnifier
           && ToolDef.IsCaptureTool(_mode)
           && !_isConfirmingSelection
           && _confirmHandleDragIndex < 0
           && !_isConfirmDragging
           && !IsPointInOverlayUi(p);

    private Point GetReadoutCursorPoint()
    {
        // Confirm mode uses a dedicated top-left drag pill (see RefreshConfirmSizeReadoutRect).
        // Live selection still follows the drag end so the readout stays near the hand.
        if (_isConfirmingSelection && _confirmRect.Width > 2 && _confirmRect.Height > 2)
            return new Point(_confirmRect.Left, _confirmRect.Top);

        // While dragging a fresh selection, follow the drag end so the readout stays near the hand.
        if (_selectionEnd != Point.Empty)
            return _selectionEnd;
        if (_lastCursorPos != Point.Empty)
            return _lastCursorPos;
        return Point.Empty;
    }

    /// <summary>
    /// Confirm-mode action pills + annotation column are reserved so the size drag-pill
    /// never stacks on chrome.
    /// </summary>
    private IReadOnlyList<Rectangle>? GetConfirmReadoutAvoidRects()
    {
        if (!_isConfirmingSelection || _confirmDocksHiddenForFrameManip)
            return null;

        LayoutConfirmChromeRects();
        var list = new List<Rectangle>();
        foreach (var r in _confirmChromeRects)
        {
            if (r.Width > 0 && r.Height > 0)
                list.Add(r);
        }
        if (!_confirmChromeWrapperRect.IsEmpty)
            list.Add(_confirmChromeWrapperRect);
        if (ShowAnnotationChrome && _toolbarRect.Width > 0)
            list.Add(_toolbarRect);
        return list.Count > 0 ? list : null;
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
            !_selectionMonitorClientBounds.IsEmpty ? _selectionMonitorClientBounds : ClientRectangle);
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
            !_selectionMonitorClientBounds.IsEmpty ? _selectionMonitorClientBounds : ClientRectangle);
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
    private static readonly Color SelectionDimColor = Color.FromArgb(105, 0, 0, 0);

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
        PendingCommitAction = ConfirmCommitAction.Default;
        // Ensure monitor clamp is set (click-to-select / auto-detect paths may skip drag start).
        if (_selectionMonitorClientBounds.IsEmpty)
        {
            var anchor = releaseAnchor
                ?? new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            CaptureSelectionMonitorAt(anchor);
        }
        _confirmRect = ClampRectToSelectionMonitor(rect);
        _confirmHandleDragIndex = -1;
        // Snapshot capture purpose before any annotation-tool restore changes `_mode`.
        _modeBeforeConfirm = _mode;
        _toolIdBeforeConfirm = _activeToolId;
        _isConfirmingSelection = true;
        // Fresh anchor on the frame — do not keep a leftover drag offset from capture phase.
        _toolbarCustomOffset = Point.Empty;
        _confirmCustomOffset = Point.Empty;
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
        var settings = Services.SettingsService.LoadStatic();
        _confirmPillShowLabels = settings?.ConfirmPillShowLabels ?? false;
        RebuildConfirmChromeKinds();
        RecomputeConfirmButtonWidth();
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        // Keep the release point as the readout anchor until the cursor moves again.
        if (releaseAnchor is { } releasePt)
            _lastCursorPos = releasePt;
        _selectionEnd = Point.Empty;
        try { CloseCaptureMagnifier(); } catch { }
        // Restore the last annotation tool on the bar, but never flash its help banner here —
        // the user just finished selecting a region and should only see Confirm/Retry chrome.
        TryRestoreLastAnnotationTool();
        HideToolBannerImmediate();
        for (int i = 0; i < ConfirmShineSlots; i++)
        {
            _shinePhase[i] = 0f;
            // Buttons start fully visible, no traveling shine until individually hovered.
            _shineMain[i] = 1f;
            _shineDup[i] = 0f;
        }
        _confirmWrapperShinePhase = 0f;
        _hoveredConfirmSizeReadout = false;

        // Annotation column FIRST, then destination pills. Laying out pills before CalcToolbar
        // used the capture-phase toolbar rect and shoved the dock toward the left of the monitor
        // (especially for selections on the right half).
        _confirmChromeLayoutDirty = true;
        CalcToolbar();
        _confirmChromeLayoutDirty = true;
        LayoutConfirmChromeRects();
        RefreshConfirmSizeReadoutRect();
        MarkToolbarRenderDirty();
        PresentAnnotationToolbarNow();
        EnsureToolbarReady();

        // Wrapper shine runs while confirming so the dock stays findable on busy wallpapers.
        if (!UI.Motion.Disabled) _confirmShineTimer.Start();
        Invalidate();
        // One synchronous paint so destination pills + frame appear with the annotation dock.
        try { Update(); } catch { }
    }

    /// <summary>Applies Settings → Confirm pill labels without re-entering confirm mode.</summary>
    public void SetConfirmPillShowLabels(bool show)
    {
        if (_confirmPillShowLabels == show) return;
        _confirmPillShowLabels = show;
        _confirmChromeLayoutDirty = true;
        if (_isConfirmingSelection)
        {
            RecomputeConfirmButtonWidth();
            LayoutConfirmChromeRects();
            Invalidate();
        }
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

        // Never auto-restore placement tools that draw a large floating ghost under the cursor.
        // Magnifier was being re-selected every confirm session (via LastAnnotationToolId) and
        // its live preview was mistaken for the capture pixel magnifier / leaving trails.
        if (tool.Mode is CaptureMode.Magnifier or CaptureMode.Emoji or CaptureMode.StepNumber)
            return;

        // Only restore if the tool is currently visible on the annotation bar.
        if (!_flyoutTools.Any(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase))
            && !_mainBarTools.Any(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase)))
            return;

        SetTool(tool, showHelpBanner: false);
    }

    private bool HasConfirmAnnotations() => _undoStack.Count > 0;

    /// <summary>
    /// Retry / reselect: with annotations, ask first. Without, leave confirm immediately.
    /// </summary>
    private void RequestRetrySelection()
    {
        if (!_isConfirmingSelection)
            return;

        if (HasConfirmAnnotations())
        {
            bool ok = false;
            try
            {
                ok = UI.ThemedConfirmDialog.Confirm(
                    Handle,
                    LocalizationService.Translate("Retry selection?"),
                    LocalizationService.Translate("This will discard the annotations on this capture."),
                    LocalizationService.Translate("Retry"),
                    LocalizationService.Translate("Cancel"),
                    danger: true,
                    iconId: "redo");
            }
            catch
            {
                ok = false;
            }
            if (!ok)
                return;
        }

        ExitConfirmMode();
    }

    private void ClearConfirmSessionAnnotations()
    {
        if (_undoStack.Count == 0 && _editUndoStack.Count == 0 && _editRedoStack.Count == 0)
            return;

        _undoStack.Clear();
        ClearEditHistory();
        _selectedAnnotationIndex = -1;
        _multiSelectedIndices.Clear();
        _moveHoverIndex = -1;
        _eraserHoverIndex = -1;
        _renderSkipIndex = -1;
        _selectPreviewAnnotation = null;
        _selectResizeOriginalAnnotation = null;
        _isSelectDragging = false;
        _isSelectResizing = false;
        if (_isTyping)
        {
            try { CommitOrCancelInlineText(commit: false); } catch { }
        }
        RefreshNextStepNumber();
        MarkCommittedAnnotationsDirty();
    }

    /// <summary>True when the point is in the dimmed exterior (not frame, docks, or size pill).</summary>
    private bool IsOutsideLockedCaptureFrame(Point p)
    {
        if (!_isConfirmingSelection || _confirmDocksHiddenForFrameManip)
            return false;
        if (_confirmRect.Width > 2 && _confirmRect.Contains(p))
            return false;
        if (HitTestConfirmHandle(p) >= 0)
            return false;
        if (HitTestConfirmButton(p) >= 0)
            return false;
        if (HitTestConfirmSizeReadout(p))
            return false;
        if (_toolbarRect.Width > 0 && _toolbarRect.Contains(p))
            return false;
        if (IsPointInToolbarChrome(p))
            return false;
        if (_menuActivatorRect.Contains(p) || _brandRect.Contains(p) || _logoRect.Contains(p))
            return false;
        if (IsPointInAltToolPopup(p))
            return false;
        if (_colorPickerOpen && _colorPickerRect.Contains(p))
            return false;
        if (_fontPickerOpen && _fontPickerRect.Contains(p))
            return false;
        if (_emojiPickerOpen && _emojiPickerRect.Contains(p))
            return false;
        return true;
    }

    private void StartAreaSelectionFromPoint(Point clientPt)
    {
        HideToolbarForCaptureTool();
        if (_windowDetectionMode == WindowDetectionMode.Off)
        {
            _autoDetectRect = Rectangle.Empty;
            _autoDetectActive = false;
        }
        else
        {
            _autoDetectRect = WindowDetector.GetDetectionRectAtPoint(
                clientPt, _virtualBounds, _windowDetectionMode);
            _autoDetectActive = _autoDetectRect.Width > 0 && _autoDetectRect.Height > 0;
        }

        CaptureSelectionMonitorAt(clientPt);
        var start = ClampPointToSelectionMonitor(clientPt);
        _isSelecting = true;
        _selectionStart = _selectionEnd = start;
        _selectionRect = Rectangle.Empty;
        _hasSelection = false;
        _hasDragged = false;
        ResetCaptureMagnifierDragPlacement();
        CloseSelectionAdorner();
        Invalidate();
    }

    private void ExitConfirmMode(bool showToolbar = true)
    {
        _isConfirmingSelection = false;
        _confirmRect = Rectangle.Empty;
        _confirmHandleDragIndex = -1;
        _hoveredConfirmButton = -1;
        _outsideReselectArmed = false;
        _outsideReselectMoved = false;
        _confirmDocksHiddenForFrameManip = false;
        _confirmCustomOffset = Point.Empty;
        ResetConfirmPress();
        CloseAltToolPopup(invalidate: false);
        ClearConfirmSessionAnnotations();
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        _selectionEnd = Point.Empty;
        _confirmSizeReadoutRect = Rectangle.Empty;
        // Retry = re-select area: put the original capture tool back (not the annotation tool
        // restored for confirm-mode editing, which made Eraser stick after Retry).
        SetMode(_modeBeforeConfirm, _toolIdBeforeConfirm, showHelpBanner: false);
        HideToolBannerImmediate();
        if (showToolbar)
        {
            CalcToolbar();
            MarkToolbarRenderDirty();
            PositionToolbarForm();
            EnsureToolbarReady();
            RefreshToolbar();
        }
        else
        {
            HideToolbarImmediately();
        }
        Invalidate();
    }

    private void CommitConfirmedSelection()
    {
        var rect = _confirmRect;
        var captureMode = _modeBeforeConfirm;
        _isConfirmingSelection = false;
        _confirmRect = Rectangle.Empty;
        _confirmHandleDragIndex = -1;
        _hoveredConfirmButton = -1;
        ResetConfirmPress();
        InvokeRegionSelected(rect, captureMode);
    }

    private void CommitConfirmedSelection(ConfirmCommitAction action)
    {
        PendingCommitAction = action;
        CommitConfirmedSelection();
    }

    private int IndexOfConfirmChrome(ConfirmChromeKind kind)
    {
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            if (_confirmChromeKinds[i] == kind)
                return i;
        }
        return -1;
    }

    private void RebuildConfirmChromeKinds()
    {
        _confirmChromeKinds = new[]
        {
            ConfirmChromeKind.ModeImage,
            ConfirmChromeKind.ModeOcr,
            ConfirmChromeKind.ModeVideo,
            ConfirmChromeKind.ModeGif,
            ConfirmChromeKind.ModeScroll,
            ConfirmChromeKind.ModeQr,
            ConfirmChromeKind.TogglePreview,
            ConfirmChromeKind.Retry,
            ConfirmChromeKind.Cancel,
            ConfirmChromeKind.Done
        };
        _confirmChromeRects = new Rectangle[_confirmChromeKinds.Length];
        _confirmChromeLayoutDirty = true;
    }

    private static bool IsImageAutoCopyEnabled()
    {
        var settings = Services.SettingsService.LoadStatic();
        return settings != null
            && Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Image);
    }

    private static bool IsOcrAutoCopyEnabled()
    {
        var settings = Services.SettingsService.LoadStatic();
        return settings != null
            && Helpers.AutoCopyPreferences.ShouldCopy(settings, Helpers.AutoCopyKind.Ocr);
    }

    private static string GetOcrExtractTooltip()
        => LocalizationService.Translate(IsOcrAutoCopyEnabled()
            ? "Extract and copy text from the selection"
            : "Extract text from the selection");

    private static bool IsConfirmChromeDisabled(ConfirmChromeKind kind) => false;

    private int IndexOfPrimaryConfirmAction()
    {
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            if (_confirmChromeKinds[i] == ConfirmChromeKind.Done)
                return i;
        }
        return -1;
    }

    private static bool IsConfirmDestinationKind(ConfirmChromeKind kind) => false;

    private static ToastButtonKind? ConfirmChromeToToastKind(ConfirmChromeKind kind) => null;



    private void CommitPrimaryConfirmAction()
    {
        int idx = IndexOfPrimaryConfirmAction();
        if (idx >= 0)
        {
            StartConfirmPress(idx);
            return;
        }
        CommitConfirmedSelection(ConfirmCommitAction.Save);
    }

    private static string? ConfirmChromeFluentIcon(ConfirmChromeKind kind) => kind switch
    {
        ConfirmChromeKind.Cancel => "signOut",
        ConfirmChromeKind.Retry => "redo",
        ConfirmChromeKind.Done => "check",
        ConfirmChromeKind.ModeImage => "captureRect",
        ConfirmChromeKind.ModeOcr => "ocr",
        ConfirmChromeKind.ModeVideo => "record",
        ConfirmChromeKind.ModeGif => "recordGif",
        ConfirmChromeKind.ModeScroll => "scrollCapture",
        ConfirmChromeKind.ModeQr => "scan",
        _ => null
    };

    private static string ConfirmChromeShortLabel(ConfirmChromeKind kind) => kind switch
    {
        ConfirmChromeKind.Cancel => LocalizationService.Translate("Cancel"),
        ConfirmChromeKind.Retry => LocalizationService.Translate("Retry"),
        ConfirmChromeKind.Done => LocalizationService.Translate("Done"),
        ConfirmChromeKind.TogglePreview => LocalizationService.Translate("Preview"),
        ConfirmChromeKind.ModeImage => LocalizationService.Translate("Image"),
        ConfirmChromeKind.ModeOcr => "OCR",
        ConfirmChromeKind.ModeVideo => "Video",
        ConfirmChromeKind.ModeGif => "GIF",
        ConfirmChromeKind.ModeScroll => LocalizationService.Translate("Scroll"),
        ConfirmChromeKind.ModeQr => "QR",
        _ => ""
    };

    private static string ConfirmChromeTitle(ConfirmChromeKind kind) => kind switch
    {
        ConfirmChromeKind.Cancel => LocalizationService.Translate("Cancel capture completely"),
        ConfirmChromeKind.Retry => LocalizationService.Translate("Retry area"),
        ConfirmChromeKind.Done => LocalizationService.Translate("Done"),
        ConfirmChromeKind.TogglePreview => LocalizationService.Translate("Show preview after capture"),
        ConfirmChromeKind.ModeImage => LocalizationService.Translate("Capture as Image"),
        ConfirmChromeKind.ModeOcr => LocalizationService.Translate("Extract Text (OCR)"),
        ConfirmChromeKind.ModeVideo => LocalizationService.Translate("Record Video"),
        ConfirmChromeKind.ModeGif => LocalizationService.Translate("Record GIF"),
        ConfirmChromeKind.ModeScroll => LocalizationService.Translate("Scrolling Capture"),
        ConfirmChromeKind.ModeQr => LocalizationService.Translate("Scan QR & Barcode"),
        _ => kind.ToString()
    };

    private static string ConfirmChromeHotkeyHint(ConfirmChromeKind kind) => kind switch
    {
        ConfirmChromeKind.Cancel => "Esc",
        ConfirmChromeKind.Retry => "R",
        ConfirmChromeKind.Done => "Enter",
        ConfirmChromeKind.ModeImage => "I",
        ConfirmChromeKind.ModeOcr => "O",
        ConfirmChromeKind.ModeVideo => "V",
        ConfirmChromeKind.ModeGif => "G",
        ConfirmChromeKind.ModeScroll => "S",
        ConfirmChromeKind.ModeQr => "Q",
        _ => ""
    };

    private bool ConfirmChromeIsIconOnly(ConfirmChromeKind kind)
    {
        if (kind is ConfirmChromeKind.Cancel or ConfirmChromeKind.Retry)
            return true;
        if (kind == ConfirmChromeKind.TogglePreview)
            return false;
        return !_confirmPillShowLabels;
    }

    private int MeasureConfirmChromeButtonWidth(ConfirmChromeKind kind, int iconOnlySize)
    {
        if (kind == ConfirmChromeKind.TogglePreview)
        {
            string labelText = ConfirmChromeShortLabel(kind);
            using var toggleFont = CreateConfirmButtonFont();
            var toggleTextSize = TextRenderer.MeasureText(
                labelText,
                toggleFont,
                new Size(int.MaxValue, iconOnlySize),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int trackWidth = UiChrome.ScaleInt(34);
            int toggleGap = UiChrome.ScaleInt(8);
            int togglePadX = UiChrome.ScaleInt(12);
            return togglePadX + toggleTextSize.Width + toggleGap + trackWidth + togglePadX;
        }

        if (ConfirmChromeIsIconOnly(kind))
            return iconOnlySize;

        string label = ConfirmChromeShortLabel(kind);
        if (string.IsNullOrEmpty(label))
            return iconOnlySize;

        using var font = CreateConfirmButtonFont();
        var textSize = TextRenderer.MeasureText(
            label,
            font,
            new Size(int.MaxValue, iconOnlySize),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int iconPart = Math.Max(UiChrome.ScaleInt(16), (int)(iconOnlySize * 0.52f));
        int gap = UiChrome.ScaleInt(6);
        int padX = UiChrome.ScaleInt(12);
        return Math.Max(iconOnlySize + UiChrome.ScaleInt(28), padX + iconPart + gap + textSize.Width + padX);
    }

    private string ConfirmChromeDrawLabel(ConfirmChromeKind kind)
        => ConfirmChromeIsIconOnly(kind) ? "" : ConfirmChromeShortLabel(kind);

    private void InvokeRegionSelected(Rectangle rect, CaptureMode? captureMode = null)
    {
        var mode = captureMode ?? _mode;
        if (mode == CaptureMode.Ocr) OcrRegionSelected?.Invoke(rect);
        else if (mode == CaptureMode.Scan) ScanRegionSelected?.Invoke(rect);
        else if (mode == CaptureMode.Sticker) StickerRegionSelected?.Invoke(rect);
        else if (mode == CaptureMode.Upscale) UpscaleRegionSelected?.Invoke(rect);
        else if (mode == CaptureMode.ScrollCapture) ScrollRegionSelected?.Invoke(rect);
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

        // Area / center / OCR always lock the region (annotation + action chrome),
        // including Enter-to-commit during drag. Scroll still commits immediately.
        // Scan/etc. honor ConfirmRegionBeforeCapture when not forcing a direct path.
        bool forceConfirm = _mode is CaptureMode.Rectangle or CaptureMode.Center or CaptureMode.Ocr;
        if (_mode != CaptureMode.ScrollCapture
            && (forceConfirm || (!directCapture && ConfirmRegionBeforeCapture)))
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
            CommitPrimaryConfirmAction();
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
    /// <summary>Wider gap between Retry and the first destination (mirrors Settings designer divider).</summary>
    private static readonly int ConfirmChromeGroupGap = 26;

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

    /// <summary>
    /// Hit-test for frame resize: corner grips first, then full border bands (not only mid-edge dots).
    /// Indices match <see cref="GetConfirmHandleRects"/> (0–3 corners, 4–7 edges).
    /// </summary>
    private int HitTestConfirmFrameBorder(Point p)
    {
        if (_confirmRect.Width <= 2 || _confirmRect.Height <= 2)
            return -1;

        int edge = Math.Max(UiChrome.ScaleInt(8), UiChrome.ScaleInt(ConfirmHandleSize) / 2 + 2);
        int corner = Math.Max(edge + 2, UiChrome.ScaleInt(14));
        var r = _confirmRect;

        // Expanded outer/inner band around the frame.
        var outer = r;
        outer.Inflate(edge, edge);
        if (!outer.Contains(p))
            return -1;

        bool nearLeft = p.X <= r.Left + edge;
        bool nearRight = p.X >= r.Right - edge;
        bool nearTop = p.Y <= r.Top + edge;
        bool nearBottom = p.Y >= r.Bottom - edge;

        // Interior of the crop (outside the border band) is not a resize hit.
        if (!nearLeft && !nearRight && !nearTop && !nearBottom)
            return -1;

        // Corners win over pure edges.
        if (nearTop && nearLeft) return 0;
        if (nearTop && nearRight) return 1;
        if (nearBottom && nearLeft) return 2;
        if (nearBottom && nearRight) return 3;

        // Full-length edges (excluding corner zones so cursor/resize mode stays clean).
        if (nearTop && p.X > r.Left + corner && p.X < r.Right - corner) return 4;
        if (nearBottom && p.X > r.Left + corner && p.X < r.Right - corner) return 7;
        if (nearLeft && p.Y > r.Top + corner && p.Y < r.Bottom - corner) return 5;
        if (nearRight && p.Y > r.Top + corner && p.Y < r.Bottom - corner) return 6;

        // Near a side but also near a corner zone without both flags: still treat as nearest edge.
        if (nearTop) return 4;
        if (nearBottom) return 7;
        if (nearLeft) return 5;
        if (nearRight) return 6;
        return -1;
    }

    private void ConfirmAndCancelCapture()
    {
        if (_undoStack.Count > 0)
        {
            var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 220);
            menu.Font = UiChrome.ChromeFont(11.0f);
            _confirmContextMenu = menu;
            menu.Closed += (s, e) => {
                _lastContextMenuClosedTime = DateTime.UtcNow;
                _lastContextMenuBtnIndex = -99;
                _confirmContextMenu = null;
            };

            var titleLabel = new ToolStripLabel(LocalizationService.Translate("Annotations will be lost"))
            {
                ForeColor = UiChrome.SurfaceTextMuted,
                Font = UiChrome.ChromeFont(9f),
                Padding = new System.Windows.Forms.Padding(10, 8, 0, 2),
                AutoSize = true,
            };
            menu.Items.Add(titleLabel);
            menu.Items.Add(new ToolStripSeparator());

            var yesItem = WindowsMenuRenderer.Item(
                LocalizationService.Translate("Confirm cancellation"),
                iconId: "signOut",
                danger: true,
                iconSize: 24);
            yesItem.Click += (_, _) => Cancel();
            menu.Items.Add(yesItem);

            var noItem = WindowsMenuRenderer.Item(
                LocalizationService.Translate("Continue selection"),
                iconId: "check",
                iconSize: 24);
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
        if (button >= 0 && button < _confirmChromeKinds.Length
            && IsConfirmChromeDisabled(_confirmChromeKinds[button]))
            return;
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
        LayoutConfirmChromeRects();
        if (button >= 0 && button < _confirmChromeRects.Length)
            Invalidate(InflateForRepaint(_confirmChromeRects[button], 24));
    }

    private void ConfirmPressTick()
    {
        float elapsed = (float)(DateTime.UtcNow - _pressAnimStart).TotalMilliseconds;
        float phase = Math.Min(1f, elapsed / ConfirmPressDurationMs);
        _confirmPressAmt = (float)Math.Sin(phase * Math.PI); // 0 → 1 → 0 squash-and-release

        int button = _pressedConfirmButton;
        if (button >= 0 && button < _confirmChromeRects.Length)
            Invalidate(InflateForRepaint(_confirmChromeRects[button], 24));

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
        if (button < 0 || button >= _confirmChromeKinds.Length)
            return;
        if (IsConfirmChromeDisabled(_confirmChromeKinds[button]))
            return;

        switch (_confirmChromeKinds[button])
        {
            case ConfirmChromeKind.Retry:
                RequestRetrySelection();
                break;
            case ConfirmChromeKind.Cancel:
                ConfirmAndCancelCapture();
                break;
            case ConfirmChromeKind.Done:
            case ConfirmChromeKind.ModeImage:
                CommitConfirmedSelection(ConfirmCommitAction.Default);
                break;
            case ConfirmChromeKind.TogglePreview:
                var settings = Services.SettingsService.LoadStatic() ?? new AppSettings();
                bool newValue = !settings.ShowCapturePreview;
                SettingsService.SaveShowCapturePreview(newValue);
                _confirmChromeLayoutDirty = true;
                Invalidate();
                break;
            case ConfirmChromeKind.ModeOcr:
                OcrRegionSelected?.Invoke(_confirmRect);
                break;
            case ConfirmChromeKind.ModeVideo:
                RecordingRequested?.Invoke(Models.RecordingFormat.MP4);
                break;
            case ConfirmChromeKind.ModeGif:
                RecordingRequested?.Invoke(Models.RecordingFormat.GIF);
                break;
            case ConfirmChromeKind.ModeScroll:
                ScrollRegionSelected?.Invoke(_confirmRect);
                break;
            case ConfirmChromeKind.ModeQr:
                ScanRegionSelected?.Invoke(_confirmRect);
                break;
        }
    }

    private bool TryHandleConfirmDestinationHotkey(Keys keyCode)
    {
        if (!_isConfirmingSelection || _isTyping || _emojiPickerOpen)
            return false;

        ConfirmChromeKind? kind = keyCode switch
        {
            Keys.I => ConfirmChromeKind.ModeImage,
            Keys.O => ConfirmChromeKind.ModeOcr,
            Keys.V => ConfirmChromeKind.ModeVideo,
            Keys.G => ConfirmChromeKind.ModeGif,
            Keys.S => ConfirmChromeKind.ModeScroll,
            Keys.Q => ConfirmChromeKind.ModeQr,
            Keys.Enter => ConfirmChromeKind.Done,
            _ => null
        };
        if (kind is null)
            return false;

        int idx = IndexOfConfirmChrome(kind.Value);
        if (idx < 0 || IsConfirmChromeDisabled(kind.Value))
            return false;

        StartConfirmPress(idx);
        return true;
    }

    /// <summary>Hides a destination pill from the confirm bar and persists via ToastButtonsChanged.</summary>
    private void HideConfirmDestination(ConfirmChromeKind kind)
    {
        var toastKind = ConfirmChromeToToastKind(kind);
        if (toastKind is null)
            return;

        var settings = Services.SettingsService.LoadStatic() ?? new AppSettings();
        var toast = settings.ToastButtons ?? new AppSettings.ToastButtonLayoutSettings();
        if (!ToastButtonLayout.IsVisible(toast, toastKind.Value))
            return;

        // Keep at least one destination: if this is the last visible confirm action, inject Save
        // (or refuse hide when already on Save alone).
        int visibleCount = ToastButtonLayout.ConfirmActionButtons
            .Count(b => ToastButtonLayout.IsVisible(toast, b));
        if (visibleCount <= 1)
        {
            if (toastKind == ToastButtonKind.Save)
                return; // cannot hide the last fallback destination
            ToastButtonLayout.SetVisible(toast, toastKind.Value, false);
            ToastButtonLayout.SetVisible(toast, ToastButtonKind.Save, true);
        }
        else
        {
            ToastButtonLayout.SetVisible(toast, toastKind.Value, false);
        }

        toast.Manual = true;
        settings.ToastButtons = toast;
        ToastButtonsChanged?.Invoke(toast);

        if (_isConfirmingSelection)
        {
            RebuildConfirmChromeKinds();
            RecomputeConfirmButtonWidth();
            LayoutConfirmChromeRects();
            Invalidate();
        }
    }

    private void ShowConfirmDestination(ToastButtonKind toastKind)
    {
        if (!ToastButtonLayout.IsConfirmActionButton(toastKind))
            return;

        var settings = Services.SettingsService.LoadStatic() ?? new AppSettings();
        var toast = settings.ToastButtons ?? new AppSettings.ToastButtonLayoutSettings();
        if (ToastButtonLayout.IsVisible(toast, toastKind))
            return;

        // Prefer left-to-right free slot among confirm destinations.
        if (!ToastButtonLayout.PlaceFromHidden(toast, toastKind, ToastButtonSlot.TopLeft)
            && !ToastButtonLayout.AssignCorner(toast, toastKind, ToastCorner.TopLeft)
            && !ToastButtonLayout.AssignCorner(toast, toastKind, ToastCorner.TopRight)
            && !ToastButtonLayout.AssignCorner(toast, toastKind, ToastCorner.BottomLeft)
            && !ToastButtonLayout.AssignCorner(toast, toastKind, ToastCorner.BottomRight))
        {
            ToastButtonLayout.SetVisible(toast, toastKind, true);
        }

        toast.Manual = true;
        settings.ToastButtons = toast;
        ToastButtonsChanged?.Invoke(toast);

        if (_isConfirmingSelection)
        {
            RebuildConfirmChromeKinds();
            RecomputeConfirmButtonWidth();
            LayoutConfirmChromeRects();
            Invalidate();
        }
    }

    private void ToggleConfirmPillShowLabels()
    {
        _confirmPillShowLabels = !_confirmPillShowLabels;
        ConfirmPillShowLabelsChanged?.Invoke(_confirmPillShowLabels);
        _confirmChromeLayoutDirty = true;
        RecomputeConfirmButtonWidth();
        LayoutConfirmChromeRects();
        Invalidate();
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
    /// Confirm chrome animation:
    /// - Wrapper always gets a slow traveling shine (keeps the dock visible on busy desktops).
    /// - Individual pills only shine while THAT pill is hovered — never a group dim/shine.
    /// </summary>
    private void ConfirmShineTick()
    {
        if (UI.Motion.Disabled || !_isConfirmingSelection)
        {
            _confirmShineTimer.Stop();
            return;
        }

        // Dock wrapper: perpetual slow lap (~3.2s).
        _confirmWrapperShinePhase += (float)(UiChrome.FrameIntervalMs / 3200.0);
        if (_confirmWrapperShinePhase >= 1f) _confirmWrapperShinePhase -= 1f;

        int hov = _hoveredConfirmButton;
        float baseDelta = (float)(UiChrome.FrameIntervalMs / 2200.0);
        int count = Math.Min(ConfirmShineSlots, Math.Max(3, _confirmChromeKinds.Length));
        for (int i = 0; i < count; i++)
        {
            // Buttons stay fully visible (no group dim). Only the hovered one animates a comet.
            _shineMain[i] = 1f;
            if (hov == i)
            {
                _shinePhase[i] += baseDelta * 2f;
                if (_shinePhase[i] >= 1f) _shinePhase[i] -= 1f;
                _shineDup[i] += (1f - _shineDup[i]) * 0.3f;
            }
            else
            {
                _shineDup[i] += (0f - _shineDup[i]) * 0.3f;
                if (_shineDup[i] < 0.01f) _shineDup[i] = 0f;
            }
        }

        // Full chrome union (includes wrapper) so soft glow never leaves a partial smear.
        InvalidateConfirmChromeHover();
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
        // All confirm chrome pills are icon-only; keep a sensible fallback width
        // for MeasureConfirmChromeButtonWidth if a labeled pill is reintroduced.
        _confirmButtonWidth = UiChrome.ScaleInt(112);
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
        // Compatibility wrapper for call sites that still expect the classic triple.
        LayoutConfirmChromeRects();
        Rectangle Find(ConfirmChromeKind kind)
        {
            int idx = IndexOfConfirmChrome(kind);
            return idx >= 0 && idx < _confirmChromeRects.Length
                ? _confirmChromeRects[idx]
                : Rectangle.Empty;
        }
        int primaryIdx = IndexOfPrimaryConfirmAction();
        var primary = primaryIdx >= 0 && primaryIdx < _confirmChromeRects.Length
            ? _confirmChromeRects[primaryIdx]
            : Rectangle.Empty;
        return (primary, Find(ConfirmChromeKind.Retry), Find(ConfirmChromeKind.Cancel));
    }

    private Rectangle UnionConfirmChromeRects()
    {
        var union = Rectangle.Empty;
        foreach (var r in _confirmChromeRects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            union = union.IsEmpty ? r : Rectangle.Union(union, r);
        }
        if (!_confirmChromeWrapperRect.IsEmpty)
            union = union.IsEmpty ? _confirmChromeWrapperRect : Rectangle.Union(union, _confirmChromeWrapperRect);
        return union;
    }

    private void LayoutConfirmChromeRects()
    {
        if (_confirmChromeKinds.Length == 0)
        {
            _confirmChromeRects = Array.Empty<Rectangle>();
            _confirmChromeSeparatorRect1 = Rectangle.Empty;
            _confirmChromeSeparatorRect2 = Rectangle.Empty;
            _confirmChromeWrapperRect = Rectangle.Empty;
            _confirmChromeLayoutDirty = false;
            _confirmChromeLaidOutForRect = Rectangle.Empty;
            return;
        }

        // Paint + hit-test call this often; skip when the selection and chrome set are unchanged.
        if (!_confirmChromeLayoutDirty
            && _confirmChromeLaidOutForRect == _confirmRect
            && _confirmChromeLaidOutWithLabels == _confirmPillShowLabels
            && _confirmChromeRects.Length == _confirmChromeKinds.Length)
            return;

        _confirmChromeLayoutDirty = false;
        _confirmChromeLaidOutForRect = _confirmRect;
        _confirmChromeLaidOutWithLabels = _confirmPillShowLabels;

        int bh = UiChrome.ScaleInt(ConfirmButtonHeight);
        int gap = UiChrome.ScaleInt(ConfirmButtonGap);
        int groupGap = UiChrome.ScaleInt(ConfirmChromeGroupGap);
        var r = _confirmRect;

        int[] widths = new int[_confirmChromeKinds.Length];
        int clusterW = 0;
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            widths[i] = MeasureConfirmChromeButtonWidth(_confirmChromeKinds[i], bh);
            clusterW += widths[i];
            if (i > 0)
                clusterW += GapBeforeConfirmChromeIndex(i, gap, groupGap);
        }

        int gripW = UiChrome.ScaleInt(8);
        int gripGap = UiChrome.ScaleInt(4);
        int gripLen = UiChrome.ScaleInt(16);
        int gripToContentGap = UiChrome.ScaleInt(10);
        clusterW += gripW + gripToContentGap;

        int offset = UiChrome.ScaleInt(12);
        int margin = UiChrome.ScaleInt(10);
        int cursorGap = UiChrome.ScaleInt(10);

        float anchorX = r.Left + _confirmButtonAnchorFracX * r.Width;
        float anchorY = r.Top + _confirmButtonAnchorFracY * r.Height;
        var monitor = GetConfirmButtonMonitorClientBounds(new Point((int)Math.Round(anchorX), (int)Math.Round(anchorY)));
        int minX = monitor.Left + margin;
        int maxX = monitor.Right - margin;
        int minY = monitor.Top + margin;
        int maxY = monitor.Bottom - margin;
        int maxTop = Math.Max(minY, maxY - bh);

        int outsideBelow = r.Bottom + offset;
        int outsideAbove = r.Top - bh - offset;
        bool belowFits = outsideBelow >= minY && outsideBelow + bh <= maxY;
        bool aboveFits = outsideAbove >= minY && outsideAbove + bh <= maxY;

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
            y = preferredY;
        else if (canPlaceInside)
        {
            y = insideY;
            insidePlacement = true;
        }
        else if (farOutside is int farY)
            y = farY;
        else
        {
            y = Math.Clamp((int)Math.Round(anchorY - bh / 2f), minY, maxTop);
            if (y >= r.Top && y + bh <= r.Bottom)
                insidePlacement = true;
        }

        y = Math.Clamp(y, minY, maxTop);

        // Escuadra: keep the destination dock clear of the annotation column corner.
        // When the tools sit on the right of the frame, pills must not extend under that column;
        // when on the left, they must not start under it.
        if (ShowAnnotationChrome && _toolbarRect.Width > 0 && _toolbarRect.Height > 0)
        {
            int clear = UiChrome.ScaleInt(8);
            if (_annotationFrameDockSide == CaptureDockSide.Right)
                maxX = Math.Min(maxX, _toolbarRect.Left - clear);
            else if (_annotationFrameDockSide == CaptureDockSide.Left)
                minX = Math.Max(minX, _toolbarRect.Right + clear);
        }

        // Prefer the pill strip under the capture frame width (L-shape), not past the
        // frame edge that hosts the annotation column.
        if (ShowAnnotationChrome && r.Width > 0)
        {
            if (_annotationFrameDockSide == CaptureDockSide.Right)
                maxX = Math.Min(maxX, r.Right);
            else if (_annotationFrameDockSide == CaptureDockSide.Left)
                minX = Math.Max(minX, r.Left);
        }

        if (maxX < minX)
            maxX = minX;

        // Prefer the dock under the frame, near the release point, but always clamped to
        // [minX, maxX] after escuadra limits (never jump to the far side of the monitor).
        int anchorBtnW = widths[^1];
        int clusterLeft;
        if (insidePlacement)
        {
            clusterLeft = (int)Math.Round(anchorX - clusterW);
        }
        else
        {
            // Center the cluster under the anchor, with a slight bias so the primary (last) pill
            // stays near the release point.
            int anchorCenter = (int)Math.Round(anchorX - anchorBtnW / 2f);
            clusterLeft = anchorCenter - (clusterW - anchorBtnW);
            // Soft preference: keep as much of the dock under the frame as possible.
            int frameCenterLeft = r.Left + (r.Width - clusterW) / 2;
            // Blend toward frame center when the pure anchor would sit far outside the frame band.
            if (clusterLeft + clusterW < r.Left || clusterLeft > r.Right)
                clusterLeft = frameCenterLeft;
        }

        // Hard clamp last — this is what keeps right-side selections from parking the dock
        // on the left of the monitor when maxX was temporarily wrong.
        if (clusterW >= maxX - minX)
            clusterLeft = minX;
        else if (clusterLeft < minX)
            clusterLeft = minX;
        else if (clusterLeft + clusterW > maxX)
            clusterLeft = maxX - clusterW;

        if (_confirmChromeRects.Length != _confirmChromeKinds.Length)
            _confirmChromeRects = new Rectangle[_confirmChromeKinds.Length];

        _confirmGripRect = new Rectangle(clusterLeft, y + (bh - gripLen) / 2, gripW, gripLen);

        int x = clusterLeft + gripW + gripToContentGap;
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            _confirmChromeRects[i] = new Rectangle(x, y, widths[i], bh);
            if (i + 1 < _confirmChromeKinds.Length)
                x += widths[i] + GapBeforeConfirmChromeIndex(i + 1, gap, groupGap);
        }

        _confirmChromeSeparatorRect1 = Rectangle.Empty;
        _confirmChromeSeparatorRect2 = Rectangle.Empty;

        int sepW = Math.Max(1, UiChrome.ScaleInt(1));
        int sepH = Math.Max(UiChrome.ScaleInt(14), (int)(bh * 0.55f));

        // Separator 1: Between ModeQr (index 5) and Done (index 6)
        if (_confirmChromeRects.Length > 6)
        {
            var left = _confirmChromeRects[5];
            var right = _confirmChromeRects[6];
            int mid = (left.Right + right.Left) / 2;
            _confirmChromeSeparatorRect1 = new Rectangle(
                mid - sepW / 2,
                y + (bh - sepH) / 2,
                sepW,
                sepH);
        }

        // Separator 2: Between Done (index 6) and TogglePreview (index 7)
        if (_confirmChromeRects.Length > 7)
        {
            var left = _confirmChromeRects[6];
            var right = _confirmChromeRects[7];
            int mid = (left.Right + right.Left) / 2;
            _confirmChromeSeparatorRect2 = new Rectangle(
                mid - sepW / 2,
                y + (bh - sepH) / 2,
                sepW,
                sepH);
        }

        // Dock wrapper behind all pills so icon buttons stay readable on light/busy wallpapers.
        var pillUnion = Rectangle.Empty;
        foreach (var pr in _confirmChromeRects)
        {
            if (pr.Width <= 0 || pr.Height <= 0) continue;
            pillUnion = pillUnion.IsEmpty ? pr : Rectangle.Union(pillUnion, pr);
        }
        if (pillUnion.IsEmpty)
        {
            _confirmChromeWrapperRect = Rectangle.Empty;
        }
        else
        {
            int padX = UiChrome.ScaleInt(10);
            int padY = UiChrome.ScaleInt(8);
            var union = Rectangle.Union(_confirmGripRect, pillUnion);
            _confirmChromeWrapperRect = Rectangle.Inflate(union, padX, padY);
        }

        if (!_confirmCustomOffset.IsEmpty)
        {
            _confirmGripRect.Offset(_confirmCustomOffset);
            for (int i = 0; i < _confirmChromeRects.Length; i++)
            {
                _confirmChromeRects[i].Offset(_confirmCustomOffset);
            }
            if (!_confirmChromeSeparatorRect1.IsEmpty)
                _confirmChromeSeparatorRect1.Offset(_confirmCustomOffset);
            if (!_confirmChromeSeparatorRect2.IsEmpty)
                _confirmChromeSeparatorRect2.Offset(_confirmCustomOffset);
            if (!_confirmChromeWrapperRect.IsEmpty)
                _confirmChromeWrapperRect.Offset(_confirmCustomOffset);
        }
    }

    /// <summary>Mark confirm chrome for re-layout after move/resize or settings change.</summary>
    private void InvalidateConfirmChromeLayout()
    {
        _confirmChromeLayoutDirty = true;
    }

    /// <summary>
    /// Padding used when invalidating confirm chrome so the wrapper shadow + pill glow
    /// never leave trails when the cluster moves or hover state changes.
    /// </summary>
    private static int ConfirmChromeInvalidatePad => UiChrome.ScaleInt(36);

    /// <summary>Invalidate old + new confirm chrome and selection frames after a move/resize.</summary>
    private void InvalidateConfirmChromeMove(
        Rectangle oldChromeUnion,
        Rectangle newChromeUnion,
        Rectangle oldSelection,
        Rectangle newSelection)
    {
        var dirty = Rectangle.Empty;
        if (!oldChromeUnion.IsEmpty)
            dirty = InflateForRepaint(oldChromeUnion, ConfirmChromeInvalidatePad);
        if (!newChromeUnion.IsEmpty)
        {
            var n = InflateForRepaint(newChromeUnion, ConfirmChromeInvalidatePad);
            dirty = dirty.IsEmpty ? n : Rectangle.Union(dirty, n);
        }
        if (!oldSelection.IsEmpty)
        {
            var s = InflateForRepaint(oldSelection, UiChrome.ScaleInt(24));
            dirty = dirty.IsEmpty ? s : Rectangle.Union(dirty, s);
        }
        if (!newSelection.IsEmpty)
        {
            var s = InflateForRepaint(newSelection, UiChrome.ScaleInt(24));
            dirty = dirty.IsEmpty ? s : Rectangle.Union(dirty, s);
        }
        if (!dirty.IsEmpty)
            Invalidate(dirty);
    }

    private void InvalidateConfirmChromeHover()
    {
        LayoutConfirmChromeRects();
        var union = UnionConfirmChromeRects();
        if (!union.IsEmpty)
            Invalidate(InflateForRepaint(union, ConfirmChromeInvalidatePad));
    }

    /// <summary>Hover entered/left a confirm pill — repaint dock; wrapper timer stays running.</summary>
    private void OnConfirmHoverChanged(int previousHovered)
    {
        _ = previousHovered;
        InvalidateConfirmChromeHover();
        if (!UI.Motion.Disabled && _isConfirmingSelection && !_confirmShineTimer.Enabled)
            _confirmShineTimer.Start();
    }

    private int GapBeforeConfirmChromeIndex(int index, int gap, int groupGap)
    {
        if (index <= 0 || index >= _confirmChromeKinds.Length)
            return gap;

        if (index == 6 || index == 7)
            return groupGap;

        return gap;
    }

    private int HitTestConfirmHandle(Point p)
    {
        // Prefer explicit grip squares (slightly larger hit than the painted dots).
        var handles = GetConfirmHandleRects();
        for (int i = 0; i < handles.Length; i++)
        {
            var h = handles[i];
            h.Inflate(UiChrome.ScaleInt(2), UiChrome.ScaleInt(2));
            if (h.Contains(p)) return i;
        }

        // Then the full perimeter so any point on the border can resize (not only the mid-edge grips).
        return HitTestConfirmFrameBorder(p);
    }

    private int HitTestConfirmButton(Point p)
    {
        if (_confirmDocksHiddenForFrameManip)
            return -1;

        LayoutConfirmChromeRects();
        for (int i = 0; i < _confirmChromeRects.Length; i++)
        {
            if (_confirmChromeRects[i].Contains(p))
                return i;
        }
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
