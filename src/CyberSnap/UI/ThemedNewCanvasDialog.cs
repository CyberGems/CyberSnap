using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
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

    /// <summary>Solid fill chosen by the user, or null to keep the default transparent checkerboard.</summary>
    public System.Drawing.Color? BackgroundColor;
}

/// <summary>
/// Simplified "New canvas" dialog â€” resolution presets and width/height inputs.
/// Much leaner than ThemedResizeDialog (no aspect lock, scale, or anchor grid).
/// </summary>
internal sealed class ThemedNewCanvasDialog : Window
{
    private const double GlowMargin = 16;
    private const double PanelWidth = 400;

    private const int MaxManualWidth = Controls.AnnotationCanvas.MaxCanvasSize;
    private const int MaxManualHeight = Controls.AnnotationCanvas.MaxCanvasSize;

    private static readonly (string Label, int W, int H)[] ResolutionPresets =
    {
        ("640 × 480", 640, 480),
        ("800 × 600", 800, 600),
        ("1024 × 768", 1024, 768),
        ("1280 × 720", 1280, 720),
        ("1366 × 768", 1366, 768),
        ("1920 × 1080", 1920, 1080),
        ("2560 × 1440", 2560, 1440),
        ("3840 × 2160", 3840, 2160),
    };

    private int _width;
    private int _height;
    private bool _confirmed;
    private bool _suppress;

    private WpfTextBox _widthBox = null!;
    private WpfTextBox _heightBox = null!;

    private System.Drawing.Color? _backgroundColor;
    private DrawingVisual _swatchVisual = null!;
    private Border _swatchHost = null!;
    private Popup _bgPopup = null!;
    private ColorPickerPopup _colorPicker = null!;
    private DateTime _bgPopupClosedAt;
    private TextBlock _warningText = null!;
    private Button _createButton = null!;
    private bool _isOversized;

    private readonly System.Collections.Generic.List<(Border Border, int W, int H)> _resolutionChipElements = new();

