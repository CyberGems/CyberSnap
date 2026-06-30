using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.UI.Editor;

namespace CyberSnap.UI.Controls;

public sealed partial class AnnotationCanvas
{
    // ── Scrollbar overlay state ───────────────────────────────────────────
    //
    // Custom-painted scrollbars that float over the canvas. They mirror
    // the current pan/zoom so the user can see — and grab — scroll position.
    // Thin (3 px) at rest, expanding to 8 px on hover with a smooth
    // animation. Auto-hide after 1 s of inactivity; optionally always-on.

    private const int ScrollbarMargin        = 4;   // px from canvas edge
    private const int ScrollbarThumbMinLen   = 24;  // minimum grabbable thumb length
    private const int ScrollbarHitZone       = 14;  // invisible hit-test width
    private const int ScrollbarIdleThickness = 3;
    private const int ScrollbarHoverThickness = 8;
    private const int ScrollbarCornerGap     = 14;  // reserved corner square
    private const int ScrollbarAutoHideMs    = 1000; // 1 s

    private bool  _scrollbarHoverH;
    private bool  _scrollbarHoverV;
    private bool  _scrollbarDragH;
    private bool  _scrollbarDragV;
    private float _scrollbarDragStartPan;     // _pan component when drag began
    private int   _scrollbarDragStartMouse;   // mouse coord when drag began
    private float _scrollbarFadeOpacity;      // 0 = hidden, 1 = visible
    private float _scrollbarHoverProgressH;   // 0..1 for hover expand animation
    private float _scrollbarHoverProgressV;
    private System.Windows.Forms.Timer? _scrollbarFadeTimer;
    private System.Windows.Forms.Timer? _scrollbarAnimTimer;
    private long  _scrollbarLastActivityTick; // Environment.TickCount64

