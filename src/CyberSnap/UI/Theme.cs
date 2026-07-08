using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace CyberSnap.UI;

// Centralized theme colors.
public static class Theme
{
    public static bool IsDark { get; private set; } = true;

    // Backgrounds - Cyberpunk deep blues
    public static Color BgPrimary => IsDark ? C(13, 15, 23) : C(223, 226, 234);
    public static Color BgSecondary => IsDark ? C(18, 20, 31) : C(230, 233, 241);
    public static Color BgElevated => IsDark ? C(23, 26, 40) : C(236, 239, 246);
    public static Color BgHover => IsDark ? C(33, 38, 58) : C(214, 218, 229);
    public static Color BgCard => IsDark ? C(23, 26, 40) : C(236, 239, 246);
    public static Color BgOverlay => IsDark ? CA(0, 0, 0, 160) : CA(0, 0, 0, 100);

    // Text
    public static Color TextPrimary => IsDark ? C(230, 240, 255) : C(26, 26, 26);
    public static Color TextSecondary => IsDark ? C(160, 180, 210) : C(96, 96, 96);
    public static Color TextMuted => IsDark ? C(110, 130, 160) : C(128, 128, 128);

    // Borders
    public static Color Border => IsDark ? CA(0, 255, 255, 60) : CA(0, 0, 0, 22);
    public static Color BorderSubtle => IsDark ? CA(0, 255, 255, 30) : CA(0, 0, 0, 14);

    // Shared stroke: the one white outline used on preview, toast, buttons, cards
    public static Color Stroke => IsDark ? CA(255, 255, 255, 0xCC) : CA(0, 0, 0, 0x40);
    public const double StrokeThickness = 1.5;
    public static SolidColorBrush StrokeBrush() => Brush(Stroke);

    // Accent (Cyberpunk cyan)
    public static Color Accent => IsDark ? C(0, 255, 255) : C(0, 120, 215);
    public static Color AccentSubtle => IsDark ? CA(0, 255, 255, 20) : CA(0, 120, 215, 18);
    public static Color AccentHover => IsDark ? CA(0, 255, 255, 40) : CA(0, 120, 215, 28);
    public static Color DangerHover => IsDark ? CA(255, 0, 85, 210) : CA(196, 43, 28, 225);

    // Selection
    public static Color SelectionBg => IsDark ? CA(0, 255, 255, 25) : CA(0, 120, 215, 10);

    // Window chrome
    public static Color TitleBar => IsDark ? C(10, 12, 18) : C(220, 223, 232);
    public static Color WindowBorder => IsDark ? CA(0, 255, 255, 75) : CA(0, 0, 0, 20);
    public static Color CardBg => IsDark ? C(23, 26, 40) : C(236, 239, 246);
    public static Color TabActiveBg => IsDark ? CA(0, 255, 255, 25) : CA(0, 0, 0, 16);
    public static Color TabHoverBg => IsDark ? CA(0, 255, 255, 12) : CA(0, 0, 0, 10);
    public static Color PreviewStroke => IsDark ? CA(0, 255, 255, 64) : CA(0, 0, 0, 25);

    // Section icon tints
    public static Color SectionIconBg => IsDark ? CA(0, 240, 255, 14) : CA(0, 0, 0, 8);
    public static Color SectionIconFg => IsDark ? C(0, 240, 255) : CA(0, 0, 0, 170);

    // Separator
    public static Color Separator => IsDark ? CA(255, 255, 255, 16) : CA(0, 0, 0, 10);

