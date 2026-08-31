using System.Diagnostics;
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
/// Renders a centered pill-shaped banner that fades in, holds briefly, then fades out.
/// Hovering the pill dismisses it immediately so it does not block the capture area.
///
/// Theme-aware: Dark (CyberSnap cyan), Light (blue on pale card), Grayscale (silver on charcoal).
///
/// Usage:
///   _banner = new StandaloneToolBanner("Your instructions here", workingArea, Bounds);
///   // In OnPaint:   _banner.Render(g);
///   // In OnMouseMove: _banner.DismissIfHovered(e.Location);
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
    private float _slide = 1f;
    private float _animFromOpacity;
    private float _animFromSlide = 1f;
    private float _animDuration = 0.10f;
    private System.Windows.Forms.Timer? _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _stateStartedAt;
    private RectangleF _bannerRect;
    private State _state = State.FadeIn;

    private const float FadeInSeconds = 0.10f;
    private const float HoldSeconds = 1.20f;
    private const float AutoFadeOutSeconds = 0.12f;
    private const float DismissFadeOutSeconds = 0.05f;
    private const float SlidePixels = 12f;

    private enum State { FadeIn, Hold, FadeOut }

    /// <summary>True while the pill is still visible (opacity &gt; 0).</summary>
    public bool IsVisible => _opacity > 0f;

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
        _animDuration = FadeInSeconds;
        _stateStartedAt = _clock.ElapsedMilliseconds;
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, UiChrome.FrameIntervalMs) };
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
        _animDuration = FadeInSeconds;
        _stateStartedAt = _clock.ElapsedMilliseconds;
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, UiChrome.FrameIntervalMs) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Edge inset (in client pixels) between the working-area edge and the banner pill.</summary>
    private const float EdgeMargin = 35f;

    /// <summary>Union of every banner rect painted so far — used so fade/replace invalidates
    /// cover the largest footprint and do not leave ghosts on the dimmed confirm overlay.</summary>
    private Rectangle _dirtyUnion = Rectangle.Empty;

    private void ComputeBannerRect()
    {
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        using var font = UiChrome.ChromeFont(16f, FontStyle.Regular);
        var size = MeasureContent(g, font);
        ApplyBannerRect(size);
    }

    /// <summary>Match segment layout to <see cref="Render"/> (sum of typographic segment widths).</summary>
    private SizeF MeasureContent(Graphics g, Font font)
    {
        return BannerRenderer.MeasureContent(g, _text, _segments, font);
    }

    private void ApplyBannerRect(SizeF size)
    {
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
            var r = VisualBannerRect();
            r.Inflate(16, 16 + SlidePixels);
            var current = Rectangle.Round(r);
            return _dirtyUnion.IsEmpty ? current : Rectangle.Union(_dirtyUnion, current);
        }
    }

    /// <summary>Trigger a host repaint — region-scoped when a rect callback was supplied, else full.</summary>
    private void RaiseInvalidate()
    {
        var bounds = InvalidateBounds;
        _dirtyUnion = bounds;
        if (_onInvalidateRect != null)
            _onInvalidateRect(bounds);
        else
            _onInvalidate?.Invoke();
    }

    /// <summary>Whether the given client-space cursor position is over the banner.</summary>
    public bool ContainsCursor(Point cursorPos) => VisualBannerRect().Contains(cursorPos);

    private RectangleF VisualBannerRect()
    {
        var r = _bannerRect;
        r.Y -= SlidePixels * _slide;
        return r;
    }

    /// <summary>
    /// If the cursor is over a still-visible banner, fade it out quickly so the hint
    /// does not obstruct the capture / tool surface. Safe to call every mouse-move.
    /// </summary>
    public void DismissIfHovered(Point cursorPos)
    {
        if (!IsVisible || !ContainsCursor(cursorPos))
            return;
        Dismiss();
    }

    /// <summary>Call on every OnPaint to render the banner on top of the form.</summary>
    public void Render(Graphics g)
    {
        if (!Enabled || _opacity <= 0f) return;

        var state = g.Save();
        try
        {
            BannerRenderer.Render(
                g,
                VisualBannerRect(),
                _text,
                _segments,
                _iconId,
                _iconColorOverride,
                _opacity);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        float elapsed = (_clock.ElapsedMilliseconds - _stateStartedAt) / 1000f;
        float u = Math.Clamp(elapsed / Math.Max(0.001f, _animDuration), 0f, 1f);
        switch (_state)
        {
            case State.FadeIn:
            {
                float ease = UiChrome.EaseOutCubic(u);
                _opacity = _animFromOpacity + (1f - _animFromOpacity) * ease;
                _slide = _animFromSlide * (1f - ease);
                if (elapsed >= _animDuration)
                {
                    _opacity = 1f;
                    _slide = 0f;
                    _state = State.Hold;
                    _stateStartedAt = _clock.ElapsedMilliseconds;
                    if (_persistent)
                        _timer?.Stop();
                }
                RaiseInvalidate();
                break;
            }
            case State.Hold:
                if (_persistent)
                {
                    _timer?.Stop();
                    break;
                }
                if (elapsed >= HoldSeconds)
                    StartFadeOut(AutoFadeOutSeconds);
                break;
            case State.FadeOut:
            {
                float ease = UiChrome.EaseInCubic(u);
                _opacity = _animFromOpacity * (1f - ease);
                _slide = _animFromSlide + (1f - _animFromSlide) * ease;
                if (elapsed >= _animDuration)
                {
                    _opacity = 0f;
                    _slide = 1f;
                    _timer?.Stop();
                    RaiseInvalidate();
                    _dirtyUnion = Rectangle.Empty;
                    break;
                }
                RaiseInvalidate();
                break;
            }
        }
    }

    private void StartFadeOut(float duration)
    {
        _state = State.FadeOut;
        _animFromOpacity = Math.Max(_opacity, 0.001f);
        _animFromSlide = _slide;
        _animDuration = Math.Max(0.02f, duration);
        _stateStartedAt = _clock.ElapsedMilliseconds;
        _timer?.Start();
        RaiseInvalidate();
    }

    /// <summary>Reset to fully visible (e.g. when the user clicks without completing a drag
    /// selection). Prefer <see cref="DismissIfHovered"/> on mouse-move — hovering should
    /// clear the hint, not keep it up.</summary>
    public void Revive()
    {
        if (_state == State.FadeOut || _opacity < 1f)
        {
            _state = State.FadeIn;
            _animFromOpacity = _opacity;
            _animFromSlide = _slide;
            _animDuration = FadeInSeconds;
            _stateStartedAt = _clock.ElapsedMilliseconds;
            _timer?.Start();
            RaiseInvalidate();
        }
        else if (_state == State.Hold)
        {
            _stateStartedAt = _clock.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Fade the banner out quickly. Safe to call from any state, including
    /// an already-running auto fade-out (accelerates it).
    /// </summary>
    public void Dismiss()
    {
        if (_opacity <= 0f)
        {
            _state = State.FadeOut;
            _timer?.Stop();
            return;
        }

        StartFadeOut(DismissFadeOutSeconds);
    }

    /// <summary>
    /// Hard-hide the banner on the next paint (opacity → 0, timer stopped). Use when the
    /// host is about to dispose the banner, or when an animated dismiss would contend with
    /// a heavy full-surface drag repaint.
    /// </summary>
    public void DismissImmediate()
    {
        if (_opacity <= 0f && _state == State.FadeOut)
        {
            _timer?.Stop();
            return;
        }

        _state = State.FadeOut;
        _opacity = 0f;
        _slide = 1f;
        _timer?.Stop();
        RaiseInvalidate();
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
