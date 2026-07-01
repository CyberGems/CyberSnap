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
    public const int TotalW = LensSize + Pad * 2;
    public const int MinFormW = 200;

    private const int InfoGap = 10;
    private const int InfoH = 58;
    private const int SwatchSize = 40;
    private const int SwatchGap = 10;
    private const int PillPad = 8;
    private const int PillRadius = 10;

    private static int LensDiameter => LensSize;

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

    private readonly Font _hexFont = new("Segoe UI Variable Display", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _rgbFont = new(UiChrome.PreferredFamilyName, 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly SolidBrush _labelBrush = new(UiChrome.SurfaceTextPrimary);
    private readonly SolidBrush _bgBrush = new(UiChrome.SurfaceTier1);
    private readonly Pen _lensBorderPen = new(Color.FromArgb(160, UiChrome.AccentColor), 1.2f);
    private readonly SolidBrush _pillBg = new(UiChrome.SurfaceTier1);
    private readonly Pen _pillBorder = new(Color.FromArgb(160, UiChrome.AccentColor), 1f);

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
        Size = new Size(MinFormW, GetTotalHeight(true));
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
        var targetSize = new Size(MinFormW, GetTotalHeight(showInfo));
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
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int cx = sz.Width / 2;
        int cy = Pad + LensSize / 2;
        int lensD = LensDiameter;
        var lensRect = new Rectangle(cx - lensD / 2, cy - lensD / 2, lensD, lensD);

        var shadowRect = lensRect;
        shadowRect.Inflate(1, 1);
        foreach (var (dx, dy, brush) in LensShadowPasses)
        {
            var sr = shadowRect;
            sr.Offset(dx, dy);
            using var shadowPath = CirclePath(sr);
            g.FillPath(brush, shadowPath);
        }

        using var lensPath = CirclePath(lensRect);
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

        DrawCenterSelectionRing(g, cx, cy);

        if (_showInfo)
            DrawInfoPill(g, sz, lensRect);

        g.Flush(FlushIntention.Sync);

        var screenPt = new Native.User32.POINT { X = Left, Y = Top };
        var size = new Native.User32.SIZE { cx = sz.Width, cy = sz.Height };
        var srcPt = new Native.User32.POINT { X = 0, Y = 0 };
        var blend = new Native.User32.BLENDFUNCTION
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1
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

    private void DrawCenterSelectionRing(Graphics g, int cx, int cy)
    {
        const int cellPx = LensSize / 11;
        int half = cellPx / 2;
        var ringRect = new Rectangle(cx - half, cy - half, cellPx, cellPx);

        Color ringColor = IsLightColor(_picked)
            ? Color.FromArgb(220, 0, 0, 0)
            : Color.FromArgb(220, 255, 255, 255);

        using var ringPen = new Pen(ringColor, 2f);
        g.DrawRectangle(ringPen, ringRect.X, ringRect.Y, ringRect.Width - 1, ringRect.Height - 1);
    }

    private void DrawInfoPill(Graphics g, Size sz, Rectangle lensRect)
    {
        string hexLabel = $"#{_hex}";
        string rgbLabel = $"R: {_picked.R}  G: {_picked.G}  B: {_picked.B}";
        int pillW = sz.Width - Pad * 2;
        int pillH = InfoH;
        int pillX = Pad;
        int pillY = lensRect.Bottom + InfoGap;
        var pillRect = new Rectangle(pillX, pillY, pillW, pillH);

        using var pillPath = RoundedPill(pillRect, PillRadius);
        g.FillPath(_pillBg, pillPath);
        g.DrawPath(_pillBorder, pillPath);

        int swatchX = pillX + PillPad;
        int swatchY = pillY + (pillH - SwatchSize) / 2;
        var swatchRect = new Rectangle(swatchX, swatchY, SwatchSize, SwatchSize);

        using (var swatchPath = RoundedRect(swatchRect, 6f))
        using (var swatchFill = new SolidBrush(_picked))
        {
            g.FillPath(swatchFill, swatchPath);
            using var outerBorder = new Pen(Color.FromArgb(140, 255, 255, 255), 1.2f);
            using var innerBorder = new Pen(Color.FromArgb(100, 0, 0, 0), 1f);
            g.DrawPath(outerBorder, swatchPath);
            g.DrawPath(innerBorder, swatchPath);
        }

        int textX = swatchRect.Right + SwatchGap;
        int textW = pillRect.Right - PillPad - textX;
        var hexRect = new Rectangle(textX, pillY + 6, textW, (pillH - 12) / 2 + 2);
        var rgbRect = new Rectangle(textX, hexRect.Bottom - 2, textW, (pillH - 12) / 2 + 2);

        var oldHint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        TextRenderer.DrawText(g, hexLabel, _hexFont, hexRect, UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, rgbLabel, _rgbFont, rgbRect, UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        g.TextRenderingHint = oldHint;
    }

    private static bool IsLightColor(Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) >= 128;

    private static GraphicsPath CirclePath(RectangleF r)
    {
        var path = new GraphicsPath();
        path.AddEllipse(r);
        return path;
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
            _pillBg.Dispose();
            _pillBorder.Dispose();
        }
        base.Dispose(disposing);
    }
}
