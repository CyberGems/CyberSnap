using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CyberSnap.Helpers;

internal static class CursorFactory
{
    private static Cursor? _eraserCursor;
    private static Cursor? _eyedropperCursor;
    private static Cursor? _precisionCursor;
    private static Cursor? _rotateCursor;
    private static Cursor? _hiddenCursor;

    /// <summary>A fully transparent 1×1 cursor — hides the pointer over a control while keeping
    /// hover/move events flowing (used where an on-canvas ghost is the pointer itself).</summary>
    public static Cursor HiddenCursor
    {
        get
        {
            if (_hiddenCursor is null)
            {
                using var bmp = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var hIcon = bmp.GetHicon();
                try
                {
                    _hiddenCursor = new Cursor(hIcon);
                }
                catch
                {
                    DestroyIcon(hIcon);
                    _hiddenCursor = Cursors.Default;
                }
            }
            return _hiddenCursor;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    public static Cursor PanCursor => GrabCursor;

    // Drag ("grab") and dragging ("grabbing") now use the traditional 4-way move cross
    // (SizeAll) instead of the hand icon, per user request.
    public static Cursor GrabCursor => Cursors.SizeAll;

    public static Cursor GrabbingCursor => Cursors.SizeAll;

    public static Cursor EraserCursor
    {
        get
        {
            if (_eraserCursor is null)
                _eraserCursor = CreateEraserCursor();
            return _eraserCursor;
        }
    }

    private static Cursor CreateEraserCursor()
    {
        const int size = 44;
        const int cx = size / 2, cy = size / 2;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Render the toolbar eraser icon (Filled version for solid appearance).
        // Use a shadow technique (dark offset + white) for visibility on any background.
        const int iconSize = 30;
        int offset = (size - iconSize) / 2;

        // Shadow offset by 1px down-right — dark version first
        var shadow = StreamlineIcons.RenderBitmap("eraser", Color.FromArgb(160, 0, 0, 0), iconSize, active: true);
        if (shadow != null)
        {
            g.DrawImage(shadow, offset + 1, offset + 1, iconSize, iconSize);
            shadow.Dispose();
        }

        // White main icon (nearly opaque for crispness)
        var icon = StreamlineIcons.RenderBitmap("eraser", Color.FromArgb(245, 255, 255, 255), iconSize, active: true);
        if (icon != null)
        {
            g.DrawImage(icon, offset, offset, iconSize, iconSize);
            icon.Dispose();
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            var iconInfo = new IconInfo();
            if (GetIconInfo(hIcon, ref iconInfo))
            {
                iconInfo.fIcon = false;
                iconInfo.xHotspot = cx;
                iconInfo.yHotspot = cy;

                IntPtr hCursor = CreateIconIndirect(ref iconInfo);

                if (iconInfo.hbmMask != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmColor);

                if (hCursor != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                    return new Cursor(hCursor);
                }
            }
        }
        catch
        {
            DestroyIcon(hIcon);
        }

        return Cursors.Default;
    }

    /// <summary>
    /// Eyedropper cursor for screen color picking. Hotspot sits at the dropper tip so the
    /// sampled pixel aligns with where the user is pointing.
    /// </summary>
    public static Cursor EyedropperCursor
    {
        get
        {
            if (_eyedropperCursor is null)
                _eyedropperCursor = CreateEyedropperCursor();
            return _eyedropperCursor;
        }
    }

    private static Cursor CreateEyedropperCursor()
    {
        const int size = 50;
        const int iconSize = 36;
        int offset = (size - iconSize) / 2;

        // Fluent eyedropper tip ≈ (3.4, 12.85) in a 20×20 viewBox — nudge Y down so the
        // hotspot aligns with the visible tip (rendered icon tip sits lower than path coords).
        int hotspotX = offset + (int)Math.Round(3.4 / 20.0 * iconSize);
        int hotspotY = offset + (int)Math.Round(12.85 / 20.0 * iconSize) + 6;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var shadow = StreamlineIcons.RenderBitmap("picker", Color.FromArgb(160, 0, 0, 0), iconSize, active: true);
        if (shadow != null)
        {
            g.DrawImage(shadow, offset + 1, offset + 1, iconSize, iconSize);
            shadow.Dispose();
        }

        var icon = StreamlineIcons.RenderBitmap("picker", Color.FromArgb(245, 255, 255, 255), iconSize, active: true);
        if (icon != null)
        {
            g.DrawImage(icon, offset, offset, iconSize, iconSize);
            icon.Dispose();
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            var iconInfo = new IconInfo();
            if (GetIconInfo(hIcon, ref iconInfo))
            {
                iconInfo.fIcon = false;
                iconInfo.xHotspot = hotspotX;
                iconInfo.yHotspot = hotspotY;

                IntPtr hCursor = CreateIconIndirect(ref iconInfo);

                if (iconInfo.hbmMask != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmColor);

                if (hCursor != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                    return new Cursor(hCursor);
                }
            }
        }
        catch
        {
            DestroyIcon(hIcon);
        }

        return Cursors.Cross;
    }

    /// <summary>
    /// A fine precision crosshair for the drawing/crop tools: four short arms around a
    /// central gap (so the exact target pixel stays visible), each white arm wrapped in a
    /// soft dark halo so it reads on any background. Replaces the heavy stock
    /// <see cref="Cursors.Cross"/>.
    /// </summary>
    public static Cursor PrecisionCursor
    {
        get
        {
            if (_precisionCursor is null)
                _precisionCursor = CreatePrecisionCursor();
            return _precisionCursor;
        }
    }

    private static Cursor CreatePrecisionCursor()
    {
        // Larger canvas + longer arms so the cross is easier to find on high-DPI
        // displays, while stroke widths stay thin so the center stays precise.
        const int size = 40;
        const int c = size / 2;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        const float gap = 4f;     // clear space around the exact center (target pixel visible)
        const float arm = 10.5f;  // length of each crosshair arm (was 7 on a 32px cursor)

        void Arms(Graphics gr, Pen p)
        {
            gr.DrawLine(p, c, c - gap - arm, c, c - gap); // top
            gr.DrawLine(p, c, c + gap, c, c + gap + arm); // bottom
            gr.DrawLine(p, c - gap - arm, c, c - gap, c); // left
            gr.DrawLine(p, c + gap, c, c + gap + arm, c); // right
        }

        // Soft dark halo for contrast on light backgrounds — kept thinner/lighter so it
        // doesn't grey out the arms. White stroke is fully opaque for maximum brightness.
        using (var halo = new Pen(Color.FromArgb(110, 0, 0, 0), 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            Arms(g, halo);
        using (var line = new Pen(Color.FromArgb(255, 255, 255, 255), 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            Arms(g, line);

        // Bright center pip marks the hotspot without filling the gap.
        using (var pip = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            g.FillEllipse(pip, c - 0.8f, c - 0.8f, 1.6f, 1.6f);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            var iconInfo = new IconInfo();
            if (GetIconInfo(hIcon, ref iconInfo))
            {
                iconInfo.fIcon = false;
                iconInfo.xHotspot = c;
                iconInfo.yHotspot = c;

                IntPtr hCursor = CreateIconIndirect(ref iconInfo);

                if (iconInfo.hbmMask != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmColor);

                if (hCursor != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                    return new Cursor(hCursor);
                }
            }
        }
        catch
        {
            DestroyIcon(hIcon);
        }

        return Cursors.Cross;
    }

    /// <summary>
    /// Circular double-headed rotate cursor (open at the top), white with a black
    /// outline so it reads on light and dark backgrounds.
    /// </summary>
    public static Cursor RotateCursor
    {
        get
        {
            if (_rotateCursor is null)
                _rotateCursor = CreateRotateCursor();
            return _rotateCursor;
        }
    }

    private static Cursor CreateRotateCursor()
    {
        const int size = 32;
        const int c = size / 2;
        const float r = 9.2f;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // GDI+ 0° = east, clockwise. Gap at the top (270°): draw from 310° through the
        // bottom to 230° so both tips sit at the upper-left / upper-right.
        const float startDeg = 310f;
        const float sweepDeg = 280f;
        var box = new RectangleF(c - r, c - r, r * 2f, r * 2f);

        using (var halo = new Pen(Color.FromArgb(240, 0, 0, 0), 3.4f)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(halo, box, startDeg, sweepDeg);
        using (var pen = new Pen(Color.White, 1.85f)
               { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, box, startDeg, sweepDeg);

        PointF On(float deg)
        {
            float a = deg * MathF.PI / 180f;
            return new PointF(c + MathF.Cos(a) * r, c + MathF.Sin(a) * r);
        }

        PointF From(float deg, float delta) => On(deg + delta);
        using var outline = new SolidBrush(Color.FromArgb(240, 0, 0, 0));
        using var fill = new SolidBrush(Color.White);
        PaintCursorArrowHead(g, outline, From(startDeg, 18f), On(startDeg), 6.4f);
        PaintCursorArrowHead(g, fill, From(startDeg, 18f), On(startDeg), 5.3f);
        float endDeg = startDeg + sweepDeg;
        PaintCursorArrowHead(g, outline, From(endDeg, -18f), On(endDeg), 6.4f);
        PaintCursorArrowHead(g, fill, From(endDeg, -18f), On(endDeg), 5.3f);

        return CursorFromBitmap(bmp, c, c, Cursors.Hand);
    }

    private static void PaintCursorArrowHead(Graphics g, Brush brush, PointF from, PointF tip, float size)
    {
        float dx = tip.X - from.X, dy = tip.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f) return;
        dx /= len; dy /= len;
        float px = -dy, py = dx;
        g.FillPolygon(brush,
        [
            tip,
            new PointF(tip.X - dx * size + px * size * 0.48f, tip.Y - dy * size + py * size * 0.48f),
            new PointF(tip.X - dx * size - px * size * 0.48f, tip.Y - dy * size - py * size * 0.48f),
        ]);
    }

    private static Cursor CursorFromBitmap(Bitmap bmp, int hotspotX, int hotspotY, Cursor fallback)
    {
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            var iconInfo = new IconInfo();
            if (GetIconInfo(hIcon, ref iconInfo))
            {
                iconInfo.fIcon = false;
                iconInfo.xHotspot = hotspotX;
                iconInfo.yHotspot = hotspotY;

                IntPtr hCursor = CreateIconIndirect(ref iconInfo);

                if (iconInfo.hbmMask != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero)
                    DeleteObject(iconInfo.hbmColor);

                if (hCursor != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                    return new Cursor(hCursor);
                }
            }
        }
        catch
        {
            DestroyIcon(hIcon);
        }

        return fallback;
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, ref IconInfo pIconInfo);
}
