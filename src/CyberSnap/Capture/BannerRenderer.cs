using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.Helpers;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>Shared layout and painting for the in-host and layered hint banners.</summary>
internal static class BannerRenderer
{
    internal const int PaddingH = 28;
    internal const int PaddingV = 17;
    internal const int IconGap = 10;
    /// <summary>Extra breathing room between the label (ending in ':') and its hint.</summary>
    internal const float SegmentGap = 8f;

    internal static SizeF MeasureContent(Graphics g, string text, IReadOnlyList<BannerSegment>? segments, Font font)
    {
        var sf = StringFormat.GenericTypographic;
        if (segments is { Count: > 0 })
        {
            float width = 0f;
            float height = 0f;
            for (int index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var size = g.MeasureString(segment.Text, font, PointF.Empty, sf);
                width += size.Width;
                if (index > 0)
                    width += SegmentGap;
                height = Math.Max(height, size.Height);
            }

            return new SizeF(width, Math.Max(height, 1f));
        }

        return g.MeasureString(text, font, PointF.Empty, sf);
    }

    internal static SizeF GetBannerSize(SizeF contentSize, bool hasIcon)
    {
        float iconBlock = hasIcon ? contentSize.Height * 0.92f + IconGap : 0f;
        return new SizeF(
            contentSize.Width + iconBlock + PaddingH * 2,
            contentSize.Height + PaddingV * 2);
    }

    internal static void Render(
        Graphics g,
        RectangleF bannerRect,
        string text,
        IReadOnlyList<BannerSegment>? segments,
        string? iconId,
        Color? iconColorOverride,
        float opacity)
    {
        if (opacity <= 0f)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = UiChrome.ChromeFont(16f, FontStyle.Regular);
        var contentSize = MeasureContent(g, text, segments, font);
        float iconSize = contentSize.Height * 0.92f;
        float iconBlock = iconId != null ? iconSize + IconGap : 0f;

        int alphaBg = Math.Min((int)((Theme.IsDark ? 255 : 235) * opacity), 255);
        int alphaBorder = (int)((Theme.IsDark ? 140 : 110) * opacity);
        int alphaGlow = (int)((Theme.IsDark ? 40 : 24) * opacity);
        int alphaText = (int)(255 * opacity);

        var accent = StandaloneToolBanner.AccentColor;
        var bg = StandaloneToolBanner.BackgroundColor;
        var label = StandaloneToolBanner.LabelColor;
        var iconColor = iconColorOverride ?? label;

        using var path = RoundedRect(bannerRect, 10);
        using var bgBrush = new SolidBrush(Color.FromArgb(alphaBg, bg));
        using var glowPen = new Pen(Color.FromArgb(alphaGlow, accent), 3f);
        using var borderPen = new Pen(Color.FromArgb(alphaBorder, accent), 1.5f);

        g.FillPath(bgBrush, path);
        g.DrawPath(glowPen, path);
        g.DrawPath(borderPen, path);

        if (iconId != null)
        {
            float iconX = bannerRect.X + PaddingH;
            float iconY = bannerRect.Y + (bannerRect.Height - iconSize) / 2f;
            FluentIcons.DrawIcon(g, iconId,
                new RectangleF(iconX, iconY, iconSize, iconSize),
                Color.FromArgb(alphaText, iconColor), 0f);
        }

        if (segments != null)
        {
            var typo = StringFormat.GenericTypographic;
            float cursorX = bannerRect.X + PaddingH + iconBlock;
            float textTop = bannerRect.Y + PaddingV;
            for (int index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                if (index > 0)
                    cursorX += SegmentGap;
                var segmentColor = ResolveSegmentColor(segment.Color, accent, label);
                using var brush = new SolidBrush(Color.FromArgb(alphaText, segmentColor));
                g.DrawString(segment.Text, font, brush, cursorX, textTop, typo);
                cursorX += g.MeasureString(segment.Text, font, PointF.Empty, typo).Width;
            }
        }
        else
        {
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            var textRect = new RectangleF(
                bannerRect.X + PaddingH + iconBlock,
                bannerRect.Y + PaddingV,
                contentSize.Width,
                contentSize.Height);
            using var textBrush = new SolidBrush(Color.FromArgb(alphaText, accent));
            g.DrawString(text, font, textBrush, textRect, sf);
        }
    }

    private static Color ResolveSegmentColor(Color? overrideColor, Color accent, Color label)
    {
        if (overrideColor is null)
            return accent;
        if (overrideColor.Value.ToArgb() == Color.White.ToArgb())
            return label;
        return overrideColor.Value;
    }

    internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
