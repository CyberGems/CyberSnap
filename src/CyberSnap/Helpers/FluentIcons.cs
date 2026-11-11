using System.Drawing;
using System.Windows.Media.Imaging;

namespace CyberSnap.Helpers;

/// <summary>
/// Compatibility facade for call sites that request Fluent icons.
/// Rendering stays backed by bundled SVG path data, not OS icon fonts, so icons
/// are stable across Windows 10 and Windows 11.
/// </summary>
public static class FluentIcons
{
    public static void Preload() => StreamlineIcons.Preload();

    public static Bitmap? GetIcon(string id, bool active = false)
        => StreamlineIcons.GetIcon(id, active);

    public static Bitmap? RenderBitmap(string id, Color color, int size, bool active = false)
        => StreamlineIcons.RenderBitmap(id, color, size, active);

    public static bool HasIcon(string id) => StreamlineIcons.HasIcon(id);

    public static void DrawIcon(Graphics g, string id, RectangleF bounds, Color color, float iconInset = 7f, bool active = false, bool flipHorizontal = false)
        => StreamlineIcons.DrawIcon(g, id, bounds, color, iconInset, active, flipHorizontal);

    public static BitmapSource? RenderWpf(string id, Color color, int size, bool active = false)
        => StreamlineIcons.RenderWpf(id, color, size, active);

    public static System.Windows.Media.Geometry? GetGeometry(string id, bool active = false)
        => StreamlineIcons.GetGeometry(id, active);

    internal static System.Windows.Controls.Image CreateWpfMenuIcon(string id, int size = 16,
        System.Windows.Media.Color? colorOverride = null)
    {
        var drawing = new System.Windows.Media.DrawingGroup();
        using (var context = drawing.Open())
        {
            // Keep the icon in the same 20-unit viewbox, including a margin around its outline.
            context.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new System.Windows.Rect(0, 0, 20, 20));
            context.PushTransform(new System.Windows.Media.MatrixTransform(0.88, 0, 0, 0.88, 1.2, 1.2));
            context.DrawGeometry(UI.Theme.Brush(colorOverride ?? UI.Theme.TextPrimary), null, GetGeometry(id));
            context.Pop();
        }
        drawing.Freeze();
        var source = new System.Windows.Media.DrawingImage(drawing);
        source.Freeze();
        return new System.Windows.Controls.Image
        {
            Source = source,
            Width = size,
            Height = size,
            SnapsToDevicePixels = true,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
    }
}
