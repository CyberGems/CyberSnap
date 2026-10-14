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
using CyberSnap.UI.Controls;
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
    private WpfButton _copyCloseBtn = null!;
    private WpfButton? _copyOnlyBtn;
    private TextBlock? _primaryLabel;
    private System.Windows.Shapes.Path? _primaryCopyGlyph;
    private Border? _copiedBadge;
    private string? _copiedText;
    private Action<bool>? _setShowToggle;
    private Action<bool>? _setAutoToggle;
    private CyberSnapTitleBar _titleBar = null!;

    private string _builtLang = "";
    private WpfColor _builtAccent;
    private bool _builtDark;
    private bool _builtGray;

    public event Action? RepickRequested;

    public ColorDetailWindow(byte r, byte g, byte b, string? alreadyCopiedText = null)
    {
        _r = r;
        _g = g;
        _b = b;
        _copiedText = alreadyCopiedText;

        Theme.Refresh();
        try { Theme.ApplyTo(Resources); } catch { }
        LoadPrefs();

        Title = LocalizationService.Translate(WindowTitles.Color);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/Icons/ColorPicker.ico")); } catch { }
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        Topmost = true;
        FontFamily = new WpfFontFamily(UiChrome.PreferredFamilyName);
        Foreground = Theme.Brush(Theme.TextPrimary);

        Content = BuildContent();
        PositionNearCursor();
        RefreshAll();
        SnapshotLiveState();

        PreviewKeyDown += OnPreviewKeyDown;
        Activated += (_, _) => RefreshLiveState();
        ContentRendered += (_, _) => ClampToMonitor();
        // Window-level drag covers the header plus its surrounding margin/padding,
        // which otherwise form a dead strip above the title.
        PreviewMouseLeftButtonDown += OnWindowDragDown;
        PreviewMouseMove += OnHeaderDragMove;
        PreviewMouseLeftButtonUp += OnHeaderDragEnd;
        Loaded += (_, _) => CyberSnapWindowChrome.EnsureForeground(this, _copyCloseBtn);
    }

    private void SnapshotLiveState()
    {
        _builtLang = LocalizationService.CurrentLanguageCode;
        _builtAccent = Theme.Accent;
        _builtDark = Theme.IsDark;
        _builtGray = Theme.IsGray;
    }

    /// <summary>Rebuilds content when theme or language changed while open (like other windows).</summary>
    private void RefreshLiveState()
    {
        try
        {
            Theme.Refresh();
            if (LocalizationService.CurrentLanguageCode == _builtLang
                && Theme.Accent == _builtAccent
                && Theme.IsDark == _builtDark
                && Theme.IsGray == _builtGray)
                return;

            try { Theme.ApplyTo(Resources); } catch { }
            LoadPrefs();
            Title = LocalizationService.Translate(WindowTitles.Color);
            Content = BuildContent();
            SnapshotLiveState();
            ApplyFooterExpansionState();
            RefreshAll();
        }
        catch (Exception ex) { AppDiagnostics.LogError("color-detail.live", ex); }
    }

    private void LoadPrefs()
    {
        try
        {
            if (Application.Current is App app)
            {
                var s = app.SettingsService.Settings;
                ApplyDeliveryPrefs(s.ColorDetailAutoCopy, s.ShowColorDetailWindow);
                _format = s.ColorDetailCopyFormat;
                _includeHash = s.ColorDetailIncludeHash;
                return;
            }
        }
        catch { }
        var fallback = SettingsService.LoadStatic();
        ApplyDeliveryPrefs(fallback?.ColorDetailAutoCopy ?? true, fallback?.ShowColorDetailWindow ?? true);
        _format = fallback?.ColorDetailCopyFormat ?? ColorDetailCopyFormat.Hex;
        _includeHash = fallback?.ColorDetailIncludeHash ?? true;
    }

    /// <summary>Keeps copy and the window from both being off, matching <see cref="ResultDelivery.ForColor"/>.</summary>
    private void ApplyDeliveryPrefs(bool copy, bool showWindow)
    {
        var plan = ResultDelivery.Normalize(copy, showWindow);
        _autoCopy = plan.Copy;
        _showWindowPref = plan.ShowWindow;
    }

    private void SavePrefs()
    {
        try
        {
            ApplyDeliveryPrefs(_autoCopy, _showWindowPref);
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

    /// <summary>
    /// Standard suite title bar (logo, title, More menu, close), like the OCR
    /// and QR windows. Minimize/maximize stay hidden: this is a fixed tool
    /// popup. The advanced-options chevron moved into the More menu.
    /// </summary>
    private FrameworkElement BuildHeader()
    {
        _titleBar = new CyberSnapTitleBar
        {
            Title = Title,
            // Full-bleed inside the card padding so More/Close sit in the
            // corner like every other window; 10px gap to the pick row below.
            Margin = new Thickness(-14, -14, -14, 10),
        };
        _titleBar.CloseRequested += (_, _) => Close();
        return _titleBar;
    }

    /// <summary>
    /// Recent-colors history, full width. The re-pick gotero moved to the
    /// footer next to Copy: up here it sat too close to Close (missclicks).
    /// </summary>
    private FrameworkElement BuildPickRow()
    {
        _recentPanel = new WrapPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        };
        return _recentPanel;
    }

    private bool _footerExpanded = true;

    internal bool IsOptionsExpanded => _footerExpanded;

    /// <summary>Entry point for the title-bar More menu's checkable item.</summary>
    internal void SetOptionsExpanded(bool expanded)
    {
        _footerExpanded = expanded;
        ApplyFooterExpansionState();
        Dispatcher.BeginInvoke(new Action(ClampToMonitor), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Entry point for the title-bar More menu's re-pick item.</summary>
    internal void RequestRepick()
    {
        Close();
        RepickRequested?.Invoke();
    }

    /// <summary>
    /// Applies the expanded/collapsed state without flipping it. Only the
    /// advanced options collapse; the Copied badge and the footer action
    /// buttons always stay visible.
    /// </summary>
    private void ApplyFooterExpansionState()
    {
        if (_optionsPanel is not null)
            _optionsPanel.Visibility = _footerExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _headerDragging;
    private System.Windows.Point _headerDragOffset;

    /// <summary>
    /// Manual header drag (instead of blocking DragMove, which misbehaves on this
    /// layered window: mouse-capture loss and mid-drag rebuilds could stall it).
    /// Covers the title bar plus its surrounding margin/padding; interactive
    /// controls keep their own behavior. Swallows the press so the standard
    /// title bar does not start its own DragMove: this stays the single drag
    /// authority. Title-bar chrome buttons are Borders with a Hand cursor;
    /// logo/title/separators keep the Arrow cursor and stay draggable.
    /// </summary>
    private void OnWindowDragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _titleBar is null)
            return;

        double headerBottom;
        try
        {
            var topLeft = _titleBar.TranslatePoint(new System.Windows.Point(0, 0), this);
            headerBottom = topLeft.Y + _titleBar.ActualHeight;
        }
        catch { return; }

        if (e.GetPosition(this).Y > headerBottom)
            return;

        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is WpfButton
                || current is System.Windows.Controls.TextBox
                || current is WpfComboBox)
                return;
            if (current is Border border && ReferenceEquals(border.Cursor, WpfCursors.Hand))
                return;
            current = VisualTreeHelper.GetParent(current);
        }

        _headerDragging = true;
        _headerDragOffset = e.GetPosition(this);
        try { Mouse.Capture(this, System.Windows.Input.CaptureMode.Element); } catch { }
        Cursor = System.Windows.Input.Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnHeaderDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_headerDragging)
            return;
        try
        {
            var pos = e.GetPosition(this);
            Left += pos.X - _headerDragOffset.X;
            Top += pos.Y - _headerDragOffset.Y;
        }
        catch (Exception ex) { AppDiagnostics.LogError("color-detail.drag", ex); }
    }

    private void OnHeaderDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_headerDragging)
            return;
        _headerDragging = false;
        try
        {
            if (ReferenceEquals(Mouse.Captured, this))
                Mouse.Capture(null);
        }
        catch { }
        ClearValue(CursorProperty);
        Dispatcher.BeginInvoke(new Action(ClampToMonitor), System.Windows.Threading.DispatcherPriority.Background);
    }

    private FrameworkElement? _footer;
    private StackPanel? _optionsPanel;

    private FrameworkElement BuildPreview()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _previewBorder = new Border
        {
            Height = 56,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Theme.Brush(NeutralSwatchBorder()),
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
        // The whole row copies (same action as its copy button, which swallows
        // its own clicks so there is never a double copy).
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = WpfCursors.Hand,
            Background = WpfBrushes.Transparent,
        };
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

        WpfColor idleFieldBg = Theme.IsDark
            ? WpfColor.FromArgb(255, 20, 22, 33)
            : WpfColor.FromArgb(255, 238, 240, 245);
        var tb = new WpfTextBox
        {
            Height = 32,
            FontSize = 13,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(idleFieldBg),
            Foreground = Theme.Brush(Theme.TextPrimary),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            FocusVisualStyle = new Style(),
            Cursor = System.Windows.Input.Cursors.Arrow,
        };
        // Display-only values: not focusable/selectable, no context menu, and
        // click-through so the row grid below receives the click and copies.
        tb.Focusable = false;
        tb.IsHitTestVisible = false;
        tb.ContextMenu = new ContextMenu { Visibility = Visibility.Collapsed };
        box = tb;
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var copyGlyph = new System.Windows.Shapes.Path
        {
            Width = 13,
            Height = 13,
            Stretch = System.Windows.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 2.2,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            Fill = System.Windows.Media.Brushes.Transparent,
        };
        if (TryFindResource("CopyIconGeometry") is System.Windows.Media.Geometry copyData)
            copyGlyph.Data = copyData;
        copyGlyph.SetBinding(
            System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(WpfButton.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(WpfButton), 1),
            });
        var copy = new WpfButton
        {
            Width = 34,
            Height = 32,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = WpfCursors.Hand,
            Style = GhostButtonStyle(6),
            Content = copyGlyph,
        };
        string captured = label;
        ToolTipService.SetToolTip(copy, T(copyTooltip));
        copy.Click += (_, _) => CopyRow(captured);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);

        ToolTipService.SetToolTip(grid, T(copyTooltip));
        grid.MouseLeftButtonDown += (_, _) => CopyRow(captured);
        grid.MouseEnter += (_, _) => tb.Background = Theme.Brush(Theme.AccentSubtle);
        grid.MouseLeave += (_, _) => tb.Background = Theme.Brush(idleFieldBg);

        return grid;
    }

    private FrameworkElement BuildFooter()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _footer = stack;

        // Advanced options collapse via the title-bar chevron. The Copied
        // badge and the action buttons live outside this panel so they
        // always stay visible.
        _optionsPanel = new StackPanel();
        _optionsPanel.Visibility = _footerExpanded ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(_optionsPanel);

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
            RefreshAll();
        };
        Grid.SetColumn(_formatCombo, 1);
        formatRow.Children.Add(_formatCombo);
        _optionsPanel.Children.Add(formatRow);

        _optionsPanel.Children.Add(OptionToggleRow(T("Auto-copy"), _autoCopy, v =>
        {
            // Interlocked pair: switching auto-copy off while the window is
            // hidden shows the window, so a pick is never silently lost.
            if (!v && !_showWindowPref)
            {
                _showWindowPref = true;
                _setShowToggle?.Invoke(true);
            }
            _autoCopy = v;
            SavePrefs();
        }, set => _setAutoToggle = set,
            T("Copy the picked color automatically. Turning this off shows this window instead.")));
        _optionsPanel.Children.Add(OptionToggleRow(T("Include # in HEX"), _includeHash, v => { _includeHash = v; SavePrefs(); RefreshAll(); }));
        _optionsPanel.Children.Add(OptionToggleRow(T("Show this window"), _showWindowPref, v =>
        {
            // Interlocked pair: hiding the window while auto-copy is off
            // enables auto-copy instead of bricking both deliveries.
            if (!v && !_autoCopy)
            {
                _autoCopy = true;
                _setAutoToggle?.Invoke(true);
                _showWindowPref = false;
            }
            else
            {
                _showWindowPref = v;
            }
            SavePrefs();
        }, set => _setShowToggle = set,
            T("Show this window after picking. Turning this off enables auto-copy instead.")));

        var badgeLabel = new TextBlock
        {
            Text = T("Copied"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(Theme.TextPrimary),
        };
        var badgeRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeRow.Children.Add(new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 1,6 L 4.5,9.5 L 11,1.5"),
            Stroke = Theme.Brush(Theme.Accent),
            StrokeThickness = 2,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            Width = 11,
            Height = 10,
            Stretch = System.Windows.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        });
        badgeRow.Children.Add(badgeLabel);
        _copiedBadge = new Border
        {
            HorizontalAlignment = WpfHAlign.Right,
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 8, 0, 0),
            Background = Theme.Brush(WithAlpha(Theme.Accent, Theme.IsDark ? (byte)48 : (byte)36)),
            BorderBrush = Theme.Brush(Theme.Accent),
            BorderThickness = new Thickness(1),
            Child = badgeRow,
        };
        stack.Children.Add(_copiedBadge);

        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Re-pick gotero, moved here from the history row where it sat too
        // close to Close. Same ghost treatment as the copy buttons.
        var repick = new WpfButton
        {
            Width = 34,
            Height = 30,
            Cursor = WpfCursors.Hand,
            Style = GhostButtonStyle(6),
            Content = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf("picker", ToDrawing(Theme.TextPrimary), 30),
                Width = 15,
                Height = 15,
            },
        };
        ToolTipService.SetToolTip(repick, T("Pick") + ": " + T("Pick another color from the screen"));
        repick.Click += (_, _) => RequestRepick();
        Grid.SetColumn(repick, 0);
        actions.Children.Add(repick);

        var copyOnly = StyledButton(T("Copy"), isAccent: false, leadingCopy: true);
        ToolTipService.SetToolTip(copyOnly, T("Copy the text and keep this window open."));
        copyOnly.Click += (_, _) => CopyFavorite();
        _copyOnlyBtn = copyOnly;
        Grid.SetColumn(copyOnly, 2);
        actions.Children.Add(copyOnly);

        var copyClose = StyledButton(T("Copy and close"), isAccent: true);
        copyClose.IsDefault = true;
        copyClose.Click += (_, _) =>
        {
            if (IsFavoriteCopied() || CopyFavorite())
                Close();
        };
        Grid.SetColumn(copyClose, 4);
        actions.Children.Add(copyClose);
        _copyCloseBtn = copyClose;

        stack.Children.Add(actions);
        return stack;
    }

    /// <summary>Settings-style option row: label on the left, app-wide pill toggle on the right.</summary>
    private static FrameworkElement OptionToggleRow(string text, bool initial, Action<bool> onChange, Action<Action<bool>>? registerSet = null, string? tooltip = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        if (!string.IsNullOrEmpty(tooltip))
            ToolTipService.SetToolTip(grid, tooltip);
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

        // Global CheckBox style from App.xaml (same 44x24 pill with check
        // used in Settings/About/History). Content stays empty like there.
        bool suppress = false;
        var toggle = new System.Windows.Controls.CheckBox
        {
            Content = "",
            IsChecked = initial,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = WpfHAlign.Right,
        };
        toggle.Checked += (_, _) => { if (!suppress) onChange(true); };
        toggle.Unchecked += (_, _) => { if (!suppress) onChange(false); };
        registerSet?.Invoke(value =>
        {
            suppress = true;
            try { toggle.IsChecked = value; }
            finally { suppress = false; }
        });
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
    }

    /// <summary>
    /// Neutral swatch edge so very dark/light picked colors stay visible on
    /// any card background. Deliberately gray, never the accent tint.
    /// </summary>
    private static WpfColor NeutralSwatchBorder() => Theme.IsDark
        ? WpfColor.FromArgb(64, 255, 255, 255)
        : WpfColor.FromArgb(48, 0, 0, 0);

    /// <summary>
    /// Ghost button mirroring the OCR ghost buttons: neutral at rest, accent
    /// wash + accent border + accent foreground on hover. Template triggers
    /// own the visuals so the default Button chrome cannot swallow them.
    /// </summary>
    private static Style GhostButtonStyle(int cornerRadius = 6)
    {
        var idleBg = Theme.IsDark
            ? WpfColor.FromArgb(255, 48, 50, 66)
            : WpfColor.FromArgb(255, 228, 230, 237);
        var style = new Style(typeof(WpfButton));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Theme.Brush(idleBg)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Theme.Brush(Theme.BorderSubtle)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Theme.Brush(Theme.TextPrimary)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, WpfCursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));

        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "Bd";
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        borderFactory.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(WpfButton.Background))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        borderFactory.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding(nameof(WpfButton.BorderBrush))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        borderFactory.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding(nameof(WpfButton.BorderThickness))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        borderFactory.SetBinding(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(WpfButton.Padding))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);
        var template = new ControlTemplate(typeof(WpfButton)) { VisualTree = borderFactory };
        // Named setters (TargetName "Bd") are only legal in template triggers,
        // not in style triggers: Style.Triggers threw and broke the window.
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush(Theme.AccentSubtle), "Bd"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush(Theme.Accent), "Bd"));
        hover.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Theme.Brush(Theme.Accent)));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = WpfButton.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush(Theme.AccentSubtle), "Bd"));
        pressed.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush(Theme.Accent), "Bd"));
        pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.8, "Bd"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.45, "Bd"));
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));

        return style;
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

    private WpfButton StyledButton(string text, bool isAccent, bool leadingCopy = false)
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
            // Consolidated shared style owns every visual: its template triggers beat
            // the default Button hover chrome that used to swallow these colors and
            // break contrast. Trailing Enter glyph matches the QR/OCR copy buttons.
            btn.SetResourceReference(FrameworkElement.StyleProperty, "CyberSnapAccentButton");
            var row = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = WpfHAlign.Center,
            };
            // Detach first: as Button.Content the label is already a logical child.
            btn.Content = null;
            _primaryLabel = label;
            // Leading copy glyph like the QR/OCR buttons (shared App.xaml geometry).
            if (TryFindResource("CopyIconGeometry") is System.Windows.Media.Geometry copyData)
            {
                var copyGlyph = new System.Windows.Shapes.Path
                {
                    Data = copyData,
                    StrokeThickness = 2.2,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    Width = 12,
                    Height = 12,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 0),
                };
                copyGlyph.SetBinding(
                    System.Windows.Shapes.Shape.StrokeProperty,
                    new System.Windows.Data.Binding(nameof(WpfButton.Foreground))
                    {
                        RelativeSource = new System.Windows.Data.RelativeSource(
                            System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(WpfButton), 1),
                    });
                row.Children.Add(copyGlyph);
                _primaryCopyGlyph = copyGlyph;
                label.Margin = new Thickness(6, 0, 0, 0);
            }
            row.Children.Add(label);
            // Enter keycap like the QR/OCR copy buttons (plain Enter confirms here).
            row.Children.Add(new HotkeyBadge
            {
                Kind = HotkeyBadgeKind.Enter,
                Margin = new Thickness(7, 0, 0, 0),
            });
            btn.Content = row;
        }
        else
        {
            // Same ghost treatment as the OCR copy buttons: the style's
            // template triggers paint the accent hover, and the glyph binds
            // to Foreground so the icon turns accent with the text.
            btn.Style = GhostButtonStyle(6);
            if (leadingCopy && TryFindResource("CopyIconGeometry") is System.Windows.Media.Geometry copyData)
            {
                btn.Content = null;
                var row = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = WpfHAlign.Center,
                };
                var copyGlyph = new System.Windows.Shapes.Path
                {
                    Data = copyData,
                    StrokeThickness = 2.2,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    Width = 12,
                    Height = 12,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                copyGlyph.SetBinding(
                    System.Windows.Shapes.Shape.StrokeProperty,
                    new System.Windows.Data.Binding(nameof(WpfButton.Foreground))
                    {
                        RelativeSource = new System.Windows.Data.RelativeSource(
                            System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(WpfButton), 1),
                    });
                label.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(copyGlyph);
                row.Children.Add(label);
                btn.Content = row;
            }
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

        string name = ColorFormatHelper.ApproximateName(_r, _g, _b);        double onWhite = ColorFormatHelper.ContrastRatio(_r, _g, _b, againstWhite: true);
        double onBlack = ColorFormatHelper.ContrastRatio(_r, _g, _b, againstWhite: false);
        _previewName.Text = $"{T(name)}  ·  {FavoriteText()}";
        _previewContrast.Text = $"{T("Contrast")}: {onWhite:0.0}:1 {T("on white")} · {onBlack:0.0}:1 {T("on black")} ({ColorFormatHelper.ContrastGrade(Math.Max(onWhite, onBlack))})";

        PopulateRecents();
        ApplyCopyActionState();
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
                    BorderBrush = Theme.Brush(NeutralSwatchBorder()),
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

    private bool CopyFavorite() => CopyText(FavoriteText());

    private bool IsFavoriteCopied() =>
        ResultDelivery.MatchesCopied(FavoriteText(), _copiedText);

    private void ApplyCopyActionState()
    {
        if (_primaryLabel is null)
            return;

        bool copied = IsFavoriteCopied();
        if (_copiedBadge != null)
            _copiedBadge.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
        // Like the OCR ghost Copy: pointless while the favorite value is
        // exactly what is already on the clipboard (e.g. just auto-copied).
        if (_copyOnlyBtn is not null)
            _copyOnlyBtn.IsEnabled = !copied;
        _primaryLabel.Text = T(copied ? "Close" : "Copy and close");
        if (_primaryCopyGlyph != null)
        {
            _primaryCopyGlyph.Visibility = copied ? Visibility.Collapsed : Visibility.Visible;
            _primaryLabel.Margin = copied ? new Thickness(0) : new Thickness(6, 0, 0, 0);
        }
    }

    private bool CopyText(string text)
    {
        if (!ResultDelivery.TryCopyText(text))
        {
            _copiedText = null;
            ApplyCopyActionState();
            ToastWindow.ShowError(
                "Copy failed",
                "CyberSnap could not copy the color. Keep this window open and try again.");
            return false;
        }

        _copiedText = text;
        try { SoundService.PlayColorSound(); } catch { }
        ApplyCopyActionState();
        return true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            if (IsFavoriteCopied() || CopyFavorite())
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
            // Initial placement next to the cursor; ContentRendered clamps with the real size.
            var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            double xPhys = cursor.X + 24;
            double yPhys = cursor.Y - 240;
            xPhys = Math.Min(xPhys, work.Right - 100);
            yPhys = Math.Clamp(yPhys, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - 200));
            Left = xPhys / dpi.DpiScaleX;
            Top = yPhys / dpi.DpiScaleY;
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>Keeps the whole window inside its own monitor's working area (all four edges).</summary>
    private void ClampToMonitor()
    {
        try
        {
            if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            var dpi = VisualTreeHelper.GetDpi(this);

            double wPhys = ActualWidth * dpi.DpiScaleX;
            double hPhys = ActualHeight * dpi.DpiScaleY;
            double xPhys = Left * dpi.DpiScaleX;
            double yPhys = Top * dpi.DpiScaleY;

            // Use the window's own center so a dragged window clamps to the monitor it's on.
            var center = new System.Drawing.Point(
                (int)(xPhys + wPhys / 2),
                (int)(yPhys + hPhys / 2));
            var work = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;

            const double margin = 8;
            if (wPhys < work.Width - margin * 2)
                xPhys = Math.Clamp(xPhys, work.Left + margin, work.Right - margin - wPhys);
            else
                xPhys = work.Left + margin;

            if (hPhys < work.Height - margin * 2)
                yPhys = Math.Clamp(yPhys, work.Top + margin, work.Bottom - margin - hPhys);
            else
                yPhys = work.Top + margin;

            Left = xPhys / dpi.DpiScaleX;
            Top = yPhys / dpi.DpiScaleY;
        }
        catch (Exception ex) { AppDiagnostics.LogError("color-detail.clamp", ex); }
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
    public static void ShowForColor(byte r, byte g, byte b, Action? repick = null, string? alreadyCopiedText = null)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var win = new ColorDetailWindow(r, g, b, alreadyCopiedText);
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
