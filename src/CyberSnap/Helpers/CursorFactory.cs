using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CyberSnap.Helpers;

internal static class CursorFactory
{
    private static Cursor? _eraserCursor;
    private static Cursor? _panCursor;

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

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, ref IconInfo pIconInfo);
}
