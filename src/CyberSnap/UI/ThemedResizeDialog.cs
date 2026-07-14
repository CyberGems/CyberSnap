using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CyberSnap.Helpers;
using CyberSnap.Models.Commands;
using Button = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI;

/// <summary>Result of <see cref="ThemedResizeDialog"/>; null when the user cancels.</summary>
internal sealed class ResizeResult
{
    public int Width;
    public int Height;
    public bool ScaleContent;
    public AnchorPosition Anchor;
}

// CyberGems-styled "Resize canvas" dialog. Same neon panel/glow language as
// ThemedConfirmDialog: width/height inputs, lock-aspect and scale-content toggles,
// a 3×3 anchor grid (canvas-size mode), and resolution/ratio preset chips.
internal sealed class ThemedResizeDialog : Window
{
    private const double GlowMargin = 16;
    private const double PanelWidth = 680;

    private const string SVG_LOCKED = "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71";
    private const string SVG_UNLOCKED = "M18.84 12.2a4.49 4.49 0 0 0-6.36-6.36l-1.54 1.54 M8.9 14.9L7.36 16.4a4.49 4.49 0 0 0 6.36 6.36l1.54-1.54";

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

    private static readonly (string Label, double Ratio)[] AspectPresets =
    {
        ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2),
        ("16:9", 16.0 / 9), ("16:10", 16.0 / 10), ("9:16", 9.0 / 16),
    };

    private static readonly (string Label, double Scale)[] ScalePresets =
    {
        ("25%", 0.25), ("50%", 0.50), ("200%", 2.00)
    };

    private int _width;
    private int _height;
    private bool _lockAspect = true;
    private bool _scaleContent = true;
    private double _aspect;
    private AnchorPosition _anchor = AnchorPosition.Center;
    private bool _confirmed;
    private bool _suppress;

    private readonly int _origWidth;
    private readonly int _origHeight;

    private WpfTextBox _widthBox = null!;
    private WpfTextBox _heightBox = null!;
    private Border _anchorSection = null!;
    private readonly Border[] _anchorCells = new Border[9];

    // HUD and toggle sync controls
    private Border _aspectToggleBorder = null!;
    private Border _aspectToggleKnob = null!;
    private Border _aspectLockBorder = null!;
    private System.Windows.Shapes.Path _aspectLockIconPath = null!;
    private Slider _scaleSlider = null!;
    private Slider _scaleSliderW = null!;
    private Slider _scaleSliderH = null!;
    private TextBlock _sliderWPctText = null!;
    private TextBlock _sliderHPctText = null!;
    private FrameworkElement _sliderLockedContainer = null!;
    private FrameworkElement _sliderUnlockedContainer = null!;
    private TextBlock _alterationValText = null!;
    private TextBlock _resultValText = null!;
    private Border? _lastClickedPresetBorder;

    private readonly System.Collections.Generic.List<(Border Border, int W, int H)> _resolutionChipElements = new();
    private readonly System.Collections.Generic.List<(Border Border, double Ratio)> _aspectChipElements = new();
    private readonly System.Collections.Generic.List<(Border Border, double Scale)> _scaleChipElements = new();

    private ThemedResizeDialog(int curW, int curH)
    {
        Theme.Refresh();
        _origWidth = curW;
        _origHeight = curH;
        _width = curW;
        _height = curH;
        _aspect = curH > 0 ? (double)curW / curH : 1.0;

        Title = Services.LocalizationService.Translate("Resize canvas");
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

        // Keep the window auto-sized to its content height. Toggling "Scale content" shows or
        // hides the anchor grid, which changes the required height; SizeToContent re-fits
        // automatically, and the SizeChanged clamp keeps the window inside the work area.
        SizeToContent = SizeToContent.Height;
        SizeChanged += (_, _) => ClampToWorkingArea();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
        };
    }

    private void RecalculateWindowHeight()
    {
        // SizeToContent.Height already re-fits the window; just make sure the new size
        // still sits fully inside the work area once layout settles.
        Dispatcher.BeginInvoke(new Action(ClampToWorkingArea), System.Windows.Threading.DispatcherPriority.Loaded);
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

    public static ResizeResult? Show(IntPtr ownerHandle, int curW, int curH)
    {
        var dialog = new ThemedResizeDialog(curW, curH);
        if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(dialog).Owner = ownerHandle;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        if (dialog.ShowDialog() == true && dialog._confirmed)
        {
            return new ResizeResult
            {
                Width = dialog._width,
                Height = dialog._height,
                ScaleContent = dialog._scaleContent,
                Anchor = dialog._anchor,
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
            Text = Services.LocalizationService.Translate("Resize canvas").ToUpper(CultureInfo.CurrentCulture),
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

        // Body Grid: Two columns (Left Stack: Presets & Toggles, Right Stack: Digital Readout & Inputs & Slider)
        var bodyGrid = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) }); // Left column
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });                       // Spacer
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) }); // Right column

        // LEFT COLUMN
        var leftStack = new StackPanel();

        // Quick Presets groups
        leftStack.Children.Add(SectionLabel("Resolution", 0));
        var resWrap = new UniformGrid { Columns = 4 };
        _resolutionChipElements.Clear();
        foreach (var (label, w, h) in ResolutionPresets)
        {
            var chip = MakeChip(label, border => {
                _lastClickedPresetBorder = border;
                ApplyResolution(w, h);
            });
            _resolutionChipElements.Add((chip, w, h));
            resWrap.Children.Add(chip);
        }
        leftStack.Children.Add(resWrap);

        leftStack.Children.Add(SectionLabel("Aspect ratio", 10));
        var aspWrap = new UniformGrid { Columns = 3 };
        _aspectChipElements.Clear();
        foreach (var (label, ratio) in AspectPresets)
        {
            var chip = MakeChip(label, border => {
                _lastClickedPresetBorder = border;
                ApplyAspect(ratio);
            });
            _aspectChipElements.Add((chip, ratio));
            aspWrap.Children.Add(chip);
        }
        leftStack.Children.Add(aspWrap);

        leftStack.Children.Add(SectionLabel("Scale", 10));
        var scaleWrap = new UniformGrid { Columns = 3 };
        _scaleChipElements.Clear();
        foreach (var (label, scale) in ScalePresets)
        {
            var chip = MakeChip(label, border => {
                _lastClickedPresetBorder = border;
                ApplyScalePreset(scale);
            });
            _scaleChipElements.Add((chip, scale));
            scaleWrap.Children.Add(chip);
        }
        leftStack.Children.Add(scaleWrap);

        // Toggles
        leftStack.Children.Add(BuildToggleWithDescription(
            "Lock aspect ratio",
            "Keep width and height in proportion",
            _lockAspect,
            ToggleAspect,
            topMargin: 16,
            isAspectLock: true
        ));

        leftStack.Children.Add(BuildToggleWithDescription(
            "Scale content",
            "Resize existing annotations along with the canvas",
            _scaleContent,
            v =>
            {
                _scaleContent = v;
                _anchorSection.Visibility = v ? Visibility.Collapsed : Visibility.Visible;
                RecalculateWindowHeight();
            },
            topMargin: 12
        ));

        Grid.SetColumn(leftStack, 0);
        bodyGrid.Children.Add(leftStack);

        // RIGHT COLUMN (Digital HUD Panel)
        var hudPanel = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(Theme.IsDark ? WpfColor.FromArgb(20, 255, 255, 255) : WpfColor.FromArgb(12, 0, 0, 0)),
            BorderThickness = new Thickness(1)
        };

        var hudStack = new StackPanel();

        // 1. Original Size
        hudStack.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate("Original Size").ToUpper(CultureInfo.CurrentCulture),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(0, 0, 0, 4)
        });

        hudStack.Children.Add(new TextBlock
        {
            Text = $"{_origWidth} × {_origHeight} PX",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            FontFamily = new WpfFontFamily("Consolas"),
            Foreground = Theme.Brush(Theme.TextSecondary),
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // Horizontal Line
        hudStack.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Brush(SecondaryButtonBorder),
            Margin = new Thickness(0, 0, 0, 12)
        });

        // 2. Target Size Title
        hudStack.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate("Target Size").ToUpper(CultureInfo.CurrentCulture),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(0, 0, 0, 8)
        });

        // Inputs & Aspect Lock link button row
        var inputsRow = new Grid();
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        inputsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _widthBox = BuildNumberField("Width", _width, OnWidthChanged);
        _heightBox = BuildNumberField("Height", _height, OnHeightChanged);

        var widthCol = WrapField("Width", _widthBox);
        var heightCol = WrapField("Height", _heightBox);

        _aspectLockIconPath = new System.Windows.Shapes.Path
        {
            Stroke = Theme.Brush(_lockAspect ? Theme.Accent : Theme.TextMuted),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse(_lockAspect ? SVG_LOCKED : SVG_UNLOCKED)
        };

        _aspectLockBorder = new Border
        {
            Width = 38,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(_lockAspect ? WithAlpha(Theme.Accent, 30) : SecondaryButtonBg),
            BorderBrush = Theme.Brush(_lockAspect ? Theme.Accent : SecondaryButtonBorder),
            BorderThickness = new Thickness(1),
            Cursor = WpfCursors.Hand,
            Child = _aspectLockIconPath,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0)
        };
        _aspectLockBorder.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ToggleAspect(!_lockAspect);
        };

        Grid.SetColumn(widthCol, 0);
        Grid.SetColumn(_aspectLockBorder, 2);
        Grid.SetColumn(heightCol, 4);

        inputsRow.Children.Add(widthCol);
        inputsRow.Children.Add(_aspectLockBorder);
        inputsRow.Children.Add(heightCol);

        hudStack.Children.Add(inputsRow);

        // Sliders
        _scaleSlider = BuildSlider(OnSliderValueChanged);
        _sliderLockedContainer = _scaleSlider;

        var sliderWGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        sliderWGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        sliderWGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderWGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

        var lblW = new TextBlock
        {
            Text = "W",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new WpfFontFamily("Consolas")
        };
        _scaleSliderW = BuildSlider(OnSliderWValueChanged);
        _sliderWPctText = new TextBlock
        {
            Text = "100%",
            FontSize = 11,
            FontFamily = new WpfFontFamily("Consolas"),
            Foreground = Theme.Brush(Theme.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };

        Grid.SetColumn(lblW, 0);
        Grid.SetColumn(_scaleSliderW, 1);
        Grid.SetColumn(_sliderWPctText, 2);
        sliderWGrid.Children.Add(lblW);
        sliderWGrid.Children.Add(_scaleSliderW);
        sliderWGrid.Children.Add(_sliderWPctText);

        var sliderHGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        sliderHGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        sliderHGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliderHGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

        var lblH = new TextBlock
        {
            Text = "H",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new WpfFontFamily("Consolas")
        };
        _scaleSliderH = BuildSlider(OnSliderHValueChanged);
        _sliderHPctText = new TextBlock
        {
            Text = "100%",
            FontSize = 11,
            FontFamily = new WpfFontFamily("Consolas"),
            Foreground = Theme.Brush(Theme.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };

        Grid.SetColumn(lblH, 0);
        Grid.SetColumn(_scaleSliderH, 1);
        Grid.SetColumn(_sliderHPctText, 2);
        sliderHGrid.Children.Add(lblH);
        sliderHGrid.Children.Add(_scaleSliderH);
        sliderHGrid.Children.Add(_sliderHPctText);

        var dualSlidersPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        dualSlidersPanel.Children.Add(sliderWGrid);
        dualSlidersPanel.Children.Add(sliderHGrid);
        _sliderUnlockedContainer = dualSlidersPanel;

        hudStack.Children.Add(_sliderLockedContainer);
        hudStack.Children.Add(_sliderUnlockedContainer);

        _sliderLockedContainer.Visibility = _lockAspect ? Visibility.Visible : Visibility.Collapsed;
        _sliderUnlockedContainer.Visibility = _lockAspect ? Visibility.Collapsed : Visibility.Visible;

        // Horizontal Line
        hudStack.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Brush(SecondaryButtonBorder),
            Margin = new Thickness(0, 12, 0, 12)
        });

        // 3. Status Stats Badge (Alteration & Result)
        var badgePanel = new StackPanel();

        var alterationRow = new Grid();
        alterationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        alterationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var altLbl = new TextBlock
        {
            Text = Services.LocalizationService.Translate("Alteration:"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted)
        };
        _alterationValText = new TextBlock
        {
            Text = "100% (+0%)",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextSecondary),
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };
        Grid.SetColumn(altLbl, 0);
        Grid.SetColumn(_alterationValText, 1);
        alterationRow.Children.Add(altLbl);
        alterationRow.Children.Add(_alterationValText);
        badgePanel.Children.Add(alterationRow);

        var resultRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var resLbl = new TextBlock
        {
            Text = Services.LocalizationService.Translate("Result:"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted)
        };
        _resultValText = new TextBlock
        {
            Text = $"{_width} × {_height} PX",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.Accent),
            FontFamily = new WpfFontFamily("Consolas"),
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };
        Grid.SetColumn(resLbl, 0);
        Grid.SetColumn(_resultValText, 1);
        resultRow.Children.Add(resLbl);
        resultRow.Children.Add(_resultValText);
        badgePanel.Children.Add(resultRow);

        hudStack.Children.Add(badgePanel);

        // Anchor grid lives in the HUD's spare space below the stats. Only shown when
        // content is NOT scaled, so the user can pick where existing annotations sit in
        // the new canvas. Placing it here (instead of the taller left column) keeps the
        // dialog from growing off-screen when "Scale content" is toggled off.
        _anchorSection = BuildAnchorSection();
        _anchorSection.Visibility = _scaleContent ? Visibility.Collapsed : Visibility.Visible;
        hudStack.Children.Add(_anchorSection);

        hudPanel.Child = hudStack;
        Grid.SetColumn(hudPanel, 2);
        bodyGrid.Children.Add(hudPanel);

        root.Children.Add(bodyGrid);

        // Buttons.
        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        buttons.Children.Add(BuildButton("Cancel", isPrimary: false, () => Close()));
        buttons.Children.Add(BuildButton("Apply", isPrimary: true, Commit));
        root.Children.Add(buttons);

        shell.Child = root;
        shell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is not WpfTextBox && e.OriginalSource is not Slider && e.OriginalSource is not Thumb)
            {
                try { DragMove(); } catch { /* released mid-drag */ }
            }
        };

        // Initialize state readouts
        SyncSlidersFromInputs();
        UpdateStats();

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

    // ── Input & Sync handling ───────────────────────────────────────────────────

    private void OnWidthChanged(int v)
    {
        if (_suppress) return;
        _lastClickedPresetBorder = null;
        _width = v;
        if (_lockAspect && _aspect > 0)
        {
            _height = Math.Max(1, (int)Math.Round(v / _aspect));
            SetBoxText(_heightBox, _height);
        }
        SyncSlidersFromInputs();
        UpdateStats();
    }

    private void OnHeightChanged(int v)
    {
        if (_suppress) return;
        _lastClickedPresetBorder = null;
        _height = v;
        if (_lockAspect && _aspect > 0)
        {
            _width = Math.Max(1, (int)Math.Round(v * _aspect));
            SetBoxText(_widthBox, _width);
        }
        SyncSlidersFromInputs();
        UpdateStats();
    }

    private void ApplyResolution(int w, int h)
    {
        _suppress = true;
        _width = w; _height = h;
        _aspect = h > 0 ? (double)w / h : _aspect;
        SetBoxText(_widthBox, w);
        SetBoxText(_heightBox, h);
        _suppress = false;
        SyncSlidersFromInputs();
        UpdateStats();
    }

    private void ApplyAspect(double ratio)
    {
        _suppress = true;
        _aspect = ratio;
        // Keep the current width, recompute height from the chosen ratio.
        _height = Math.Max(1, (int)Math.Round(_width / ratio));
        SetBoxText(_heightBox, _height);
        _suppress = false;
        SyncSlidersFromInputs();
        UpdateStats();
    }

    private void ApplyScalePreset(double scale)
    {
        _suppress = true;
        _width = Math.Max(1, (int)Math.Round(_origWidth * scale));
        _height = Math.Max(1, (int)Math.Round(_origHeight * scale));
        _aspect = _height > 0 ? (double)_width / _height : 1.0;
        SetBoxText(_widthBox, _width);
        SetBoxText(_heightBox, _height);
        _suppress = false;
        SyncSlidersFromInputs();
        UpdateStats();
    }

    private void ToggleAspect(bool locked)
    {
        if (_suppress) return;
        _lockAspect = locked;
        
        if (locked && _height > 0)
        {
            _aspect = (double)_width / _height;
        }

        // Sync visual controls
        if (_aspectToggleBorder != null && _aspectToggleKnob != null)
        {
            _aspectToggleBorder.Background = Theme.Brush(locked ? Theme.Accent : TrackOff);
            _aspectToggleKnob.HorizontalAlignment = locked ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left;
        }

        if (_aspectLockBorder != null)
        {
            _aspectLockBorder.Background = Theme.Brush(locked ? WithAlpha(Theme.Accent, 30) : SecondaryButtonBg);
            _aspectLockBorder.BorderBrush = Theme.Brush(locked ? Theme.Accent : SecondaryButtonBorder);
            if (_aspectLockIconPath != null)
            {
                _aspectLockIconPath.Stroke = Theme.Brush(locked ? Theme.Accent : Theme.TextMuted);
                _aspectLockIconPath.Data = Geometry.Parse(locked ? SVG_LOCKED : SVG_UNLOCKED);
            }
        }

        if (_sliderLockedContainer != null)
            _sliderLockedContainer.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        if (_sliderUnlockedContainer != null)
            _sliderUnlockedContainer.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        SyncSlidersFromInputs();
        UpdateStats();
        RecalculateWindowHeight();
    }

    private void OnSliderValueChanged(double val)
    {
        if (_suppress) return;
        _lastClickedPresetBorder = null;
        _suppress = true;
        try
        {
            _width = Math.Max(1, (int)Math.Round(_origWidth * (val / 100.0)));
            _height = Math.Max(1, (int)Math.Round(_origHeight * (val / 100.0)));
            SetBoxText(_widthBox, _width);
            SetBoxText(_heightBox, _height);
        }
        finally
        {
            _suppress = false;
        }
        UpdateStats();
    }

    private void OnSliderWValueChanged(double val)
    {
        if (_suppress) return;
        _lastClickedPresetBorder = null;
        _suppress = true;
        try
        {
            _width = Math.Max(1, (int)Math.Round(_origWidth * (val / 100.0)));
            SetBoxText(_widthBox, _width);
            if (_sliderWPctText != null)
                _sliderWPctText.Text = $"{(int)Math.Round(val)}%";
        }
        finally
        {
            _suppress = false;
        }
        UpdateStats();
    }

    private void OnSliderHValueChanged(double val)
    {
        if (_suppress) return;
        _lastClickedPresetBorder = null;
        _suppress = true;
        try
        {
            _height = Math.Max(1, (int)Math.Round(_origHeight * (val / 100.0)));
            SetBoxText(_heightBox, _height);
            if (_sliderHPctText != null)
                _sliderHPctText.Text = $"{(int)Math.Round(val)}%";
        }
        finally
        {
            _suppress = false;
        }
        UpdateStats();
    }

    private void SyncSlidersFromInputs()
    {
        if (_suppress) return;
        _suppress = true;
        try
        {
            double pctW = (_origWidth > 0) ? ((double)_width / _origWidth) * 100 : 100;
            double pctH = (_origHeight > 0) ? ((double)_height / _origHeight) * 100 : 100;

            if (_lockAspect)
            {
                if (_scaleSlider != null)
                    _scaleSlider.Value = Math.Clamp(pctW, 10, 400);
            }
            else
            {
                if (_scaleSliderW != null)
                    _scaleSliderW.Value = Math.Clamp(pctW, 10, 400);
                if (_scaleSliderH != null)
                    _scaleSliderH.Value = Math.Clamp(pctH, 10, 400);
                if (_sliderWPctText != null)
                    _sliderWPctText.Text = $"{(int)Math.Round(pctW)}%";
                if (_sliderHPctText != null)
                    _sliderHPctText.Text = $"{(int)Math.Round(pctH)}%";
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void UpdateStats()
    {
        if (_alterationValText == null || _resultValText == null) return;

        double scaleW = _origWidth > 0 ? (double)_width / _origWidth : 1.0;
        int scalePercent = (int)Math.Round(scaleW * 100);
        int alteration = scalePercent - 100;
        string sign = alteration > 0 ? "+" : "";
        
        _alterationValText.Text = $"{scalePercent}% ({sign}{alteration}%)";
        if (alteration == 0)
        {
            _alterationValText.Foreground = Theme.Brush(Theme.TextSecondary);
        }
        else if (alteration > 0)
        {
            _alterationValText.Foreground = new SolidColorBrush(WpfColor.FromRgb(46, 204, 113)); // Light green
        }
        else
        {
            _alterationValText.Foreground = new SolidColorBrush(WpfColor.FromRgb(241, 196, 15));  // Yellow
        }

        _resultValText.Text = $"{_width} × {_height} PX";

        HighlightActivePresets();
    }

    private void HighlightActivePresets()
    {
        double currentRatio = _height > 0 ? (double)_width / _height : 1.0;
        double scaleW = _origWidth > 0 ? (double)_width / _origWidth : 1.0;
        double scaleH = _origHeight > 0 ? (double)_height / _origHeight : 1.0;
        bool scalesMatch = Math.Abs(scaleW - scaleH) < 0.005;

        // 1. Resolution chips
        foreach (var chip in _resolutionChipElements)
        {
            bool active = (chip.Border == _lastClickedPresetBorder) || (_width == chip.W && _height == chip.H);
            SetChipActiveState(chip.Border, active);
        }

        // 2. Aspect ratio chips
        foreach (var chip in _aspectChipElements)
        {
            bool active = (chip.Border == _lastClickedPresetBorder) || (Math.Abs(currentRatio - chip.Ratio) < 0.01);
            SetChipActiveState(chip.Border, active);
        }

        // 3. Scale chips
        foreach (var chip in _scaleChipElements)
        {
            bool active = (chip.Border == _lastClickedPresetBorder) || (scalesMatch && (Math.Abs(scaleW - chip.Scale) < 0.005));
            SetChipActiveState(chip.Border, active);
        }
    }

    private void SetChipActiveState(Border chip, bool active)
    {
        chip.Tag = active;
        var textBlock = chip.Child as TextBlock;
        if (active)
        {
            chip.Background = Theme.Brush(WithAlpha(Theme.Accent, 40));
            chip.BorderBrush = Theme.Brush(Theme.Accent);
            if (textBlock != null)
                textBlock.Foreground = Theme.Brush(Theme.Accent);
        }
        else
        {
            chip.Background = Theme.Brush(FieldBackground);
            chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60));
            if (textBlock != null)
                textBlock.Foreground = Theme.Brush(Theme.TextSecondary);
        }
    }

    private void SetBoxText(WpfTextBox box, int value)
    {
        bool prev = _suppress;
        _suppress = true;
        box.Text = value.ToString(CultureInfo.InvariantCulture);
        box.CaretIndex = box.Text.Length;
        _suppress = prev;
    }

    // ── Field / toggle / chip builders ───────────────────────────────────────

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
        // Label sits above via WrapField's reuse of the box; render the label here as a tag.
        box.Tag = label;
        return box;
    }

    private FrameworkElement BuildToggle(string label, bool initial, Action<bool> onChanged, double topMargin, bool isAspectLock = false)
    {
        bool state = initial;
        var track = new Border
        {
            Width = 38,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(state ? Theme.Accent : TrackOff),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var knob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = WpfBrushes.White,
            HorizontalAlignment = state ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 3, 0),
        };
        track.Child = knob;

        if (isAspectLock)
        {
            _aspectToggleBorder = track;
            _aspectToggleKnob = knob;
        }

        var text = new TextBlock
        {
            Text = Services.LocalizationService.Translate(label),
            FontSize = 12.5,
            Foreground = Theme.Brush(Theme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, topMargin, 0, 0),
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
        };
        row.Children.Add(track);
        row.Children.Add(text);
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            track.Background = Theme.Brush(state ? Theme.Accent : TrackOff);
            knob.HorizontalAlignment = state ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left;
            onChanged(state);
        };
        return row;
    }

    private FrameworkElement BuildToggleWithDescription(string label, string desc, bool initial, Action<bool> onChanged, double topMargin, bool isAspectLock = false)
    {
        bool state = initial;
        var track = new Border
        {
            Width = 38,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(state ? Theme.Accent : TrackOff),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var knob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = WpfBrushes.White,
            HorizontalAlignment = state ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 3, 0),
        };
        track.Child = knob;

        if (isAspectLock)
        {
            _aspectToggleBorder = track;
            _aspectToggleKnob = knob;
        }

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        textStack.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate(label),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextSecondary),
        });
        textStack.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate(desc),
            FontSize = 10.5,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240
        });

        var grid = new Grid
        {
            Margin = new Thickness(0, topMargin, 0, 0),
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        Grid.SetColumn(track, 0);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(track);
        grid.Children.Add(textStack);

        grid.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            track.Background = Theme.Brush(state ? Theme.Accent : TrackOff);
            knob.HorizontalAlignment = state ? WpfHorizontalAlignment.Right : WpfHorizontalAlignment.Left;
            onChanged(state);
        };
        return grid;
    }

    private Slider BuildSlider(Action<double> onValueChanged)
    {
        var slider = new Slider
        {
            Minimum = 10,
            Maximum = 400,
            Value = 100,
            Height = 32,
            Margin = new Thickness(0, 6, 0, 6),
            Cursor = WpfCursors.Hand,
            IsSnapToTickEnabled = false,
            Background = Theme.Brush(TrackOff),
            Foreground = Theme.Brush(Theme.Accent)
        };

        slider.ValueChanged += (_, e) =>
        {
            onValueChanged(e.NewValue);
        };
        
        return slider;
    }

    private Border BuildAnchorSection()
    {
        var wrap = new Border();
        var stack = new StackPanel();

        // Divider so the anchor reads as part of the HUD readout above it.
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Brush(SecondaryButtonBorder),
            Margin = new Thickness(0, 12, 0, 12)
        });

        // Label on the left, 3×3 grid on the right — mirrors the alteration/result rows.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Services.LocalizationService.Translate("Anchor"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new UniformGrid
        {
            Rows = 3,
            Columns = 3,
            Width = 66,
            Height = 66,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (int i = 0; i < 9; i++)
        {
            int idx = i;
            var cell = new Border
            {
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = Theme.Brush(idx == (int)_anchor ? WithAlpha(Theme.Accent, 200) : TrackOff),
                BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60)),
                BorderThickness = new Thickness(1),
                Cursor = WpfCursors.Hand,
            };
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _anchor = (AnchorPosition)idx;
                for (int j = 0; j < 9; j++)
                    _anchorCells[j].Background = Theme.Brush(j == idx ? WithAlpha(Theme.Accent, 200) : TrackOff);
            };
            _anchorCells[i] = cell;
            grid.Children.Add(cell);
        }

        Grid.SetColumn(label, 0);
        Grid.SetColumn(grid, 1);
        row.Children.Add(label);
        row.Children.Add(grid);
        stack.Children.Add(row);

        wrap.Child = stack;
        return wrap;
    }

    private FrameworkElement BuildResolutionChips()
    {
        var wrap = new WrapPanel();
        foreach (var (label, w, h) in ResolutionPresets)
            wrap.Children.Add(MakeChip(label, _ => ApplyResolution(w, h)));
        return wrap;
    }

    private FrameworkElement BuildAspectChips()
    {
        var wrap = new WrapPanel();
        foreach (var (label, ratio) in AspectPresets)
            wrap.Children.Add(MakeChip(label, _ => ApplyAspect(ratio)));
        return wrap;
    }

    private Border MakeChip(string text, Action<Border> onClick)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 10.2,
            Foreground = Theme.Brush(Theme.TextSecondary),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(6, 5, 6, 5),
            Cursor = WpfCursors.Hand,
            Child = textBlock
        };
        chip.MouseEnter += (_, _) => {
            bool isActive = chip.Tag is bool b && b;
            if (!isActive)
            {
                chip.Background = Theme.Brush(WithAlpha(Theme.Accent, 40));
                chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 150));
            }
        };
        chip.MouseLeave += (_, _) => {
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
            Source = FluentIcons.RenderWpf("maximize", ToDrawingColor(accent, 235), 22),
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
            button.Foreground = Theme.Brush(Theme.TextSecondary);
            button.MouseEnter += (_, _) => { button.Background = Theme.Brush(Theme.TabHoverBg); button.BorderBrush = Theme.Brush(WithAlpha(accent, 120)); button.Foreground = Theme.Brush(Theme.TextPrimary); };
            button.MouseLeave += (_, _) => { button.Background = Theme.Brush(SecondaryButtonBg); button.BorderBrush = Theme.Brush(SecondaryButtonBorder); button.Foreground = Theme.Brush(Theme.TextSecondary); };
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
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s) if (c < '0' || c > '9') return false;
        return s.Length > 0;
    }

    private static DropShadowEffect Glow(WpfColor color, double blur, double opacity) => new()
    {
        Color = color,
        ShadowDepth = 0,
        BlurRadius = blur,
        Opacity = opacity
    };

    private static WpfColor WithAlpha(WpfColor c, byte alpha) => WpfColor.FromArgb(alpha, c.R, c.G, c.B);

    private static System.Drawing.Color ToDrawingColor(WpfColor color, byte alpha) =>
        System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static WpfColor PanelBackground =>
        Theme.IsDark ? WpfColor.FromRgb(15, 17, 26) : WpfColor.FromRgb(245, 246, 248);

    private static WpfColor FieldBackground =>
        Theme.IsDark ? WpfColor.FromRgb(22, 26, 38) : WpfColor.FromRgb(255, 255, 255);

    private static WpfColor TrackOff =>
        Theme.IsDark ? WpfColor.FromRgb(48, 54, 74) : WpfColor.FromRgb(206, 210, 220);

    private static WpfColor SecondaryButtonBg =>
        Theme.IsDark ? WpfColor.FromRgb(26, 30, 46) : WpfColor.FromRgb(249, 249, 249);

    private static WpfColor SecondaryButtonBorder =>
        Theme.IsDark ? WpfColor.FromArgb(32, 255, 255, 255) : WpfColor.FromArgb(26, 0, 0, 0);
}
