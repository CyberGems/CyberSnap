using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
/// A reusable, self-contained advanced color picker panel featuring:
/// - HSV Color Wheel & Brightness Slider
/// - Split color preview (Reference vs New)
/// - HEX input and validation
/// - Numerical inputs for RGB & HSV
/// - Recently used colors (loaded/saved from AppSettings)
/// - Standard annotation palette colors
/// - Pick from screen gotero tool
/// </summary>
internal sealed class ColorPickerPopup : ContentControl
{
    public const double FlyoutWidth = 268;

    private double _hue;
    private double _saturation = 1.0;
    private double _value = 1.0;
    private WpfColor _currentColor = WpfColor.FromRgb(128, 128, 128);
    private bool _hasColor;
    private WpfColor _referenceColor;
    private bool _hasReference;

    private HsvColorWheel _colorWheel = null!;
    private HsvValueSlider _valueSlider = null!;
    private SplitColorSwatch _previewSwatch = null!;
    private WpfTextBox _boxHex = null!;
    private WpfTextBox _boxR = null!;
    private WpfTextBox _boxG = null!;
    private WpfTextBox _boxB = null!;
    private WpfTextBox _boxH = null!;
    private WpfTextBox _boxS = null!;
    private WpfTextBox _boxV = null!;
    private WrapPanel _recentColorsPanel = null!;
    private bool _suppress;

    public event Action<WpfColor?>? ColorChanged;
    public event Action? CloseRequested;

    public WpfColor? SelectedColor => _hasColor ? _currentColor : null;

    private static readonly WpfColor[] PaletteColors =
    {
        WpfColor.FromRgb(0, 255, 255),
        WpfColor.FromRgb(0, 136, 255),
        WpfColor.FromRgb(168, 85, 247),
        WpfColor.FromRgb(255, 45, 85),
        WpfColor.FromRgb(245, 158, 11),
        WpfColor.FromRgb(234, 179, 8),
        WpfColor.FromRgb(34, 197, 94),
        WpfColor.FromRgb(255, 255, 255),
        WpfColor.FromRgb(15, 23, 42),
        WpfColor.FromRgb(236, 72, 153),
        WpfColor.FromRgb(148, 163, 184),
        WpfColor.FromRgb(0, 0, 0),
    };

