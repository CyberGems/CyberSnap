using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.Helpers;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>A text segment with optional color override. When <see cref="Color"/> is null,
/// the banner's default accent color is used.</summary>
public readonly record struct BannerSegment(string Text, Color? Color = null);

/// <summary>
/// Reusable animated instruction banner for standalone tool forms (e.g. ruler, color picker).
/// Renders a centered pill-shaped banner that fades in, holds while hovered, then fades out.
///
/// Theme-aware: Dark (CyberSnap cyan), Light (blue on pale card), Grayscale (silver on charcoal).
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
    /// <summary>Optional Streamline/Fluent icon id rendered as a real vector glyph to the left of
    /// the text — the SAME SVG the capture toolbar draws, so the banner matches it exactly
    /// (a font char would just render as tofu in the banner's text font).</summary>
    private readonly string? _iconId;
    private readonly Color? _iconColorOverride;
    /// <summary>Gap between the leading icon and the text.</summary>
    private const int IconGap = 10;
    private readonly Rectangle _workingArea;
    private readonly Rectangle _bounds;
    private readonly Action? _onInvalidate;
    private readonly Action<Rectangle>? _onInvalidateRect;
    private readonly bool _persistent;
    /// <summary>When true, the banner is centered near the bottom of the working area
    /// (used when the capture toolbar occupies the top so they do not overlap).</summary>
    private readonly bool _anchorBottom;

    /// <summary>Master switch — when false, no banner renders anywhere.</summary>
    public static bool Enabled { get; set; } = true;

    // ── Theme tokens (mirror Theme / EditorColors so capture banners match the rest of the app) ──

    /// <summary>Accent used for action text, border, and glow.
    /// Dark: neon cyan · Light: Windows blue · Gray: sober silver.</summary>
    public static Color AccentColor =>
        Theme.IsGray ? Color.FromArgb(184, 190, 198)
        : Theme.IsDark ? Color.FromArgb(0, 255, 255)
        : Color.FromArgb(0, 120, 215);

    /// <summary>Primary label color (tool name / icon). White on dark/gray; near-black on light.</summary>
    public static Color LabelColor =>
        Theme.IsDark ? Color.FromArgb(255, 255, 255)
        : Color.FromArgb(26, 26, 26);

    /// <summary>Pill background. Matches Theme.BgPrimary (dark/gray) and EditorColors.BgCard (light).</summary>
    public static Color BackgroundColor =>
        Theme.IsGray ? Color.FromArgb(22, 24, 27)
        : Theme.IsDark ? Color.FromArgb(13, 15, 23)
        : Color.FromArgb(232, 238, 247);

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
    /// <param name="anchorBottom">When true, place the banner near the bottom of the working area
    /// instead of the top (to avoid overlapping a top-docked capture toolbar).</param>
    public StandaloneToolBanner(string text, Rectangle workingArea, Rectangle bounds, Action? onInvalidate = null, bool persistent = false, Action<Rectangle>? onInvalidateRect = null, string? iconId = null, Color? iconColor = null, bool anchorBottom = false)
    {
        _text = text;
        _segments = null;
        _iconId = iconId;
        _iconColorOverride = iconColor;
        _workingArea = workingArea;
        _bounds = bounds;
        _onInvalidate = onInvalidate;
        _onInvalidateRect = onInvalidateRect;
        _persistent = persistent;
        _anchorBottom = anchorBottom;

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
    /// <param name="anchorBottom">When true, place the banner near the bottom of the working area
    /// instead of the top (to avoid overlapping a top-docked capture toolbar).</param>
    public StandaloneToolBanner(IReadOnlyList<BannerSegment> segments, Rectangle workingArea, Rectangle bounds, Action? onInvalidate = null, bool persistent = false, Action<Rectangle>? onInvalidateRect = null, string? iconId = null, Color? iconColor = null, bool anchorBottom = false)
    {
        _segments = segments;
        _text = string.Concat(segments.Select(s => s.Text));
        _iconId = iconId;
        _iconColorOverride = iconColor;
        _workingArea = workingArea;
        _bounds = bounds;
        _onInvalidate = onInvalidate;
        _onInvalidateRect = onInvalidateRect;
        _persistent = persistent;
        _anchorBottom = anchorBottom;

        ComputeBannerRect();

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Edge inset (in client pixels) between the working-area edge and the banner pill.</summary>
    private const float EdgeMargin = 35f;

    private void ComputeBannerRect()
    {
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        using var font = UiChrome.ChromeFont(16f, FontStyle.Regular);
        var size = g.MeasureString(_text, font);

        const int paddingH = 28;
        const int paddingV = 17;
        float iconBlock = _iconId != null ? size.Height * 0.92f + IconGap : 0f;
        float width = size.Width + iconBlock + paddingH * 2;
        float height = size.Height + paddingV * 2;
        float y = ComputeBannerY(height);
        float x = _workingArea.Left - _bounds.Left + (_workingArea.Width - width) / 2f;
        _bannerRect = new RectangleF(x, y, width, height);
    }

    private float ComputeBannerY(float height) =>
        _anchorBottom
            ? _workingArea.Bottom - _bounds.Top - height - EdgeMargin
            : _workingArea.Top - _bounds.Top + EdgeMargin;

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

            using var font = UiChrome.ChromeFont(16f, FontStyle.Regular);
            var size = g.MeasureString(_text, font);

            const int paddingH = 28;
            const int paddingV = 17;
            float iconSize = size.Height * 0.92f;
            float iconBlock = _iconId != null ? iconSize + IconGap : 0f;
            float width = size.Width + iconBlock + paddingH * 2;
            float height = size.Height + paddingV * 2;

            float y = ComputeBannerY(height);
            float x = _workingArea.Left - _bounds.Left + (_workingArea.Width - width) / 2f;

            _bannerRect = new RectangleF(x, y, width, height);

            // Light uses a slightly translucent card so the dimmed capture still shows through;
            // dark/gray stay near-opaque so cyan/silver type stays readable on the screenshot.
            int alphaBg = Math.Min((int)((Theme.IsDark ? 255 : 235) * _opacity), 255);
            int alphaBorder = (int)((Theme.IsDark ? 140 : 110) * _opacity);
            int alphaGlow = (int)((Theme.IsDark ? 40 : 24) * _opacity);
            int alphaText = (int)(255 * _opacity);

            var accent = AccentColor;
            var bg = BackgroundColor;
            var label = LabelColor;
            var iconColor = _iconColorOverride ?? label;

            using var path = RoundedRect(_bannerRect, 10);
            using var bgBrush = new SolidBrush(Color.FromArgb(alphaBg, bg));
            using var glowPen = new Pen(Color.FromArgb(alphaGlow, accent), 3f);
            using var borderPen = new Pen(Color.FromArgb(alphaBorder, accent), 1.5f);

            g.FillPath(bgBrush, path);
            g.DrawPath(glowPen, path);
            g.DrawPath(borderPen, path);

            // Leading vector icon — the exact same Streamline/Fluent SVG the capture toolbar
            // draws, rendered at inset 0 to fill its slot. Fades with the banner via alphaText.
            if (_iconId != null)
            {
                float iconX = x + paddingH;
                float iconY = y + (height - iconSize) / 2f;
                FluentIcons.DrawIcon(g, _iconId,
                    new RectangleF(iconX, iconY, iconSize, iconSize),
                    Color.FromArgb(alphaText, iconColor), 0f);
            }

            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center
            };

            if (_segments != null)
            {
                // Draw each segment with its own color, laid out left-to-right.
                float cursorX = x + paddingH + iconBlock;
                float textTop = y + paddingV;
                foreach (var seg in _segments)
                {
                    // null → accent; pure white (legacy call sites) → theme label color
                    var segColor = ResolveSegmentColor(seg.Color, accent, label);
                    using var segBrush = new SolidBrush(Color.FromArgb(alphaText, segColor));
                    g.DrawString(seg.Text, font, segBrush, cursorX, textTop);
                    cursorX += g.MeasureString(seg.Text, font).Width;
                }
            }
            else
            {
                var textRect = new RectangleF(x + paddingH + iconBlock, y + paddingV, size.Width, size.Height);
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

    /// <summary>Reset to fully visible (e.g. when cursor re-enters the banner during fade-out,
    /// or when the user clicks without completing a drag selection).</summary>
    public void Revive()
    {
        if (_state == State.FadeOut)
        {
            _state = State.FadeIn;
            _opacity = 0f; // restart fade-in from transparent
            _timer?.Start();
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

    /// <summary>
    /// Maps a segment color override to a concrete paint color.
    /// <c>null</c> → accent; pure white (legacy label call sites) → theme <see cref="LabelColor"/>.
    /// </summary>
    private static Color ResolveSegmentColor(Color? overrideColor, Color accent, Color label)
    {
        if (overrideColor is null)
            return accent;
        // Historical call sites used Color.White for the tool-name label. Remap so light mode
        // gets near-black text without forcing every caller to switch overnight.
        if (overrideColor.Value.ToArgb() == Color.White.ToArgb())
            return label;
        return overrideColor.Value;
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
