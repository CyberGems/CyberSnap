using System;

namespace CyberSnap.Helpers;

/// <summary>
/// Pure color-format conversions shared by the standalone color detail window.
/// No UI or settings dependencies so it stays unit-testable.
/// </summary>
public static class ColorFormatHelper
{
    public static string ToHex(byte r, byte g, byte b, bool includeHash) =>
        (includeHash ? "#" : "") + $"{r:X2}{g:X2}{b:X2}";

    public static string ToRgb(byte r, byte g, byte b) => $"rgb({r}, {g}, {b})";

    public static string ToHsl(byte r, byte g, byte b)
    {
        RgbToHsl(r, g, b, out double h, out double s, out double l);
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }

    public static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        h = 0;
        if (delta > 0)
        {
            if (max == rd)
                h = 60 * (((gd - bd) / delta) % 6);
            else if (max == gd)
                h = 60 * (((bd - rd) / delta) + 2);
            else
                h = 60 * (((rd - gd) / delta) + 4);
            if (h < 0)
                h += 360;
        }

        l = (max + min) / 2;
        s = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * l - 1));
    }

    /// <summary>WCAG relative luminance (0-1).</summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Channel(double c)
        {
            c /= 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    public static double ContrastRatio(byte r, byte g, byte b, bool againstWhite)
    {
        double lum = RelativeLuminance(r, g, b);
        double other = againstWhite ? 1.0 : 0.0;
        double lighter = Math.Max(lum, other);
        double darker = Math.Min(lum, other);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static string ContrastGrade(double ratio) =>
        ratio >= 7 ? "AAA" : ratio >= 4.5 ? "AA" : ratio >= 3 ? "AA18" : "Fail";

    /// <summary>Best-effort CSS-style color name for the preview line.</summary>
    public static string ApproximateName(byte r, byte g, byte b)
    {
        RgbToHsl(r, g, b, out double h, out double s, out double l);
        if (s < 0.08)
        {
            if (l > 0.95) return "White";
            if (l < 0.05) return "Black";
            return l > 0.5 ? "Light gray" : "Dark gray";
        }
        string hue = h switch
        {
            < 15 or >= 345 => "Red",
            < 45 => "Orange",
            < 70 => "Yellow",
            < 160 => "Green",
            < 200 => "Cyan",
            < 260 => "Blue",
            < 300 => "Purple",
            < 330 => "Pink",
            _ => "Red",
        };
        if (l > 0.85) return "Light " + hue.ToLowerInvariant();
        if (l < 0.15) return "Dark " + hue.ToLowerInvariant();
        return hue;
    }
}
