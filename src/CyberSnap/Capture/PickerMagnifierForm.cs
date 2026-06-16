using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

public sealed class PickerMagnifierForm : Form
{
    public const int LensSize = 110;
    public const int Pad = 5;
    // MinFormW: wide enough for RGB info pill text without clipping
    public const int TotalW = LensSize + Pad * 2;
    private const int MinFormW = 190;

    private const int InfoGap = 10;
    private const int InfoH = 52;
    private const int LensRadius = 14;

    private Bitmap? _surface;
    private Graphics? _surfaceGraphics;
    private bool _showInfo = true;

    private Bitmap? _magnifier;
    private string _hex = "000000";
    private string _rgb = "0, 0, 0";
    private Color _picked = Color.Black;
    private Size _lastSurfaceSize = Size.Empty;
    private bool _lastShowInfo = true;
    private Bitmap? _lastMagnifier;
    private Point _lastCursor = Point.Empty;
    private string _lastHex = "";
    private string _lastRgb = "";
    private int _lastPickedArgb;

    // Cached GDI objects — styled to match RulerRenderer data box
    private readonly Font _hexFont = new("Segoe UI Variable Text", 10f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _rgbFont = new("Segoe UI Variable Text", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly SolidBrush _labelBrush = new(UiChrome.SurfaceTextPrimary);
    private readonly SolidBrush _accentBrush = new(UiChrome.AccentColor);
    private readonly SolidBrush _bgBrush = new(UiChrome.SurfaceTier1);
    // Lens border: matches RulerRenderer accent (cyan/blue with alpha ~160)
    private readonly Pen _lensBorderPen = new(Color.FromArgb(160, UiChrome.AccentColor), 1.2f);
    private readonly SolidBrush _pillBg = new(UiChrome.SurfaceTier1);
    private readonly Pen _pillBorder = new(Color.FromArgb(160, UiChrome.AccentColor), 1f);
    private readonly SolidBrush _dotFill = new(UiChrome.SurfaceTextPrimary);
    // Crosshair dot border: thin accent
    private readonly Pen _dotBorder = new(UiChrome.AccentColor, 1f);

    // Lens shadow passes are constant â€” cache the brushes once.
    private static readonly (int dx, int dy, SolidBrush brush)[] LensShadowPasses =
    {
        (2, 3, new SolidBrush(Color.FromArgb(16, 0, 0, 0))),
        (1, 2, new SolidBrush(Color.FromArgb(30, 0, 0, 0))),
        (0, 1, new SolidBrush(Color.FromArgb(44, 0, 0, 0))),
    };

    public PickerMagnifierForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(TotalW, GetTotalHeight(true));
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureWindowExclusion.Apply(this);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }

        base.WndProc(ref m);
    }

    public void UpdateMagnifier(Bitmap magnifier, Point cursor, Color picked, string hex, string rgb, bool showInfo = true)
    {
        int formW = showInfo ? MinFormW : MinFormW;
        var targetSize = new Size(formW, GetTotalHeight(showInfo));
        if (_lastSurfaceSize == targetSize &&
            _lastShowInfo == showInfo &&
            ReferenceEquals(_lastMagnifier, magnifier) &&
            _lastCursor == cursor &&
            _lastPickedArgb == picked.ToArgb() &&
            string.Equals(_lastHex, hex, StringComparison.Ordinal) &&
            string.Equals(_lastRgb, rgb, StringComparison.Ordinal))
        {
            return;
        }

        _magnifier = magnifier;
        _picked = picked;
        _hex = hex;
        _rgb = rgb;
        _showInfo = showInfo;
        if (Size != targetSize)
            Size = targetSize;
        UpdateSurface();
        _lastSurfaceSize = targetSize;
        _lastShowInfo = showInfo;
        _lastMagnifier = magnifier;
        _lastCursor = cursor;
        _lastPickedArgb = picked.ToArgb();
        _lastHex = hex;
        _lastRgb = rgb;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        var sz = Size;
        if (sz.Width <= 0 || sz.Height <= 0) return;

        if (_surface == null || _surface.Width != sz.Width || _surface.Height != sz.Height)
        {
            _surfaceGraphics?.Dispose();
            _surface?.Dispose();
            _surface = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppPArgb);
            _surfaceGraphics = Graphics.FromImage(_surface);
        }

        var g = _surfaceGraphics!;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        int cx = sz.Width / 2;
        int cy = Pad + LensSize / 2;
        var lensRect = new Rectangle(cx - LensSize / 2, Pad, LensSize, LensSize);

