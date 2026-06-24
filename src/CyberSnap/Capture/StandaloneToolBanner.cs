using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>A text segment with optional color override. When <see cref="Color"/> is null,
/// the banner's default accent color is used.</summary>
public readonly record struct BannerSegment(string Text, Color? Color = null);

/// <summary>
/// Reusable animated instruction banner for standalone tool forms (e.g. ruler, color picker).
/// Renders a centered pill-shaped banner that fades in, holds while hovered, then fades out.
/// 
/// Usage:
///   _banner = new StandaloneToolBanner("Your instructions here", workingArea, Bounds);
///   // In OnPaint:   _banner.Render(g);
///   // In OnMouseMove: check _banner.ContainsCursor(e.Location)
///   // Dispose when form closes.
/// </summary>
public sealed class StandaloneToolBanner : IDisposable
{
    private readonly string _text;
    private readonly IReadOnlyList<BannerSegment>? _segments;
    private readonly Rectangle _workingArea;
    private readonly Rectangle _bounds;
    private readonly Action? _onInvalidate;
    private readonly Action<Rectangle>? _onInvalidateRect;
    private readonly bool _persistent;

    /// <summary>Master switch — when false, no banner renders anywhere.</summary>
    public static bool Enabled { get; set; } = true;
    private float _opacity;
    private System.Windows.Forms.Timer? _timer;
    private int _holdTicks;
    private RectangleF _bannerRect;
    private State _state = State.FadeIn;

    private enum State { FadeIn, Hold, FadeOut }

