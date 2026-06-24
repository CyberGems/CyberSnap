using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI.Controls;

/// <summary>
/// A reusable, self-contained RGB color picker panel: preview swatch, R/G/B sliders
/// with numeric boxes, and a "Pick from screen" button that drives the standalone
/// color picker tool. Renders its own card chrome so it can be dropped straight into a
/// <see cref="System.Windows.Controls.Primitives.Popup"/> flyout (New canvas dialog today,
/// the shape color selector in the tools column next).
/// </summary>
internal sealed class ColorPickerPopup : ContentControl
{
    /// <summary>Default flyout width; the card sizes its height to content.</summary>
    public const double FlyoutWidth = 248;

    private WpfColor _currentColor = WpfColor.FromRgb(128, 128, 128);
    private bool _hasColor;

    private Border _previewBorder = null!;
    private DrawingVisual _previewSwatch = null!;
    private Slider _sliderR = null!;
    private Slider _sliderG = null!;
    private Slider _sliderB = null!;
    private WpfTextBox _boxR = null!;
    private WpfTextBox _boxG = null!;
    private WpfTextBox _boxB = null!;
    private bool _suppress;

    /// <summary>Raised whenever the selected color changes; null means "no color / checkerboard".</summary>
    public event Action<WpfColor?>? ColorChanged;

    /// <summary>Raised when the host flyout should close (Accept pressed, or a screen pick started).</summary>
    public event Action? CloseRequested;

    /// <summary>The currently selected color, or null when cleared.</summary>
    public WpfColor? SelectedColor => _hasColor ? _currentColor : null;

    public ColorPickerPopup()
    {
        Theme.Refresh();
        BuildUI();
    }

    /// <summary>Adopt a concrete color (updates sliders, boxes, preview) and notify listeners.</summary>
    public void SetColor(WpfColor color)
    {
        _hasColor = true;
        _currentColor = color;
        _suppress = true;
        _sliderR.Value = color.R;
        _sliderG.Value = color.G;
        _sliderB.Value = color.B;
        SetBoxText(_boxR, color.R);
        SetBoxText(_boxG, color.G);
        SetBoxText(_boxB, color.B);
        _suppress = false;
        RefreshPreview();
        ColorChanged?.Invoke(_currentColor);
    }

    /// <summary>Clear back to the "no color" (checkerboard) state and notify listeners.</summary>
    public void ClearColor()
    {
        _hasColor = false;
        _currentColor = WpfColor.FromRgb(128, 128, 128);
        _suppress = true;
        _sliderR.Value = 128;
        _sliderG.Value = 128;
        _sliderB.Value = 128;
        SetBoxText(_boxR, 128);
        SetBoxText(_boxG, 128);
        SetBoxText(_boxB, 128);
        _suppress = false;
        RefreshPreview();
        ColorChanged?.Invoke(null);
    }

    private void BuildUI()
    {
        var accent = Theme.Accent;
        var accentBrush = Theme.Brush(WithAlpha(accent, 90));

        var stack = new StackPanel();

        _previewSwatch = new DrawingVisual();
        _previewBorder = new Border
        {
            Height = 52,
            CornerRadius = new CornerRadius(8),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 12),
            Child = new VisualDrawingVisualHost(_previewSwatch),
        };
        // ActualWidth/Height are 0 until the first layout pass — repaint once we have a real size.
        // A Border's CornerRadius does NOT clip its child, so also set a rounded Clip to keep the
        // painted swatch inside the rounded frame instead of poking out square at the corners.
        _previewBorder.SizeChanged += (_, e) =>
        {
            _previewBorder.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
            RefreshPreview();
        };
        stack.Children.Add(_previewBorder);

        stack.Children.Add(MakeSliderRow("R", 128, ChannelR, out _sliderR, out _boxR));
        stack.Children.Add(MakeSliderRow("G", 128, ChannelG, out _sliderG, out _boxG));
        stack.Children.Add(MakeSliderRow("B", 128, ChannelB, out _sliderB, out _boxB));