        var shadowRect = lensRect;
        shadowRect.Inflate(1, 1);
        foreach (var (dx, dy, brush) in LensShadowPasses)
        {
            var sr = shadowRect;
            sr.Offset(dx, dy);
            using var shadowPath = RoundedRect(sr, LensRadius + 2);
            g.FillPath(brush, shadowPath);
        }

        using var lensPath = RoundedRect(lensRect, LensRadius);
        g.FillPath(_bgBrush, lensPath);

        if (_magnifier != null)
        {
            var state = g.Save();
            g.SetClip(lensPath);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_magnifier, lensRect);
            g.Restore(state);
        }

        g.DrawPath(_lensBorderPen, lensPath);

        // Crosshair dot at center
        int dotSize = 4;
        g.FillRectangle(_dotFill, cx - dotSize / 2, cy - dotSize / 2, dotSize, dotSize);
        g.DrawRectangle(_dotBorder, cx - dotSize / 2, cy - dotSize / 2, dotSize, dotSize);

        if (_showInfo)
        {
            // Info pill below lens — fixed wide width for RGB text, with separator
            string hexLabel = $"#{_hex}";
            string rgbLabel = $"R: {_picked.R}  G: {_picked.G}  B: {_picked.B}";
            int pillW = sz.Width - Pad * 2; // fill form width
            int pillH = InfoH;
            int pillX = Pad;
            int pillY = lensRect.Bottom + InfoGap;
            var pillRect = new Rectangle(pillX, pillY, pillW, pillH);

            using var pillPath = RoundedPill(pillRect, pillH / 2f);
            g.FillPath(_pillBg, pillPath);
            g.DrawPath(_pillBorder, pillPath);

            // Hex row (top half): accent color, bold
            var hexRect = new Rectangle(pillX, pillY, pillW, pillH / 2);
            TextRenderer.DrawText(g, hexLabel, _hexFont, hexRect, _accentBrush.Color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Separator line
            int sepY = pillY + pillH / 2;
            using var sepPen = new Pen(Color.FromArgb(40, UiChrome.SurfaceTextPrimary), 1f);
            g.DrawLine(sepPen, pillX + 14, sepY, pillX + pillW - 14, sepY);

            // RGB row (bottom half): normal text
            var rgbRect = new Rectangle(pillX, sepY, pillW, pillH / 2);
            TextRenderer.DrawText(g, rgbLabel, _rgbFont, rgbRect, UiChrome.SurfaceTextPrimary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        g.Flush(FlushIntention.Sync);

        var screenPt = new Native.User32.POINT { X = Left, Y = Top };
        var size = new Native.User32.SIZE { cx = sz.Width, cy = sz.Height };
        var srcPt = new Native.User32.POINT { X = 0, Y = 0 };
        var blend = new Native.User32.BLENDFUNCTION
        {
            BlendOp = 0, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1  // AC_SRC_ALPHA
        };

        IntPtr hdcScreen = Native.User32.GetDC(IntPtr.Zero);
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        try
        {
            hdcMem = Native.User32.CreateCompatibleDC(hdcScreen);
            hBmp = _surface!.GetHbitmap(Color.FromArgb(0));
            hOld = Native.User32.SelectObject(hdcMem, hBmp);
            Native.User32.UpdateLayeredWindow(Handle, hdcScreen, ref screenPt, ref size,
                hdcMem, ref srcPt, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            if (hdcMem != IntPtr.Zero && hOld != IntPtr.Zero)
                Native.User32.SelectObject(hdcMem, hOld);
            if (hBmp != IntPtr.Zero)
                Native.User32.DeleteObject(hBmp);
            if (hdcMem != IntPtr.Zero)
                Native.User32.DeleteDC(hdcMem);
            Native.User32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static GraphicsPath RoundedPill(RectangleF r, float radius)
        => RoundedRect(r, radius);

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static int GetTotalHeight(bool showInfo)
        => LensSize + Pad * 2 + (showInfo ? InfoGap + InfoH : 0);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lastMagnifier = null;
            _surfaceGraphics?.Dispose();
            _surface?.Dispose();
            _hexFont.Dispose();
            _rgbFont.Dispose();
            _labelBrush.Dispose();
            _bgBrush.Dispose();
            _lensBorderPen.Dispose();
            _accentBrush.Dispose();
            _pillBg.Dispose();
            _pillBorder.Dispose();
            _dotFill.Dispose();
            _dotBorder.Dispose();
        }
        base.Dispose(disposing);
    }
}
