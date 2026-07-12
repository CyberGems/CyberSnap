using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CyberSnap.Models;
using CyberSnap.Services;
using RadioButton = System.Windows.Controls.RadioButton;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace CyberSnap.UI.Controls;

/// <summary>
/// A macOS-style theme picker tile: a metallic card holding a live mini-preview of a
/// theme (window + text lines + accent dot), with a caption below and an accent glow
/// when selected. It IS a RadioButton, so grouping / IsChecked / the existing
/// Checked handlers keep working — only the presentation changes.
///
/// The preview is painted from Theme.PreviewPalette(mode), computed off-band, so each
/// of the four cards shows its OWN theme instead of the single global DynamicResource
/// palette. The "System" card renders a diagonal light/dark split to signal "follows OS".
/// </summary>
internal sealed class ThemeCard : RadioButton
{
    // Card footprint is configurable so the same control fits the roomy Settings row
    // (132) and the narrow SetupWizard column (~104). Everything inside scales off it.
    private const double DefaultCardWidth = 132;
    private const double PreviewAspect = 70.0 / 108.0;   // height / width of the mini-window
    private const double CardPadding = 9;                // metallic frame around the preview

    private double CardW => CardWidth;
    private double PreviewW => CardWidth - CardPadding * 2;
    private double PreviewH => PreviewW * PreviewAspect;

    private Border _card = null!;
    private Border _glow = null!;
    private TextBlock _caption = null!;

