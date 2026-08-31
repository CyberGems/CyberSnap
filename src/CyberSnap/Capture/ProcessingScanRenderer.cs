using System.Drawing;
using System.Drawing.Drawing2D;
using CyberSnap.Helpers;

namespace CyberSnap.Capture;

/// <summary>
/// Shared lightweight scan-line rendering for processing selected regions.
/// </summary>
internal static class ProcessingScanRenderer
{
    internal const int AnimationIntervalMs = 24;
    internal const int AnimationMinDurationMs = 150;
    internal const int AnimationMaxDurationMs = 420;

    internal static int GetAnimationDurationMs(int rectHeight) =>
        Math.Clamp(100 + Math.Max(0, rectHeight) / 2, AnimationMinDurationMs, AnimationMaxDurationMs);

    internal static void Draw(Graphics g, Rectangle rect, float progress)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        var accent = UiChrome.AccentColor;
        var state = g.Save();
        g.SetClip(rect);

        // Keep the selected content visible while making the processing state feel active.
        int tintAlpha = UiChrome.IsDark ? 28 : 18;
        using (var tintBrush = new SolidBrush(Color.FromArgb(tintAlpha, accent)))
            g.FillRectangle(tintBrush, rect);

        // One deterministic top-to-bottom pass, independent of the selection size.
        float travel = Math.Clamp(progress, 0f, 1f);
        int bandHeight = GetBandHeight(rect.Height);
        float centerY = rect.Top + bandHeight / 2f
            + travel * Math.Max(0, rect.Height - bandHeight);
        var bandRect = new Rectangle(
            rect.Left,
            Math.Max(rect.Top, (int)(centerY - bandHeight / 2f)),
            rect.Width,
            bandHeight);

        using (var bandBrush = new LinearGradientBrush(
            bandRect,
            Color.FromArgb(0, accent),
            Color.FromArgb(0, accent),
            LinearGradientMode.Vertical))
        {
            bandBrush.InterpolationColors = new ColorBlend
            {
                Positions = new[] { 0f, 0.5f, 1f },
                Colors = new[]
                {
                    Color.FromArgb(0, accent),
                    Color.FromArgb(120, accent),
                    Color.FromArgb(0, accent)
                }
            };
            g.FillRectangle(bandBrush, bandRect);
        }

        float glowWidth = Math.Max(2f, UiChrome.ScaleFloat(4f));
        float coreWidth = Math.Max(1f, UiChrome.ScaleFloat(1.2f));
        using (var glowPen = new Pen(Color.FromArgb(95, accent), glowWidth))
            g.DrawLine(glowPen, rect.Left, centerY, rect.Right, centerY);
        using (var corePen = new Pen(Color.FromArgb(210, accent), coreWidth))
            g.DrawLine(corePen, rect.Left, centerY, rect.Right, centerY);

        g.Restore(state);
    }

    private static int GetBandHeight(int rectHeight) =>
        Math.Max(10, Math.Min(42, rectHeight / 4));
}
