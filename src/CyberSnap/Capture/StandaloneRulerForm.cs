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

    // ── Banner ──
    private float _bannerOpacity;
    private System.Windows.Forms.Timer? _bannerTimer;
    private int _bannerHoldTicks;
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
                if (_bannerHoldTicks >= 90) // ~1.5s hold
                {
                    _bannerState = BannerState.FadeOut;
                }
                break;

            case BannerState.FadeOut:
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
            _isDragging = true;
            _rulerStart = e.Location;
            _cursorPos = e.Location;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursorPos = e.Location;
        if (_isDragging)
        {
            var oldBounds = RulerRenderer.GetPaintBounds(_rulerStart, GetRulerEnd(e.Location));
            // Invalidate conservatively to clear previous frame's label
            var newBounds = RulerRenderer.GetPaintBounds(_rulerStart, GetRulerEnd(e.Location));
            Invalidate(Rectangle.Union(oldBounds, newBounds));
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
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

        // Draw ruler if dragging
        if (_isDragging)
        {
            var end = GetRulerEnd(_cursorPos);
            RulerRenderer.Paint(g, _rulerStart, end, ClientRectangle, Theme.IsDark);
        }

        // Draw banner
        RenderBanner(g);
    }

    // ── Helpers ──

    private Point GetRulerEnd(Point current)
    {
        if ((ModifierKeys & Keys.Shift) == 0) return current;

        int dx = current.X - _rulerStart.X;
        int dy = current.Y - _rulerStart.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return new Point(current.X, _rulerStart.Y);
        return new Point(_rulerStart.X, current.Y);
    }

    private void RenderBanner(Graphics g)
    {
        if (_bannerOpacity <= 0f) return;

        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float x = (ClientRectangle.Width - 700) / 2f; // centered
            float y = 60;

            using var font = new Font("Segoe UI Variable Display", 11f, FontStyle.Regular, GraphicsUnit.Point);
            var size = g.MeasureString(BannerText, font);

            int paddingH = 22;
            int paddingV = 12;
            float width = size.Width + paddingH * 2;
            float height = size.Height + paddingV * 2;

            x = (ClientRectangle.Width - width) / 2f;

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
