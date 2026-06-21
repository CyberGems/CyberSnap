using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CyberSnap.Helpers;
using Button = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI;

internal sealed class PdfExportOptions
{
    public string Title = "";
    public string Author = Environment.UserName;
    public string Tags = "";
    public string PageSize = "Letter"; // "Letter", "A4", "Fit"
    public string Orientation = "Portrait"; // "Portrait", "Landscape"
    public double MarginTop = 1.0; // in cm
    public double MarginBottom = 1.0;
    public double MarginLeft = 1.0;
    public double MarginRight = 1.0;
    public string ImageLayout = "Fit"; // "Fit", "Span"
}

internal sealed class ThemedPdfExportDialog : Window
{
    private const double GlowMargin = 16;
    private const double PanelWidth = 680;

    private readonly PdfExportOptions _options = new();
    private bool _confirmed;

    private WpfTextBox _titleBox = null!;
    private WpfTextBox _authorBox = null!;
    private WpfTextBox _tagsBox = null!;

    private WpfTextBox _marginTopBox = null!;
    private WpfTextBox _marginBottomBox = null!;
    private WpfTextBox _marginLeftBox = null!;
    private WpfTextBox _marginRightBox = null!;

    private readonly System.Collections.Generic.List<(Border Border, string Value)> _pageSizeChips = new();
    private readonly System.Collections.Generic.List<(Border Border, string Value)> _orientationChips = new();
    private readonly System.Collections.Generic.List<(Border Border, string Value)> _layoutChips = new();

    private Border _previewPaper = null!;
    private Border _previewMargin = null!;
    private Border _previewImage = null!;
    private TextBlock _previewPageCount = null!;

    private ThemedPdfExportDialog()
    {
        Theme.Refresh();
        Title = Services.LocalizationService.Translate("PDF Export Options");
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
        };

