using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

/// <summary>
/// Shared center move badge (selection drag affordance): elegant semi-transparent
/// dark disc, thin accent border, neutral-light 4-directional arrows. Used by the
/// capture, recording and scrolling selection surfaces so the badge reads the same
/// everywhere. Callers own visibility, hover tracking and opacity animation.
/// </summary>
internal static class MoveBadgeRenderer
{
    public static int BadgeDiameter => UiChrome.ScaleInt(36);

    public static Rectangle BadgeRectFor(Rectangle region)
    {
        int d = BadgeDiameter;
        return new Rectangle(
            region.X + (region.Width - d) / 2,
            region.Y + (region.Height - d) / 2,
            d, d);
    }

    public static void Paint(Graphics g, RectangleF rect, bool hover, float opacity = 1f)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || opacity <= 0f)
            return;

        var accent = UiChrome.AccentColor;
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;

        // Drop shadow.
        int shadowAlpha = (int)((hover ? 90 : 60) * opacity);
        using (var shadowBrush = new SolidBrush(Color.FromArgb(shadowAlpha, 0, 0, 0)))
            g.FillEllipse(shadowBrush, rect.X, rect.Y + 1.5f, rect.Width, rect.Height);

        // Elegant semi-transparent dark body.
        int bgA = (int)((hover ? 190 : 150) * opacity);
        using (var bgBrush = new SolidBrush(Color.FromArgb(bgA, 24, 26, 32)))
            g.FillEllipse(bgBrush, rect);

        // Thin accent border.
        float borderW = hover ? 1.4f : 1f;
        int borderA = (int)((hover ? 190 : 110) * opacity);
        using (var borderPen = new Pen(Color.FromArgb(borderA, accent), borderW))
            g.DrawEllipse(borderPen, rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1f, rect.Height - 1f);

        // Neutral-light 4-directional move icon.
        float iconSz = UiChrome.ScaleFloat(12f);
        float stemW = UiChrome.ScaleFloat(1.8f);
        float arrowH = UiChrome.ScaleFloat(4.8f);
        float arrowHW = UiChrome.ScaleFloat(3.2f);
        float stemEnd = iconSz - arrowH;

        int iconA = (int)((hover ? 235 : 200) * opacity);
        Color iconColor = Color.FromArgb(iconA, 232, 236, 242);

        using (var iconBrush = new SolidBrush(iconColor))
        using (var stemPen = new Pen(iconColor, stemW)
        { StartCap = LineCap.Round, EndCap = LineCap.Flat })
        {
            g.DrawLine(stemPen, cx - stemEnd, cy, cx + stemEnd, cy);
            g.DrawLine(stemPen, cx, cy - stemEnd, cx, cy + stemEnd);

            g.FillPolygon(iconBrush, new PointF[]
                { new(cx - iconSz, cy), new(cx - stemEnd, cy - arrowHW), new(cx - stemEnd, cy + arrowHW) });
            g.FillPolygon(iconBrush, new PointF[]
                { new(cx + iconSz, cy), new(cx + stemEnd, cy - arrowHW), new(cx + stemEnd, cy + arrowHW) });
            g.FillPolygon(iconBrush, new PointF[]
                { new(cx, cy - iconSz), new(cx - arrowHW, cy - stemEnd), new(cx + arrowHW, cy - stemEnd) });
            g.FillPolygon(iconBrush, new PointF[]
                { new(cx, cy + iconSz), new(cx - arrowHW, cy + stemEnd), new(cx + arrowHW, cy + stemEnd) });
        }
    }
}
