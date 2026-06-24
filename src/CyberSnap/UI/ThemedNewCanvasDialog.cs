using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Helpers;
using Button = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI;

/// <summary>Result of <see cref="ThemedNewCanvasDialog"/>; null when the user cancels.</summary>
internal sealed class NewCanvasResult
{
    public int Width;
    public int Height;
}

/// <summary>
/// Simplified "New canvas" dialog — resolution presets and width/height inputs.
/// Much leaner than ThemedResizeDialog (no aspect lock, scale, or anchor grid).
/// </summary>
internal sealed class ThemedNewCanvasDialog : Window
{
    private const double GlowMargin = 16;
    private const double PanelWidth = 400;

    private static readonly (string Label, int W, int H)[] ResolutionPresets =
    {
        ("640 × 480", 640, 480),
        ("800 × 600", 800, 600),
        ("1024 × 768", 1024, 768),
        ("1366 × 768", 1366, 768),
        ("1920 × 1080", 1920, 1080),
        ("2560 × 1440", 2560, 1440),
    };

    private int _width;
    private int _height;
    private bool _confirmed;
    private bool _suppress;

    private WpfTextBox _widthBox = null!;
    private WpfTextBox _heightBox = null!;

    private readonly System.Collections.Generic.List<(Border Border, int W, int H)> _resolutionChipElements = new();