    private ThemedNewCanvasDialog()
    {
        Theme.Refresh();

        // Default canvas size
        _width = 1024;
        _height = 768;

        var savedBg = SettingsService.LoadStatic()?.EditorNewCanvasBackgroundColorArgb ?? 0;
        if (savedBg != 0)
            _backgroundColor = System.Drawing.Color.FromArgb(savedBg);

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
            else if (e.Key == Key.Enter)
            {
                if (_isOversized) return;
                e.Handled = true;
                Commit();
            }
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
                BackgroundColor = dialog._backgroundColor,
            };
        }
        return null;
    }

    // â”€â”€ Content â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
                HighlightResolutionChip(border);
            });
            _resolutionChipElements.Add((chip, w, h));
            resWrap.Children.Add(chip);
        }
        bodyStack.Children.Add(resWrap);

        // Pre-select the chip matching the default size so the dialog opens with a clear choice.
        foreach (var (chip, w, h) in _resolutionChipElements)
            if (w == _width && h == _height)
            {
                HighlightResolutionChip(chip);
                break;
            }

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

        // Warning for oversized canvas
        _warningText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(System.Windows.Media.Color.FromRgb(248, 113, 113)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        bodyStack.Children.Add(_warningText);
        // Canvas background: a swatch dropdown that opens the reusable color-picker flyout.
        bodyStack.Children.Add(SectionLabel("Canvas background", 14));
        bodyStack.Children.Add(BuildCanvasBackgroundDropdown());

        root.Children.Add(bodyStack);

        // Buttons row
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        buttons.Children.Add(BuildButton("Cancel", isPrimary: false, () => Close()));
        _createButton = BuildButton("Create", isPrimary: true, Commit);
        buttons.Children.Add(_createButton);
        root.Children.Add(buttons);

        UpdateWarning();

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
        if (_isOversized) return;
        _width = Math.Clamp(_width, Controls.AnnotationCanvas.MinCanvasSize, Controls.AnnotationCanvas.MaxCanvasSize);
        _height = Math.Clamp(_height, Controls.AnnotationCanvas.MinCanvasSize, Controls.AnnotationCanvas.MaxCanvasSize);
        _confirmed = true;
        DialogResult = true;
        Close();
    }

    // â”€â”€ Input handling â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnWidthChanged(int v)
    {
        if (_suppress) return;
        _width = v;
        DeselectAllChips();
        UpdateWarning();
    }

    private void OnHeightChanged(int v)
    {
        if (_suppress) return;
        _height = v;
        DeselectAllChips();
        UpdateWarning();
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

    private void UpdateWarning()
    {
        bool exceeds = _width > MaxManualWidth || _height > MaxManualHeight;
        _isOversized = exceeds;
        _createButton.IsEnabled = !exceeds;

        var accent = Theme.Accent;
        if (exceeds)
        {
            _warningText.BeginAnimation(OpacityProperty, null);
            _warningText.Text = string.Format(
                Services.LocalizationService.Translate("Canvas size exceeds maximum limit"),
                MaxManualWidth, MaxManualHeight);
            _warningText.Visibility = Visibility.Visible;
            _warningText.Opacity = 1.0;

            _createButton.Background = Theme.Brush(SecondaryButtonBg);
            _createButton.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            _createButton.Foreground = Theme.Brush(Theme.TextMuted);
            _createButton.Cursor = WpfCursors.Arrow;
        }
        else
        {
            if (_warningText.Visibility == Visibility.Visible)
            {
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0,
                    TimeSpan.FromMilliseconds(200));
                fadeOut.Completed += (_, _) =>
                {
                    _warningText.BeginAnimation(OpacityProperty, null);
                    _warningText.Visibility = Visibility.Collapsed;
                    _warningText.Opacity = 1.0;
                };
                _warningText.BeginAnimation(OpacityProperty, fadeOut);
            }

            _createButton.Background = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)40 : (byte)28));
            _createButton.BorderBrush = Theme.Brush(WithAlpha(accent, 170));
            _createButton.Foreground = Theme.Brush(WpfColor.FromRgb(accent.R, accent.G, accent.B));
            _createButton.Cursor = WpfCursors.Hand;
        }
    }

    private void HighlightResolutionChip(Border active)
    {
        var accent = Theme.Accent;
        foreach (var (chip, _, _) in _resolutionChipElements)
        {
            bool isActive = chip == active;
            chip.Tag = isActive;
            chip.Background = Theme.Brush(isActive ? WithAlpha(accent, 60) : FieldBackground);
            chip.BorderBrush = Theme.Brush(isActive ? WithAlpha(accent, 200) : WithAlpha(accent, 60));
        }
    }

    private void DeselectAllChips()
    {
        foreach (var (chip, _, _) in _resolutionChipElements)
        {
            chip.Tag = false;
            chip.Background = Theme.Brush(FieldBackground);
            chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60));
        }
    }

    // â”€â”€ Canvas background â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private FrameworkElement BuildCanvasBackgroundDropdown()
    {
        var accent = Theme.Accent;

        // Swatch (fills) + chevron, inside a single rounded button-like border.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _swatchVisual = new DrawingVisual();
        _swatchHost = new Border
        {
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            ClipToBounds = true,
            Child = new ColorPickerPopup.VisualDrawingVisualHost(_swatchVisual),
        };
        _swatchHost.SizeChanged += (_, _) => RefreshSwatch();
        Grid.SetColumn(_swatchHost, 0);
        grid.Children.Add(_swatchHost);

        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 8,0 L 4,5 Z"),
            Fill = Theme.Brush(Theme.TextSecondary),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevronCell = new Border
        {
            Width = 28,
            Background = WpfBrushes.Transparent, // Transparent so it blends with dropdown background
            BorderThickness = new Thickness(0), // No harsh border inside the unified card
            Child = chevron,
        };
        Grid.SetColumn(chevronCell, 1);
        grid.Children.Add(chevronCell);

        var dropdown = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(FieldBackground), // Field background on dropdown for cohesive look
            BorderBrush = Theme.Brush(WithAlpha(accent, 90)),
            BorderThickness = new Thickness(1),
            Cursor = WpfCursors.Hand,
            Child = grid,
        };
        ToolTipService.SetToolTip(dropdown, Services.LocalizationService.Translate("Canvas background color (solid or transparent checkerboard)"));
        // Toggle on mouse-UP: opening on mouse-down lets the popup (StaysOpen=false) read the
        // matching mouse-up as an outside click and close itself instantly. Mouse-down is still
        // swallowed so it doesn't reach the shell's DragMove handler and move the window.
        dropdown.MouseLeftButtonDown += (_, e) => e.Handled = true;
        dropdown.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleBgPopup(); };

        // Reusable color-picker flyout, hosted in a Popup anchored to the dropdown.
        _colorPicker = new ColorPickerPopup();
        if (_backgroundColor is { } savedBg)
            _colorPicker.SetColor(WpfColor.FromArgb(savedBg.A, savedBg.R, savedBg.G, savedBg.B));
        _colorPicker.ColorChanged += color =>
        {
            _backgroundColor = color is { } c
                ? System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B)
                : (System.Drawing.Color?)null;
            RefreshSwatch();
            PersistBackgroundColorChoice();
        };
        
        // Accept pressed, or a screen pick started → close the flyout instantly to prevent fade capturing.
        _colorPicker.CloseRequested += () =>
        {
            _bgPopup.PopupAnimation = PopupAnimation.None;
            _bgPopup.IsOpen = false;
        };

        _bgPopup = new Popup
        {
            PlacementTarget = dropdown,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = _colorPicker,
        };
        _bgPopup.Closed += (_, _) => _bgPopupClosedAt = DateTime.UtcNow;

        // Wrap dropdown and reset button in a parent layout Grid
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(dropdown, 0);
        mainGrid.Children.Add(dropdown);

        // Reset button to restore default transparent checkerboard
        var resetBtn = new Border
        {
            Width = 28,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(SecondaryButtonBg),
            BorderBrush = Theme.Brush(SecondaryButtonBorder),
            BorderThickness = new Thickness(1),
            Cursor = WpfCursors.Hand,
            Child = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("close", ToDrawingColor(Theme.TextSecondary, 220), 12),
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
        resetBtn.MouseEnter += (_, _) => resetBtn.Background = Theme.Brush(Theme.TabHoverBg);
        resetBtn.MouseLeave += (_, _) => resetBtn.Background = Theme.Brush(SecondaryButtonBg);
        resetBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _backgroundColor = null;
            _colorPicker.ClearColor();
            RefreshSwatch();
            PersistBackgroundColorChoice();
        };
        ToolTipService.SetToolTip(resetBtn, Services.LocalizationService.Translate("Clear"));
        Grid.SetColumn(resetBtn, 1);
        mainGrid.Children.Add(resetBtn);

        // Host in a container so the (zero-size) popup doesn't affect layout.
        var container = new Grid();
        container.Children.Add(mainGrid);
        container.Children.Add(_bgPopup);
        return container;
    }

    private void ToggleBgPopup()
    {
        if (_bgPopup.IsOpen)
        {
            _bgPopup.IsOpen = false;
            return;
        }
        // Clicking the button while the popup is open first closes it via the outside-click hook
        // (on mouse-down); don't let this mouse-up immediately reopen it.
        if ((DateTime.UtcNow - _bgPopupClosedAt).TotalMilliseconds < 250)
            return;
        
        _bgPopup.PopupAnimation = PopupAnimation.Fade; // Re-enable fade animation on opening
        _bgPopup.IsOpen = true;
    }

    private void RefreshSwatch()
    {
        var w = Math.Max(1, (int)_swatchHost.ActualWidth);
        var h = Math.Max(1, (int)_swatchHost.ActualHeight);

        using var dc = _swatchVisual.RenderOpen();
        // Clip to round the left corners to match the parent border's inner CornerRadius(5)
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w + 50, h), 5, 5));
        if (_backgroundColor is { } c)
            dc.DrawRectangle(Theme.Brush(WpfColor.FromRgb(c.R, c.G, c.B)), null, new Rect(0, 0, w, h));
        else
            ColorPickerPopup.PaintCheckerboard(dc, w, h);
        dc.Pop();
    }

    // â”€â”€ UI builders â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void PersistBackgroundColorChoice() =>
        PersistBackgroundColorChoice(_backgroundColor);

    private static void PersistBackgroundColorChoice(System.Drawing.Color? color)
    {
        int argb = color?.ToArgb() ?? 0;
        if (System.Windows.Application.Current is CyberSnap.App app)
            app.PersistEditorNewCanvasBackgroundColor(argb);
    }

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

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
