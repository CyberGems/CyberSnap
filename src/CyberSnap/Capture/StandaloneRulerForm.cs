using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone on-screen ruler activated via global hotkey or tray menu.
/// Overlays a screenshot of all monitors and lets the user drag to measure
/// distances and angles without entering the full capture overlay.
/// Right-click or Escape to close. Shift constrains to horizontal/vertical.
/// </summary>
public sealed class StandaloneRulerForm : Form
{
    private readonly Bitmap _screenshot;
    private Point _rulerStart;
    private bool _isDragging;
    private Point _cursorPos;
    private bool _closed;

    // Last committed measurement persists on screen until next drag
    private Point _lastFrom;
    private Point _lastTo;
    private bool _hasLastMeasurement;

    // Post-drag editing: move or resize the committed ruler
    private enum EditState { None, Moving, ResizingFrom, ResizingTo }
    private EditState _editState = EditState.None;
    private Point _editOffset; // cursor offset from _lastFrom during move

    // ── Banner ──
    private float _bannerOpacity;
    private System.Windows.Forms.Timer? _bannerTimer;
    private int _bannerHoldTicks;
    private RectangleF _bannerRect;
    private enum BannerState { FadeIn, Hold, FadeOut }
    private BannerState _bannerState = BannerState.FadeIn;
    private static readonly string BannerText = "Click & drag to measure  ·  Right-click or Esc to close  ·  Hold Shift to constrain";

    public StandaloneRulerForm()
    {
        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        Theme.Refresh();
        var (bmp, _) = ScreenCapture.CaptureAllScreens(includeCursor: false);
        _screenshot = bmp;

        RulerRenderer.EnsureChrome(Theme.IsDark);

        Cursor = Cursors.Cross;

        // ── Banner timer ──
        _bannerTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _bannerTimer.Tick += BannerTimer_Tick;
        _bannerTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bannerTimer?.Stop();
            _bannerTimer?.Dispose();
            _screenshot?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Banner animation ──

    private void BannerTimer_Tick(object? sender, EventArgs e)
    {
        switch (_bannerState)
        {
            case BannerState.FadeIn:
                _bannerOpacity += 0.12f;
                if (_bannerOpacity >= 1.0f)
                {
                    _bannerOpacity = 1.0f;
                    _bannerState = BannerState.Hold;
                    _bannerHoldTicks = 0;
                }
                Invalidate();
                break;

            case BannerState.Hold:
                _bannerHoldTicks++;
                // Keep banner visible while hovering over it
                if (_bannerRect.Contains(_cursorPos))
                {
                    _bannerHoldTicks = 0;
                    break;
                }
                if (_bannerHoldTicks >= 90) // ~1.5s hold
                {
                    _bannerState = BannerState.FadeOut;
                }
                break;

            case BannerState.FadeOut:
                // Revive if cursor moves over banner during fade-out
                if (_bannerRect.Contains(_cursorPos))
                {
                    _bannerState = BannerState.FadeIn;
                    break;
                }
                _bannerOpacity -= 0.08f;
                if (_bannerOpacity <= 0.0f)
                {
                    _bannerOpacity = 0.0f;
                    _bannerTimer?.Stop();
                }
                Invalidate();
                break;
        }
    }

    // ── Keyboard ──

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Mouse ──

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Close();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            // If there's a committed ruler, check if we're editing it
            if (_hasLastMeasurement && _editState == EditState.None)
            {
                var hit = HitTestRuler(e.Location);
                switch (hit)
                {
                    case EditState.Moving:
                        _editState = EditState.Moving;
                        _editOffset = new Point(e.Location.X - _lastFrom.X, e.Location.Y - _lastFrom.Y);
                        Invalidate();
                        return;
                    case EditState.ResizingFrom:
                        _editState = EditState.ResizingFrom;
                        Invalidate();
                        return;
                    case EditState.ResizingTo:
                        _editState = EditState.ResizingTo;
                        Invalidate();
                        return;
                }
            }

            // Not editing existing ruler — start a fresh drag
            _editState = EditState.None;
            _hasLastMeasurement = false;
            _isDragging = true;
            _rulerStart = e.Location;
            _cursorPos = e.Location;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var prevCursorPos = _cursorPos;
        _cursorPos = e.Location;

        if (_isDragging)
        {
            var oldBounds = RulerRenderer.GetPaintBounds(_rulerStart, GetRulerEnd(prevCursorPos));
            var newBounds = RulerRenderer.GetPaintBounds(_rulerStart, GetRulerEnd(e.Location));
            Invalidate(Rectangle.Union(oldBounds, newBounds));
        }
        else if (_editState != EditState.None)
        {
            var oldBounds = RulerRenderer.GetPaintBounds(_lastFrom, _lastTo);
            switch (_editState)
            {
                case EditState.Moving:
                    int dx = e.Location.X - _editOffset.X;
                    int dy = e.Location.Y - _editOffset.Y;
                    if ((ModifierKeys & Keys.Shift) != 0)
                    {
                        int rawDx = dx - _lastFrom.X;
                        int rawDy = dy - _lastFrom.Y;
                        if (Math.Abs(rawDx) >= Math.Abs(rawDy))
                            dy = _lastFrom.Y;
                        else
                            dx = _lastFrom.X;
                    }
                    int moveDx = dx - _lastFrom.X;
                    int moveDy = dy - _lastFrom.Y;
                    _lastFrom = new Point(dx, dy);
                    _lastTo = new Point(_lastTo.X + moveDx, _lastTo.Y + moveDy);
                    break;
                case EditState.ResizingFrom:
                    if ((ModifierKeys & Keys.Shift) != 0)
                        _lastFrom = ConstrainPoint(e.Location, _lastTo);
                    else
                        _lastFrom = e.Location;
                    break;
                case EditState.ResizingTo:
                    if ((ModifierKeys & Keys.Shift) != 0)
                        _lastTo = ConstrainPoint(e.Location, _lastFrom);
                    else
                        _lastTo = e.Location;
                    break;
            }
            var newBounds = RulerRenderer.GetPaintBounds(_lastFrom, _lastTo);
            Invalidate(Rectangle.Union(oldBounds, newBounds));
        }
        else if (_hasLastMeasurement)
        {
            // Update cursor to hint editability
            var hit = HitTestRuler(e.Location);
            Cursor = hit switch
            {
                EditState.Moving => Cursors.SizeAll,
                EditState.ResizingFrom or EditState.ResizingTo => Cursors.SizeNWSE,
                _ => Cursors.Cross
            };
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            _lastFrom = _rulerStart;
            _lastTo = GetRulerEnd(_cursorPos);
            _hasLastMeasurement = true;
            Invalidate();
        }
        else if (_editState != EditState.None && e.Button == MouseButtons.Left)
        {
            _editState = EditState.None;
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    // ── Paint ──

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_closed) return;
        var g = e.Graphics;

        // Draw screenshot as background
        g.DrawImage(_screenshot, ClientRectangle);

        // Draw ruler if dragging or last measurement persists
        if (_isDragging)
        {
            var end = GetRulerEnd(_cursorPos);
            RulerRenderer.Paint(g, _rulerStart, end, ClientRectangle, Theme.IsDark);
        }
        else if (_hasLastMeasurement)
        {
            RulerRenderer.Paint(g, _lastFrom, _lastTo, ClientRectangle, Theme.IsDark);
        }

        // Draw banner
        RenderBanner(g);
    }

