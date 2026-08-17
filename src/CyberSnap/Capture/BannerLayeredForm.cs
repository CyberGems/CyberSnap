using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Native;

namespace CyberSnap.Capture;

/// <summary>
/// Independent, click-through layered window for a tool hint. Animation updates only this
/// small surface, so a full-screen capture host does not repaint while the hint fades.
/// </summary>
internal sealed class BannerLayeredForm : Form
{
    private const int SurfacePadding = 16;
    private const int WmDpiChanged = 0x02E0;
    private const float FadeInSeconds = 0.144f;
    private const float HoldSeconds = 1.44f;
    private const float DismissSeconds = 0.096f;
    private const float AutoFadeSeconds = 0.192f;

    private readonly string _text;
    private readonly IReadOnlyList<BannerSegment>? _segments;
    private readonly string? _iconId;
    private readonly Color? _iconColor;
    private readonly Rectangle _workingArea;
    private readonly bool _persistent;
    private readonly bool _anchorBottom;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer;

    private Bitmap? _surface;
    private Rectangle _pillScreenBounds;
    private RectangleF _pillSurfaceBounds;
    private float _opacity;
    private float _animationStartOpacity;
    private float _animationDuration;
    private AnimationState _state = AnimationState.FadeIn;
    private long _stateStartedAt;
    private bool _disposed;

    private enum AnimationState { FadeIn, Hold, FadeOut }

    public bool IsVisible => _opacity > 0f;