        UpdatePreview();
    }

    public static PdfExportOptions? Show(IntPtr ownerHandle)
    {
        var dialog = new ThemedPdfExportDialog();
        if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(dialog).Owner = ownerHandle;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        if (dialog.ShowDialog() == true && dialog._confirmed)
        {
            return dialog._options;
        }
        return null;
    }

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
            Text = Services.LocalizationService.Translate("PDF Export Options").ToUpper(CultureInfo.CurrentCulture),
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

        // Body Grid: Left Stack (Settings), Right Stack (Preview & Save/Cancel)
        var bodyGrid = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) }); // Left settings
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });                       // Spacer
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) }); // Right preview

        // LEFT COLUMN (Settings)
        var leftStack = new StackPanel();

        // 1. Metadata Section
        leftStack.Children.Add(SectionLabel("Document Metadata", 0));
        _titleBox = BuildTextField("Title", _options.Title, v => { _options.Title = v; UpdatePreview(); });
        _authorBox = BuildTextField("Author", _options.Author, v => { _options.Author = v; UpdatePreview(); });
        _tagsBox = BuildTextField("Tags / Keywords", _options.Tags, v => { _options.Tags = v; UpdatePreview(); });
        leftStack.Children.Add(WrapField("Title", _titleBox));
        leftStack.Children.Add(WrapField("Author", _authorBox));
        leftStack.Children.Add(WrapField("Tags", _tagsBox));

        // 2. Page Setup Section
        leftStack.Children.Add(SectionLabel("Page Size", 12));
        var sizeWrap = new WrapPanel();
        _pageSizeChips.Add((MakeSelectableChip("Letter", sizeWrap, "Letter", v => { _options.PageSize = v; UpdatePreview(); }), "Letter"));
        _pageSizeChips.Add((MakeSelectableChip("A4", sizeWrap, "A4", v => { _options.PageSize = v; UpdatePreview(); }), "A4"));
        _pageSizeChips.Add((MakeSelectableChip("Fit Image Size", sizeWrap, "Fit", v => { _options.PageSize = v; UpdatePreview(); }), "Fit"));
        UpdateChipSelection(_pageSizeChips, _options.PageSize);
        leftStack.Children.Add(sizeWrap);

        leftStack.Children.Add(SectionLabel("Orientation", 10));
        var orientWrap = new WrapPanel();
        _orientationChips.Add((MakeSelectableChip("Portrait", orientWrap, "Portrait", v => { _options.Orientation = v; UpdatePreview(); }), "Portrait"));
        _orientationChips.Add((MakeSelectableChip("Landscape", orientWrap, "Landscape", v => { _options.Orientation = v; UpdatePreview(); }), "Landscape"));
        UpdateChipSelection(_orientationChips, _options.Orientation);
        leftStack.Children.Add(orientWrap);

        // Margins Row (Top, Bottom, Left, Right)
        leftStack.Children.Add(SectionLabel("Margins (cm)", 10));
        var marginsGrid = new Grid();
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        marginsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _marginTopBox = BuildMarginField(_options.MarginTop, v => { _options.MarginTop = v; UpdatePreview(); });
        _marginBottomBox = BuildMarginField(_options.MarginBottom, v => { _options.MarginBottom = v; UpdatePreview(); });
        _marginLeftBox = BuildMarginField(_options.MarginLeft, v => { _options.MarginLeft = v; UpdatePreview(); });
        _marginRightBox = BuildMarginField(_options.MarginRight, v => { _options.MarginRight = v; UpdatePreview(); });

        var topCol = WrapField("Top", _marginTopBox);
        var bottomCol = WrapField("Bottom", _marginBottomBox);
        var leftCol = WrapField("Left", _marginLeftBox);
        var rightCol = WrapField("Right", _marginRightBox);

        Grid.SetColumn(topCol, 0);
        Grid.SetColumn(bottomCol, 2);
        Grid.SetColumn(leftCol, 4);
        Grid.SetColumn(rightCol, 6);

        marginsGrid.Children.Add(topCol);
        marginsGrid.Children.Add(bottomCol);
        marginsGrid.Children.Add(leftCol);
        marginsGrid.Children.Add(rightCol);
        leftStack.Children.Add(marginsGrid);

        // 3. Image Layout Section
        leftStack.Children.Add(SectionLabel("Image Layout", 12));
        var layoutWrap = new WrapPanel();
        _layoutChips.Add((MakeSelectableChip("Fit on page (shrink)", layoutWrap, "Fit", v => { _options.ImageLayout = v; UpdatePreview(); }), "Fit"));
        _layoutChips.Add((MakeSelectableChip("Allow to span multiple pages", layoutWrap, "Span", v => { _options.ImageLayout = v; UpdatePreview(); }), "Span"));
        UpdateChipSelection(_layoutChips, _options.ImageLayout);
        leftStack.Children.Add(layoutWrap);

        Grid.SetColumn(leftStack, 0);
        bodyGrid.Children.Add(leftStack);

        // RIGHT COLUMN (Preview Panel)
        var previewContainer = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(Theme.IsDark ? WpfColor.FromArgb(20, 255, 255, 255) : WpfColor.FromArgb(12, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Height = 360,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var previewStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        previewStack.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate("PREVIEW").ToUpper(CultureInfo.CurrentCulture),
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextMuted),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        });

        // The Paper Frame
        _previewPaper = new Border
        {
            Width = 140,
            Height = 180,
            Background = WpfBrushes.White,
            BorderBrush = WpfBrushes.DarkGray,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 1, Opacity = 0.15 }
        };

        // Dashed Margins Border
        _previewMargin = new Border
        {
            Margin = new Thickness(10),
            BorderBrush = WpfBrushes.CornflowerBlue,
            BorderThickness = new Thickness(1),
            Background = WpfBrushes.Transparent
        };
        // Apply dashed styling to margins
        var dashArray = new DoubleCollection { 3, 3 };
        // We'll simulate dashed margins using a Rectangle shape inside the border instead, for perfect scaling
        var marginShape = new System.Windows.Shapes.Rectangle
        {
            Stroke = Theme.Brush(Theme.Accent),
            StrokeThickness = 1,
            StrokeDashArray = dashArray,
            Margin = new Thickness(10)
        };

        // Simulated Image Area inside paper
        _previewImage = new Border
        {
            Background = Theme.Brush(WithAlpha(Theme.Accent, 75)),
            BorderBrush = Theme.Brush(Theme.Accent),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(15),
            Child = new TextBlock
            {
                Text = Services.LocalizationService.Translate("IMAGE"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 30, 46)),
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var paperGrid = new Grid();
        paperGrid.Children.Add(marginShape);
        paperGrid.Children.Add(_previewImage);
        _previewPaper.Child = paperGrid;

        previewStack.Children.Add(_previewPaper);

        _previewPageCount = new TextBlock
        {
            Text = "1 Page",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextSecondary),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        previewStack.Children.Add(_previewPageCount);

        previewContainer.Child = previewStack;
        Grid.SetColumn(previewContainer, 2);
        bodyGrid.Children.Add(previewContainer);

        root.Children.Add(bodyGrid);

        // Footer buttons
        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        buttons.Children.Add(BuildButton("Cancel", isPrimary: false, () => Close()));
        buttons.Children.Add(BuildButton("Export", isPrimary: true, Commit));
        root.Children.Add(buttons);

        shell.Child = root;
        shell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not WpfTextBox)
            {
                try { DragMove(); } catch { }
            }
        };

        return shell;
    }

    private void Commit()
    {
        _confirmed = true;
        DialogResult = true;
        Close();
    }

    private void UpdatePreview()
    {
        if (_previewPaper == null) return;

        // Toggle margins & orientation visually
        bool isLandscape = _options.Orientation == "Landscape";
        if (isLandscape)
        {
            _previewPaper.Width = 180;
            _previewPaper.Height = 140;
        }
        else
        {
            _previewPaper.Width = 140;
            _previewPaper.Height = 180;
        }

        // Show Fit size visual override
        if (_options.PageSize == "Fit")
        {
            _previewPaper.Background = Theme.Brush(WithAlpha(Theme.Accent, 15));
            _previewImage.Margin = new Thickness(0);
            _previewPageCount.Text = Services.LocalizationService.Translate("1 Page (Auto-fit size)");
        }
        else
        {
            _previewPaper.Background = WpfBrushes.White;
            // Set margin thicknesses relative to page dimensions
            double marginVal = Math.Clamp(_options.MarginLeft, 0, 5) * 5; // scaled down visual scale
            _previewImage.Margin = new Thickness(marginVal + 4);
            _previewPageCount.Text = _options.ImageLayout == "Span" 
                ? Services.LocalizationService.Translate("Multiple Pages (Slices)") 
                : Services.LocalizationService.Translate("1 Page (Scale to fit)");
        }
    }

    // ── Dialog element builders ──────────────────────────────────────────────

    private TextBlock SectionLabel(string text, double top) => new()
    {
        Text = Services.LocalizationService.Translate(text).ToUpper(CultureInfo.CurrentCulture),
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Foreground = Theme.Brush(Theme.TextMuted),
        Margin = new Thickness(0, top, 0, 6)
    };

    private FrameworkElement WrapField(string label, FrameworkElement field)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate(label),
            FontSize = 11,
            Foreground = Theme.Brush(Theme.TextSecondary),
            Margin = new Thickness(2, 0, 0, 4)
        });
        stack.Children.Add(field);
        return stack;
    }

    private WpfTextBox BuildTextField(string placeholder, string val, Action<string> onChanged)
    {
        var box = new WpfTextBox
        {
            Text = val,
            FontSize = 12,
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(SecondaryButtonBorder),
            BorderThickness = new Thickness(1),
            Foreground = Theme.Brush(Theme.TextPrimary),
            Padding = new Thickness(8, 6, 8, 6),
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => onChanged(box.Text);
        return box;
    }

    private WpfTextBox BuildMarginField(double val, Action<double> onChanged)
    {
        var box = new WpfTextBox
        {
            Text = val.ToString("0.0", CultureInfo.InvariantCulture),
            FontSize = 12,
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(SecondaryButtonBorder),
            BorderThickness = new Thickness(1),
            Foreground = Theme.Brush(Theme.TextPrimary),
            Padding = new Thickness(4, 6, 4, 6),
            Height = 32,
            FontFamily = new WpfFontFamily("Consolas"),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) =>
        {
            if (double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            {
                onChanged(result);
            }
        };
        return box;
    }

    private Border MakeSelectableChip(string label, WrapPanel parent, string val, Action<string> onClick)
    {
        var border = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(5),
            Background = Theme.Brush(SecondaryButtonBg),
            BorderBrush = Theme.Brush(SecondaryButtonBorder),
            BorderThickness = new Thickness(1),
            Cursor = WpfCursors.Hand,
            Child = new TextBlock
            {
                Text = Services.LocalizationService.Translate(label),
                FontSize = 11,
                Foreground = Theme.Brush(Theme.TextSecondary)
            }
        };

        border.MouseEnter += (_, _) =>
        {
            if (border.Background != Theme.Brush(Theme.Accent))
            {
                border.Background = Theme.Brush(Theme.TabHoverBg);
                if (border.Child is TextBlock tb)
                    tb.Foreground = Theme.Brush(Theme.TextPrimary);
            }
        };
        border.MouseLeave += (_, _) =>
        {
            if (border.Background != Theme.Brush(Theme.Accent))
            {
                border.Background = Theme.Brush(SecondaryButtonBg);
                if (border.Child is TextBlock tb)
                    tb.Foreground = Theme.Brush(Theme.TextSecondary);
            }
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick(val);
            // We'll update visual chips through helper call
            var flow = parent.Children;
            foreach (UIElement item in flow)
            {
                if (item is Border b)
                {
                    b.Background = Theme.Brush(SecondaryButtonBg);
                    b.BorderBrush = Theme.Brush(SecondaryButtonBorder);
                    if (b.Child is TextBlock tb)
                        tb.Foreground = Theme.Brush(Theme.TextSecondary);
                }
            }

            border.Background = Theme.Brush(Theme.Accent);
            border.BorderBrush = Theme.Brush(Theme.Accent);
            if (border.Child is TextBlock textBlock)
                textBlock.Foreground = Theme.Brush(Theme.IsDark ? Colors.Black : Colors.White);
        };

        parent.Children.Add(border);
        return border;
    }

    private static void UpdateChipSelection(System.Collections.Generic.List<(Border Border, string Value)> chips, string currentVal)
    {
        foreach (var (border, val) in chips)
        {
            bool active = val == currentVal;
            border.Background = Theme.Brush(active ? Theme.Accent : SecondaryButtonBg);
            border.BorderBrush = Theme.Brush(active ? Theme.Accent : SecondaryButtonBorder);
            if (border.Child is TextBlock tb)
                tb.Foreground = Theme.Brush(active ? (Theme.IsDark ? Colors.Black : Colors.White) : Theme.TextSecondary);
        }
    }

    private Button BuildButton(string text, bool isPrimary, Action click)
    {
        var accent = Theme.Accent;
        var button = new Button
        {
            Content = Services.LocalizationService.Translate(text),
            MinWidth = 94,
            Height = 34,
            Margin = new Thickness(6, 0, 6, 0),
            Padding = new Thickness(22, 0, 22, 0),
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
            button.MouseEnter += (_, _) =>
            {
                button.Background = Theme.Brush(accent);
                button.Foreground = hoverText;
                button.Effect = Glow(accent, 7, 0.40);
            };
            button.MouseLeave += (_, _) =>
            {
                button.Background = Theme.Brush(baseBg);
                button.Foreground = restText;
                button.Effect = null;
            };
        }
        else
        {
            button.Background = Theme.Brush(SecondaryButtonBg);
            button.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            button.Foreground = Theme.Brush(Theme.TextSecondary);
            button.MouseEnter += (_, _) =>
            {
                button.Background = Theme.Brush(Theme.TabHoverBg);
                button.BorderBrush = Theme.Brush(WithAlpha(accent, 120));
                button.Foreground = Theme.Brush(Theme.TextPrimary);
            };
            button.MouseLeave += (_, _) =>
            {
                button.Background = Theme.Brush(SecondaryButtonBg);
                button.BorderBrush = Theme.Brush(SecondaryButtonBorder);
                button.Foreground = Theme.Brush(Theme.TextSecondary);
            };
        }

        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate BuildButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Button.BorderThickness)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Button.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

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
                Source = FluentIcons.RenderWpf("close", System.Drawing.Color.FromArgb(220, 128, 128, 128), 14),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.MouseEnter += (_, _) => close.Background = Theme.Brush(Theme.TabHoverBg);
        close.MouseLeave += (_, _) => close.Background = WpfBrushes.Transparent;
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        return close;
    }

    private static FrameworkElement BuildIcon(WpfColor accent) => new System.Windows.Controls.Image
    {
        Source = FluentIcons.RenderWpf("document", System.Drawing.Color.FromArgb(235, accent.R, accent.G, accent.B), 24),
        Width = 24,
        Height = 24,
        Stretch = Stretch.Uniform
    };

    private static DropShadowEffect Glow(WpfColor color, double blur, double opacity) => new()
    {
        Color = color,
        ShadowDepth = 0,
        BlurRadius = blur,
        Opacity = opacity
    };

    private static WpfColor WithAlpha(WpfColor c, byte alpha) => WpfColor.FromArgb(alpha, c.R, c.G, c.B);

    private static WpfColor PanelBackground =>
        Theme.IsDark ? WpfColor.FromRgb(15, 17, 26) : WpfColor.FromRgb(245, 246, 248);

    private static WpfColor FieldBackground =>
        Theme.IsDark ? WpfColor.FromRgb(10, 11, 18) : WpfColor.FromRgb(237, 239, 243);

    private static WpfColor SecondaryButtonBg =>
        Theme.IsDark ? WpfColor.FromRgb(26, 30, 46) : WpfColor.FromRgb(249, 249, 249);

    private static WpfColor SecondaryButtonBorder =>
        Theme.IsDark ? WpfColor.FromArgb(32, 255, 255, 255) : WpfColor.FromArgb(26, 0, 0, 0);

    private static WpfColor TrackOff =>
        Theme.IsDark ? WpfColor.FromRgb(46, 50, 66) : WpfColor.FromRgb(219, 222, 228);
}
