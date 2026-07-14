using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CyberSnap.Helpers;

internal static class CursorFactory
{
    private static Cursor? _eraserCursor;
    private static Cursor? _eyedropperCursor;
    private static Cursor? _panCursor;
    private static Cursor? _precisionCursor;
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
                _hiddenCursor = new Cursor(bmp.GetHicon());
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

    public static Cursor PanCursor
    {
        get
        {
            if (_panCursor is null)
                _panCursor = CreatePanCursor();
            return _panCursor;
        }
    }

    private static Cursor CreatePanCursor()
    {
        const int size = 44;
        const int cx = size / 2, cy = size / 2;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        const int iconSize = 30;
        int offset = (size - iconSize) / 2;

        var shadow = StreamlineIcons.RenderBitmap("pan", Color.FromArgb(160, 0, 0, 0), iconSize, active: true);
        if (shadow != null)
        {
            g.DrawImage(shadow, offset + 1, offset + 1, iconSize, iconSize);
            shadow.Dispose();
        }

        var icon = StreamlineIcons.RenderBitmap("pan", Color.FromArgb(245, 255, 255, 255), iconSize, active: true);
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

        return Cursors.Hand;
    }

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

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, ref IconInfo pIconInfo);
}