    // Toast background (needs to be opaque enough to read)
    public static Color ToastBg => IsDark ? C(26, 27, 31) : C(234, 237, 244);
    public static Color ToastBorder => IsDark ? CA(255, 255, 255, 30) : CA(0, 0, 0, 18);

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static void ApplyTo(System.Windows.ResourceDictionary resources)
    {
        resources["ChromeButtonBackground"] = Brush(BgSecondary);
        resources["ChromeButtonForeground"] = Brush(TextPrimary);
        resources["ChromeButtonBorderBrush"] = Brush(BorderSubtle);
        resources["ChromeButtonHoverBrush"] = Brush(BgHover);
        resources["ChromeButtonPressedBrush"] = Brush(SelectionBg);
        resources["ChromeDangerButtonBackground"] = Brush(IsDark ? CA(196, 43, 28, 42) : CA(196, 43, 28, 24));
        resources["ChromeDangerButtonBorderBrush"] = Brush(IsDark ? CA(196, 43, 28, 92) : CA(196, 43, 28, 72));
        resources["ThemeTextPrimaryBrush"] = Brush(TextPrimary);
        resources["ThemeTextSecondaryBrush"] = Brush(TextSecondary);
        resources["ThemeMutedBrush"] = Brush(TextMuted);
        resources["ThemeCardBrush"] = Brush(BgCard);
        resources["ThemeInputBackgroundBrush"] = Brush(BgSecondary);
        resources["ThemeInputBorderBrush"] = Brush(BorderSubtle);
        resources["ThemeTabHoverBrush"] = Brush(TabHoverBg);
        resources["ThemeTabActiveBrush"] = Brush(TabActiveBg);
        resources["ThemeWindowBorderBrush"] = Brush(WindowBorder);
        resources["ThemeSeparatorBrush"] = Brush(Separator);
        resources["ThemeAccentBrush"] = Brush(Accent);
        resources["ThemeAccentSubtleBrush"] = Brush(AccentSubtle);
        resources["ThemeAccentHoverBrush"] = Brush(AccentHover);
        resources["ThemeTooltipBackgroundBrush"] = Brush(IsDark ? C(20, 20, 20) : C(233, 236, 243));
        resources["ThemeTooltipBorderBrush"] = Brush(IsDark ? CA(255, 255, 255, 26) : CA(0, 0, 0, 16));
        resources["SoundItemCustomSourceBrush"] = Brush(AccentHover);
    }

    private static Models.AppThemeMode _forcedMode = Models.AppThemeMode.System;

    /// <summary>Set the app-wide theme mode and refresh IsDark.</summary>
    public static void SetMode(Models.AppThemeMode mode)
    {
        _forcedMode = mode;
        Refresh();
    }

    public static void Refresh()
    {
        IsDark = _forcedMode switch
        {
            Models.AppThemeMode.Dark => true,
            Models.AppThemeMode.Light => false,
            _ => DetectDarkMode()
        };
    }

    private static bool DetectDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch { return true; }
    }

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color CA(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);

    public static System.Windows.Media.Brush CreateCheckerboardBrush()
    {
        var c1 = IsDark ? Color.FromRgb(20, 22, 33) : Color.FromRgb(245, 246, 250);
        var c2 = IsDark ? Color.FromRgb(28, 30, 43) : Color.FromRgb(233, 235, 243);

        var brush = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new System.Windows.Rect(0, 0, 32, 32),
            ViewportUnits = BrushMappingMode.Absolute
        };

        var geometryGroup = new GeometryGroup();
        geometryGroup.Children.Add(new RectangleGeometry(new System.Windows.Rect(0, 0, 16, 16)));
        geometryGroup.Children.Add(new RectangleGeometry(new System.Windows.Rect(16, 16, 16, 16)));

        var geometryDrawing = new GeometryDrawing
        {
            Brush = new SolidColorBrush(c2),
            Geometry = geometryGroup
        };

        var backgroundDrawing = new GeometryDrawing
        {
            Brush = new SolidColorBrush(c1),
            Geometry = new RectangleGeometry(new System.Windows.Rect(0, 0, 32, 32))
        };

        var drawingGroup = new DrawingGroup();
        drawingGroup.Children.Add(backgroundDrawing);
        drawingGroup.Children.Add(geometryDrawing);

        brush.Drawing = drawingGroup;
        brush.Freeze();
        return brush;
    }
}