    private ThemedNewCanvasDialog()
    {
        Theme.Refresh();

        // Default canvas size
        _width = 1024;
        _height = 768;

        Title = Services.LocalizationService.Translate("New canvas");
        Width = PanelWidth + (GlowMargin * 2);
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new WpfFontFamily(UiChrome.PreferredFamilyName);
        Foreground = Theme.Brush(Theme.TextPrimary);

        var content = BuildContent();
        Content = content;
        UiScale.ApplyToWindow(this, content, scaleWindowBounds: true);

        SizeToContent = SizeToContent.Height;
        SizeChanged += (_, _) => ClampToWorkingArea();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
        };
    }

    private void ClampToWorkingArea()
    {
        if (ActualHeight <= 0) return;
        var wa = SystemParameters.WorkArea;
        double top = Top;
        if (top + ActualHeight > wa.Bottom) top = wa.Bottom - ActualHeight;
        if (top < wa.Top) top = wa.Top;
        Top = top;
    }

    public static NewCanvasResult? Show(IntPtr ownerHandle)
    {
        var dialog = new ThemedNewCanvasDialog();
        if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(dialog).Owner = ownerHandle;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        if (dialog.ShowDialog() == true && dialog._confirmed)
        {
            return new NewCanvasResult
            {
                Width = dialog._width,
                Height = dialog._height,
            };
        }
        return null;
    }

    // ── Content ────────────────────────────────────────────────────────────

    private FrameworkElement BuildContent()
    {
        var accent = Theme.Accent;

        var shell = new Border
        {
            Margin = new Thickness(GlowMargin),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(PanelBackground),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
            BorderThickness = new Thickness(1),
            Effect = Glow(accent, Theme.IsDark ? 10 : 7, Theme.IsDark ? 0.18 : 0.11)
        };

        var root = new StackPanel { Margin = new Thickness(22) };

        // Title row: icon badge + title + close.
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = BuildIcon(accent);
        Grid.SetColumn(badge, 0);
        titleGrid.Children.Add(badge);
        var titleText = new TextBlock
        {
            Text = Services.LocalizationService.Translate("New canvas").ToUpper(CultureInfo.CurrentCulture),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(titleText, 1);
        titleGrid.Children.Add(titleText);
        var close = BuildClose();
        Grid.SetColumn(close, 1);
        titleGrid.Children.Add(close);
        root.Children.Add(titleGrid);

        // Body
        var bodyStack = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };

        // Resolution presets
        bodyStack.Children.Add(SectionLabel("Resolution", 0));
        var resWrap = new WrapPanel();
        _resolutionChipElements.Clear();
        foreach (var (label, w, h) in ResolutionPresets)
        {
            var chip = MakeChip(label, border =>
            {
                ApplyResolution(w, h);
                // Highlight the clicked chip
                foreach (var (cb, _, _) in _resolutionChipElements)
                {
                    bool isActive = cb == border;
                    cb.Tag = isActive;
                    cb.Background = Theme.Brush(isActive ? WithAlpha(accent, 60) : FieldBackground);
                    cb.BorderBrush = Theme.Brush(isActive ? WithAlpha(accent, 200) : WithAlpha(accent, 60));
                }
            });
            _resolutionChipElements.Add((chip, w, h));
            resWrap.Children.Add(chip);
        }
        bodyStack.Children.Add(resWrap);

        // Width / Height inputs
        bodyStack.Children.Add(SectionLabel("Dimensions", 14));
        var inputsRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _widthBox = BuildNumberField("Width", _width, OnWidthChanged);
        _heightBox = BuildNumberField("Height", _height, OnHeightChanged);

        var widthCol = WrapField("Width", _widthBox);
        var heightCol = WrapField("Height", _heightBox);

        Grid.SetColumn(widthCol, 0);
        Grid.SetColumn(heightCol, 2);
        inputsRow.Children.Add(widthCol);
        inputsRow.Children.Add(heightCol);
        bodyStack.Children.Add(inputsRow);

        root.Children.Add(bodyStack);

        // Buttons row
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        buttons.Children.Add(BuildButton("Cancel", isPrimary: false, () => Close()));
        buttons.Children.Add(BuildButton("Create", isPrimary: true, Commit));
        root.Children.Add(buttons);

        shell.Child = root;
        shell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not WpfTextBox)
            {
                try { DragMove(); } catch { /* released mid-drag */ }
            }
        };

        return shell;
    }

    private void Commit()
    {
        _width = Math.Clamp(_width, Controls.AnnotationCanvas.MinCanvasSize, Controls.AnnotationCanvas.MaxCanvasSize);
        _height = Math.Clamp(_height, Controls.AnnotationCanvas.MinCanvasSize, Controls.AnnotationCanvas.MaxCanvasSize);
        _confirmed = true;
        DialogResult = true;
        Close();
    }

    // ── Input handling ─────────────────────────────────────────────────────

    private void OnWidthChanged(int v)
    {
        if (_suppress) return;
        _width = v;
    }

    private void OnHeightChanged(int v)
    {
        if (_suppress) return;
        _height = v;
    }

    private void ApplyResolution(int w, int h)
    {
        _suppress = true;
        _width = w;
        _height = h;
        SetBoxText(_widthBox, w);
        SetBoxText(_heightBox, h);
        _suppress = false;
    }

    // ── UI builders ────────────────────────────────────────────────────────

    private FrameworkElement SectionLabel(string text, double topMargin) => new TextBlock
    {
        Text = Services.LocalizationService.Translate(text),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = Theme.Brush(Theme.TextMuted),
        Margin = new Thickness(0, topMargin, 0, 6),
    };

    private WpfTextBox BuildNumberField(string label, int value, Action<int> onChanged)
    {
        var box = new WpfTextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            FontSize = 14,
            Height = 34,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(FieldBackground),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 90)),
            BorderThickness = new Thickness(1),
            CaretBrush = Theme.Brush(Theme.Accent),
        };
        box.PreviewTextInput += (_, e) => e.Handled = !IsAllDigits(e.Text);
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
                onChanged(v);
        };
        box.Tag = label;
        return box;
    }

    private FrameworkElement WrapField(string label, WpfTextBox box)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate(label),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(2, 0, 0, 5),
        });
        panel.Children.Add(box);
        return panel;
    }

    private Border MakeChip(string text, Action<Border> onClick)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.TextSecondary),
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = WpfCursors.Hand,
            Child = textBlock
        };
        chip.MouseEnter += (_, _) =>
        {
            bool isActive = chip.Tag is bool b && b;
            if (!isActive)
            {
                chip.Background = Theme.Brush(WithAlpha(Theme.Accent, 40));
                chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 150));
            }
        };
        chip.MouseLeave += (_, _) =>
        {
            bool isActive = chip.Tag is bool b && b;
            if (!isActive)
            {
                chip.Background = Theme.Brush(FieldBackground);
                chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60));
            }
        };
        chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(chip); };
        return chip;
    }

    private static FrameworkElement BuildIcon(WpfColor accent) => new Border
    {
        Width = 40,
        Height = 40,
        CornerRadius = new CornerRadius(11),
        VerticalAlignment = VerticalAlignment.Center,
        Background = Theme.Brush(WithAlpha(accent, 28)),
        BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
        BorderThickness = new Thickness(1),
        Effect = Glow(accent, Theme.IsDark ? 10 : 7, Theme.IsDark ? 0.18 : 0.11),
        Child = new System.Windows.Controls.Image
        {
            Source = FluentIcons.RenderWpf("document", ToDrawingColor(accent, 235), 22),
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private FrameworkElement BuildClose()
    {
        var close = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = WpfBrushes.Transparent,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = WpfCursors.Hand,
            Child = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("close", ToDrawingColor(Theme.TextMuted, 220), 14),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
            }
        };
        close.MouseEnter += (_, _) => close.Background = Theme.Brush(Theme.TabHoverBg);
        close.MouseLeave += (_, _) => close.Background = WpfBrushes.Transparent;
        close.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };
        return close;
    }

    private Button BuildButton(string text, bool isPrimary, Action click)
    {
        var accent = Theme.Accent;
        var button = new Button
        {
            Content = Services.LocalizationService.Translate(text).ToUpper(CultureInfo.CurrentCulture),
            MinWidth = 94,
            Height = 34,
            Margin = new Thickness(isPrimary ? 10 : 0, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = WpfCursors.Hand,
            IsDefault = isPrimary,
            IsCancel = !isPrimary,
            BorderThickness = new Thickness(1),
            Template = BuildButtonTemplate()
        };

        if (isPrimary)
        {
            var baseBg = WithAlpha(accent, Theme.IsDark ? (byte)40 : (byte)28);
            var restText = Theme.Brush(WpfColor.FromRgb(accent.R, accent.G, accent.B));
            var hoverText = Theme.Brush(Theme.IsDark ? Colors.Black : Colors.White);
            button.Background = Theme.Brush(baseBg);
            button.BorderBrush = Theme.Brush(WithAlpha(accent, 170));
            button.Foreground = restText;
            button.MouseEnter += (_, _) => { button.Background = Theme.Brush(accent); button.Foreground = hoverText; button.Effect = Glow(accent, 7, 0.40); };
            button.MouseLeave += (_, _) => { button.Background = Theme.Brush(baseBg); button.Foreground = restText; button.Effect = null; };
        }
        else
        {
            button.Background = Theme.Brush(SecondaryButtonBg);
            button.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            button.Foreground = Theme.Brush(Theme.TextPrimary);
            button.MouseEnter += (_, _) => { button.Background = Theme.Brush(Theme.TabHoverBg); };
            button.MouseLeave += (_, _) => { button.Background = Theme.Brush(SecondaryButtonBg); };
        }

        button.Click += (_, _) => click();
        return button;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void SetBoxText(WpfTextBox box, int value)
    {
        box.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsAllDigits(string text)
    {
        foreach (char c in text)
            if (!char.IsDigit(c)) return false;
        return true;
    }

    // Theme constants matching ThemedResizeDialog
    private static WpfColor PanelBackground => Theme.IsDark
        ? WpfColor.FromArgb(255, 28, 30, 43)
        : WpfColor.FromArgb(255, 248, 249, 252);
    private static WpfColor FieldBackground => Theme.IsDark
        ? WpfColor.FromArgb(255, 20, 22, 33)
        : WpfColor.FromArgb(255, 238, 240, 245);
    private static WpfColor SecondaryButtonBg => Theme.IsDark
        ? WpfColor.FromArgb(255, 48, 50, 66)
        : WpfColor.FromArgb(255, 228, 230, 237);
    private static WpfColor SecondaryButtonBorder => Theme.IsDark
        ? WpfColor.FromArgb(40, 255, 255, 255)
        : WpfColor.FromArgb(30, 0, 0, 0);

    private static WpfColor WithAlpha(WpfColor color, byte alpha) =>
        WpfColor.FromArgb(alpha, color.R, color.G, color.B);

    private static System.Windows.Media.Effects.DropShadowEffect Glow(WpfColor color, double blur, double opacity) =>
        new() { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };

    private static System.Drawing.Color ToDrawingColor(WpfColor color, byte alpha) =>
        System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static ControlTemplate BuildButtonTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        factory.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        factory.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        factory.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(0, -1, 0, 0));
        factory.AppendChild(contentPresenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = factory };
    }
}
