using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using CyberSnap.Capture;
using CyberSnap.Helpers;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Painters for the Blur, Step Number, Magnifier and Emoji annotation tools.
/// Ported from the capture overlay (RegionOverlayForm.Paint.*); adapted to render
/// in image space against <see cref="_baseBitmap"/> so they compose correctly inside
/// the canvas zoom/pan transform.
/// </summary>
public sealed partial class AnnotationCanvas
{
    // ── Blur ───────────────────────────────────────────────────────────────

    /// <summary>Pixelates/blurs a region by downscaling a copy of the base bitmap and
    /// drawing it back enlarged. Reads from the clean base image (not other annotations),
    /// matching the overlay's behavior.</summary>
    private void PaintBlurRect(Graphics g, Rectangle rect)
    {
        if (rect.Width < 3 || rect.Height < 3) return;
        var clamped = Rectangle.Intersect(rect, new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        if (clamped.Width < 1 || clamped.Height < 1) return;

        int blockSize = 10;
        int sw = Math.Max(2, clamped.Width / blockSize);
        int sh = Math.Max(2, clamped.Height / blockSize);

        if (_blurScratch == null || _blurScratch.Width != sw || _blurScratch.Height != sh)
        {
            _blurScratch?.Dispose();
            _blurScratch = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        }

        using (var small = Graphics.FromImage(_blurScratch))
        {
            small.Clear(Color.Transparent);
            small.InterpolationMode = InterpolationMode.HighQualityBilinear;
            small.PixelOffsetMode = PixelOffsetMode.Half;
            small.DrawImage(_baseBitmap, new Rectangle(0, 0, sw, sh), clamped, GraphicsUnit.Pixel);
        }

        var state = g.Save();
        try
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_blurScratch, clamped);
        }
        finally
        {
            g.Restore(state);
        }
    }

    // ── Step number badge ──────────────────────────────────────────────────

    private static Font? _stepNumberFont;
    private static readonly SolidBrush StepNumberShadowBrush = new(Color.FromArgb(50, 0, 0, 0));
    private static readonly Pen StepNumberInnerEdgePen = new(Color.FromArgb(40, 255, 255, 255), 1f);
    private static readonly SolidBrush StepNumberDarkText = new(Color.FromArgb(20, 20, 20));
    private static readonly SolidBrush StepNumberLightText = new(Color.FromArgb(255, 255, 255));

    private static void PaintStepNumber(Graphics g, Point pos, int num, Color color, float opacity = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var font = _stepNumberFont ??= UiChrome.ChromeFont(11f, FontStyle.Bold);
        string text = num.ToString();
        var sz = g.MeasureString(text, font);

        float padX = 8f, padY = 4f;
        float w = Math.Max(sz.Width + padX * 2, sz.Height + padY * 2);
        float h = sz.Height + padY * 2;
        float r = h / 2f;
        var rect = new RectangleF(pos.X - w / 2f, pos.Y - h / 2f, w, h);

        using var shadowPath = SketchRenderer.RoundedRect(
            new RectangleF(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), r);
        using var bgPath = SketchRenderer.RoundedRect(rect, r);
        int luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;

        if (opacity >= 1f)
        {
            g.FillPath(StepNumberShadowBrush, shadowPath);
            g.FillPath(SketchRenderer.GetToolColorBrush(color), bgPath);
            g.DrawPath(StepNumberInnerEdgePen, bgPath);
            var textBrush = luma > 140 ? StepNumberDarkText : StepNumberLightText;
            g.DrawString(text, font, textBrush, rect.X + (rect.Width - sz.Width) / 2f, rect.Y + (rect.Height - sz.Height) / 2f);
        }
        else
        {
            // Translucent ghost preview — fade every layer uniformly so the badge reads as
            // "about to be placed" rather than committed.
            int a = (int)Math.Clamp(255 * opacity, 0, 255);
            using var shadow = new SolidBrush(Color.FromArgb((int)(50 * opacity), 0, 0, 0));
            using var bg = new SolidBrush(Color.FromArgb(a, color.R, color.G, color.B));
            using var edge = new Pen(Color.FromArgb((int)(40 * opacity), 255, 255, 255), 1f);
            g.FillPath(shadow, shadowPath);
            g.FillPath(bg, bgPath);
            g.DrawPath(edge, bgPath);
            var tc = luma > 140 ? Color.FromArgb(a, 20, 20, 20) : Color.FromArgb(a, 255, 255, 255);
            using var textBrush = new SolidBrush(tc);
            g.DrawString(text, font, textBrush, rect.X + (rect.Width - sz.Width) / 2f, rect.Y + (rect.Height - sz.Height) / 2f);
        }

        g.TextRenderingHint = TextRenderingHint.SystemDefault;
        g.SmoothingMode = SmoothingMode.Default;
    }

    // ── Magnifier ────────────────────────────────────────────────────────────

    private const int MagnifierZoom = 3;
    private const int MagnifierSrcSize = 50;

    /// <summary>Source region (image-space) the magnifier samples for a point at <paramref name="img"/>,
    /// clamped to the base bitmap. Shared by placement and the hover preview.</summary>
    private Rectangle GetMagnifierSrcRect(Point img)
    {
        int sx = Math.Clamp(img.X - MagnifierSrcSize / 2, 0, Math.Max(0, _baseBitmap.Width - MagnifierSrcSize));
        int sy = Math.Clamp(img.Y - MagnifierSrcSize / 2, 0, Math.Max(0, _baseBitmap.Height - MagnifierSrcSize));
        return new Rectangle(sx, sy, MagnifierSrcSize, MagnifierSrcSize);
    }

    /// <summary>Visual bounds (image-space) of a placed magnifier lens, used for repaint
    /// invalidation. Mirrors the placement math in <see cref="PaintMagnifier"/>.</summary>
    private Rectangle GetMagnifierLensBounds(Point pos, Rectangle srcRect)
    {
        int dstSize = srcRect.Width * MagnifierZoom;
        int px = pos.X + 20;
        int py = pos.Y + 20;
        if (px + dstSize + 6 > _baseBitmap.Width) px = pos.X - 20 - dstSize;
        if (py + dstSize + 6 > _baseBitmap.Height) py = pos.Y - 20 - dstSize;
        return new Rectangle(px - 6, py - 6, dstSize + 12, dstSize + 12);
    }

    private void PaintMagnifier(Graphics g, Point pos, Rectangle srcRect, float opacity = 1f)
    {
        int dstSize = srcRect.Width * MagnifierZoom;

        int px = pos.X + 20;
        int py = pos.Y + 20;
        if (px + dstSize + 6 > _baseBitmap.Width) px = pos.X - 20 - dstSize;
        if (py + dstSize + 6 > _baseBitmap.Height) py = pos.Y - 20 - dstSize;

        var dstRect = new Rectangle(px, py, dstSize, dstSize);

        var state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bgPath = new GraphicsPath())
            {
                bgPath.AddEllipse(new RectangleF(px - 2, py - 2, dstSize + 4, dstSize + 4));
                var bg = SketchRenderer.GetToolColorBrush(Color.FromArgb((int)(200 * opacity), UiChrome.SurfaceElevated.R, UiChrome.SurfaceElevated.G, UiChrome.SurfaceElevated.B));
                g.FillPath(bg, bgPath);
            }

            using var clipPath = new GraphicsPath();
            clipPath.AddEllipse(dstRect);
            g.SetClip(clipPath);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_baseBitmap, dstRect, srcRect, GraphicsUnit.Pixel);

            int ccx = px + dstSize / 2, ccy = py + dstSize / 2;
            var crossPen = SketchRenderer.GetRoundCapPen(Color.FromArgb((int)(180 * opacity), UiChrome.SurfaceTextPrimary.R, UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B), 1f);
            g.DrawLine(crossPen, ccx - 8, ccy, ccx + 8, ccy);
            g.DrawLine(crossPen, ccx, ccy - 8, ccx, ccy + 8);

            g.ResetClip();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var borderPen = SketchRenderer.GetRoundCapPen(Color.FromArgb((int)(70 * opacity), UiChrome.SurfaceBorderStrong.R, UiChrome.SurfaceBorderStrong.G, UiChrome.SurfaceBorderStrong.B), 1f);
            g.DrawPath(borderPen, clipPath);
        }
        finally
        {
            g.Restore(state);
        }
    }

    // ── Emoji ────────────────────────────────────────────────────────────────

    private static ImageAttributes? _emojiOpacityAttr;
    private static ColorMatrix? _emojiOpacityMatrix;

    private void PaintEmoji(Graphics g, Point pos, string emoji, float size, float opacity = 1f)
    {
        var emojiBmp = _emojiRenderer.GetEmoji(emoji, size);

        if (opacity < 1f)
        {
            _emojiOpacityAttr ??= new ImageAttributes();
            _emojiOpacityMatrix ??= new ColorMatrix();
            _emojiOpacityMatrix.Matrix00 = 1f;
            _emojiOpacityMatrix.Matrix11 = 1f;
            _emojiOpacityMatrix.Matrix22 = 1f;
            _emojiOpacityMatrix.Matrix33 = opacity;
            _emojiOpacityMatrix.Matrix44 = 1f;
            _emojiOpacityAttr.SetColorMatrix(_emojiOpacityMatrix);
            g.DrawImage(emojiBmp, new Rectangle(pos.X, pos.Y, emojiBmp.Width, emojiBmp.Height),
                0, 0, emojiBmp.Width, emojiBmp.Height, GraphicsUnit.Pixel, _emojiOpacityAttr);
        }
        else
        {
            g.DrawImage(emojiBmp, pos.X, pos.Y);
        }
    }
}