    // ── Helpers ──

    private Point GetRulerEnd(Point current)
    {
        if ((ModifierKeys & Keys.Shift) == 0) return current;

        return ConstrainPoint(current, _rulerStart);
    }

    /// <summary>Constrain a point to horizontal or vertical axis from an anchor.</summary>
    private static Point ConstrainPoint(Point current, Point anchor)
    {
        int dx = current.X - anchor.X;
        int dy = current.Y - anchor.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return new Point(current.X, anchor.Y);
        return new Point(anchor.X, current.Y);
    }

    /// <summary>Hit-test the committed ruler: returns which part the cursor is near.</summary>
    private EditState HitTestRuler(Point p)
    {
        if (!_hasLastMeasurement) return EditState.None;

        const int endpointRadius = 16;
        const int lineThreshold = 10;

        // Check endpoints first (they take priority over the line)
        int distFrom = DistSq(p, _lastFrom);
        int distTo = DistSq(p, _lastTo);
        if (distFrom <= endpointRadius * endpointRadius)
            return EditState.ResizingFrom;
        if (distTo <= endpointRadius * endpointRadius)
            return EditState.ResizingTo;

        // Check distance to the line segment
        float lineDist = DistToSegmentSq(p, _lastFrom, _lastTo);
        if (lineDist <= lineThreshold * lineThreshold)
            return EditState.Moving;

        return EditState.None;
    }

    private static int DistSq(Point a, Point b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static float DistToSegmentSq(Point p, Point a, Point b)
    {
        float abx = b.X - a.X;
        float aby = b.Y - a.Y;
        float lenSq = abx * abx + aby * aby;
        if (lenSq < 0.5f) return DistSq(p, a);

        float t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq, 0f, 1f);
        float projX = a.X + t * abx;
        float projY = a.Y + t * aby;
        float dx = p.X - projX;
        float dy = p.Y - projY;
        return dx * dx + dy * dy;
    }

    private void RenderBanner(Graphics g)
    {
        if (_bannerOpacity <= 0f) return;

        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float x = SystemInformation.WorkingArea.Left + 18;
            float y = SystemInformation.WorkingArea.Top + 18;

            using var font = new Font("Segoe UI Variable Display", 13f, FontStyle.Regular, GraphicsUnit.Point);
            var size = g.MeasureString(BannerText, font);

            int paddingH = 26;
            int paddingV = 15;
            float width = size.Width + paddingH * 2;
            float height = size.Height + paddingV * 2;

            _bannerRect = new RectangleF(x, y, width, height);

            int alphaBg = (int)(180 * _bannerOpacity);
            int alphaBorder = (int)(120 * _bannerOpacity);
            int alphaGlow = (int)(30 * _bannerOpacity);
            int alphaText = (int)(255 * _bannerOpacity);

            var accent = Theme.IsDark
                ? Color.FromArgb(75, 130, 246)
                : Color.FromArgb(0, 120, 215);

            using var path = RoundedRect(new RectangleF(x, y, width, height), 10);
            using var bgBrush = new SolidBrush(Color.FromArgb(alphaBg, 13, 15, 23));
            using var glowPen = new Pen(Color.FromArgb(alphaGlow, accent), 3f);
            using var borderPen = new Pen(Color.FromArgb(alphaBorder, accent), 1.2f);
            using var textBrush = new SolidBrush(Color.FromArgb(alphaText, accent));

            g.FillPath(bgBrush, path);
            g.DrawPath(glowPen, path);
            g.DrawPath(borderPen, path);

            var textRect = new RectangleF(x + paddingH, y + paddingV, size.Width, size.Height);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(BannerText, font, textBrush, textRect, sf);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        path.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        path.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        base.OnFormClosed(e);
    }
}
