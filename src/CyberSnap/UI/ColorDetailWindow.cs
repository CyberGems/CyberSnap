using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfColor = System.Windows.Media.Color;

namespace CyberSnap.UI;

/// <summary>
/// Optional detail window shown after the standalone picker grabs a color.
/// PowerToys-style rows (HEX / RGB / HSL) with per-row copy buttons, plus
/// CyberSnap improvements: editable values, contrast info, recent swatches,
/// favorite-format auto-copy and a re-pick button. Always created and shown
/// on the WPF UI thread (never on the picker's WinForms STA thread).
/// </summary>
internal sealed class ColorDetailWindow : Window
{
    private byte _r;
    private byte _g;
    private byte _b;

    private bool _includeHash;
    private bool _autoCopy;
    private ColorDetailCopyFormat _format;
    private bool _showWindowPref;

    private Border _previewBorder = null!;
    private TextBlock _previewName = null!;
    private TextBlock _previewContrast = null!;
    private WpfTextBox _hexBox = null!;
    private WpfTextBox _rgbBox = null!;
    private WpfTextBox _hslBox = null!;
    private WrapPanel _recentPanel = null!;
    private WpfComboBox _formatCombo = null!;
    private System.Windows.Controls.Image _expanderIcon = null!;
    private WpfButton _expanderBtn = null!;

    public event Action? RepickRequested;

    public ColorDetailWindow(byte r, byte g, byte b)
    {
        _r = r;
        _g = g;
        _b = b;

        Theme.Refresh();
        try { Theme.ApplyTo(Resources); } catch { }
        LoadPrefs();

        Title = LocalizationService.Translate("Color picker");
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        Topmost = true;
        FontFamily = new WpfFontFamily(UiChrome.PreferredFamilyName);
        Foreground = Theme.Brush(Theme.TextPrimary);

        Content = BuildContent();
        PositionNearCursor();
        RefreshAll();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void LoadPrefs()
    {
        try
        {
            if (Application.Current is App app)
            {
                var s = app.SettingsService.Settings;
                _showWindowPref = s.ShowColorDetailWindow;
                _autoCopy = s.ColorDetailAutoCopy;
                _format = s.ColorDetailCopyFormat;
                _includeHash = s.ColorDetailIncludeHash;
                return;
            }
        }
        catch { }
        var fallback = SettingsService.LoadStatic();
        _showWindowPref = fallback?.ShowColorDetailWindow ?? true;
        _autoCopy = fallback?.ColorDetailAutoCopy ?? true;
        _format = fallback?.ColorDetailCopyFormat ?? ColorDetailCopyFormat.Hex;
        _includeHash = fallback?.ColorDetailIncludeHash ?? true;
    }

    private void SavePrefs()
    {
        try
        {
            if (Application.Current is App app)
                app.PersistColorDetailPrefs(_showWindowPref, _autoCopy, _format, _includeHash);
        }
        catch (Exception ex) { AppDiagnostics.LogError("color-detail.prefs", ex); }
    }

    private static string T(string text) => LocalizationService.Translate(text);

    private FrameworkElement BuildContent()
    {
        var accent = Theme.Accent;
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(Theme.IsDark
                ? WpfColor.FromArgb(255, 28, 30, 43)
                : WpfColor.FromArgb(255, 248, 249, 252)),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = Theme.IsDark ? 0.55 : 0.3,
            },
        };