    public BannerLayeredForm(
        IReadOnlyList<BannerSegment> segments,
        Rectangle workingArea,
        bool persistent = false,
        string? iconId = null,
        Color? iconColor = null,
        bool anchorBottom = false)
    {
        _segments = segments;
        _text = string.Concat(segments.Select(segment => segment.Text));
        _workingArea = workingArea;
        _persistent = persistent;
        _iconId = iconId;
        _iconColor = iconColor;
        _anchorBottom = anchorBottom;
        _animationDuration = FadeInSeconds;
        _stateStartedAt = _clock.ElapsedMilliseconds;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                  ControlStyles.OptimizedDoubleBuffer, true);
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, UiChrome.FrameIntervalMs) };
        _timer.Tick += OnTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureWindowExclusion.Apply(this);
    }

    /// <summary>Shows the banner without activating it or its owner.</summary>
    public void ShowFor(Form owner)
    {
        if (_disposed || owner.IsDisposed)
            return;

        Show(owner);
        BuildSurface();
        _timer.Start();
        Present();
    }

    public void RefreshTheme() => Present(rebuildSurface: true);

    public bool ContainsScreenPoint(Point point)
        => IsVisible && _pillScreenBounds.Contains(point);

    public void DismissIfHovered(Point screenPoint)
    {
        if (ContainsScreenPoint(screenPoint))
            Dismiss();
    }

    public void Dismiss()
    {
        if (_disposed || _opacity <= 0f)
            return;

        StartFadeOut(DismissSeconds);
        Present();
    }

    public void DismissImmediate()
    {
        if (_disposed)
            return;

        _opacity = 0f;
        _state = AnimationState.FadeOut;
        _timer.Stop();
        Present();
    }

    public void Revive()
    {
        if (_disposed)
            return;

        _state = AnimationState.FadeIn;
        _animationStartOpacity = _opacity;
        _animationDuration = FadeInSeconds;
        _stateStartedAt = _clock.ElapsedMilliseconds;
        _timer.Start();
        Present();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        float elapsed = (float)(_clock.ElapsedMilliseconds - _stateStartedAt) / 1000f;
        switch (_state)
        {
            case AnimationState.FadeIn:
                _opacity = Math.Clamp(_animationStartOpacity +
                    (1f - _animationStartOpacity) * Math.Clamp(elapsed / _animationDuration, 0f, 1f), 0f, 1f);
                if (elapsed >= _animationDuration)
                {
                    _opacity = 1f;
                    _state = AnimationState.Hold;
                    _stateStartedAt = _clock.ElapsedMilliseconds;
                    if (_persistent)
                        _timer.Stop();
                }
                break;

            case AnimationState.Hold:
                if (_persistent)
                    return;
                if (elapsed >= HoldSeconds)
                    StartFadeOut(AutoFadeSeconds);
                else
                    return;
                break;

            case AnimationState.FadeOut:
                _opacity = Math.Clamp(_animationStartOpacity *
                    (1f - Math.Clamp(elapsed / _animationDuration, 0f, 1f)), 0f, 1f);
                if (elapsed >= _animationDuration)
                {
                    _opacity = 0f;
                    _timer.Stop();
                }
                break;
        }

        Present();
    }

    private void StartFadeOut(float duration)
    {
        _state = AnimationState.FadeOut;
        _animationStartOpacity = _opacity;
        _animationDuration = Math.Max(0.001f, duration);
        _stateStartedAt = _clock.ElapsedMilliseconds;
        _timer.Start();
    }

    private void BuildSurface()
    {
        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        using var font = UiChrome.ChromeFont(16f, FontStyle.Regular);
        var content = BannerRenderer.MeasureContent(measureGraphics, _text, _segments, font);
        var pillSize = BannerRenderer.GetBannerSize(content, _iconId != null);
        var surfaceSize = new Size(
            Math.Max(1, (int)Math.Ceiling(pillSize.Width) + SurfacePadding * 2),
            Math.Max(1, (int)Math.Ceiling(pillSize.Height) + SurfacePadding * 2));

        _pillSurfaceBounds = new RectangleF(SurfacePadding, SurfacePadding, pillSize.Width, pillSize.Height);
        int x = _workingArea.Left + (int)Math.Round((_workingArea.Width - pillSize.Width) / 2f) - SurfacePadding;
        int y = _anchorBottom
            ? _workingArea.Bottom - (int)Math.Round(pillSize.Height) - 35 - SurfacePadding
            : _workingArea.Top + 35 - SurfacePadding;
        _pillScreenBounds = new Rectangle(x + SurfacePadding, y + SurfacePadding,
            (int)Math.Ceiling(pillSize.Width), (int)Math.Ceiling(pillSize.Height));

        if (_surface == null || _surface.Size != surfaceSize)
        {
            _surface?.Dispose();
            _surface = new Bitmap(surfaceSize.Width, surfaceSize.Height, PixelFormat.Format32bppPArgb);
        }

        SetBounds(x, y, surfaceSize.Width, surfaceSize.Height);
    }

    private void Present(bool rebuildSurface = false)
    {
        if (_disposed || !IsHandleCreated || _surface == null)
            return;
        if (rebuildSurface)
            BuildSurface();

        using (var graphics = Graphics.FromImage(_surface))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            BannerRenderer.Render(graphics, _pillSurfaceBounds, _text, _segments,
                _iconId, _iconColor, _opacity);
            graphics.Flush(FlushIntention.Sync);
        }

        var screenPoint = new User32.POINT { X = Left, Y = Top };
        var size = new User32.SIZE { cx = Width, cy = Height };
        var sourcePoint = new User32.POINT { X = 0, Y = 0 };
        var blend = new User32.BLENDFUNCTION
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1
        };

        IntPtr hdcScreen = User32.GetDC(IntPtr.Zero);
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hdcMem = User32.CreateCompatibleDC(hdcScreen);
            hBitmap = _surface.GetHbitmap(Color.FromArgb(0));
            oldBitmap = User32.SelectObject(hdcMem, hBitmap);
            User32.UpdateLayeredWindow(Handle, hdcScreen, ref screenPoint, ref size,
                hdcMem, ref sourcePoint, 0, ref blend, 2);
        }
        finally
        {
            if (hdcMem != IntPtr.Zero && oldBitmap != IntPtr.Zero)
                User32.SelectObject(hdcMem, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                User32.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                User32.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                User32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) // WM_NCHITTEST
        {
            m.Result = (IntPtr)(-1); // HTTRANSPARENT
            return;
        }

        base.WndProc(ref m);
        if (m.Msg == WmDpiChanged && IsHandleCreated && !_disposed)
            BeginInvoke(RefreshTheme);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _surface?.Dispose();
        _surface = null;
        base.Dispose(disposing);
    }
}