    public ColorPickerPopup()
    {
        Theme.Refresh();
        BuildUI();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasColor)
        {
            _referenceColor = _currentColor;
            _hasReference = true;
        }
        else
        {
            _hasReference = false;
        }
        RefreshPreview();
        PopulateRecentColors();
    }

    public void SetColor(WpfColor color)
    {
        _hasColor = true;
        _currentColor = color;
        ColorToHsv(color, out _hue, out _saturation, out _value);
        
        _suppress = true;
        _colorWheel.Hue = _hue;
        _colorWheel.Saturation = _saturation;
        _valueSlider.Hue = _hue;
        _valueSlider.Saturation = _saturation;
        _valueSlider.Value = _value;
        UpdateTextBoxes();
        _suppress = false;

        RefreshPreview();
        ColorChanged?.Invoke(_currentColor);
    }

    public void ClearColor()
    {
        _hasColor = false;
        _currentColor = WpfColor.FromRgb(128, 128, 128);
        _hue = 0;
        _saturation = 0;
        _value = 0.5;

        _suppress = true;
        _colorWheel.Hue = _hue;
        _colorWheel.Saturation = _saturation;
        _valueSlider.Hue = _hue;
        _valueSlider.Saturation = _saturation;
        _valueSlider.Value = _value;
        UpdateTextBoxes();
        _suppress = false;

        RefreshPreview();
        ColorChanged?.Invoke(null);
    }

    private void BuildUI()
    {
        var accent = Theme.Accent;
        var accentBrush = Theme.Brush(WithAlpha(accent, 90));

        var stack = new StackPanel();

        // 1. HSV Wheel & Brightness Slider Row
        var wheelRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        wheelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wheelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // Brightness slider

        _colorWheel = new HsvColorWheel { Height = 124, Width = 124, HorizontalAlignment = WpfHorizontalAlignment.Center };
        _colorWheel.HsvChanged += (h, s) =>
        {
            if (_suppress) return;
            _hue = h;
            _saturation = s;
            _hasColor = true;
            _currentColor = ColorFromHsv(_hue, _saturation, _value);
            
            _suppress = true;
            _valueSlider.Hue = _hue;
            _valueSlider.Saturation = _saturation;
            UpdateTextBoxes();
            _suppress = false;

            RefreshPreview();
            ColorChanged?.Invoke(_currentColor);
        };
        Grid.SetColumn(_colorWheel, 0);
        wheelRow.Children.Add(_colorWheel);

        _valueSlider = new HsvValueSlider { Height = 124, Width = 14, Margin = new Thickness(6, 0, 0, 0) };
        _valueSlider.ValueChanged += v =>
        {
            if (_suppress) return;
            _value = v;
            _hasColor = true;
            _currentColor = ColorFromHsv(_hue, _saturation, _value);
            
            _suppress = true;
            UpdateTextBoxes();
            _suppress = false;

            RefreshPreview();
            ColorChanged?.Invoke(_currentColor);
        };
        ToolTipService.SetToolTip(_valueSlider, LocalizationService.Translate("Adjust brightness"));
        Grid.SetColumn(_valueSlider, 1);
        wheelRow.Children.Add(_valueSlider);

        stack.Children.Add(wheelRow);

        // 2. Swatch, Gotero, & HEX Row (Horizontal layout)
        var hexRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); // Split preview
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // spacer
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Gotero & Hex panel

        _previewSwatch = new SplitColorSwatch
        {
            Height = 28,
            HorizontalAlignment = WpfHorizontalAlignment.Stretch
        };
        var previewBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _previewSwatch
        };
        ToolTipService.SetToolTip(previewBorder, LocalizationService.Translate("Color comparison (Left: original, Right: new color)"));
        Grid.SetColumn(previewBorder, 0);
        hexRow.Children.Add(previewBorder);

        var goteroHexPanel = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var goteroBtn = new Button
        {
            Height = 28,
            Width = 28,
            Cursor = WpfCursors.Hand,
            BorderThickness = new Thickness(1),
            Template = BuildButtonTemplate(),
            Background = Theme.Brush(SecondaryButtonBg),
            BorderBrush = Theme.Brush(SecondaryButtonBorder),
            Content = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("picker", System.Drawing.Color.FromArgb(Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B), 14),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            }
        };
        goteroBtn.Click += (_, _) => { CloseRequested?.Invoke(); PickFromScreen(); };
        goteroBtn.MouseEnter += (_, _) => goteroBtn.Background = Theme.Brush(Theme.TabHoverBg);
        goteroBtn.MouseLeave += (_, _) => goteroBtn.Background = Theme.Brush(SecondaryButtonBg);
        ToolTipService.SetToolTip(goteroBtn, LocalizationService.Translate("Pick color from screen (Press Esc to cancel)"));
        goteroHexPanel.Children.Add(goteroBtn);

        var hexContainer = new Grid { Margin = new Thickness(8, 0, 0, 0) };
        hexContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hexContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) }); // Hex input

        var hashLabel = new TextBlock
        {
            Text = "#",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(Theme.TextSecondary),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 4, 0)
        };
        
        _boxHex = new WpfTextBox
        {
            FontSize = 12,
            Height = 28,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(FieldBg),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = Theme.Brush(accent)
        };
        ToolTipService.SetToolTip(_boxHex, LocalizationService.Translate("Hexadecimal color code (RRGGBB)"));
        _boxHex.TextChanged += (s, e) =>
        {
            if (_suppress) return;
            string txt = _boxHex.Text.Trim();
            if (txt.StartsWith("#")) txt = txt.Substring(1);
            if (txt.Length == 6)
            {
                try
                {
                    byte r = byte.Parse(txt.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(txt.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(txt.Substring(4, 2), NumberStyles.HexNumber);
                    
                    _suppress = true;
                    _hasColor = true;
                    _currentColor = WpfColor.FromRgb(r, g, b);
                    ColorToHsv(_currentColor, out _hue, out _saturation, out _value);
                    _colorWheel.Hue = _hue;
                    _colorWheel.Saturation = _saturation;
                    _valueSlider.Hue = _hue;
                    _valueSlider.Saturation = _saturation;
                    _valueSlider.Value = _value;
                    UpdateTextBoxes();
                    _suppress = false;

                    RefreshPreview();
                    ColorChanged?.Invoke(_currentColor);
                }
                catch { }
            }
        };
        
        Grid.SetColumn(hashLabel, 0);
        Grid.SetColumn(_boxHex, 1);
        hexContainer.Children.Add(hashLabel);
        hexContainer.Children.Add(_boxHex);
        goteroHexPanel.Children.Add(hexContainer);

        Grid.SetColumn(goteroHexPanel, 2);
        hexRow.Children.Add(goteroHexPanel);

        stack.Children.Add(hexRow);

        // 3. Compact Numerical Grid (RGB & HSV)
        var numGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        numGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        numGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        numGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // RGB Controls
        _boxR = BuildMiniField("R", out var lblR);
        _boxG = BuildMiniField("G", out var lblG);
        _boxB = BuildMiniField("B", out var lblB);

        Grid.SetColumn(lblR, 0); Grid.SetColumn(_boxR, 1); Grid.SetRow(lblR, 0); Grid.SetRow(_boxR, 0);
        Grid.SetColumn(lblG, 3); Grid.SetColumn(_boxG, 4); Grid.SetRow(lblG, 0); Grid.SetRow(_boxG, 0);
        Grid.SetColumn(lblB, 6); Grid.SetColumn(_boxB, 7); Grid.SetRow(lblB, 0); Grid.SetRow(_boxB, 0);

        numGrid.Children.Add(lblR); numGrid.Children.Add(_boxR);
        numGrid.Children.Add(lblG); numGrid.Children.Add(_boxG);
        numGrid.Children.Add(lblB); numGrid.Children.Add(_boxB);

        // HSV Controls
        _boxH = BuildMiniField("H", out var lblH);
        _boxS = BuildMiniField("S", out var lblS);
        _boxV = BuildMiniField("V", out var lblV);

        Grid.SetColumn(lblH, 0); Grid.SetColumn(_boxH, 1); Grid.SetRow(lblH, 2); Grid.SetRow(_boxH, 2);
        Grid.SetColumn(lblS, 3); Grid.SetColumn(_boxS, 4); Grid.SetRow(lblS, 2); Grid.SetRow(_boxS, 2);
        Grid.SetColumn(lblV, 6); Grid.SetColumn(_boxV, 7); Grid.SetRow(lblV, 2); Grid.SetRow(_boxV, 2);

        numGrid.Children.Add(lblH); numGrid.Children.Add(_boxH);
        numGrid.Children.Add(lblS); numGrid.Children.Add(_boxS);
        numGrid.Children.Add(lblV); numGrid.Children.Add(_boxV);

        // Add Handlers to boxes
        _boxR.TextChanged += OnRgbBoxChanged;
        _boxG.TextChanged += OnRgbBoxChanged;
        _boxB.TextChanged += OnRgbBoxChanged;

        _boxH.TextChanged += OnHsvBoxChanged;
        _boxS.TextChanged += OnHsvBoxChanged;
        _boxV.TextChanged += OnHsvBoxChanged;

        stack.Children.Add(numGrid);

        // 4. Recently Used Colors Bar
        var recentHeader = new TextBlock
        {
            Text = LocalizationService.Translate("Recently Used"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(recentHeader);

        _recentColorsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        stack.Children.Add(_recentColorsPanel);

        // 5. Standard Palette Bar
        var standardHeader = new TextBlock
        {
            Text = LocalizationService.Translate("Standard Palette"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(standardHeader);

        var standardPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var col in PaletteColors)
        {
            standardPanel.Children.Add(BuildPaletteSwatch(col));
        }
        stack.Children.Add(standardPanel);

        // 6. Action Row
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

        var acceptButton = BuildButton(LocalizationService.Translate("Accept"), isAccent: true, () =>
        {
            if (_hasColor)
            {
                if (Application.Current is CyberSnap.App app)
                {
                    app.PersistRecentColor(ToHex(_currentColor));
                }
            }
            CloseRequested?.Invoke();
        });
        acceptButton.HorizontalAlignment = WpfHorizontalAlignment.Stretch;
        acceptButton.MinWidth = 0;
        acceptButton.Margin = new Thickness(0);
        Grid.SetColumn(acceptButton, 2);
        actionRow.Children.Add(acceptButton);

        stack.Children.Add(actionRow);

        var card = new Border
        {
            Width = FlyoutWidth,
            Margin = new Thickness(6), // Room for drop shadow, prevents bottom buttons from cutting off
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(PanelBg),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
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
        UpdateTextBoxes();
        RefreshPreview();
    }

    private WpfTextBox BuildMiniField(string label, out TextBlock lbl)
    {
        lbl = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0)
        };

        var box = new WpfTextBox
        {
            FontSize = 10.5,
            Height = 22,
            Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = WpfHorizontalAlignment.Center,
            Background = Theme.Brush(FieldBg),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 90)),
            BorderThickness = new Thickness(1),
            CaretBrush = Theme.Brush(Theme.Accent),
            Tag = label
        };
        box.PreviewTextInput += (_, e) => e.Handled = !IsAllDigits(e.Text);

        if (label == "R" || label == "G" || label == "B")
        {
            ToolTipService.SetToolTip(box, LocalizationService.Translate("RGB color value (0-255)"));
        }
        else if (label == "H")
        {
            ToolTipService.SetToolTip(box, LocalizationService.Translate("Hue value (0-360 degrees)"));
        }
        else if (label == "S")
        {
            ToolTipService.SetToolTip(box, LocalizationService.Translate("Saturation value (0-100%)"));
        }
        else if (label == "V")
        {
            ToolTipService.SetToolTip(box, LocalizationService.Translate("Brightness value (0-100%)"));
        }

        return box;
    }

    private FrameworkElement BuildPaletteSwatch(WpfColor color)
    {
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = Theme.Brush(color),
            BorderBrush = Theme.Brush(WithAlpha(Colors.Black, 40)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = WpfCursors.Hand
        };
        swatch.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetColor(color);
        };
        return swatch;
    }

    private void PopulateRecentColors()
    {
        _recentColorsPanel.Children.Clear();
        var s = SettingsService.LoadStatic();
        if (s?.RecentColors != null)
        {
            foreach (var hex in s.RecentColors)
            {
                try
                {
                    string clean = hex.StartsWith("#") ? hex.Substring(1) : hex;
                    byte r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber);
                    var col = WpfColor.FromRgb(r, g, b);
                    _recentColorsPanel.Children.Add(BuildPaletteSwatch(col));
                }
                catch { }
            }
        }

        if (_recentColorsPanel.Children.Count == 0)
        {
            _recentColorsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("No recent colors"),
                FontSize = 9.5,
                FontStyle = FontStyles.Italic,
                Foreground = Theme.Brush(Theme.TextMuted),
                Margin = new Thickness(0, 2, 0, 6)
            });
        }
    }

    private void OnRgbBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not WpfTextBox box || box.Tag is not string channel) return;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;

        v = Math.Clamp(v, 0, 255);
        byte r = _currentColor.R;
        byte g = _currentColor.G;
        byte b = _currentColor.B;

        switch (channel)
        {
            case "R": r = (byte)v; break;
            case "G": g = (byte)v; break;
            case "B": b = (byte)v; break;
        }

        _currentColor = WpfColor.FromRgb(r, g, b);
        _hasColor = true;
        ColorToHsv(_currentColor, out _hue, out _saturation, out _value);

        _suppress = true;
        _colorWheel.Hue = _hue;
        _colorWheel.Saturation = _saturation;
        _valueSlider.Hue = _hue;
        _valueSlider.Saturation = _saturation;
        _valueSlider.Value = _value;
        UpdateTextBoxes();
        _suppress = false;

        RefreshPreview();
        ColorChanged?.Invoke(_currentColor);
    }

    private void OnHsvBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not WpfTextBox box || box.Tag is not string channel) return;
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;

        double h = _hue;
        double s = _saturation;
        double val = _value;

        switch (channel)
        {
            case "H": h = Math.Clamp(v, 0.0, 360.0); break;
            case "S": s = Math.Clamp(v / 100.0, 0.0, 1.0); break;
            case "V": val = Math.Clamp(v / 100.0, 0.0, 1.0); break;
        }

        _hue = h;
        _saturation = s;
        _value = val;
        _currentColor = ColorFromHsv(_hue, _saturation, _value);
        _hasColor = true;

        _suppress = true;
        _colorWheel.Hue = _hue;
        _colorWheel.Saturation = _saturation;
        _valueSlider.Hue = _hue;
        _valueSlider.Saturation = _saturation;
        _valueSlider.Value = _value;
        UpdateTextBoxes();
        _suppress = false;

        RefreshPreview();
        ColorChanged?.Invoke(_currentColor);
    }

    private void UpdateTextBoxes()
    {
        _boxR.Text = _currentColor.R.ToString(CultureInfo.InvariantCulture);
        _boxG.Text = _currentColor.G.ToString(CultureInfo.InvariantCulture);
        _boxB.Text = _currentColor.B.ToString(CultureInfo.InvariantCulture);

        _boxH.Text = Math.Round(_hue).ToString(CultureInfo.InvariantCulture);
        _boxS.Text = Math.Round(_saturation * 100.0).ToString(CultureInfo.InvariantCulture);
        _boxV.Text = Math.Round(_value * 100.0).ToString(CultureInfo.InvariantCulture);

        _boxHex.Text = ToHex(_currentColor).Substring(1);
    }

    private void RefreshPreview()
    {
        if (_hasColor)
        {
            _previewSwatch.NewColor = _currentColor;
        }
        else
        {
            _previewSwatch.ClearNew();
        }

        if (_hasReference)
        {
            _previewSwatch.ReferenceColor = _referenceColor;
        }
        else
        {
            _previewSwatch.ClearReference();
        }
    }

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

    public static WpfColor ColorFromHsv(double h, double s, double v)
    {
        int hi = Convert.ToInt32(Math.Floor(h / 60.0)) % 6;
        double f = (h / 60.0) - Math.Floor(h / 60.0);

        v = v * 255.0;
        byte vByte = Convert.ToByte(v);
        byte p = Convert.ToByte(v * (1.0 - s));
        byte q = Convert.ToByte(v * (1.0 - f * s));
        byte t = Convert.ToByte(v * (1.0 - (1.0 - f) * s));

        return hi switch
        {
            0 => WpfColor.FromRgb(vByte, t, p),
            1 => WpfColor.FromRgb(q, vByte, p),
            2 => WpfColor.FromRgb(p, vByte, t),
            3 => WpfColor.FromRgb(p, q, vByte),
            4 => WpfColor.FromRgb(t, p, vByte),
            _ => WpfColor.FromRgb(vByte, p, q),
        };
    }

    public static void ColorToHsv(WpfColor color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        h = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                h = 60.0 * (((g - b) / delta) % 6.0);
            }
            else if (max == g)
            {
                h = 60.0 * (((b - r) / delta) + 2.0);
            }
            else if (max == b)
            {
                h = 60.0 * (((r - g) / delta) + 4.0);
            }
            if (h < 0)
                h += 360.0;
        }

        s = max == 0.0 ? 0.0 : delta / max;
        v = max;
    }

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

    private Button BuildButton(string text, bool isAccent, Action click)
    {
        var accent = Theme.Accent;

        var label = new TextBlock
        {
            Text = text.ToUpper(CultureInfo.CurrentCulture),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Button
        {
            Content = label,
            Height = 28,
            MinWidth = isAccent ? 100 : 64,
            Margin = new Thickness(0, 0, isAccent ? 8 : 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = WpfCursors.Hand,
            BorderThickness = new Thickness(1),
            Template = BuildButtonTemplate(),
        };

        if (isAccent)
        {
            var baseBg = WithAlpha(accent, Theme.IsDark ? (byte)40 : (byte)28);
            var restText = WpfColor.FromRgb(accent.R, accent.G, accent.B);
            var hoverText = Theme.IsDark ? Colors.Black : Colors.White;
            button.Background = Theme.Brush(baseBg);
            button.BorderBrush = Theme.Brush(WithAlpha(accent, 170));
            label.Foreground = Theme.Brush(restText);
            button.MouseEnter += (_, _) => { button.Background = Theme.Brush(accent); label.Foreground = Theme.Brush(hoverText); };
            button.MouseLeave += (_, _) => { button.Background = Theme.Brush(baseBg); label.Foreground = Theme.Brush(restText); };
        }
        else
        {
            button.Background = Theme.Brush(SecondaryButtonBg);
            button.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            label.Foreground = Theme.Brush(Theme.TextPrimary);
            button.MouseEnter += (_, _) => button.Background = Theme.Brush(Theme.TabHoverBg);
            button.MouseLeave += (_, _) => button.Background = Theme.Brush(SecondaryButtonBg);
        }

        button.Click += (_, _) => click();
        return button;
    }

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

    private static string ToHex(WpfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

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

    // --- Sub-components for HSV Picker ---

    internal sealed class HsvColorWheel : FrameworkElement
    {
        private WriteableBitmap? _bitmap;
        private double _hue;
        private double _saturation = 1.0;

        public double Hue
        {
            get => _hue;
            set { if (_hue != value) { _hue = value; InvalidateVisual(); } }
        }

        public double Saturation
        {
            get => _saturation;
            set { if (_saturation != value) { _saturation = value; InvalidateVisual(); } }
        }

        public event Action<double, double>? HsvChanged;

        public HsvColorWheel()
        {
            Cursor = WpfCursors.Hand;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            EnsureBitmap((int)w, (int)h);
            if (_bitmap != null)
            {
                dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
            }

            double centerX = w / 2.0;
            double centerY = h / 2.0;
            double radius = Math.Min(centerX, centerY);
            double angle = _hue * Math.PI / 180.0;
            double dist = _saturation * radius;

            double tx = centerX + dist * Math.Cos(angle);
            double ty = centerY - dist * Math.Sin(angle);

            dc.DrawEllipse(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 2.5), new System.Windows.Point(tx, ty), 5, 5);
            dc.DrawEllipse(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.5), new System.Windows.Point(tx, ty), 5, 5);
        }

        private void EnsureBitmap(int w, int h)
        {
            if (_bitmap != null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h) return;

            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int[] pixels = new int[w * h];
            double centerX = w / 2.0;
            double centerY = h / 2.0;
            double radius = Math.Min(centerX, centerY) - 1.0; // leave a 1px buffer for anti-aliasing

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = x - centerX;
                    double dy = centerY - y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist <= radius + 1.0)
                    {
                        double angle = Math.Atan2(dy, dx);
                        if (angle < 0) angle += 2 * Math.PI;
                        double hue = angle * 180.0 / Math.PI;
                        double sat = Math.Min(1.0, dist / radius);

                        WpfColor c = ColorFromHsv(hue, sat, 1.0);
                        
                        double alphaFactor = 1.0;
                        if (dist > radius)
                        {
                            alphaFactor = 1.0 - (dist - radius);
                            if (alphaFactor < 0) alphaFactor = 0;
                            if (alphaFactor > 1) alphaFactor = 1;
                        }
                        byte alpha = (byte)(alphaFactor * 255);

                        pixels[y * w + x] = (alpha << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    }
                    else
                    {
                        pixels[y * w + x] = 0;
                    }
                }
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton == MouseButton.Left)
            {
                CaptureMouse();
                UpdateFromPoint(e.GetPosition(this));
            }
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured)
            {
                UpdateFromPoint(e.GetPosition(this));
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        private void UpdateFromPoint(System.Windows.Point pt)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double centerX = w / 2.0;
            double centerY = h / 2.0;
            double radius = Math.Min(centerX, centerY);
            if (radius <= 0) return;

            double dx = pt.X - centerX;
            double dy = centerY - pt.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            double angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += 2 * Math.PI;

            double hue = angle * 180.0 / Math.PI;
            double sat = Math.Min(1.0, dist / radius);

            _hue = hue;
            _saturation = sat;

            InvalidateVisual();
            HsvChanged?.Invoke(_hue, _saturation);
        }
    }

    internal sealed class HsvValueSlider : FrameworkElement
    {
        private double _hue;
        private double _saturation = 1.0;
        private double _value = 1.0;

        public double Hue
        {
            get => _hue;
            set { if (_hue != value) { _hue = value; InvalidateVisual(); } }
        }

        public double Saturation
        {
            get => _saturation;
            set { if (_saturation != value) { _saturation = value; InvalidateVisual(); } }
        }

        public double Value
        {
            get => _value;
            set { if (_value != value) { _value = value; InvalidateVisual(); } }
        }

        public event Action<double>? ValueChanged;

        public HsvValueSlider()
        {
            Cursor = WpfCursors.Hand;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            WpfColor pureColor = ColorFromHsv(_hue, _saturation, 1.0);
            var brush = new LinearGradientBrush(pureColor, Colors.Black, new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, w, h), 4, 4);

            double ty = (1.0 - _value) * h;
            dc.DrawRectangle(System.Windows.Media.Brushes.White, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 1), new Rect(-2, ty - 2, w + 4, 4));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton == MouseButton.Left)
            {
                CaptureMouse();
                UpdateFromPoint(e.GetPosition(this));
            }
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsMouseCaptured)
            {
                UpdateFromPoint(e.GetPosition(this));
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        private void UpdateFromPoint(System.Windows.Point pt)
        {
            double h = ActualHeight;
            if (h <= 0) return;

            double val = 1.0 - Math.Clamp(pt.Y / h, 0.0, 1.0);
            _value = val;
            InvalidateVisual();
            ValueChanged?.Invoke(_value);
        }
    }

    internal sealed class SplitColorSwatch : FrameworkElement
    {
        private WpfColor _referenceColor = Colors.Transparent;
        private WpfColor _newColor = Colors.Transparent;
        private bool _hasReferenceColor;
        private bool _hasNewColor;

        public WpfColor ReferenceColor
        {
            get => _referenceColor;
            set { _referenceColor = value; _hasReferenceColor = true; InvalidateVisual(); }
        }

        public WpfColor NewColor
        {
            get => _newColor;
            set { _newColor = value; _hasNewColor = true; InvalidateVisual(); }
        }

        public void ClearReference() { _hasReferenceColor = false; InvalidateVisual(); }
        public void ClearNew() { _hasNewColor = false; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            ColorPickerPopup.PaintCheckerboard(dc, (int)w, (int)h);

            var clipGeom = new RectangleGeometry(new Rect(0, 0, w, h), 6, 6);
            dc.PushClip(clipGeom);

            if (_hasReferenceColor)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w / 2.0, h)));
                dc.DrawRectangle(Theme.Brush(_referenceColor), null, new Rect(0, 0, w, h));
                dc.Pop();
            }

            if (_hasNewColor)
            {
                dc.PushClip(new RectangleGeometry(new Rect(w / 2.0, 0, w / 2.0, h)));
                dc.DrawRectangle(Theme.Brush(_newColor), null, new Rect(0, 0, w, h));
                dc.Pop();
            }

            dc.Pop(); // Pop clipGeom

            var borderPen = new System.Windows.Media.Pen(Theme.Brush(WithAlpha(Theme.Accent, 90)), 1);
            dc.DrawRoundedRectangle(null, borderPen, new Rect(0.5, 0.5, w - 1, h - 1), 6, 6);
        }
    }
}