    public static readonly DependencyProperty ThemeModeProperty =
        DependencyProperty.Register(nameof(ThemeMode), typeof(AppThemeMode), typeof(ThemeCard),
            new PropertyMetadata(AppThemeMode.Dark, OnVisualChanged));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(ThemeCard),
            new PropertyMetadata("Theme", OnVisualChanged));

    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(ThemeCard),
            new PropertyMetadata(DefaultCardWidth, OnVisualChanged));

    public static readonly DependencyProperty IsDefaultThemeProperty =
        DependencyProperty.Register(nameof(IsDefaultTheme), typeof(bool), typeof(ThemeCard),
            new PropertyMetadata(false, OnDefaultThemeChanged));

    public AppThemeMode ThemeMode
    {
        get => (AppThemeMode)GetValue(ThemeModeProperty);
        set => SetValue(ThemeModeProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public bool IsDefaultTheme
    {
        get => (bool)GetValue(IsDefaultThemeProperty);
        set => SetValue(IsDefaultThemeProperty, value);
    }

    public ThemeCard()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        FocusVisualStyle = null;
        HorizontalAlignment = WpfHorizontalAlignment.Center;
        VerticalAlignment = WpfVerticalAlignment.Top;
        // Strip the default radio bullet: the whole card is the control.
        Template = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "TargetType=\"RadioButton\"><ContentPresenter/></ControlTemplate>");

        Content = Build();
        UpdateSelectionVisual();

        Checked += (_, _) => UpdateSelectionVisual();
        Unchecked += (_, _) => UpdateSelectionVisual();
        MouseEnter += (_, _) => UpdateSelectionVisual();
        MouseLeave += (_, _) => UpdateSelectionVisual();
        Loaded += (_, _) => UpdateDefaultTooltip();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThemeCard c) c.Rebuild();
    }

    private static void OnDefaultThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThemeCard c) c.UpdateDefaultTooltip();
    }

    private void UpdateDefaultTooltip()
    {
        if (IsDefaultTheme)
        {
            var baseTip = ToolTip?.ToString() ?? Caption;
            ToolTip = baseTip + "\n" + LocalizationService.Translate("Default theme");
        }
    }

    private void Rebuild()
    {
        Content = Build();
        UpdateSelectionVisual();
    }

    private FrameworkElement Build()
    {
        var stack = new StackPanel { Width = CardW };

        // Accent glow sits UNDER the card so a selected tile reads as lit from behind.
        _glow = new Border
        {
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(-1),
            Background = Brushes.Transparent
        };

        _card = new Border
        {
            Width = CardW,
            CornerRadius = new CornerRadius(11),
            Background = MetallicBrush(),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(40, 255, 255, 255)),
            Padding = new Thickness(CardPadding),
            Child = BuildPreview()
        };

        var cardHost = new Grid();
        cardHost.Children.Add(_glow);
        cardHost.Children.Add(_card);

        _caption = new TextBlock
        {
            Text = Caption,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            FontFamily = new WpfFontFamily("Segoe UI Variable Text"),
            FontSize = 12.5
        };

        stack.Children.Add(cardHost);
        stack.Children.Add(_caption);
        return stack;
    }

    // Brushed-metal backdrop, matching the reference: cool graphite with a soft top sheen.
    private static Brush MetallicBrush()
    {
        var g = new LinearGradientBrush { StartPoint = new WpfPoint(0.1, 0), EndPoint = new WpfPoint(0.9, 1) };
        g.GradientStops.Add(new GradientStop(WpfColor.FromRgb(64, 66, 71), 0.0));
        g.GradientStops.Add(new GradientStop(WpfColor.FromRgb(48, 50, 55), 0.45));
        g.GradientStops.Add(new GradientStop(WpfColor.FromRgb(38, 40, 45), 1.0));
        g.Freeze();
        return g;
    }

    private FrameworkElement BuildPreview()
    {
        var pal = Theme.PreviewPalette(ThemeMode);

        // The little floating "app window" inside the tile.
        var window = new Border
        {
            Width = PreviewW,
            Height = PreviewH,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(pal.Background),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = WpfVerticalAlignment.Center,
            ClipToBounds = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.5,
                Color = Colors.Black
            }
        };

        if (ThemeMode == AppThemeMode.System)
        {
            window.Child = BuildSystemSplit();
        }
        else
        {
            window.Child = BuildWindowContent(pal);
        }

        return window;
    }

    // Content of one mini-window: reload glyph, three text lines, an accent dot.
    private FrameworkElement BuildWindowContent(Theme.ThemePalette pal)
    {
        double inset = PreviewW * 0.085;
        var root = new Grid { Margin = new Thickness(inset, inset * 0.9, inset, inset * 0.9) };

        var lines = new StackPanel { VerticalAlignment = WpfVerticalAlignment.Center };

        // Small ring glyph top-left (the ⟳ in the reference).
        var ring = new Ellipse
        {
            Width = 9,
            Height = 9,
            Stroke = new SolidColorBrush(pal.TextMuted),
            StrokeThickness = 1.4,
            HorizontalAlignment = WpfHorizontalAlignment.Left,
            VerticalAlignment = WpfVerticalAlignment.Top
        };

        // Line widths are fractions of the inner width so they scale with the card.
        double inner = PreviewW - inset * 2;
        lines.Children.Add(Line(pal.TextLine, 0.92, inner * 0.46, 12));
        lines.Children.Add(Line(pal.TextMuted, 0.75, inner * 0.64, 6));
        lines.Children.Add(Line(pal.TextMuted, 0.55, inner * 0.38, 6));

        // Accent dot bottom-right.
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(pal.Accent),
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = WpfVerticalAlignment.Bottom
        };

        root.Children.Add(lines);
        root.Children.Add(ring);
        root.Children.Add(dot);
        return root;
    }

    private static FrameworkElement Line(WpfColor color, double opacity, double width, double topMargin)
    {
        return new Border
        {
            Height = 4,
            Width = width,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(color) { Opacity = opacity },
            HorizontalAlignment = WpfHorizontalAlignment.Left,
            Margin = new Thickness(0, topMargin, 0, 0)
        };
    }

    // Diagonal light/dark split for the "System / follows OS" card.
    private FrameworkElement BuildSystemSplit()
    {
        var light = Theme.PreviewPalette(AppThemeMode.Light);
        var dark = Theme.PreviewPalette(AppThemeMode.Dark);

        var grid = new Grid();

        // Dark half fills the whole window; light half is a clipped top-left triangle.
        var darkLayer = BuildWindowContent(dark);

        var lightBg = new Border { Background = new SolidColorBrush(light.Background) };
        var lightContent = BuildWindowContent(light);
        var lightLayer = new Grid();
        lightLayer.Children.Add(lightBg);
        lightLayer.Children.Add(lightContent);
        lightLayer.Clip = new PathGeometry(new[]
        {
            new PathFigure(new WpfPoint(0, 0), new[]
            {
                new LineSegment(new WpfPoint(PreviewW, 0), false),
                new LineSegment(new WpfPoint(0, PreviewH), false)
            }, true)
        });

        grid.Children.Add(darkLayer);
        grid.Children.Add(lightLayer);
        return grid;
    }

    private void UpdateSelectionVisual()
    {
        if (_card == null) return;

        var pal = Theme.PreviewPalette(ThemeMode);
        var accent = ThemeMode == AppThemeMode.System
            ? Theme.PreviewPalette(AppThemeMode.Dark).Accent
            : pal.Accent;

        bool selected = IsChecked == true;
        bool hover = IsMouseOver;

        if (selected)
        {
            _card.BorderBrush = new SolidColorBrush(accent);
            _card.BorderThickness = new Thickness(1.6);
            _glow.Background = new SolidColorBrush(accent);
            _glow.Effect = new DropShadowEffect
            {
                BlurRadius = 9,
                ShadowDepth = 0,
                Opacity = 0.5,
                Color = accent
            };
            _glow.Opacity = 1;
            _caption.FontWeight = FontWeights.SemiBold;
            _caption.Foreground = new SolidColorBrush(WpfColor.FromRgb(240, 242, 246));
        }
        else
        {
            _card.BorderBrush = new SolidColorBrush(
                hover ? WpfColor.FromArgb(90, 255, 255, 255) : WpfColor.FromArgb(40, 255, 255, 255));
            _card.BorderThickness = new Thickness(1);
            _glow.Effect = null;
            _glow.Background = Brushes.Transparent;
            _glow.Opacity = 0;
            _caption.FontWeight = FontWeights.Normal;
            _caption.Foreground = new SolidColorBrush(WpfColor.FromRgb(150, 154, 162));
        }
    }
}