        // Row 1: full-width "Pick from screen" (gives the longer label room to breathe).
        var pickButton = BuildButton(LocalizationService.Translate("Pick from screen"), isAccent: true,
            () => { CloseRequested?.Invoke(); PickFromScreen(); }, iconId: "picker");
        pickButton.HorizontalAlignment = WpfHorizontalAlignment.Stretch;
        pickButton.MinWidth = 0;
        pickButton.Margin = new Thickness(0, 12, 0, 8);
        stack.Children.Add(pickButton);

        // Row 2: Clear | Accept, split evenly.
        var actionRow = new Grid();
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var clearButton = BuildButton(LocalizationService.Translate("Clear"), isAccent: false, ClearColor);
        clearButton.HorizontalAlignment = WpfHorizontalAlignment.Stretch;
        clearButton.MinWidth = 0;
        clearButton.Margin = new Thickness(0);
        Grid.SetColumn(clearButton, 0);
        actionRow.Children.Add(clearButton);

        var acceptButton = BuildButton(LocalizationService.Translate("Accept"), isAccent: true, () => CloseRequested?.Invoke());
        acceptButton.HorizontalAlignment = WpfHorizontalAlignment.Stretch;
        acceptButton.MinWidth = 0;
        acceptButton.Margin = new Thickness(0);
        Grid.SetColumn(acceptButton, 2);
        actionRow.Children.Add(acceptButton);

        stack.Children.Add(actionRow);

