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
        // Annotation confirm dock: use the stable expanded host so expand/collapse
        // does not resize the layered HWND (ghost trails at high DPI).
        if (ShowAnnotationChrome && !_annotationToolbarHostRect.IsEmpty)
            Add(InflateIfNeeded(_annotationToolbarHostRect, Helpers.UiChrome.ScaleInt(12)));
        else
            Add(InflateIfNeeded(_toolbarRect, Helpers.UiChrome.ScaleInt(12)));
        Add(InflateIfNeeded(GetColorPickerBounds(), Helpers.UiChrome.ScaleInt(12)));
        Add(InflateIfNeeded(GetStrokePickerBounds(), Helpers.UiChrome.ScaleInt(12)));
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
        if (_strokePickerOpen && _strokePickerRect.Contains(p)) return true;
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
    /// Click region for the question mark icon that opens the Quick Start Guide.
    /// </summary>
    private bool IsPointInBrandClickArea(Point location)
    {
        if (_logoRect.IsEmpty && _brandRect.IsEmpty)
            return false;

        if (!_brandRect.IsEmpty && _brandRect.Contains(location))
            return true;

        if (!_logoRect.IsEmpty)
        {
            var hit = _logoRect;
            hit.Inflate(Helpers.UiChrome.ScaleInt(6), Helpers.UiChrome.ScaleInt(6));
            return hit.Contains(location);
        }

        return false;
    }

    /// <summary>
    /// Area of the toolbar reserved for dragging: the dedicated grip handle.
    /// </summary>
    private bool IsPointInToolbarDragArea(Point location)
    {
        if (ShowAnnotationChrome ? HitTestAnnotationDockGrip(location) : HitTestCaptureDockGrip(location))
            return true;

        return false;
    }

    /// <summary>
    /// Cursor over the capture/confirm dock: <see cref="CursorFactory.GrabCursor"/> on designated drag surfaces
    /// (grip, empty branding area); <see cref="Cursors.Hand"/> on clickable controls including brand;
    /// <see cref="Cursors.Default"/> on all dead/background surfaces.
    /// </summary>
    private Cursor? TryGetToolbarHoverCursor(Point location)
    {
        bool overDock = _toolbarRect.Contains(location) || IsPointInToolbarChrome(location);
        if (!overDock)
            return null;

        if ((ShowAnnotationChrome && HitTestAnnotationDockGrip(location))
            || (!ShowAnnotationChrome && HitTestCaptureDockGrip(location)))
        {
            return CursorFactory.GrabCursor;
        }

        if (_menuActivatorRect.Contains(location))
            return Cursors.Hand;

        // Clickable branding (logo + text + 3px margin) -> Hand cursor for quick-start guide.
        if (IsPointInBrandClickArea(location))
            return Cursors.Hand;

        // Dedicated drag zone (around branding or Move button) -> Grab cursor.
        if (IsPointInToolbarDragArea(location))
            return CursorFactory.GrabCursor;

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
        {
            // PaintShadow bleeds ~6–10dip past the dock plate; the layered ToolbarForm composites
            // that alpha on top of overlay chrome. Inflate so size/gear pills stay clear of it
            // (without this, only the gear — further toward the dock — looks "eaten" by a ghost shadow).
            var dockAvoid = _toolbarRect;
            dockAvoid.Inflate(UiChrome.ScaleInt(16), UiChrome.ScaleInt(16));
            list.Add(dockAvoid);
        }
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
                _ = Task.Run(() =>
                {
                    try { WindowDetector.SnapshotWindows(_virtualBounds); }
                    catch { }
                });

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

    /// <summary>
    /// Drops the move/draw-tool hover chrome immediately and dirties its bounds.
    /// Call when a new annotation drag starts so the dashed box + move glyph cannot
    /// linger (or ghost) over the live preview.
    /// </summary>
    private void ClearMoveHoverHighlight()
    {
        if (_moveHoverIndex < 0)
            return;

        if (_moveHoverIndex < _undoStack.Count)
            Invalidate(Rectangle.Inflate(GetAnnotationBounds(_undoStack[_moveHoverIndex]), 40, 40));
        _moveHoverIndex = -1;
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

    /// <summary>Public wrapper so <see cref="ToolbarForm"/> can skip bitmap repaints while an
    /// annotation drag is in flight. Keeping the layered surface untouched mid-drag prevents the
    /// "invisible mask" symptom where the dock's surface composited over the live preview.</summary>
    internal bool IsAnnotationDragInProgress() => IsDraggingAnyAnnotation();

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
        _confirmDoneShowLabel = settings?.ConfirmDoneShowLabel ?? true;
        _rememberAnnotationTool = settings?.RememberAnnotationTool ?? true;
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
        ResetConfirmModesExpanded(collapsed: true);
        ResetAnnotationToolsExpanded(collapsed: true);

        // Annotation column FIRST, then destination pills. Laying out pills before CalcToolbar
        // used the capture-phase toolbar rect and shoved the dock toward the left of the monitor
        // (especially for selections on the right half).
        _confirmChromeLayoutDirty = true;
        CalcToolbar();
        _confirmChromeLayoutDirty = true;
        LayoutConfirmChromeRects();
        RefreshConfirmSizeReadoutRect();
        MarkToolbarRenderDirty();
        // Only present the toolbar where annotation chrome applies. For text-extraction
        // confirm (OCR) there is no drawing surface, and re-presenting would resurface the
        // capture dock (CalcToolbar falls back to capture tools when ShowAnnotationChrome is false).
        if (ShowAnnotationChrome)
        {
            PresentAnnotationToolbarNow();
            EnsureToolbarReady();
            // If nothing was restored, land on Arrow so the sticky trigger is a drawing tool.
            EnsureDefaultAnnotationTool();
        }

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

    /// <summary>Applies Settings → Done pill label without re-entering confirm mode.</summary>
    public void SetConfirmDoneShowLabel(bool show)
    {
        if (_confirmDoneShowLabel == show) return;
        _confirmDoneShowLabel = show;
        _confirmChromeLayoutDirty = true;
        if (_isConfirmingSelection)
        {
            RecomputeConfirmButtonWidth();
            LayoutConfirmChromeRects();
            Invalidate();
        }
    }

    /// <summary>
    /// If confirm mode has no annotation tool selected yet, activate Arrow (or the first drawing tool)
    /// so the sticky trigger slot is meaningful.
    /// </summary>
    private void EnsureDefaultAnnotationTool()
    {
        if (!_isConfirmingSelection || !ShowAnnotationChrome)
            return;

        // Already have a drawing-tool trigger identity.
        if (!string.IsNullOrEmpty(_annotationDrawingToolId)
            && _flyoutTools.Any(t => string.Equals(t.Id, _annotationDrawingToolId, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!string.IsNullOrEmpty(_activeToolId)
            && !IsPinnedAnnotationUtility(_activeToolId)
            && _flyoutTools.Any(t => string.Equals(t.Id, _activeToolId, StringComparison.OrdinalIgnoreCase)))
        {
            RememberAnnotationDrawingToolId(_activeToolId);
            return;
        }

        var preferred = _flyoutTools.FirstOrDefault(t =>
                string.Equals(t.Id, "arrow", StringComparison.OrdinalIgnoreCase))
            ?? _flyoutTools.FirstOrDefault(t =>
                string.Equals(t.Id, "line", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, "draw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Id, "text", StringComparison.OrdinalIgnoreCase))
            ?? _flyoutTools.FirstOrDefault(t => !IsPinnedAnnotationUtility(t.Id));

        if (preferred?.Mode is not null)
        {
            // Don't steal focus from select/eraser — only seed the sticky drawing trigger.
            if (string.IsNullOrEmpty(_activeToolId) || IsPinnedAnnotationUtility(_activeToolId!)
                || !_flyoutTools.Any(t => string.Equals(t.Id, _activeToolId, StringComparison.OrdinalIgnoreCase)))
            {
                SetTool(preferred, showHelpBanner: false);
            }
            RememberAnnotationDrawingToolId(preferred.Id);
        }
    }

    private void ResetAnnotationToolsExpanded(bool collapsed)
    {
        try { _annotationToolsCollapseTimer.Stop(); } catch { }
        try { _annotationToolsExpandTimer.Stop(); } catch { }
        _annotationToolsSuppressHoverExpand = false;
        _annotationToolsExpanded = !collapsed;
        _annotationToolsExpandTarget = collapsed ? 0f : 1f;
        _annotationToolsExpandAmt = _annotationToolsExpandTarget;
        _annotationToolsAnimFrom = _annotationToolsExpandAmt;
    }

    private void ExpandAnnotationTools()
    {
        if (!ShowAnnotationChrome)
            return;
        try { _annotationToolsCollapseTimer.Stop(); } catch { }
        if (_annotationToolsExpanded && _annotationToolsExpandAmt >= 0.999f)
            return;
        _annotationToolsSuppressHoverExpand = false;
        _annotationToolsExpanded = true;
        SetAnnotationToolsExpandTarget(1f);
    }

    private void CollapseAnnotationTools()
    {
        try { _annotationToolsCollapseTimer.Stop(); } catch { }
        if (!_annotationToolsExpanded && _annotationToolsExpandAmt <= 0.001f)
            return;
        _annotationToolsExpanded = false;
        SetAnnotationToolsExpandTarget(0f);
    }

    /// <summary>
    /// Snap-collapse after choosing a secondary tool so it doesn't look like the tool vanished
    /// from the open strip (it moves into the sticky trigger slot).
    /// </summary>
    private void CollapseAnnotationToolsAfterToolPick()
    {
        try { _annotationToolsCollapseTimer.Stop(); } catch { }
        try { _annotationToolsExpandTimer.Stop(); } catch { }
        _annotationToolsSuppressHoverExpand = true;
        _annotationToolsExpanded = false;
        _annotationToolsExpandTarget = 0f;
        ApplyAnnotationToolsExpandAmt(0f);
    }

    private void ScheduleAnnotationToolsCollapse()
    {
        if (!_annotationToolsExpanded && _annotationToolsExpandAmt <= 0.001f)
            return;
        if (_isDraggingToolbar || _colorPickerOpen || _altCapturePopupOpen)
            return;
        if (_confirmContextMenu?.Visible == true || _toolbarContextMenu?.Visible == true)
            return;
        if (_annotationToolsCollapseTimer.Enabled)
            return;
        _annotationToolsCollapseTimer.Stop();
        _annotationToolsCollapseTimer.Interval = AnnotationToolsCollapseDelayMs;
        _annotationToolsCollapseTimer.Start();
    }

    private void CancelAnnotationToolsCollapse()
    {
        try { _annotationToolsCollapseTimer.Stop(); } catch { }
    }

    private void SetAnnotationToolsExpandTarget(float target)
    {
        // Snap only — animating layered HWND size/content caused duplicate ghost bars at 150% DPI.
        target = Math.Clamp(target, 0f, 1f);
        try { _annotationToolsExpandTimer.Stop(); } catch { }
        _annotationToolsExpandTarget = target;
        ApplyAnnotationToolsExpandAmt(target);
    }

    private void AnnotationToolsExpandTick()
    {
        // Animation disabled (see SetAnnotationToolsExpandTarget); keep tick harmless.
        _annotationToolsExpandTimer.Stop();
    }

    private void ApplyAnnotationToolsExpandAmt(float amt)
    {
        amt = Math.Clamp(amt, 0f, 1f);
        if (Math.Abs(_annotationToolsExpandAmt - amt) < 0.0005f)
            return;

        _annotationToolsExpandAmt = amt;
        if (_isConfirmingSelection && ShowAnnotationChrome)
        {
            int pad = UiChrome.ScaledToolbarInnerPadding;
            int buttonSize = UiChrome.ScaledToolbarButtonSize;
            int buttonSpacing = UiChrome.ScaledToolbarButtonSpacing;
            Rectangle screenBounds = _toolbarAnchorArea.IsEmpty ? _virtualBounds : _toolbarAnchorArea;
            CalcAnnotationOnlyToolbar(screenBounds, pad, buttonSize, buttonSpacing);
            PositionToolbarForm();
            MarkToolbarRenderDirty();
            _toolbarForm?.UpdateSurface();
        }
    }

    /// <summary>
    /// Expand only from the drawing-tool trigger (or while over revealed retractable tools).
    /// Color / stroke / eraser / select do not open the strip. Grip and brand do not open it
    /// either, but they sustain an already-open strip so drag / logo click stay usable.
    /// </summary>
    private void UpdateAnnotationToolsHover(Point p)
    {
        if (!_isConfirmingSelection || !ShowAnnotationChrome || _confirmDocksHiddenForFrameManip)
            return;

        if (_colorPickerOpen || _altCapturePopupOpen
            || _confirmContextMenu?.Visible == true
            || _toolbarContextMenu?.Visible == true
            || _isDraggingToolbar)
        {
            CancelAnnotationToolsCollapse();
            return;
        }

        // Grip / logo / brand strip: keep expanded while interacting (same idea as confirm grip).
        if (HitTestAnnotationDockGrip(p)
            || IsPointInBrandClickArea(p)
            || (!_brandRect.IsEmpty && _brandRect.Contains(p)))
        {
            CancelAnnotationToolsCollapse();
            return;
        }

        bool overCluster = IsPointOverAnnotationToolsCluster(p);
        if (_annotationToolsSuppressHoverExpand)
        {
            if (!overCluster)
                _annotationToolsSuppressHoverExpand = false;
            else
            {
                // Stay collapsed until the pointer leaves the trigger/strip area.
                CancelAnnotationToolsCollapse();
                return;
            }
        }

        if (overCluster)
        {
            CancelAnnotationToolsCollapse();
            // Defer the actual expand by ExpandHoverDelayMs so a quick pass through the
            // trigger doesn't pop the strip immediately. Only a sustained hover triggers it.
            if (!_annotationToolsExpanded)
            {
                try
                {
                    _annotationToolsHoverDelayTimer.Stop();
                    _annotationToolsHoverDelayTimer.Start();
                }
                catch { }
            }
        }
        else
        {
            try { _annotationToolsHoverDelayTimer.Stop(); } catch { }
            ScheduleAnnotationToolsCollapse();
        }
    }

    private bool IsPointOverAnnotationToolsCluster(Point p)
    {
        int triggerIdx = GetAnnotationTriggerFlyoutIndex();
        bool onTrigger = false;
        if (triggerIdx >= 0)
        {
            int btn = FlyoutStartIndex + triggerIdx;
            if (btn >= 0 && btn < _toolbarButtons.Length
                && _toolbarButtons[btn].Width > 0
                && _toolbarButtons[btn].Contains(p))
                onTrigger = true;
        }

        // Trigger semantics: only the trigger button can EXPAND a collapsed strip. The full bar
        // background *sustains* an already-open strip so sliding off the last pill doesn't snap
        // it shut. Separate checks prevent the "hover the bar opens the strip" surprise.
        if (onTrigger)
            return true;

        // Background-only sustain: pointer is on the bar's chrome — keep an open strip open,
        // but do not start the open animation from a cold state.
        bool onBar =
            (!_toolbarRect.IsEmpty && _toolbarRect.Contains(p))
            || (!_annotationToolbarHostRect.IsEmpty && _annotationToolbarHostRect.Contains(p));

        if (_annotationToolsExpanded || _annotationToolsExpandAmt > 0.02f)
        {
            if (onBar)
                return true;

            // Keep open while moving through the revealed strip / picking a secondary tool.
            if (!_annotationRetractRevealRect.IsEmpty && _annotationRetractRevealRect.Contains(p))
                return true;

            int start = FlyoutStartIndex;
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                if (!IsRetractableAnnotationFlyoutIndex(i, triggerIdx))
                    continue;
                int btn = start + i;
                if (btn >= _toolbarButtons.Length)
                    continue;
                var r = _toolbarButtons[btn];
                if (r.Width <= 0)
                    continue;
                if (!_annotationRetractRevealRect.IsEmpty && !r.IntersectsWith(_annotationRetractRevealRect))
                    continue;
                if (r.Contains(p))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// After the region is locked for annotation, restore the last Group-1 tool the user used
    /// (if it is still enabled on the bar). Capture-only users never set the preference.
    /// </summary>
    private void TryRestoreLastAnnotationTool()
    {
        if (!_rememberAnnotationTool)
            return;

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
        RememberAnnotationDrawingToolId(tool.Id);
    }

    private bool HasConfirmAnnotations() => _undoStack.Count > 0;

    /// <summary>
    /// Shows a themed confirm over the TopMost capture overlay without the overlay
    /// stealing focus back, centered on the locked frame's monitor.
    /// </summary>
    private bool ShowOverlayConfirm(
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string? iconId,
        bool danger = true)
    {
        bool prevAllowDeactivation = _allowDeactivation;
        _allowDeactivation = true;
        try
        {
            var anchorClient = _confirmRect.Width > 0 && _confirmRect.Height > 0
                ? new Point(
                    _confirmRect.X + Math.Max(0, _confirmRect.Width / 2),
                    _confirmRect.Y + Math.Max(0, _confirmRect.Height / 2))
                : new Point(ClientSize.Width / 2, ClientSize.Height / 2);
            UI.PopupWindowHelper.SetMonitorHintPoint(new Point(
                _virtualBounds.X + anchorClient.X,
                _virtualBounds.Y + anchorClient.Y));

            return UI.ThemedConfirmDialog.Confirm(
                Handle,
                title,
                message,
                primaryText,
                secondaryText,
                danger,
                iconId);
        }
        catch
        {
            return false;
        }
        finally
        {
            UI.PopupWindowHelper.ClearMonitorHintPoint();
            _allowDeactivation = prevAllowDeactivation;
            if (!_allowDeactivation && Visible && !IsDisposed && !Disposing)
            {
                try
                {
                    Activate();
                    Focus();
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Retry / reselect: with annotations, ask first. Without, leave confirm immediately.
    /// </summary>
    private void RequestRetrySelection()
    {
        if (!_isConfirmingSelection)
            return;

        if (HasConfirmAnnotations())
        {
            bool ok = ShowOverlayConfirm(
                LocalizationService.Translate("Retry selection?"),
                LocalizationService.Translate("This will discard the annotations on this capture."),
                LocalizationService.Translate("Retry"),
                LocalizationService.Translate("Cancel"),
                iconId: "redo",
                danger: true);
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

    /// <summary>
    /// Cursor over the confirm modes dock face:
    /// Hand on pills, SizeAll on the grip, Default on dead chrome / padding
    /// (dead chrome remains draggable on mouse-down).
    /// </summary>
    private Cursor? TryGetConfirmChromeHoverCursor(Point location)
    {
        if (_confirmChromeWrapperRect.IsEmpty || !_confirmChromeWrapperRect.Contains(location))
            return null;

        if (HitTestConfirmButton(location) >= 0)
            return Cursors.Hand;

        if (HitTestConfirmDockGrip(location))
            return CursorFactory.GrabCursor;

        // Padding / separators: normal arrow; drag still starts on mouse-down.
        return Cursors.Default;
    }

    /// <summary>True when the pointer is on modes-dock chrome that should drag the dock
    /// (wrapper face excluding action pills).</summary>
    private bool IsPointInConfirmDockDragArea(Point location)
    {
        if (_confirmChromeWrapperRect.IsEmpty || !_confirmChromeWrapperRect.Contains(location))
            return false;
        if (HitTestConfirmButton(location) >= 0)
            return false;
        return true;
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
        // Modes dock face (including padding) is chrome — not the dimmed exterior.
        if (!_confirmChromeWrapperRect.IsEmpty && _confirmChromeWrapperRect.Contains(p))
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
        if (_strokePickerOpen && _strokePickerRect.Contains(p))
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
        ResetConfirmModesExpanded(collapsed: true);
        ResetAnnotationToolsExpanded(collapsed: true);
        ResetConfirmPress();
        CloseAltToolPopup(invalidate: false);
        ClearConfirmSessionAnnotations();
        _hasSelection = false;
        _selectionRect = Rectangle.Empty;
        _selectionEnd = Point.Empty;
        _confirmSizeReadoutRect = Rectangle.Empty;
        _confirmSizeReadoutGripRect = Rectangle.Empty;
        _confirmSizeReadoutChipRect = Rectangle.Empty;
        _confirmOptionsPillRect = Rectangle.Empty;
        InvalidateCenterGripArea(_centerMoveGripRect);
        _centerMoveGripRect = Rectangle.Empty;
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
            ConfirmChromeKind.ModeQr,
            ConfirmChromeKind.ModeScroll,
            ConfirmChromeKind.ModeGif,
            ConfirmChromeKind.ModeVideo,
            ConfirmChromeKind.ModeOcr,
            ConfirmChromeKind.ModeImage,
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

    /// <summary>Destination pill highlighted as "selected" for the tool that locked the region.</summary>
    private ConfirmChromeKind SelectedConfirmModeKind() => _modeBeforeConfirm switch
    {
        CaptureMode.Ocr => ConfirmChromeKind.ModeOcr,
        CaptureMode.Record => ConfirmChromeKind.ModeVideo,
        CaptureMode.RecordGif => ConfirmChromeKind.ModeGif,
        CaptureMode.Scan => ConfirmChromeKind.ModeQr,
        CaptureMode.ScrollCapture => ConfirmChromeKind.ModeScroll,
        _ => ConfirmChromeKind.ModeImage, // Rectangle/Center and any image-producing tool
    };

    /// <summary>
    /// Visual identity of a confirm pill, swapping the retained (non-retractable) ModeImage trigger
    /// slot with the destination of the tool that locked the region. When the user confirms via OCR,
    /// for example, the always-visible trigger renders as OCR and the OCR slot (in the expanded
    /// strip) renders as Image — so no destination appears twice and the trigger reflects the real
    /// source tool. Follows the SLOT regardless of expanded/collapsed state; only click behavior
    /// changes between the two. Layout anchoring and hit-testing keep using the slot kinds.
    /// </summary>
    private ConfirmChromeKind DisplayConfirmChromeKind(ConfirmChromeKind slotKind)
    {
        var sel = SelectedConfirmModeKind();
        if (slotKind == ConfirmChromeKind.ModeImage) return sel;
        if (slotKind == sel) return ConfirmChromeKind.ModeImage;
        return slotKind;
    }

    // When collapsed and a non-image tool locked the region, the retained trigger IS that tool's
    // destination — clicking it dispatches directly. When expanded (or plain Image), the trigger
    // toggles the strip. Only affects click dispatch; visual identity is slot-driven above.
    private bool ConfirmTriggerActsAsDestination => !_confirmModesExpanded
        && SelectedConfirmModeKind() != ConfirmChromeKind.ModeImage;

    // Layout/measurement always uses the SLOT kind (ModeImage), never the swapped display identity.
    // Otherwise the collapsed trigger width would vary with the source tool ("OCR" vs "Image"),
    // shifting the whole dock sideways when alternating modes.

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
        ConfirmChromeKind.Cancel => "close",
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
        // Pill label stays "Scroll" even in Spanish ("Desplazamiento" stretches the dock).
        ConfirmChromeKind.ModeScroll => "Scroll",
        ConfirmChromeKind.ModeQr => "QR",
        _ => ""
    };

    private static string ConfirmChromeTitle(ConfirmChromeKind kind) => kind switch
    {
        ConfirmChromeKind.Cancel => LocalizationService.Translate("Cancel capture"),
        ConfirmChromeKind.Retry => LocalizationService.Translate("Retry selection"),
        ConfirmChromeKind.Done => LocalizationService.Translate("Confirm screenshot"),
        ConfirmChromeKind.TogglePreview => LocalizationService.Translate("Toggle capture preview on confirm"),
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
        ConfirmChromeKind.TogglePreview => "P",
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
        // Done has its own label toggle, independent of the mode-pill labels setting.
        if (kind == ConfirmChromeKind.Done)
            return !_confirmDoneShowLabel;
        return !_confirmPillShowLabels;
    }

    private int MeasureConfirmChromeButtonWidth(ConfirmChromeKind kind, int iconOnlySize)
    {
        if (kind == ConfirmChromeKind.TogglePreview)
        {
            // Toggle-only pill (eye icon was removed): the track + symmetric padding.
            int trackWidth = UiChrome.ScaleInt(34);
            int togglePadX = UiChrome.ScaleInt(12);
            return togglePadX + trackWidth + togglePadX;
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
        // Done lays out label + right check with its own centered padding (see
        // DrawConfirmActionPill); reserve the same horizontal budget so nothing overlaps.
        if (kind == ConfirmChromeKind.Done)
        {
            int h = iconOnlySize;
            int padX = (int)Math.Round(h * DoneLabelPadXFrac);
            int iconW = (int)Math.Round(h * DoneLabelIconFrac);
            int gap = (int)Math.Round(h * DoneLabelGapFrac);
            // No artificial floor: the padding-based width keeps label + check balanced, and any
            // extra floor would otherwise land entirely on the right (visible as a wider gap).
            return padX + textSize.Width + gap + iconW + padX;
        }

        int iconPart = Math.Max(UiChrome.ScaleInt(16), (int)(iconOnlySize * 0.52f));
        int gapStd = UiChrome.ScaleInt(6);
        int padXStd = UiChrome.ScaleInt(12);
        return Math.Max(iconOnlySize + UiChrome.ScaleInt(28), padXStd + iconPart + gapStd + textSize.Width + padXStd);
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
        if (HasConfirmAnnotations())
        {
            bool ok = ShowOverlayConfirm(
                LocalizationService.Translate("Cancel capture?"),
                LocalizationService.Translate("This will discard the annotations on this capture."),
                LocalizationService.Translate("Confirm cancellation"),
                LocalizationService.Translate("Continue selection"),
                iconId: "question",
                danger: true);
            if (!ok)
                return;
        }

        Cancel();
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
                CommitConfirmedSelection(ConfirmCommitAction.Default);
                break;
            case ConfirmChromeKind.ModeImage:
                // The trigger pill's identity is swapped with the destination of the tool that
                // locked the region. Collapsed: dispatches that destination directly (OCR extracts,
                // Video records, ...). Expanded: collapses the modes strip again. Plain Image still
                // toggles only.
                if (ConfirmTriggerActsAsDestination)
                {
                    RunConfirmDestination(SelectedConfirmModeKind());
                    break;
                }
                if (!_confirmModesExpanded)
                    ExpandConfirmModes();
                else
                    CollapseConfirmModes();
                break;
            case ConfirmChromeKind.TogglePreview:
                ToggleConfirmPreview();
                break;
            case ConfirmChromeKind.ModeOcr:
            case ConfirmChromeKind.ModeVideo:
            case ConfirmChromeKind.ModeGif:
            case ConfirmChromeKind.ModeScroll:
            case ConfirmChromeKind.ModeQr:
                // Only OCR <-> Image is a two-way mode SWAP (edit/annotate vs extract text).
                // Video/GIF/Scroll/QR are one-shot destinations: they capture immediately, as
                // before the swap feature. Picking the same destination as the source also captures.
                var dest = DisplayConfirmChromeKind(_confirmChromeKinds[button]);
                var src = SelectedConfirmModeKind();
                bool isSwapPair =
                    (src == ConfirmChromeKind.ModeOcr && dest == ConfirmChromeKind.ModeImage)
                    || (src == ConfirmChromeKind.ModeImage && dest == ConfirmChromeKind.ModeOcr);
                if (isSwapPair)
                    SwitchConfirmDestination(dest);
                else
                    RunConfirmDestination(dest);
                break;
        }
    }

    /// <summary>Switches the confirmed region to a different capture outcome without leaving the
    /// confirm dock. The whole pill set re-derives from the new source mode.</summary>
    private void SwitchConfirmDestination(ConfirmChromeKind newDestination)
    {
        // OCR is the only destination that hides the annotation chrome; everything else keeps it.
        bool wasOcr = _modeBeforeConfirm == CaptureMode.Ocr;
        _modeBeforeConfirm = newDestination switch
        {
            ConfirmChromeKind.ModeOcr => CaptureMode.Ocr,
            _ => CaptureMode.Rectangle,
        };
        bool isOcr = newDestination == ConfirmChromeKind.ModeOcr;

        _confirmModesExpanded = false;
        ResetConfirmModesExpanded(collapsed: true);
        _confirmChromeLayoutDirty = true;

        CalcToolbar();
        LayoutConfirmChromeRects();

        // Annotation chrome only re-presents when entering an annotatable mode from OCR, and the
        // user hadn't hidden it. Entering OCR (hide) is handled by EnsureToolbarReady's early-out.
        if (!isOcr && wasOcr && ShowAnnotationChrome)
        {
            PresentAnnotationToolbarNow();
            EnsureToolbarReady();
            EnsureDefaultAnnotationTool();
        }

        Invalidate();
        try { Update(); } catch { }
    }

    /// <summary>Dispatches a swappable destination pill to its capture action.</summary>
    private void RunConfirmDestination(ConfirmChromeKind kind)
    {
        switch (kind)
        {
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

    private void ToggleConfirmPreview()
    {
        var settings = Services.SettingsService.LoadStatic() ?? new AppSettings();
        bool newValue = !settings.ShowCapturePreview;
        SettingsService.SaveShowCapturePreview(newValue);
        _confirmChromeLayoutDirty = true;
        Invalidate();
    }

    private bool TryHandleConfirmDestinationHotkey(Keys keyCode)
    {
        if (!_isConfirmingSelection || _isTyping || _emojiPickerOpen)
            return false;

        ConfirmChromeKind? kind = keyCode switch
        {
            Keys.I => ConfirmChromeKind.Done, // Image hotkey still captures; the Image pill only toggles modes
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

    private void ToggleRememberAnnotationTool()
    {
        _rememberAnnotationTool = !_rememberAnnotationTool;
        RememberAnnotationToolChanged?.Invoke(_rememberAnnotationTool);
        _toolbarContextMenu?.Close();
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

    private void ToggleConfirmDoneLabel()
    {
        _confirmDoneShowLabel = !_confirmDoneShowLabel;
        ConfirmDoneShowLabelChanged?.Invoke(_confirmDoneShowLabel);
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
    /// Confirm chrome animation: individual pills only shine while THAT pill is hovered —
    /// never a group dim/shine. (Wrapper chrome matches the capture dock — no traveling shine.)
    /// </summary>
    private void ConfirmShineTick()
    {
        if (UI.Motion.Disabled || !_isConfirmingSelection)
        {
            _confirmShineTimer.Stop();
            return;
        }

        // Pause the shine animation while an annotation drag is in flight: each tick invalidates
        // the chrome cluster, and that repaint can composite over (or kick a repaint of pixels
        // that overlap) the live preview near the bottom/right edges of the selection.
        if (IsDraggingAnyAnnotation())
            return;

        _confirmWrapperShinePhase += (float)(UiChrome.FrameIntervalMs / 4000.0);
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
            _confirmChromeLaidOutExpandAmt = -1f;
            return;
        }

        // Paint + hit-test call this often; skip when the selection and chrome set are unchanged.
        if (!_confirmChromeLayoutDirty
            && _confirmChromeLaidOutForRect == _confirmRect
            && _confirmChromeLaidOutWithLabels == _confirmPillShowLabels
            && _confirmChromeLaidOutWithDoneLabel == _confirmDoneShowLabel
            && Math.Abs(_confirmChromeLaidOutExpandAmt - _confirmModesExpandAmt) < 0.0005f
            && _confirmChromeRects.Length == _confirmChromeKinds.Length)
            return;

        _confirmChromeLayoutDirty = false;
        _confirmChromeLaidOutForRect = _confirmRect;
        _confirmChromeLaidOutWithLabels = _confirmPillShowLabels;
        _confirmChromeLaidOutWithDoneLabel = _confirmDoneShowLabel;
        _confirmChromeLaidOutExpandAmt = _confirmModesExpandAmt;

        int bh = UiChrome.ScaleInt(ConfirmButtonHeight);
        int gap = UiChrome.ScaleInt(ConfirmButtonGap);
        int groupGap = UiChrome.ScaleInt(ConfirmChromeGroupGap);
        var r = _confirmRect;
        float expandAmt = Math.Clamp(_confirmModesExpandAmt, 0f, 1f);

        int[] fullWidths = new int[_confirmChromeKinds.Length];
        int[] widths = new int[_confirmChromeKinds.Length];
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            fullWidths[i] = MeasureConfirmChromeButtonWidth(_confirmChromeKinds[i], bh);
            widths[i] = IsRetractableConfirmMode(_confirmChromeKinds[i])
                ? (int)Math.Round(fullWidths[i] * expandAmt)
                : fullWidths[i];
        }

        int[] collapsedWidths = new int[_confirmChromeKinds.Length];
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
            collapsedWidths[i] = IsRetractableConfirmMode(_confirmChromeKinds[i]) ? 0 : fullWidths[i];

        int gripW = UiChrome.ScaleInt(12);
        int gripLen = UiChrome.ScaleInt(22);
        // Keep a clear gutter so the grip doesn't sit flush against Image (collapsed or expanded).
        int gripToContentGap = UiChrome.ScaleInt(18);

        int collapsedClusterW = MeasureConfirmChromeClusterWidth(collapsedWidths, gap, groupGap, gripW, gripToContentGap);

        int offset = UiChrome.ScaleInt(18);
        int margin = UiChrome.ScaleInt(10);

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

        int y;
        bool insidePlacement = false;
        bool sidePlacement = false;

        int insidePad = Math.Max(offset, UiChrome.ScaleInt(ConfirmHandleSize));
        int insideMin = r.Top + insidePad;
        int insideMax = r.Bottom - insidePad - bh;
        bool canPlaceInside = insideMax >= insideMin;
        // Inside fallback docks to the bottom edge of the frame (near the lower border),
        // not to the cursor/release anchor, so a click-picked tall window keeps the
        // confirm pills at the bottom of the selection instead of the middle of the screen.
        int insideY = insideMax;

        // Side fallback for short selections with no room below: park the pill strip beside
        // the frame on the side opposite the annotation column so it stays clear of it.
        // Use the collapsed width for side fit so expanding modes grow left without flipping sides.
        int sideGap = UiChrome.ScaleInt(12);
        int sideLeft = r.Left - sideGap - collapsedClusterW;
        int sideRight = r.Right + sideGap;
        bool leftSideFits = sideLeft >= monitor.Left + margin;
        bool rightSideFits = sideRight + collapsedClusterW <= monitor.Right - margin;
        bool annotationRight = _annotationFrameDockSide == CaptureDockSide.Right;
        bool oppositeSideFits = annotationRight ? leftSideFits : rightSideFits;

        // Priority: below the frame (near the lower edge) -> inside the frame docked to its
        // bottom edge -> beside the frame -> above -> last-resort clamp to the monitor.
        if (belowFits)
        {
            y = outsideBelow;
        }
        else if (canPlaceInside)
        {
            y = insideY;
            insidePlacement = true;
        }
        else if (oppositeSideFits)
        {
            y = Math.Clamp(r.Bottom - bh, minY, maxTop);
            sidePlacement = true;
        }
        else if (aboveFits)
        {
            y = outsideAbove;
        }
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

        // Prefer the pill strip under the capture frame width (L-shape), not past the frame edge
        // that hosts the annotation column. This edge clamp is GEOMETRIC (about the frame, not the
        // annotation bar), so it must apply even when the annotation chrome is hidden (OCR) — else
        // the dock floats ~anchorW/2 right of the frame and jumps left when the bar appears.
        // Derive the side from frame/monitor geometry since _annotationFrameDockSide is stale when
        // the annotation toolbar never ran (OCR flow).
        if (r.Width > 0)
        {
            bool effectiveRight = ShowAnnotationChrome
                ? _annotationFrameDockSide == CaptureDockSide.Right
                : r.Right + collapsedClusterW <= maxX; // annotation chrome would sit right if it fits
            if (effectiveRight)
                maxX = Math.Min(maxX, r.Right);
            else
                minX = Math.Max(minX, r.Left - collapsedClusterW - Math.Max(UiChrome.ScaleInt(20), r.Width / 3));
        }

        if (maxX < minX)
            maxX = minX;

        // Anchor Image + actions using the collapsed strip so alternate modes grow to the left
        // of Image without shoving Preview / Done / Cancel.
        int imageIdx = IndexOfConfirmChrome(ConfirmChromeKind.ModeImage);
        int anchorBtnW = imageIdx >= 0 ? fullWidths[imageIdx] : (widths.Length > 0 ? widths[^1] : bh);
        int collapsedClusterLeft = ResolveConfirmChromeClusterLeft(
            collapsedClusterW, anchorBtnW, r, anchorX, minX, maxX,
            sidePlacement, insidePlacement, annotationRight, sideLeft, sideRight);

        int imageLeftCollapsed = collapsedClusterLeft + gripW + gripToContentGap;
        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            if (i == imageIdx) break;
            if (collapsedWidths[i] <= 0) continue;
            imageLeftCollapsed += collapsedWidths[i] + GapBeforeConfirmChromeIndex(i + 1, gap, groupGap, collapsedWidths);
        }

        // Place ModeImage (and everything to its right) at the collapsed Image X.
        if (_confirmChromeRects.Length != _confirmChromeKinds.Length)
            _confirmChromeRects = new Rectangle[_confirmChromeKinds.Length];

        if (imageIdx < 0)
        {
            // Fallback: pack left-to-right from collapsed cluster origin.
            _confirmGripRect = new Rectangle(collapsedClusterLeft, y + (bh - gripLen) / 2, gripW, gripLen);
            int xFallback = collapsedClusterLeft + gripW + gripToContentGap;
            for (int i = 0; i < _confirmChromeKinds.Length; i++)
            {
                _confirmChromeRects[i] = widths[i] > 0
                    ? new Rectangle(xFallback, y, widths[i], bh)
                    : Rectangle.Empty;
                if (i + 1 < _confirmChromeKinds.Length)
                    xFallback += widths[i] + GapBeforeConfirmChromeIndex(i + 1, gap, groupGap, widths);
            }
        }
        else
        {
            int x = imageLeftCollapsed;
            for (int i = imageIdx; i < _confirmChromeKinds.Length; i++)
            {
                _confirmChromeRects[i] = widths[i] > 0
                    ? new Rectangle(x, y, widths[i], bh)
                    : Rectangle.Empty;
                if (i + 1 < _confirmChromeKinds.Length)
                    x += widths[i] + GapBeforeConfirmChromeIndex(i + 1, gap, groupGap, widths);
            }

            // Retractable modes grow to the left of Image.
            int cursor = imageLeftCollapsed;
            for (int i = imageIdx - 1; i >= 0; i--)
            {
                int g = GapBeforeConfirmChromeIndex(i + 1, gap, groupGap, widths);
                if (widths[i] <= 0)
                {
                    _confirmChromeRects[i] = Rectangle.Empty;
                    continue;
                }
                cursor -= g + widths[i];
                _confirmChromeRects[i] = new Rectangle(cursor, y, widths[i], bh);
            }

            int contentLeft = cursor;
            for (int i = 0; i < imageIdx; i++)
            {
                if (widths[i] > 0)
                {
                    contentLeft = _confirmChromeRects[i].Left;
                    break;
                }
            }
            if (imageIdx >= 0 && widths[imageIdx] > 0)
                contentLeft = Math.Min(contentLeft, _confirmChromeRects[imageIdx].Left);

            _confirmGripRect = new Rectangle(
                contentLeft - gripToContentGap - gripW,
                y + (bh - gripLen) / 2,
                gripW,
                gripLen);
        }

        // If expanding left pushed past the monitor, shift the whole strip right.
        int leftmost = _confirmGripRect.Left;
        for (int i = 0; i < _confirmChromeRects.Length; i++)
        {
            if (_confirmChromeRects[i].Width > 0)
                leftmost = Math.Min(leftmost, _confirmChromeRects[i].Left);
        }
        int rightmost = leftmost;
        for (int i = 0; i < _confirmChromeRects.Length; i++)
        {
            if (_confirmChromeRects[i].Width > 0)
                rightmost = Math.Max(rightmost, _confirmChromeRects[i].Right);
        }
        rightmost = Math.Max(rightmost, _confirmGripRect.Right);

        int shift = 0;
        if (leftmost < minX)
            shift = minX - leftmost;
        else if (rightmost > maxX)
            shift = maxX - rightmost;
        if (shift != 0)
        {
            _confirmGripRect.Offset(shift, 0);
            for (int i = 0; i < _confirmChromeRects.Length; i++)
            {
                if (_confirmChromeRects[i].Width > 0)
                    _confirmChromeRects[i].Offset(shift, 0);
            }
        }

        _confirmChromeSeparatorRect1 = Rectangle.Empty;
        _confirmChromeSeparatorRect2 = Rectangle.Empty;

        int sepW = Math.Max(1, UiChrome.ScaleInt(1));
        int sepH = Math.Max(UiChrome.ScaleInt(14), (int)(bh * 0.55f));
        PlaceConfirmChromeSeparator(ConfirmChromeKind.ModeImage, ConfirmChromeKind.TogglePreview, y, bh, sepW, sepH, ref _confirmChromeSeparatorRect1);
        PlaceConfirmChromeSeparator(ConfirmChromeKind.TogglePreview, ConfirmChromeKind.Retry, y, bh, sepW, sepH, ref _confirmChromeSeparatorRect2);

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

    private int MeasureConfirmChromeClusterWidth(int[] widths, int gap, int groupGap, int gripW, int gripToContentGap)
    {
        int clusterW = gripW + gripToContentGap;
        bool any = false;
        for (int i = 0; i < _confirmChromeKinds.Length && i < widths.Length; i++)
        {
            if (widths[i] <= 0) continue;
            if (any)
                clusterW += GapBeforeConfirmChromeIndex(i, gap, groupGap, widths);
            clusterW += widths[i];
            any = true;
        }
        return clusterW;
    }

    private int ResolveConfirmChromeClusterLeft(
        int clusterW,
        int anchorBtnW,
        Rectangle r,
        float anchorX,
        int minX,
        int maxX,
        bool sidePlacement,
        bool insidePlacement,
        bool annotationRight,
        int sideLeft,
        int sideRight)
    {
        int clusterLeft;
        if (sidePlacement)
        {
            clusterLeft = annotationRight ? sideLeft : sideRight;
        }
        else if (insidePlacement)
        {
            clusterLeft = (int)Math.Round(anchorX - clusterW);
        }
        else
        {
            int anchorCenter = (int)Math.Round(anchorX - anchorBtnW / 2f);
            clusterLeft = anchorCenter - (clusterW - anchorBtnW);
            int frameCenterLeft = r.Left + (r.Width - clusterW) / 2;
            if (clusterLeft + clusterW < r.Left || clusterLeft > r.Right)
                clusterLeft = frameCenterLeft;
        }

        if (clusterW >= maxX - minX)
            clusterLeft = minX;
        else if (clusterLeft < minX)
            clusterLeft = minX;
        else if (clusterLeft + clusterW > maxX)
            clusterLeft = maxX - clusterW;

        return clusterLeft;
    }

    private void PlaceConfirmChromeSeparator(
        ConfirmChromeKind leftKind,
        ConfirmChromeKind rightKind,
        int y,
        int bh,
        int sepW,
        int sepH,
        ref Rectangle dest)
    {
        int leftIdx = IndexOfConfirmChrome(leftKind);
        int rightIdx = IndexOfConfirmChrome(rightKind);
        if (leftIdx < 0 || rightIdx < 0
            || leftIdx >= _confirmChromeRects.Length
            || rightIdx >= _confirmChromeRects.Length)
            return;

        var left = _confirmChromeRects[leftIdx];
        var right = _confirmChromeRects[rightIdx];
        if (left.Width <= 0 || right.Width <= 0)
            return;

        int mid = (left.Right + right.Left) / 2;
        dest = new Rectangle(
            mid - sepW / 2,
            y + (bh - sepH) / 2,
            sepW,
            sepH);
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

    private static bool IsRetractableConfirmMode(ConfirmChromeKind kind) => kind is
        ConfirmChromeKind.ModeOcr or ConfirmChromeKind.ModeVideo or ConfirmChromeKind.ModeGif
        or ConfirmChromeKind.ModeScroll or ConfirmChromeKind.ModeQr;

    private static bool IsConfirmModesClusterKind(ConfirmChromeKind kind)
        => kind == ConfirmChromeKind.ModeImage || IsRetractableConfirmMode(kind);

    private void ResetConfirmModesExpanded(bool collapsed)
    {
        try { _confirmModesCollapseTimer.Stop(); } catch { }
        try { _confirmModesExpandTimer.Stop(); } catch { }
        _confirmModesExpanded = !collapsed;
        _confirmModesExpandTarget = collapsed ? 0f : 1f;
        _confirmModesExpandAmt = _confirmModesExpandTarget;
        _confirmModesAnimFrom = _confirmModesExpandAmt;
        _confirmChromeLayoutDirty = true;
    }

    private void ExpandConfirmModes()
    {
        try { _confirmModesCollapseTimer.Stop(); } catch { }
        if (_confirmModesExpanded && _confirmModesExpandTarget >= 1f)
            return;

        _confirmModesExpanded = true;
        SetConfirmModesExpandTarget(1f);
    }

    private void CollapseConfirmModes()
    {
        try { _confirmModesCollapseTimer.Stop(); } catch { }
        if (!_confirmModesExpanded && _confirmModesExpandTarget <= 0f)
            return;

        _confirmModesExpanded = false;
        SetConfirmModesExpandTarget(0f);
    }

    private void ScheduleConfirmModesCollapse()
    {
        if (!_confirmModesExpanded && _confirmModesExpandAmt <= 0.001f)
            return;
        if (_isDraggingConfirm)
            return;
        if (_confirmContextMenu?.Visible == true || _toolbarContextMenu?.Visible == true)
            return;
        if (_confirmModesCollapseTimer.Enabled)
            return;
        _confirmModesCollapseTimer.Stop();
        _confirmModesCollapseTimer.Interval = ConfirmModesCollapseDelayMs;
        _confirmModesCollapseTimer.Start();
    }

    private void CancelConfirmModesCollapse()
    {
        try { _confirmModesCollapseTimer.Stop(); } catch { }
    }

    private void SetConfirmModesExpandTarget(float target)
    {
        target = Math.Clamp(target, 0f, 1f);
        // Already heading here — don't restart the in-flight animation from hover spam.
        if (Math.Abs(_confirmModesExpandTarget - target) < 0.0005f)
        {
            if (Math.Abs(_confirmModesExpandAmt - target) < 0.0005f)
                return;
            if (_confirmModesExpandTimer.Enabled)
                return;
        }

        _confirmModesExpandTarget = target;
        if (UI.Motion.Disabled)
        {
            try { _confirmModesExpandTimer.Stop(); } catch { }
            ApplyConfirmModesExpandAmt(target);
            return;
        }

        _confirmModesAnimFrom = _confirmModesExpandAmt;
        _confirmModesAnimStart = DateTime.UtcNow;
        if (!_confirmModesExpandTimer.Enabled)
            _confirmModesExpandTimer.Start();
    }

    private void ConfirmModesExpandTick()
    {
        if (!_isConfirmingSelection)
        {
            _confirmModesExpandTimer.Stop();
            return;
        }

        float elapsed = (float)(DateTime.UtcNow - _confirmModesAnimStart).TotalMilliseconds;
        float t = Math.Clamp(elapsed / ConfirmModesExpandAnimMs, 0f, 1f);
        // Smoothstep for a snappy open/close without feeling linear.
        float eased = t * t * (3f - 2f * t);
        float amt = _confirmModesAnimFrom + (_confirmModesExpandTarget - _confirmModesAnimFrom) * eased;
        ApplyConfirmModesExpandAmt(amt);

        if (t >= 1f)
        {
            ApplyConfirmModesExpandAmt(_confirmModesExpandTarget);
            _confirmModesExpandTimer.Stop();
        }
    }

    private void ApplyConfirmModesExpandAmt(float amt)
    {
        amt = Math.Clamp(amt, 0f, 1f);
        if (Math.Abs(_confirmModesExpandAmt - amt) < 0.0005f)
            return;

        var oldUnion = UnionConfirmChromeRects();
        if (!_confirmChromeWrapperRect.IsEmpty)
            oldUnion = oldUnion.IsEmpty
                ? InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad)
                : Rectangle.Union(oldUnion, InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad));
        else if (!oldUnion.IsEmpty)
            oldUnion = InflateForRepaint(oldUnion, ConfirmChromeInvalidatePad);

        _confirmModesExpandAmt = amt;
        _confirmChromeLayoutDirty = true;
        LayoutConfirmChromeRects();

        var newUnion = UnionConfirmChromeRects();
        if (!_confirmChromeWrapperRect.IsEmpty)
            newUnion = newUnion.IsEmpty
                ? InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad)
                : Rectangle.Union(newUnion, InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad));
        else if (!newUnion.IsEmpty)
            newUnion = InflateForRepaint(newUnion, ConfirmChromeInvalidatePad);

        var dirty = Rectangle.Empty;
        if (!oldUnion.IsEmpty) dirty = oldUnion;
        if (!newUnion.IsEmpty)
            dirty = dirty.IsEmpty ? newUnion : Rectangle.Union(dirty, newUnion);
        if (!dirty.IsEmpty)
            Invalidate(dirty);
    }

    /// <summary>
    /// Expand alternate modes while the pointer is over Image / OCR…QR; collapse shortly after leaving that cluster.
    /// Hovering (or dragging) the confirm dock grip keeps modes open so the strip doesn't collapse mid-drag.
    /// </summary>
    private void UpdateConfirmModesHover(Point p)
    {
        if (!_isConfirmingSelection || _confirmDocksHiddenForFrameManip)
            return;

        if (_confirmContextMenu?.Visible == true || _toolbarContextMenu?.Visible == true)
        {
            CancelConfirmModesCollapse();
            return;
        }

        if (_isDraggingConfirm || HitTestConfirmDockGrip(p))
        {
            CancelConfirmModesCollapse();
            return;
        }

        if (IsPointOverConfirmModesCluster(p))
        {
            CancelConfirmModesCollapse();
            // Defer the actual expand by ExpandHoverDelayMs so a quick pass through the
            // cluster doesn't pop the strip immediately. Only a sustained hover triggers it.
            if (!_confirmModesExpanded)
            {
                try
                {
                    _confirmModesHoverDelayTimer.Stop();
                    _confirmModesHoverDelayTimer.Start();
                }
                catch { }
            }
        }
        else
        {
            try { _confirmModesHoverDelayTimer.Stop(); } catch { }
            ScheduleConfirmModesCollapse();
        }
    }

    private bool IsPointOverConfirmModesCluster(Point p)
    {
        LayoutConfirmChromeRects();
        for (int i = 0; i < _confirmChromeKinds.Length && i < _confirmChromeRects.Length; i++)
        {
            if (!IsConfirmModesClusterKind(_confirmChromeKinds[i]))
                continue;
            var rect = _confirmChromeRects[i];
            if (rect.Width > 0 && rect.Contains(p))
                return true;
        }

        // Same affordance as the annotation toolbar: hovering any pixel inside the dock's
        // wrapper background keeps an already-open cluster open, but the wrapper alone does
        // NOT expand a collapsed one. Otherwise a casual cross of the dock looks "auto-open".
        if ((_confirmModesExpanded || _confirmModesExpandAmt > 0.02f)
            && !_confirmChromeWrapperRect.IsEmpty
            && _confirmChromeWrapperRect.Contains(p))
            return true;

        return false;
    }

    private int GapBeforeConfirmChromeIndex(int index, int gap, int groupGap, int[] widths)
    {
        if (index <= 0 || index >= _confirmChromeKinds.Length)
            return gap;
        if (index >= widths.Length)
            return gap;

        // Skip gaps next to collapsed (zero-width) retractable modes.
        if (widths[index] <= 0 || widths[index - 1] <= 0)
            return 0;

        var kind = _confirmChromeKinds[index];
        if (kind == ConfirmChromeKind.TogglePreview || kind == ConfirmChromeKind.Retry)
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
            var rect = _confirmChromeRects[i];
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            if (rect.Contains(p))
                return i;
        }
        return -1;
    }

    /// <summary>Extra padding around painted grip dots so the drag affordance is easier to hit.</summary>
    private static int DockGripHitInflate => UiChrome.ScaleInt(10);

    private static Rectangle InflateDockGripHit(Rectangle grip)
    {
        if (grip.IsEmpty || grip.Width <= 0 || grip.Height <= 0)
            return Rectangle.Empty;
        var hit = grip;
        hit.Inflate(DockGripHitInflate, DockGripHitInflate);
        return hit;
    }

    private bool HitTestConfirmDockGrip(Point p)
        => !_confirmGripRect.IsEmpty && InflateDockGripHit(_confirmGripRect).Contains(p);

    private bool HitTestAnnotationDockGrip(Point p)
        => !_annotationGripRect.IsEmpty && InflateDockGripHit(_annotationGripRect).Contains(p);

    private bool HitTestCaptureDockGrip(Point p)
        => !_captureGripRect.IsEmpty && InflateDockGripHit(_captureGripRect).Contains(p);

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
