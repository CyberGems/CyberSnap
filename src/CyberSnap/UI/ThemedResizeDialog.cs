using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private const double PanelWidth = 460;

    private static readonly (string Label, int W, int H)[] ResolutionPresets =
    {
        ("640 × 480", 640, 480),
        ("800 × 600", 800, 600),
        ("1024 × 768", 1024, 768),
        ("1280 × 720", 1280, 720),
        ("1366 × 768", 1366, 768),
        ("1600 × 900", 1600, 900),
        ("1920 × 1080", 1920, 1080),
        ("2560 × 1440", 2560, 1440),
        ("3840 × 2160", 3840, 2160),
    };

    private static readonly (string Label, double Ratio)[] AspectPresets =
    {
        ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2),
        ("16:9", 16.0 / 9), ("16:10", 16.0 / 10), ("9:16", 9.0 / 16),
    };

    private int _width;
    private int _height;
    private bool _lockAspect = true;
    private bool _scaleContent = true;
    private double _aspect;
    private AnchorPosition _anchor = AnchorPosition.Center;
    private bool _confirmed;
    private bool _suppress;

    private WpfTextBox _widthBox = null!;
    private WpfTextBox _heightBox = null!;
    private Border _anchorSection = null!;
    private readonly Border[] _anchorCells = new Border[9];

    private ThemedResizeDialog(int curW, int curH)
    {
        Theme.Refresh();
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
        UiScale.ApplyToWindow(this, content, scaleWindowBounds: false);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
        };
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

        // Canvas Settings Card (logical grouping)
        var settingsCard = new Border
        {
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(Theme.IsDark ? WpfColor.FromArgb(20, 255, 255, 255) : WpfColor.FromArgb(12, 0, 0, 0)),
            BorderThickness = new Thickness(1)
        };

        var settingsGrid = new Grid();
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftStack = new StackPanel();

        // Width / Height inputs.
        var inputs = new Grid();
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _widthBox = BuildNumberField("Width", _width, v => OnWidthChanged(v));
        _heightBox = BuildNumberField("Height", _height, v => OnHeightChanged(v));
        var wCol = WrapField("Width", _widthBox);
        var hCol = WrapField("Height", _heightBox);
        Grid.SetColumn(wCol, 0);
        Grid.SetColumn(hCol, 2);
        inputs.Children.Add(wCol);
        inputs.Children.Add(hCol);
        leftStack.Children.Add(inputs);

        // Toggles.
        leftStack.Children.Add(BuildToggle("Lock aspect ratio", _lockAspect, v => _lockAspect = v, topMargin: 12));
        leftStack.Children.Add(BuildToggle("Scale content", _scaleContent, v =>
        {
            _scaleContent = v;
            _anchorSection.Visibility = v ? Visibility.Collapsed : Visibility.Visible;
        }, topMargin: 8));

        Grid.SetColumn(leftStack, 0);
        settingsGrid.Children.Add(leftStack);

        // Anchor section.
        _anchorSection = BuildAnchorSection();
        _anchorSection.Margin = new Thickness(20, 0, 0, 0);
        _anchorSection.VerticalAlignment = VerticalAlignment.Center;
        _anchorSection.Visibility = _scaleContent ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(_anchorSection, 1);
        settingsGrid.Children.Add(_anchorSection);

        settingsCard.Child = settingsGrid;
        root.Children.Add(settingsCard);

        // Presets container.
        var presetsContainer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        presetsContainer.Children.Add(SectionLabel("Resolution", 0));
        presetsContainer.Children.Add(BuildResolutionChips());
        presetsContainer.Children.Add(SectionLabel("Aspect ratio", 10));
        presetsContainer.Children.Add(BuildAspectChips());
        root.Children.Add(presetsContainer);

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

    // ── Input handling ───────────────────────────────────────────────────────

    private void OnWidthChanged(int v)
    {
        if (_suppress) return;
        _width = v;
        if (_lockAspect && _aspect > 0)
        {
            _height = Math.Max(1, (int)Math.Round(v / _aspect));
            SetBoxText(_heightBox, _height);
        }
    }

    private void OnHeightChanged(int v)
    {
        if (_suppress) return;
        _height = v;
        if (_lockAspect && _aspect > 0)
        {
            _width = Math.Max(1, (int)Math.Round(v * _aspect));
            SetBoxText(_widthBox, _width);
        }
    }

    private void ApplyResolution(int w, int h)
    {
        _suppress = true;
        _width = w; _height = h;
        _aspect = h > 0 ? (double)w / h : _aspect;
        SetBoxText(_widthBox, w);
        SetBoxText(_heightBox, h);
        _suppress = false;
    }

    private void ApplyAspect(double ratio)
    {
        _suppress = true;
        _aspect = ratio;
        // Keep the current width, recompute height from the chosen ratio.
        _height = Math.Max(1, (int)Math.Round(_width / ratio));
        SetBoxText(_heightBox, _height);
        _suppress = false;
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
            Padding = new Thickness(10, 0, 10, 0),
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

    private FrameworkElement BuildToggle(string label, bool initial, Action<bool> onChanged, double topMargin)
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

    private Border BuildAnchorSection()
    {
        var wrap = new Border();
        var outer = new StackPanel { Orientation = WpfOrientation.Vertical, HorizontalAlignment = WpfHorizontalAlignment.Center };
        outer.Children.Add(new TextBlock
        {
            Text = Services.LocalizationService.Translate("Anchor"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var grid = new UniformGrid { Rows = 3, Columns = 3, Width = 72, Height = 72 };
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
        outer.Children.Add(grid);
        wrap.Child = outer;
        return wrap;
    }

    private FrameworkElement BuildResolutionChips()
    {
        var wrap = new WrapPanel();
        foreach (var (label, w, h) in ResolutionPresets)
            wrap.Children.Add(MakeChip(label, () => ApplyResolution(w, h)));
        return wrap;
    }

    private FrameworkElement BuildAspectChips()
    {
        var wrap = new WrapPanel();
        foreach (var (label, ratio) in AspectPresets)
            wrap.Children.Add(MakeChip(label, () => ApplyAspect(ratio)));
        return wrap;
    }

    private Border MakeChip(string text, Action onClick)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(FieldBackground),
            BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = WpfCursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = Theme.Brush(Theme.TextSecondary),
            }
        };
        chip.MouseEnter += (_, _) => { chip.Background = Theme.Brush(WithAlpha(Theme.Accent, 40)); chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 150)); };
        chip.MouseLeave += (_, _) => { chip.Background = Theme.Brush(FieldBackground); chip.BorderBrush = Theme.Brush(WithAlpha(Theme.Accent, 60)); };
        chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
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