        var card = new Border
        {
            Width = FlyoutWidth,
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(PanelBg),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 3,
                Opacity = Theme.IsDark ? 0.55 : 0.30,
            },
            Child = stack,
        };

        Content = card;
        RefreshPreview();
    }

    private FrameworkElement MakeSliderRow(string label, byte defaultValue, WpfColor channelColor,
        out Slider slider, out WpfTextBox box)
    {
        var accent = Theme.Accent;
        var accentBrush = Theme.Brush(WithAlpha(accent, 90));
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        row.Children.Add(lbl);

        slider = new Slider
        {
            Minimum = 0,
            Maximum = 255,
            Value = defaultValue,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            Template = BuildSliderTemplate(channelColor),
        };
        slider.ValueChanged += OnSliderChanged;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        box = new WpfTextBox
        {
            Text = defaultValue.ToString(CultureInfo.InvariantCulture),
            FontSize = 11,
            Height = 24,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = WpfHorizontalAlignment.Center,
            Background = Theme.Brush(FieldBg),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = Theme.Brush(accent),
            Tag = label,
        };
        box.PreviewTextInput += (_, e) => e.Handled = !IsAllDigits(e.Text);
        box.TextChanged += OnBoxTextChanged;
        Grid.SetColumn(box, 2);
        row.Children.Add(box);

        return row;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _hasColor = true;
        _currentColor = WpfColor.FromRgb((byte)_sliderR.Value, (byte)_sliderG.Value, (byte)_sliderB.Value);
        _suppress = true;
        SetBoxText(_boxR, _currentColor.R);
        SetBoxText(_boxG, _currentColor.G);
        SetBoxText(_boxB, _currentColor.B);
        _suppress = false;
        RefreshPreview();
        ColorChanged?.Invoke(_currentColor);
    }

    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not WpfTextBox box || box.Tag is not string channel) return;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return;
        v = Math.Clamp(v, 0, 255);
        _suppress = true;
        switch (channel)
        {
            case "R": _sliderR.Value = v; break;
            case "G": _sliderG.Value = v; break;
            case "B": _sliderB.Value = v; break;
        }
        _suppress = false;
        _hasColor = true;
        _currentColor = WpfColor.FromRgb((byte)_sliderR.Value, (byte)_sliderG.Value, (byte)_sliderB.Value);
        RefreshPreview();
        ColorChanged?.Invoke(_currentColor);
    }

    /// <summary>
    /// Launch the standalone screen color picker on its own STA thread (matching the hotkey
    /// path), then adopt the committed color back on the UI thread. Reads the form's
    /// <see cref="StandaloneColorPickerForm.PickedColor"/> so a cancel leaves the swatch untouched.
    /// </summary>
    private void PickFromScreen()
    {
        var dispatcher = Dispatcher;
        var thread = new Thread(() =>
        {
            System.Drawing.Color? picked = null;
            try
            {
                Theme.Refresh();
                using var form = new StandaloneColorPickerForm();
                System.Windows.Forms.Application.Run(form);
                picked = form.PickedColor;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("colorpicker-popup.pick", ex);
            }

            if (picked is { } c)
                dispatcher.BeginInvoke(() => SetColor(WpfColor.FromRgb(c.R, c.G, c.B)));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void RefreshPreview()
    {
        var w = Math.Max(1, (int)_previewBorder.ActualWidth);
        var h = Math.Max(1, (int)_previewBorder.ActualHeight);

        using var dc = _previewSwatch.RenderOpen();
        if (_hasColor)
        {
            dc.DrawRectangle(Theme.Brush(_currentColor), null, new Rect(0, 0, w, h));
        }
        else
        {
            PaintCheckerboard(dc, w, h);
        }
    }

    /// <summary>
    /// Shared checkerboard fill used for the "no color" preview and the dialog swatch.
    /// Theme-aware so it previews the actual blank canvas: the same two tones the editor's
    /// <c>CreateBlankCheckerboard</c> paints (dark pair on Dark, light pair on Light).
    /// </summary>
    public static void PaintCheckerboard(DrawingContext dc, int w, int h)
    {
        const int size = 8;
        var c1 = Theme.IsDark ? WpfColor.FromRgb(20, 22, 33) : WpfColor.FromRgb(245, 246, 250);
        var c2 = Theme.IsDark ? WpfColor.FromRgb(28, 30, 43) : WpfColor.FromRgb(233, 235, 243);
        var baseTone = Theme.Brush(c1);
        var altTone = Theme.Brush(c2);
        dc.DrawRectangle(baseTone, null, new Rect(0, 0, w, h));
        for (int y = 0; y < h; y += size)
            for (int x = 0; x < w; x += size)
                if (((x / size) + (y / size)) % 2 == 1)
                    dc.DrawRectangle(altTone, null, new Rect(x, y, size, size));
    }

    private Button BuildButton(string text, bool isAccent, Action click, string? iconId = null)
    {
        var accent = Theme.Accent;

        var label = new TextBlock
        {
            Text = text.ToUpper(CultureInfo.CurrentCulture),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = iconId is null ? null : new System.Windows.Controls.Image
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        FrameworkElement content = label;
        if (icon is not null)
        {
            var sp = new StackPanel { Orientation = WpfOrientation.Horizontal };
            sp.Children.Add(icon);
            sp.Children.Add(label);
            content = sp;
        }

        var button = new Button
        {
            Content = content,
            Height = 28,
            MinWidth = isAccent ? 124 : 64,
            Margin = new Thickness(0, 0, isAccent ? 8 : 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = WpfCursors.Hand,
            BorderThickness = new Thickness(1),
            Template = BuildButtonTemplate(),
        };

        void SetIconColor(WpfColor c)
        {
            if (icon is not null)
                icon.Source = FluentIcons.RenderWpf(iconId!, System.Drawing.Color.FromArgb(c.R, c.G, c.B), 14);
        }

        if (isAccent)
        {
            var baseBg = WithAlpha(accent, Theme.IsDark ? (byte)40 : (byte)28);
            var restText = WpfColor.FromRgb(accent.R, accent.G, accent.B);
            var hoverText = Theme.IsDark ? Colors.Black : Colors.White;
            button.Background = Theme.Brush(baseBg);
            button.BorderBrush = Theme.Brush(WithAlpha(accent, 170));
            label.Foreground = Theme.Brush(restText);
            SetIconColor(restText);
            button.MouseEnter += (_, _) => { button.Background = Theme.Brush(accent); label.Foreground = Theme.Brush(hoverText); SetIconColor(hoverText); };
            button.MouseLeave += (_, _) => { button.Background = Theme.Brush(baseBg); label.Foreground = Theme.Brush(restText); SetIconColor(restText); };
        }
        else
        {
            button.Background = Theme.Brush(SecondaryButtonBg);
            button.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            label.Foreground = Theme.Brush(Theme.TextPrimary);
            SetIconColor(Theme.TextPrimary);
            button.MouseEnter += (_, _) => button.Background = Theme.Brush(Theme.TabHoverBg);
            button.MouseLeave += (_, _) => button.Background = Theme.Brush(SecondaryButtonBg);
        }

        button.Click += (_, _) => click();
        return button;
    }

    private static void SetBoxText(WpfTextBox box, int value) =>
        box.Text = value.ToString(CultureInfo.InvariantCulture);

    private static bool IsAllDigits(string text)
    {
        foreach (char c in text)
            if (!char.IsDigit(c)) return false;
        return true;
    }

    private static WpfColor PanelBg => Theme.IsDark
        ? WpfColor.FromArgb(255, 28, 30, 43)
        : WpfColor.FromArgb(255, 248, 249, 252);
    private static WpfColor FieldBg => Theme.IsDark
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

    // Per-channel thumb colors (R/G/B) and slider chrome tones.
    private static readonly WpfColor ChannelR = WpfColor.FromRgb(224, 67, 67);
    private static readonly WpfColor ChannelG = WpfColor.FromRgb(54, 178, 74);
    private static readonly WpfColor ChannelB = WpfColor.FromRgb(59, 130, 246);

    private static string ToHex(WpfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// A slim slider template: a rounded rail plus a circular thumb whose center is filled with
    /// the channel color (red on R, green on G, blue on B), so the control reads as RGB at a glance.
    /// </summary>
    private static ControlTemplate BuildSliderTemplate(WpfColor channelColor)
    {
        var rail = ToHex(Theme.IsDark ? WpfColor.FromRgb(58, 61, 77) : WpfColor.FromRgb(208, 210, 216));
        var outer = ToHex(Theme.IsDark ? WpfColor.FromRgb(201, 204, 211) : WpfColor.FromRgb(238, 240, 245));
        var stroke = ToHex(Theme.IsDark ? WpfColor.FromRgb(20, 22, 33) : WpfColor.FromRgb(176, 179, 186));
        var channel = ToHex(channelColor);

        string xaml =
            "<ControlTemplate TargetType=\"Slider\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
              " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
              "<Grid Height=\"18\" VerticalAlignment=\"Center\">" +
                $"<Border Height=\"4\" CornerRadius=\"2\" Background=\"{rail}\" VerticalAlignment=\"Center\"/>" +
                "<Track x:Name=\"PART_Track\">" +
                  "<Track.DecreaseRepeatButton>" +
                    "<RepeatButton Command=\"Slider.DecreaseLarge\" Focusable=\"False\" Opacity=\"0\"/>" +
                  "</Track.DecreaseRepeatButton>" +
                  "<Track.IncreaseRepeatButton>" +
                    "<RepeatButton Command=\"Slider.IncreaseLarge\" Focusable=\"False\" Opacity=\"0\"/>" +
                  "</Track.IncreaseRepeatButton>" +
                  "<Track.Thumb>" +
                    "<Thumb Width=\"18\" Height=\"18\">" +
                      "<Thumb.Template>" +
                        "<ControlTemplate TargetType=\"Thumb\">" +
                          "<Grid>" +
                            $"<Ellipse Fill=\"{outer}\" Stroke=\"{stroke}\" StrokeThickness=\"1\"/>" +
                            $"<Ellipse Margin=\"5\" Fill=\"{channel}\"/>" +
                          "</Grid>" +
                        "</ControlTemplate>" +
                      "</Thumb.Template>" +
                    "</Thumb>" +
                  "</Track.Thumb>" +
                "</Track>" +
              "</Grid>" +
            "</ControlTemplate>";

        return (ControlTemplate)XamlReader.Parse(xaml);
    }

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

    /// <summary>Hosts a <see cref="DrawingVisual"/> so we can paint the swatch imperatively.</summary>
    internal sealed class VisualDrawingVisualHost : FrameworkElement
    {
        private readonly DrawingVisual _visual;
        public VisualDrawingVisualHost(DrawingVisual visual) { _visual = visual; AddVisualChild(visual); }
        protected override Visual GetVisualChild(int index) => _visual;
        protected override int VisualChildrenCount => 1;
        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => availableSize;
        protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize) => finalSize;
    }
}