    private bool _showScrollbarsAlways;
    /// <summary>When true, the scrollbars remain visible at all times instead of
    /// auto-fading after inactivity. Toggled from the editor's View menu.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowScrollbarsAlways
    {
        get => _showScrollbarsAlways;
        set
        {
            if (_showScrollbarsAlways == value) return;
            _showScrollbarsAlways = value;
            if (_showScrollbarsAlways)
            {
                _scrollbarFadeOpacity = 1f;
                _scrollbarFadeTimer?.Stop();
            }
            Invalidate();
        }
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    /// <summary>Whether the horizontal scrollbar should be shown (image wider than viewport).</summary>
    private bool ScrollbarVisibleH
    {
        get
        {
            if (_baseBitmap is null) return false;
            float contentW = (float)(_baseBitmap.Width * _zoom);
            return contentW > ClientSize.Width + 0.5f;
        }
    }

    /// <summary>Whether the vertical scrollbar should be shown (image taller than viewport).</summary>
    private bool ScrollbarVisibleV
    {
        get
        {
            if (_baseBitmap is null) return false;
            float contentH = (float)(_baseBitmap.Height * _zoom);
            return contentH > ClientSize.Height + 0.5f;
        }
    }

    /// <summary>Track rectangle for the horizontal scrollbar (screen coords, full width).</summary>
    private RectangleF ScrollbarTrackH
    {
        get
        {
            int reserveRight = ScrollbarVisibleV ? ScrollbarCornerGap : 0;
            float trackW = ClientSize.Width - ScrollbarMargin * 2 - reserveRight;
            float y = ClientSize.Height - ScrollbarMargin - ScrollbarHitZone;
            return new RectangleF(ScrollbarMargin, y, Math.Max(1, trackW), ScrollbarHitZone);
        }
    }

    /// <summary>Track rectangle for the vertical scrollbar (screen coords, full height).</summary>
    private RectangleF ScrollbarTrackV
    {
        get
        {
            int reserveBottom = ScrollbarVisibleH ? ScrollbarCornerGap : 0;
            float trackH = ClientSize.Height - ScrollbarMargin * 2 - reserveBottom;
            float x = ClientSize.Width - ScrollbarMargin - ScrollbarHitZone;
            return new RectangleF(x, ScrollbarMargin, ScrollbarHitZone, Math.Max(1, trackH));
        }
    }

    /// <summary>Computes the thumb rect for one axis.
    /// <paramref name="track"/> is the track rect, <paramref name="horizontal"/> selects the axis.
    /// Returns the visible thumb rect (pill shape) in screen coords.</summary>
    private RectangleF GetScrollbarThumb(RectangleF track, bool horizontal)
    {
        if (_baseBitmap is null) return RectangleF.Empty;

        float contentSize, viewSize, panOffset, trackLen;
        if (horizontal)
        {
            contentSize = (float)(_baseBitmap.Width * _zoom);
            viewSize    = ClientSize.Width;
            panOffset   = -_pan.X;
            trackLen    = track.Width;
        }
        else
        {
            contentSize = (float)(_baseBitmap.Height * _zoom);
            viewSize    = ClientSize.Height;
            panOffset   = -_pan.Y;
            trackLen    = track.Height;
        }

        if (contentSize <= viewSize) return RectangleF.Empty;

        float thumbRatio = Math.Clamp(viewSize / contentSize, 0.05f, 1f);
        float thumbLen   = Math.Max(ScrollbarThumbMinLen, trackLen * thumbRatio);
        float scrollRange = contentSize - viewSize;
        float scrollFraction = Math.Clamp(panOffset / scrollRange, 0f, 1f);
        float thumbPos   = scrollFraction * (trackLen - thumbLen);

        // Animated thickness (idle → hover)
        float hoverProg  = horizontal ? _scrollbarHoverProgressH : _scrollbarHoverProgressV;
        float thickness  = ScrollbarIdleThickness + (ScrollbarHoverThickness - ScrollbarIdleThickness) * hoverProg;

        if (horizontal)
        {
            float x = track.X + thumbPos;
            float y = track.Bottom - ScrollbarMargin - thickness;
            return new RectangleF(x, y, thumbLen, thickness);
        }
        else
        {
            float x = track.Right - ScrollbarMargin - thickness;
            float y = track.Y + thumbPos;
            return new RectangleF(x, y, thickness, thumbLen);
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    /// <summary>Paints both scrollbar overlays on top of everything else.</summary>
    private void RenderScrollbars(Graphics g)
    {
        if (_scrollbarFadeOpacity <= 0f) return;
        bool hVisible = ScrollbarVisibleH;
        bool vVisible = ScrollbarVisibleV;
        if (!hVisible && !vVisible) return;

        var savedSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            if (hVisible)
                RenderScrollbarAxis(g, horizontal: true);
            if (vVisible)
                RenderScrollbarAxis(g, horizontal: false);
        }
        finally
        {
            g.SmoothingMode = savedSmoothing;
        }
    }

    private void RenderScrollbarAxis(Graphics g, bool horizontal)
    {
        var track = horizontal ? ScrollbarTrackH : ScrollbarTrackV;
        var thumb = GetScrollbarThumb(track, horizontal);
        if (thumb.Width < 1 || thumb.Height < 1) return;

        bool isHover = horizontal ? _scrollbarHoverH : _scrollbarHoverV;
        bool isDrag  = horizontal ? _scrollbarDragH  : _scrollbarDragV;
        float hoverProg = horizontal ? _scrollbarHoverProgressH : _scrollbarHoverProgressV;

        // Track background — only visible on hover/drag
        if (hoverProg > 0.05f)
        {
            int trackAlpha = (int)(25 * hoverProg * _scrollbarFadeOpacity);
            using var trackBrush = new SolidBrush(Color.FromArgb(trackAlpha, 128, 128, 128));

            RectangleF trackPill;
            float trackThickness = ScrollbarHoverThickness + 4;
            if (horizontal)
            {
                trackPill = new RectangleF(
                    track.X, track.Bottom - ScrollbarMargin - trackThickness,
                    track.Width, trackThickness);
            }
            else
            {
                trackPill = new RectangleF(
                    track.Right - ScrollbarMargin - trackThickness, track.Y,
                    trackThickness, track.Height);
            }
            using var trackPath = RoundedRectF(trackPill, trackThickness / 2f);
            g.FillPath(trackBrush, trackPath);
        }

        // Thumb
        int thumbAlpha;
        Color thumbColor;
        if (isDrag)
        {
            thumbAlpha = (int)(255 * _scrollbarFadeOpacity);
            thumbColor = EditorColors.Accent;
        }
        else if (isHover)
        {
            thumbAlpha = (int)(200 * _scrollbarFadeOpacity);
            thumbColor = EditorColors.Accent;
        }
        else
        {
            thumbAlpha = (int)(100 * _scrollbarFadeOpacity);
            thumbColor = EditorColors.TextMuted;
        }

        float radius = Math.Min(thumb.Width, thumb.Height) / 2f;
        using var thumbBrush = new SolidBrush(Color.FromArgb(thumbAlpha, thumbColor.R, thumbColor.G, thumbColor.B));
        using var thumbPath = RoundedRectF(thumb, radius);
        g.FillPath(thumbBrush, thumbPath);
    }

    /// <summary>Creates a rounded-rect path from a RectangleF (the integer version in EditorPaint
    /// doesn't accept floats, and we need sub-pixel accuracy for the scrollbar).</summary>
    private static GraphicsPath RoundedRectF(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0.5f, radius * 2);
        if (rect.Width < d || rect.Height < d)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── Hit-testing ───────────────────────────────────────────────────────

    private enum ScrollbarHit { None, ThumbH, ThumbV, TrackH, TrackV }

    /// <summary>Checks if a client point is inside a scrollbar zone.</summary>
    private ScrollbarHit HitTestScrollbar(Point client)
    {
        // Vertical first (the corner square belongs to neither when both overlap)
        if (ScrollbarVisibleV)
        {
            var trackV = ScrollbarTrackV;
            if (trackV.Contains(client))
            {
                var thumbV = GetScrollbarThumb(trackV, horizontal: false);
                // Expand thumb hit-test to the full hit zone width
                var thumbHitV = new RectangleF(trackV.X, thumbV.Y, trackV.Width, thumbV.Height);
                return thumbHitV.Contains(client) ? ScrollbarHit.ThumbV : ScrollbarHit.TrackV;
            }
        }
        if (ScrollbarVisibleH)
        {
            var trackH = ScrollbarTrackH;
            if (trackH.Contains(client))
            {
                var thumbH = GetScrollbarThumb(trackH, horizontal: true);
                var thumbHitH = new RectangleF(thumbH.X, trackH.Y, thumbH.Width, trackH.Height);
                return thumbHitH.Contains(client) ? ScrollbarHit.ThumbH : ScrollbarHit.TrackH;
            }
        }
        return ScrollbarHit.None;
    }

    // ── Mouse interaction ─────────────────────────────────────────────────

    /// <summary>Called at the start of OnMouseDown. Returns true if the scrollbar consumed the click.</summary>
    private bool ScrollbarMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return false;
        if (!ScrollbarVisibleH && !ScrollbarVisibleV) return false;
        if (_scrollbarFadeOpacity <= 0f && !_showScrollbarsAlways) return false;

        var hit = HitTestScrollbar(e.Location);
        switch (hit)
        {
            case ScrollbarHit.ThumbH:
                _scrollbarDragH = true;
                _scrollbarDragStartPan = _pan.X;
                _scrollbarDragStartMouse = e.X;
                Capture = true;
                Invalidate();
                return true;

            case ScrollbarHit.ThumbV:
                _scrollbarDragV = true;
                _scrollbarDragStartPan = _pan.Y;
                _scrollbarDragStartMouse = e.Y;
                Capture = true;
                Invalidate();
                return true;

            case ScrollbarHit.TrackH:
                DoPageScrollH(e.X);
                return true;

            case ScrollbarHit.TrackV:
                DoPageScrollV(e.Y);
                return true;
        }
        return false;
    }

    /// <summary>Called at the start of OnMouseMove. Returns true if a scrollbar drag is active.</summary>
    private bool ScrollbarMouseMove(MouseEventArgs e)
    {
        // Update hover state regardless of drag
        UpdateScrollbarHover(e.Location);

        if (_scrollbarDragH)
        {
            DragScrollbarH(e.X);
            return true;
        }
        if (_scrollbarDragV)
        {
            DragScrollbarV(e.Y);
            return true;
        }
        return false;
    }

    /// <summary>Called at the start of OnMouseUp. Returns true if the scrollbar consumed the release.</summary>
    private bool ScrollbarMouseUp(MouseEventArgs e)
    {
        if (_scrollbarDragH)
        {
            _scrollbarDragH = false;
            Capture = false;
            NotifyScrollbarActivity();
            Invalidate();
            return true;
        }
        if (_scrollbarDragV)
        {
            _scrollbarDragV = false;
            Capture = false;
            NotifyScrollbarActivity();
            Invalidate();
            return true;
        }
        return false;
    }

    private void DragScrollbarH(int mouseX)
    {
        var track = ScrollbarTrackH;
        var thumb = GetScrollbarThumb(track, horizontal: true);
        float trackLen = track.Width;
        float thumbLen = thumb.Width;
        float usable = trackLen - thumbLen;
        if (usable < 1) return;

        float contentW = (float)(_baseBitmap!.Width * _zoom);
        float scrollRange = contentW - ClientSize.Width;

        int mouseDelta = mouseX - _scrollbarDragStartMouse;
        float scrollDelta = (mouseDelta / usable) * scrollRange;
        float newPanX = _scrollbarDragStartPan - scrollDelta;

        // Clamp pan so image edges stay reachable
        newPanX = Math.Clamp(newPanX, ClientSize.Width - contentW, 0);
        _pan = new PointF(newPanX, _pan.Y);
        _viewFitsWindow = false;
        _userPanned = true;
        DismissWelcomeOverlay();
        Invalidate();
        OnStateChanged();
    }

    private void DragScrollbarV(int mouseY)
    {
        var track = ScrollbarTrackV;
        var thumb = GetScrollbarThumb(track, horizontal: false);
        float trackLen = track.Height;
        float thumbLen = thumb.Height;
        float usable = trackLen - thumbLen;
        if (usable < 1) return;

        float contentH = (float)(_baseBitmap!.Height * _zoom);
        float scrollRange = contentH - ClientSize.Height;

        int mouseDelta = mouseY - _scrollbarDragStartMouse;
        float scrollDelta = (mouseDelta / usable) * scrollRange;
        float newPanY = _scrollbarDragStartPan - scrollDelta;

        newPanY = Math.Clamp(newPanY, ClientSize.Height - contentH, 0);
        _pan = new PointF(_pan.X, newPanY);
        _viewFitsWindow = false;
        _userPanned = true;
        DismissWelcomeOverlay();
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Page-scroll: moves by one viewport width in the direction clicked.</summary>
    private void DoPageScrollH(int mouseX)
    {
        var track = ScrollbarTrackH;
        var thumb = GetScrollbarThumb(track, horizontal: true);
        float thumbCenter = thumb.X + thumb.Width / 2f;
        float pageSize = ClientSize.Width * 0.8f; // 80% of viewport

        float newPanX = _pan.X + (mouseX < thumbCenter ? pageSize : -pageSize);
        float contentW = (float)(_baseBitmap!.Width * _zoom);
        newPanX = Math.Clamp(newPanX, ClientSize.Width - contentW, 0);
        _pan = new PointF(newPanX, _pan.Y);
        _viewFitsWindow = false;
        _userPanned = true;
        DismissWelcomeOverlay();
        NotifyScrollbarActivity();
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Page-scroll: moves by one viewport height in the direction clicked.</summary>
    private void DoPageScrollV(int mouseY)
    {
        var track = ScrollbarTrackV;
        var thumb = GetScrollbarThumb(track, horizontal: false);
        float thumbCenter = thumb.Y + thumb.Height / 2f;
        float pageSize = ClientSize.Height * 0.8f;

        float newPanY = _pan.Y + (mouseY < thumbCenter ? pageSize : -pageSize);
        float contentH = (float)(_baseBitmap!.Height * _zoom);
        newPanY = Math.Clamp(newPanY, ClientSize.Height - contentH, 0);
        _pan = new PointF(_pan.X, newPanY);
        _viewFitsWindow = false;
        _userPanned = true;
        DismissWelcomeOverlay();
        NotifyScrollbarActivity();
        Invalidate();
        OnStateChanged();
    }

    // ── Hover tracking ────────────────────────────────────────────────────

    private void UpdateScrollbarHover(Point client)
    {
        bool wasH = _scrollbarHoverH, wasV = _scrollbarHoverV;

        if (_scrollbarDragH || _scrollbarDragV)
        {
            // Keep current hover state during drag
            return;
        }

        _scrollbarHoverH = false;
        _scrollbarHoverV = false;

        if (ScrollbarVisibleH)
        {
            var trackH = ScrollbarTrackH;
            _scrollbarHoverH = trackH.Contains(client);
        }
        if (ScrollbarVisibleV)
        {
            var trackV = ScrollbarTrackV;
            _scrollbarHoverV = trackV.Contains(client);
        }

        if (wasH != _scrollbarHoverH || wasV != _scrollbarHoverV)
        {
            EnsureScrollbarAnimTimer();
            if (_scrollbarHoverH || _scrollbarHoverV)
                NotifyScrollbarActivity();
            Invalidate();
        }
    }

    private void ClearScrollbarHover()
    {
        bool changed = _scrollbarHoverH || _scrollbarHoverV;
        _scrollbarHoverH = false;
        _scrollbarHoverV = false;
        if (changed)
        {
            EnsureScrollbarAnimTimer();
            Invalidate();
        }
    }

    // ── Activity / Auto-hide ──────────────────────────────────────────────

    /// <summary>Flash the scrollbars (or keep them on screen) after a scroll/zoom/pan action.</summary>
    internal void NotifyScrollbarActivity()
    {
        _scrollbarLastActivityTick = Environment.TickCount64;

        if (_scrollbarFadeOpacity < 1f)
        {
            _scrollbarFadeOpacity = 1f;
            Invalidate();
        }

        if (!_showScrollbarsAlways)
            EnsureScrollbarFadeTimer();
    }

    private void EnsureScrollbarFadeTimer()
    {
        if (_scrollbarFadeTimer is null)
        {
            _scrollbarFadeTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _scrollbarFadeTimer.Tick += ScrollbarFadeTimer_Tick;
        }
        if (!_scrollbarFadeTimer.Enabled)
            _scrollbarFadeTimer.Start();
    }

    private void ScrollbarFadeTimer_Tick(object? sender, EventArgs e)
    {
        if (_showScrollbarsAlways)
        {
            _scrollbarFadeOpacity = 1f;
            _scrollbarFadeTimer?.Stop();
            return;
        }

        // Keep visible while hovering or dragging
        if (_scrollbarHoverH || _scrollbarHoverV || _scrollbarDragH || _scrollbarDragV)
        {
            _scrollbarFadeOpacity = 1f;
            _scrollbarLastActivityTick = Environment.TickCount64;
            return;
        }

        long elapsed = Environment.TickCount64 - _scrollbarLastActivityTick;
        if (elapsed < ScrollbarAutoHideMs)
        {
            // Still within the hold period
            return;
        }

        // Fade out over ~200ms (30ms ticks → ~7 steps)
        _scrollbarFadeOpacity -= 0.15f;
        if (_scrollbarFadeOpacity <= 0f)
        {
            _scrollbarFadeOpacity = 0f;
            _scrollbarFadeTimer?.Stop();
        }
        Invalidate();
    }

    // ── Hover expand animation ────────────────────────────────────────────

    private void EnsureScrollbarAnimTimer()
    {
        if (_scrollbarAnimTimer is null)
        {
            _scrollbarAnimTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60 fps
            _scrollbarAnimTimer.Tick += ScrollbarAnimTimer_Tick;
        }
        if (!_scrollbarAnimTimer.Enabled)
            _scrollbarAnimTimer.Start();
    }

    private void ScrollbarAnimTimer_Tick(object? sender, EventArgs e)
    {
        const float speed = 0.18f;
        bool changed = false;

        float targetH = (_scrollbarHoverH || _scrollbarDragH) ? 1f : 0f;
        if (Math.Abs(_scrollbarHoverProgressH - targetH) > 0.01f)
        {
            _scrollbarHoverProgressH += (targetH > _scrollbarHoverProgressH) ? speed : -speed;
            _scrollbarHoverProgressH = Math.Clamp(_scrollbarHoverProgressH, 0f, 1f);
            changed = true;
        }
        else
        {
            _scrollbarHoverProgressH = targetH;
        }

        float targetV = (_scrollbarHoverV || _scrollbarDragV) ? 1f : 0f;
        if (Math.Abs(_scrollbarHoverProgressV - targetV) > 0.01f)
        {
            _scrollbarHoverProgressV += (targetV > _scrollbarHoverProgressV) ? speed : -speed;
            _scrollbarHoverProgressV = Math.Clamp(_scrollbarHoverProgressV, 0f, 1f);
            changed = true;
        }
        else
        {
            _scrollbarHoverProgressV = targetV;
        }

        if (changed)
        {
            Invalidate();
        }
        else
        {
            _scrollbarAnimTimer?.Stop();
        }
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    private void DisposeScrollbarTimers()
    {
        _scrollbarFadeTimer?.Stop();
        _scrollbarFadeTimer?.Dispose();
        _scrollbarFadeTimer = null;
        _scrollbarAnimTimer?.Stop();
        _scrollbarAnimTimer?.Dispose();
        _scrollbarAnimTimer = null;
    }
}