    /// <summary>
    /// Creates the banner and starts its fade-in animation immediately.
    /// </summary>
    /// <param name="text">Instruction text displayed in the banner.</param>
    /// <param name="workingArea">Screen working area the banner should center on (in screen coordinates).</param>
    /// <param name="bounds">Form bounds used to convert screen → client coordinates.</param>
    /// <param name="onInvalidate">Optional callback to trigger form repaint on animation ticks.</param>
    /// <param name="persistent">When true, the banner holds at full opacity indefinitely and only
    /// disappears when <see cref="Dismiss"/> is called (e.g. on first user interaction).</param>
    public StandaloneToolBanner(string text, Rectangle workingArea, Rectangle bounds, Action? onInvalidate = null, bool persistent = false, Action<Rectangle>? onInvalidateRect = null)
    {
        _text = text;
        _segments = null;
        _workingArea = workingArea;
        _bounds = bounds;
        _onInvalidate = onInvalidate;
        _onInvalidateRect = onInvalidateRect;
        _persistent = persistent;

        // Pre-compute the banner rect so region-based invalidation works from the very first tick
        // (before Render has run once). Matches the layout math in Render().
        ComputeBannerRect();

        _timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60 fps
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    /// Creates the banner with individually colored text segments.
    /// </summary>
    /// <param name="segments">Text segments, each with optional color override (null = accent color).</param>
    /// <param name="workingArea">Screen working area the banner should center on (in screen coordinates).</param>
    /// <param name="bounds">Form bounds used to convert screen → client coordinates.</param>
    /// <param name="onInvalidate">Optional callback to trigger form repaint on animation ticks.</param>
    /// <param name="persistent">When true, the banner holds at full opacity indefinitely.</param>
    public StandaloneToolBanner(IReadOnlyList<BannerSegment> segments, Rectangle workingArea, Rectangle bounds, Action? onInvalidate = null, bool persistent = false, Action<Rectangle>? onInvalidateRect = null)
    {
        _segments = segments;
        _text = string.Concat(segments.Select(s => s.Text));
        _workingArea = workingArea;
        _bounds = bounds;
        _onInvalidate = onInvalidate;
        _onInvalidateRect = onInvalidateRect;
        _persistent = persistent;

        ComputeBannerRect();

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void ComputeBannerRect()
    {
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        using var font = new Font("Segoe UI Variable Display", 16f, FontStyle.Regular, GraphicsUnit.Point);
        var size = g.MeasureString(_text, font);

        const int paddingH = 28;
        const int paddingV = 17;
        float width = size.Width + paddingH * 2;
        float height = size.Height + paddingV * 2;
        float y = _workingArea.Top - _bounds.Top + 35;
        float x = _workingArea.Left - _bounds.Left + (_workingArea.Width - width) / 2f;
        _bannerRect = new RectangleF(x, y, width, height);
    }

    /// <summary>Inflated client-space rect covering the banner pill plus its glow — the only region
    /// that needs repainting when the banner animates. Lets the host invalidate just this area
    /// instead of the whole (potentially multi-monitor) form.</summary>
    public Rectangle InvalidateBounds
    {
        get
        {
            var r = _bannerRect;
            r.Inflate(12, 12);
            return Rectangle.Round(r);
        }
    }

    /// <summary>Trigger a host repaint — region-scoped when a rect callback was supplied, else full.</summary>
    private void RaiseInvalidate()
    {
        if (_onInvalidateRect != null)
            _onInvalidateRect(InvalidateBounds);
        else
            _onInvalidate?.Invoke();
    }

    /// <summary>Whether the given client-space cursor position is over the banner.</summary>
    public bool ContainsCursor(Point cursorPos) => _bannerRect.Contains(cursorPos);

    /// <summary>Call on every OnPaint to render the banner on top of the form.</summary>
    public void Render(Graphics g)
    {
        if (!Enabled || _opacity <= 0f) return;

        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var font = new Font("Segoe UI Variable Display", 16f, FontStyle.Regular, GraphicsUnit.Point);
            var size = g.MeasureString(_text, font);

            const int paddingH = 28;
            const int paddingV = 17;
            float width = size.Width + paddingH * 2;
            float height = size.Height + paddingV * 2;

            float y = _workingArea.Top - _bounds.Top + 35;
            float x = _workingArea.Left - _bounds.Left + (_workingArea.Width - width) / 2f;

            _bannerRect = new RectangleF(x, y, width, height);

            int alphaBg = Math.Min((int)(255 * _opacity), 255);
            int alphaBorder = (int)(140 * _opacity);
            int alphaGlow = (int)(40 * _opacity);
            int alphaText = (int)(255 * _opacity);

            var accent = Theme.IsDark
                ? Color.FromArgb(0, 255, 255)   // Neon cyan — dark mode
                : Color.FromArgb(0, 170, 190);  // Muted cyan — light mode

            using var path = RoundedRect(_bannerRect, 10);
            using var bgBrush = new SolidBrush(Color.FromArgb(alphaBg, 13, 15, 23));
            using var glowPen = new Pen(Color.FromArgb(alphaGlow, accent), 3f);
            using var borderPen = new Pen(Color.FromArgb(alphaBorder, accent), 1.5f);

            g.FillPath(bgBrush, path);
            g.DrawPath(glowPen, path);
            g.DrawPath(borderPen, path);

            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center
            };

            if (_segments != null)
            {
                // Draw each segment with its own color, laid out left-to-right.
                float cursorX = x + paddingH;
                float textTop = y + paddingV;
                foreach (var seg in _segments)
                {
                    var segColor = seg.Color ?? accent;
                    using var segBrush = new SolidBrush(Color.FromArgb(alphaText, segColor));
                    g.DrawString(seg.Text, font, segBrush, cursorX, textTop);
                    cursorX += g.MeasureString(seg.Text, font).Width;
                }
            }
            else
            {
                var textRect = new RectangleF(x + paddingH, y + paddingV, size.Width, size.Height);
                using var textBrush = new SolidBrush(Color.FromArgb(alphaText, accent));
                sf.Alignment = StringAlignment.Center;
                g.DrawString(_text, font, textBrush, textRect, sf);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        switch (_state)
        {
            case State.FadeIn:
                _opacity += 0.12f;
                if (_opacity >= 1.0f)
                {
                    _opacity = 1.0f;
                    _state = State.Hold;
                    _holdTicks = 0;
                }
                RaiseInvalidate();
                break;

            case State.Hold:
                if (_persistent)
                {
                    _timer?.Stop(); // fully visible; no more animation until Dismiss()
                    break;
                }
                _holdTicks++;
                if (_holdTicks >= 90) // ~1.5 s
                    _state = State.FadeOut;
                break;

            case State.FadeOut:
                _opacity -= 0.08f;
                if (_opacity <= 0.0f)
                {
                    _opacity = 0.0f;
                    _timer?.Stop();
                }
                RaiseInvalidate();
                break;
        }
    }

    /// <summary>Reset to fully visible (e.g. when cursor re-enters the banner during fade-out).</summary>
    public void Revive()
    {
        if (_state == State.FadeOut)
        {
            _state = State.FadeIn;
        }
        else if (_state == State.Hold)
        {
            _holdTicks = 0;
        }
    }

    /// <summary>Immediately hide the banner (e.g. when the user starts interacting).</summary>
    public void Dismiss()
    {
        if (_state == State.FadeIn || _state == State.Hold)
        {
            _state = State.FadeOut;
            _opacity = 0f;
            _timer?.Stop();
            RaiseInvalidate();
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

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