        var stack = new StackPanel();
        card.Child = stack;

        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildPickRow());
        stack.Children.Add(BuildPreview());
        stack.Children.Add(BuildFormatRow("HEX", out _hexBox, "Copy HEX"));
        stack.Children.Add(BuildFormatRow("RGB", out _rgbBox, "Copy RGB"));
        stack.Children.Add(BuildFormatRow("HSL", out _hslBox, "Copy HSL"));
        stack.Children.Add(BuildFooter());

        return card;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new System.Windows.Controls.Image
        {
            Source = FluentIcons.RenderWpf("picker", ToDrawing(accentFg: true), 18),
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = T("Color picker"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        _expanderIcon = new System.Windows.Controls.Image
        {
            Source = FluentIcons.RenderWpf("chevronDown", ToDrawing(Theme.TextSecondary), 14),
            Width = 14,
            Height = 14,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(180),
        };
        _expanderBtn = new WpfButton
        {
            Width = 28,
            Height = 28,
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = _expanderIcon,
        };
        ToolTipService.SetToolTip(_expanderBtn, T("Hide options"));
        _expanderBtn.Click += (_, _) => ToggleFooterOptions();
        Grid.SetColumn(_expanderBtn, 2);
        grid.Children.Add(_expanderBtn);

        var close = new WpfButton
        {
            Width = 28,
            Height = 28,
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("close", ToDrawing(Theme.TextMuted), 12),
                Width = 12,
                Height = 12,
            },
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        grid.Children.Add(close);

        return grid;
    }

    private FrameworkElement BuildPickRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _recentPanel = new WrapPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_recentPanel, 0);
        grid.Children.Add(_recentPanel);

        // Eyedropper button like the annotation editor's flyout gotero.
        var pick = new WpfButton
        {
            Width = 32,
            Height = 32,
            Cursor = WpfCursors.Hand,
            BorderThickness = new Thickness(1),
            Background = Theme.Brush(Theme.IsDark
                ? WpfColor.FromArgb(255, 48, 50, 66)
                : WpfColor.FromArgb(255, 228, 230, 237)),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            Content = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("picker", ToDrawing(Theme.TextPrimary), 15),
                Width = 15,
                Height = 15,
            },
        };
        ToolTipService.SetToolTip(pick, T("Pick") + ": " + T("Pick another color from the screen"));
        pick.Click += (_, _) => { Close(); RepickRequested?.Invoke(); };
        pick.MouseEnter += (_, _) => pick.Background = Theme.Brush(Theme.TabHoverBg);
        pick.MouseLeave += (_, _) => pick.Background = Theme.Brush(Theme.IsDark
            ? WpfColor.FromArgb(255, 48, 50, 66)
            : WpfColor.FromArgb(255, 228, 230, 237));
        Grid.SetColumn(pick, 1);
        grid.Children.Add(pick);

        return grid;
    }

    private bool _footerExpanded = true;

    private void ToggleFooterOptions()
    {
        _footerExpanded = !_footerExpanded;
        if (_footer is not null)
            _footer.Visibility = _footerExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (_expanderIcon is not null)
            _expanderIcon.RenderTransform = new RotateTransform(_footerExpanded ? 180 : 0);
        if (_expanderBtn is not null)
            ToolTipService.SetToolTip(_expanderBtn, T(_footerExpanded ? "Hide options" : "Show options"));
    }

    private FrameworkElement? _footer;

    private FrameworkElement BuildPreview()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _previewBorder = new Border
        {
            Height = 56,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Theme.Brush(WithAlpha(Colors.Black, 60)),
            BorderThickness = new Thickness(1),
        };
        Grid.SetColumn(_previewBorder, 0);
        grid.Children.Add(_previewBorder);

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _previewName = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextPrimary),
        };
        _previewContrast = new TextBlock
        {
            FontSize = 11,
            Foreground = Theme.Brush(Theme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
        };
        textStack.Children.Add(_previewName);
        textStack.Children.Add(_previewContrast);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        return grid;
    }

    private FrameworkElement BuildFormatRow(string label, out WpfTextBox box, string copyTooltip)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var tb = new WpfTextBox
        {
            Height = 32,
            FontSize = 13,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(Theme.IsDark
                ? WpfColor.FromArgb(255, 20, 22, 33)
                : WpfColor.FromArgb(255, 238, 240, 245)),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            IsReadOnly = false,
        };
        tb.GotFocus += (_, _) => tb.SelectAll();
        box = tb;
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var copy = new WpfButton
        {
            Width = 34,
            Height = 32,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = WpfCursors.Hand,
            Background = Theme.Brush(Theme.IsDark
                ? WpfColor.FromArgb(255, 48, 50, 66)
                : WpfColor.FromArgb(255, 228, 230, 237)),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            Content = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("copy", ToDrawing(Theme.TextPrimary), 14),
                Width = 14,
                Height = 14,
            },
        };
        string captured = label;
        ToolTipService.SetToolTip(copy, T(copyTooltip));
        copy.Click += (_, _) => CopyRow(captured);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);

        return grid;
    }

    private FrameworkElement BuildFooter()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _footer = stack;

        var formatRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        formatRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        formatRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var favLabel = new TextBlock
        {
            Text = T("Favorite format"),
            FontSize = 11,
            Foreground = Theme.Brush(Theme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(favLabel, 0);
        formatRow.Children.Add(favLabel);

        _formatCombo = new WpfComboBox
        {
            Height = 34,
            MinWidth = 110,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxDropDownHeight = 240,
            Style = GetThemedComboBoxStyle(),
        };
        _formatCombo.Items.Add(BuildThemedComboBoxItem("HEX"));
        _formatCombo.Items.Add(BuildThemedComboBoxItem("RGB"));
        _formatCombo.Items.Add(BuildThemedComboBoxItem("HSL"));
        _formatCombo.SelectedIndex = _format switch
        {
            ColorDetailCopyFormat.Rgb => 1,
            ColorDetailCopyFormat.Hsl => 2,
            _ => 0,
        };
        _formatCombo.SelectionChanged += (_, _) =>
        {
            _format = _formatCombo.SelectedIndex switch
            {
                1 => ColorDetailCopyFormat.Rgb,
                2 => ColorDetailCopyFormat.Hsl,
                _ => ColorDetailCopyFormat.Hex,
            };
            SavePrefs();
        };
        Grid.SetColumn(_formatCombo, 1);
        formatRow.Children.Add(_formatCombo);
        stack.Children.Add(formatRow);

        stack.Children.Add(OptionToggleRow(T("Auto-copy on pick"), _autoCopy, v => { _autoCopy = v; SavePrefs(); }));
        stack.Children.Add(OptionToggleRow(T("Include # in HEX"), _includeHash, v => { _includeHash = v; SavePrefs(); RefreshAll(); }));
        stack.Children.Add(OptionToggleRow(T("Show this window after picking"), _showWindowPref, v => { _showWindowPref = v; SavePrefs(); }));

        var actions = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var closeBtn = StyledButton(T("Close"), isAccent: false);
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 0);
        actions.Children.Add(closeBtn);

        var copyClose = StyledButton(T("Copy & close"), isAccent: true);
        copyClose.Click += (_, _) => { CopyFavorite(); Close(); };
        Grid.SetColumn(copyClose, 2);
        actions.Children.Add(copyClose);

        stack.Children.Add(actions);
        return stack;
    }

    /// <summary>Settings-style option row: label on the left, widget-like toggle on the right.</summary>
    private static FrameworkElement OptionToggleRow(string text, bool initial, Action<bool> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var toggle = BuildToggle(initial, onChange);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
    }

    /// <summary>Widget-style toggle switch (34x18 sliding thumb, accent track when on).</summary>
    private static WpfButton BuildToggle(bool initial, Action<bool> onChange)
    {
        bool isOn = initial;
        var accent = Theme.Accent;

        var track = new Border
        {
            Width = 34,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.2),
            SnapsToDevicePixels = true,
        };
        var thumb = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.35,
            },
        };
        var host = new Grid
        {
            Width = 34,
            Height = 18,
            Background = WpfBrushes.Transparent,
            SnapsToDevicePixels = true,
        };
        host.Children.Add(track);
        host.Children.Add(thumb);

        void Refresh()
        {
            track.Background = isOn
                ? Theme.Brush(accent)
                : Theme.Brush(Theme.IsDark
                    ? WpfColor.FromArgb(255, 48, 50, 66)
                    : WpfColor.FromArgb(255, 214, 218, 229));
            track.BorderBrush = isOn
                ? Theme.Brush(accent)
                : Theme.Brush(Theme.BorderSubtle);
            thumb.Background = isOn
                ? Theme.Brush(Colors.White)
                : Theme.Brush(Theme.IsDark
                    ? WpfColor.FromRgb(150, 156, 170)
                    : WpfColor.FromRgb(120, 124, 135));
            thumb.HorizontalAlignment = isOn ? WpfHAlign.Right : WpfHAlign.Left;
            thumb.VerticalAlignment = VerticalAlignment.Center;
            thumb.Margin = isOn ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        }
        Refresh();

        var toggle = new WpfButton
        {
            Width = 34,
            Height = 18,
            Padding = new Thickness(0),
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = host,
            Template = BlankButtonTemplate(),
        };
        toggle.Click += (_, _) =>
        {
            isOn = !isOn;
            Refresh();
            onChange(isOn);
        };
        return toggle;
    }

    private static ControlTemplate BlankButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(WpfButton)) { VisualTree = border };
    }

    private static WpfComboBoxItem BuildThemedComboBoxItem(string text)
    {
        return new WpfComboBoxItem
        {
            Content = text,
            Height = 30,
            Padding = new Thickness(8, 4, 8, 4),
            Foreground = Theme.Brush(Theme.TextPrimary),
            Background = WpfBrushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = GetThemedComboBoxItemStyle(),
        };
    }

    private static Style GetThemedComboBoxStyle()
    {
        const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ComboBox'>
    <Setter Property='MinHeight' Value='34'/>
    <Setter Property='Padding' Value='8,4,8,4'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Foreground' Value='{DynamicResource ThemeTextPrimaryBrush}'/>
    <Setter Property='Background' Value='{DynamicResource ThemeInputBackgroundBrush}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource ThemeInputBorderBrush}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
    <Setter Property='MaxDropDownHeight' Value='280'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='ComboBox'>
                <Grid SnapsToDevicePixels='True'>
                    <ToggleButton x:Name='ToggleSite'
                                  HorizontalAlignment='Stretch'
                                  VerticalAlignment='Stretch'
                                  Background='Transparent'
                                  BorderThickness='0'
                                  Focusable='False'
                                  IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'
                                  ClickMode='Press'>
                        <ToggleButton.Template>
                            <ControlTemplate TargetType='ToggleButton'>
                                <ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Stretch'/>
                            </ControlTemplate>
                        </ToggleButton.Template>
                        <Border x:Name='Bd'
                                HorizontalAlignment='Stretch'
                                MinHeight='{TemplateBinding MinHeight}'
                                Background='{TemplateBinding Background}'
                                BorderBrush='{TemplateBinding BorderBrush}'
                                BorderThickness='{TemplateBinding BorderThickness}'
                                CornerRadius='6'
                                Padding='{TemplateBinding Padding}'>
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width='*'/>
                                    <ColumnDefinition Width='18'/>
                                </Grid.ColumnDefinitions>
                                <ContentPresenter x:Name='ContentSite'
                                                  Content='{TemplateBinding SelectionBoxItem}'
                                                  ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                                                  ContentStringFormat='{TemplateBinding SelectionBoxItemStringFormat}'
                                                  IsHitTestVisible='False'
                                                  Margin='0,0,6,0'
                                                  RecognizesAccessKey='True'
                                                  TextBlock.Foreground='{Binding Foreground, RelativeSource={RelativeSource AncestorType=ComboBox}}'
                                                  VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                                <Path Grid.Column='1'
                                      Width='8'
                                      Height='4'
                                      HorizontalAlignment='Center'
                                      VerticalAlignment='Center'
                                      Data='M 0 0 L 4 4 L 8 0 Z'
                                      Fill='{DynamicResource ThemeTextSecondaryBrush}'/>
                            </Grid>
                        </Border>
                    </ToggleButton>
                    <Popup x:Name='PART_Popup'
                           AllowsTransparency='True'
                           Focusable='False'
                           IsOpen='{TemplateBinding IsDropDownOpen}'
                           Placement='Bottom'
                           PopupAnimation='Fade'>
                        <Border MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
                                MaxHeight='{TemplateBinding MaxDropDownHeight}'
                                Margin='0,4,0,0'
                                Background='{DynamicResource ThemeCardBrush}'
                                BorderBrush='{DynamicResource ThemeInputBorderBrush}'
                                BorderThickness='1'
                                CornerRadius='6'
                                SnapsToDevicePixels='True'>
                            <ScrollViewer Padding='4'
                                          CanContentScroll='{TemplateBinding ScrollViewer.CanContentScroll}'
                                          HorizontalScrollBarVisibility='Disabled'
                                          VerticalScrollBarVisibility='Auto'>
                                <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>
                            </ScrollViewer>
                        </Border>
                    </Popup>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='Bd' Property='BorderBrush' Value='#35FFFFFF'/>
                    </Trigger>
                    <Trigger Property='IsKeyboardFocusWithin' Value='True'>
                        <Setter TargetName='Bd' Property='BorderBrush' Value='{DynamicResource ThemeTextPrimaryBrush}'/>
                    </Trigger>
                    <Trigger Property='IsEnabled' Value='False'>
                        <Setter TargetName='Bd' Property='Opacity' Value='0.45'/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static Style GetThemedComboBoxItemStyle()
    {
        const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ComboBoxItem'>
    <Setter Property='MinHeight' Value='30'/>
    <Setter Property='Padding' Value='9,5'/>
    <Setter Property='Foreground' Value='{DynamicResource ThemeTextPrimaryBrush}'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='ComboBoxItem'>
                <Border x:Name='Bd'
                        Background='{TemplateBinding Background}'
                        CornerRadius='4'
                        Padding='{TemplateBinding Padding}'
                        SnapsToDevicePixels='True'>
                    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                                      RecognizesAccessKey='True'
                                      VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsHighlighted' Value='True'>
                        <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ThemeTabActiveBrush}'/>
                    </Trigger>
                    <Trigger Property='IsSelected' Value='True'>
                        <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ThemeTabHoverBrush}'/>
                    </Trigger>
                    <Trigger Property='IsEnabled' Value='False'>
                        <Setter TargetName='Bd' Property='Opacity' Value='0.4'/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private WpfButton StyledButton(string text, bool isAccent)
    {
        var accent = Theme.Accent;
        var btn = new WpfButton
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = WpfHAlign.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Cursor = WpfCursors.Hand,
            BorderThickness = new Thickness(1),
        };
        var label = (TextBlock)btn.Content;
        if (isAccent)
        {
            var baseBg = WithAlpha(accent, Theme.IsDark ? (byte)40 : (byte)28);
            btn.Background = Theme.Brush(baseBg);
            btn.BorderBrush = Theme.Brush(WithAlpha(accent, 170));
            label.Foreground = Theme.Brush(WpfColor.FromRgb(accent.R, accent.G, accent.B));
            btn.MouseEnter += (_, _) => { btn.Background = Theme.Brush(accent); label.Foreground = Theme.Brush(Theme.AccentForeground); };
            btn.MouseLeave += (_, _) => { btn.Background = Theme.Brush(baseBg); label.Foreground = Theme.Brush(WpfColor.FromRgb(accent.R, accent.G, accent.B)); };
        }
        else
        {
            var bg = Theme.IsDark ? WpfColor.FromArgb(255, 48, 50, 66) : WpfColor.FromArgb(255, 228, 230, 237);
            btn.Background = Theme.Brush(bg);
            btn.BorderBrush = Theme.Brush(Theme.BorderSubtle);
            label.Foreground = Theme.Brush(Theme.TextPrimary);
            btn.MouseEnter += (_, _) => btn.Background = Theme.Brush(Theme.TabHoverBg);
            btn.MouseLeave += (_, _) => btn.Background = Theme.Brush(bg);
        }
        return btn;
    }

    private void RefreshAll()
    {
        string hex = ColorFormatHelper.ToHex(_r, _g, _b, _includeHash);
        string rgb = ColorFormatHelper.ToRgb(_r, _g, _b);
        string hsl = ColorFormatHelper.ToHsl(_r, _g, _b);
        _hexBox.Text = hex;
        _rgbBox.Text = rgb;
        _hslBox.Text = hsl;

        var media = WpfColor.FromRgb(_r, _g, _b);
        _previewBorder.Background = new SolidColorBrush(media);

        string name = ColorFormatHelper.ApproximateName(_r, _g, _b);
        double onWhite = ColorFormatHelper.ContrastRatio(_r, _g, _b, againstWhite: true);
        double onBlack = ColorFormatHelper.ContrastRatio(_r, _g, _b, againstWhite: false);
        _previewName.Text = $"{T(name)}  ·  {hex}";
        _previewContrast.Text = $"◐ {onWhite:0.0}:1 / ◑ {onBlack:0.0}:1  ·  {ColorFormatHelper.ContrastGrade(Math.Max(onWhite, onBlack))}";

        PopulateRecents();
    }

    private void PopulateRecents()
    {
        _recentPanel.Children.Clear();
        List<string> recents = new();
        try
        {
            if (Application.Current is App app)
                recents = app.SettingsService.Settings.RecentColors.Take(8).ToList();
            else
                recents = SettingsService.LoadStatic()?.RecentColors.Take(8).ToList() ?? new();
        }
        catch { }
        foreach (string hex in recents)
        {
            try
            {
                string clean = hex.TrimStart('#');
                if (clean.Length != 6) continue;
                byte r = Convert.ToByte(clean.Substring(0, 2), 16);
                byte g = Convert.ToByte(clean.Substring(2, 2), 16);
                byte b = Convert.ToByte(clean.Substring(4, 2), 16);
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(WpfColor.FromRgb(r, g, b)),
                    BorderBrush = Theme.Brush(WithAlpha(Colors.Black, 50)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 5, 0),
                    Cursor = WpfCursors.Hand,
                };
                swatch.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    _r = r;
                    _g = g;
                    _b = b;
                    RefreshAll();
                };
                ToolTipService.SetToolTip(swatch, "#" + clean.ToLowerInvariant());
                _recentPanel.Children.Add(swatch);
            }
            catch { }
        }
    }

    private string FavoriteText() => _format switch
    {
        ColorDetailCopyFormat.Rgb => _rgbBox.Text,
        ColorDetailCopyFormat.Hsl => _hslBox.Text,
        _ => _hexBox.Text,
    };

    private void CopyRow(string label)
    {
        string text = label switch
        {
            "RGB" => _rgbBox.Text,
            "HSL" => _hslBox.Text,
            _ => _hexBox.Text,
        };
        CopyText(text);
    }

    private void CopyFavorite() => CopyText(FavoriteText());

    private static void CopyText(string text)
    {
        try
        {
            ClipboardService.CopyTextToClipboard(text);
            try { SoundService.PlayColorSound(); } catch { }
        }
        catch (Exception ex) { AppDiagnostics.LogError("color-detail.copy", ex); }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CopyFavorite();
            Close();
        }
        else if (e.Key == Key.D1) CopyRow("HEX");
        else if (e.Key == Key.D2) CopyRow("RGB");
        else if (e.Key == Key.D3) CopyRow("HSL");
    }

    private void PositionNearCursor()
    {
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var source = PresentationSource.FromVisual(this);
            double dpiX = 1, dpiY = 1;
            if (source?.CompositionTarget is not null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            double w = (Width + 16) * dpiX;
            double h = 480 * dpiY;
            var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
            int x = cursor.X + 24;
            int y = cursor.Y - (int)(h / 2);
            if (x + w > work.Right - 8) x = cursor.X - (int)w - 24;
            if (y < work.Top + 8) y = work.Top + 8;
            if (y + h > work.Bottom - 8) y = (int)(work.Bottom - h - 8);
            Left = x / dpiX;
            Top = Math.Max(work.Top, y) / dpiY;
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private static System.Drawing.Color ToDrawing(WpfColor c) =>
        System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);

    private static System.Drawing.Color ToDrawing(bool accentFg = false) =>
        ToDrawing(accentFg ? Theme.Accent : Theme.TextPrimary);

    private static WpfColor WithAlpha(WpfColor color, byte alpha) =>
        WpfColor.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// Shows the detail window for a picked color on the WPF UI thread.
    /// Safe to call from any thread; falls back to the classic toast.
    /// </summary>
    public static void ShowForColor(byte r, byte g, byte b, Action? repick)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var win = new ColorDetailWindow(r, g, b);
                    if (repick is not null)
                        win.RepickRequested += repick;
                    win.Show();
                    win.Activate();
                }
                catch (Exception ex) { AppDiagnostics.LogError("color-detail.show", ex); }
            });
        }
        catch (Exception ex) { AppDiagnostics.LogError("color-detail.dispatch", ex); }
    }
}
