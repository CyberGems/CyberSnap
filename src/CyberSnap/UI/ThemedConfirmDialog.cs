using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CyberSnap.Helpers;
using Button = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace CyberSnap.UI;

// CyberGems-styled confirmation / alert dialog: dark slate panel, neon glow
// border, glowing icon badge and uppercase neon buttons. Theme-aware (cyan in
// dark mode, blue in light mode) to stay consistent with the rest of CyberSnap.
internal sealed class ThemedConfirmDialog : Window
{
    private enum Kind { Confirm, Danger, Info, Error }

    // Outer transparent margin so the neon glow has room to render.
    private const double GlowMargin = 22;
    private const double PanelWidth = 360;

    private bool _confirmed;

    private ThemedConfirmDialog(string title, string message, string primaryText, string? secondaryText, Kind kind)
    {
        Theme.Refresh();
        title = Services.LocalizationService.Translate(title);
        message = Services.LocalizationService.Translate(message);
        primaryText = Services.LocalizationService.Translate(primaryText);
        secondaryText = secondaryText is null ? null : Services.LocalizationService.Translate(secondaryText);

        Title = title;
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

        var content = BuildContent(title, message, primaryText, secondaryText, kind);
        Content = content;
        UiScale.ApplyToWindow(this, content, scaleWindowBounds: false);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string primaryText = "Yes",
        string secondaryText = "No",
        bool danger = true)
        => Show(owner, IntPtr.Zero, title, message, primaryText, secondaryText, danger ? Kind.Danger : Kind.Confirm);

    // Overload for WinForms callers (e.g. the annotation EditorForm), which own
    // an HWND rather than a WPF Window.
    public static bool Confirm(
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText = "Yes",
        string secondaryText = "No",
        bool danger = true)
        => Show(null, ownerHandle, title, message, primaryText, secondaryText, danger ? Kind.Danger : Kind.Confirm);

    // Single-button informational / error dialog (replacement for MessageBox.Show with one OK button).
    public static void Alert(Window? owner, string title, string message, bool error = false, string okText = "OK")
        => Show(owner, IntPtr.Zero, title, message, okText, null, error ? Kind.Error : Kind.Info);

    public static void Alert(IntPtr ownerHandle, string title, string message, bool error = false, string okText = "OK")
        => Show(null, ownerHandle, title, message, okText, null, error ? Kind.Error : Kind.Info);

    private static bool Show(
        Window? owner,
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        Kind kind)
    {
        var dialog = new ThemedConfirmDialog(title, message, primaryText, secondaryText, kind);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else if (ownerHandle != IntPtr.Zero)
        {
            new WindowInteropHelper(dialog).Owner = ownerHandle;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true && dialog._confirmed;
    }

    // ── Content ────────────────────────────────────────────────────────────

    private FrameworkElement BuildContent(string title, string message, string primaryText, string? secondaryText, Kind kind)
    {
        var accent = AccentFor(kind);

        var shell = new Border
        {
            Margin = new Thickness(GlowMargin),
            CornerRadius = new CornerRadius(12),
            Background = Theme.Brush(PanelBackground),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)150 : (byte)110)),
            BorderThickness = new Thickness(1.4),
            Effect = Glow(accent, Theme.IsDark ? 26 : 18, Theme.IsDark ? 0.55 : 0.30)
        };

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: glowing icon badge + title/message column.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildIcon(kind, accent);
        Grid.SetColumn(icon, 0);
        body.Children.Add(icon);

        var textPanel = new StackPanel
        {
            Margin = new Thickness(16, 1, 22, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = title.ToUpper(CultureInfo.CurrentCulture),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextPrimary),
            TextWrapping = TextWrapping.Wrap
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(0, 7, 0, 0)
        });
        Grid.SetColumn(textPanel, 1);
        body.Children.Add(textPanel);

        Grid.SetRow(body, 0);
        root.Children.Add(body);

        // Row 1: right-aligned buttons.
        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        if (secondaryText is not null)
            buttons.Children.Add(BuildButton(secondaryText, isPrimary: false, kind, () => Close()));
        buttons.Children.Add(BuildButton(primaryText, isPrimary: true, kind, () =>
        {
            _confirmed = true;
            DialogResult = true;
            Close();
        }));
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        // Close affordance (top-right corner) layered above the body.
        var close = BuildClose();
        Grid.SetRow(close, 0);
        root.Children.Add(close);

        shell.Child = root;

        // Allow dragging the dialog by its background (buttons/close mark their own events handled).
        shell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { /* DragMove can throw if the button was already released */ }
            }
        };

        return shell;
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
                Source = FluentIcons.RenderWpf("close", ToDrawingColor(Theme.TextMuted, 220), 14),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.MouseEnter += (_, _) => close.Background = Theme.Brush(Theme.TabHoverBg);
        close.MouseLeave += (_, _) => close.Background = WpfBrushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        return close;
    }

    private static FrameworkElement BuildIcon(Kind kind, WpfColor accent)
    {
        var iconId = kind switch
        {
            Kind.Danger => "trash",
            Kind.Error => "warning",
            _ => "info"
        };

        return new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Theme.Brush(WithAlpha(accent, 28)),
            BorderBrush = Theme.Brush(WithAlpha(accent, 110)),
            BorderThickness = new Thickness(1),
            Effect = Glow(accent, 12, Theme.IsDark ? 0.5 : 0.25),
            Child = new System.Windows.Controls.Image
            {
                Source = FluentIcons.RenderWpf(iconId, ToDrawingColor(accent, 235), 24),
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private Button BuildButton(string text, bool isPrimary, Kind kind, Action click)
    {
        var accent = AccentFor(kind);
        var button = new Button
        {
            Content = text.ToUpper(CultureInfo.CurrentCulture),
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
            var hoverText = Theme.Brush(PrimaryHoverText(kind));

            button.Background = Theme.Brush(baseBg);
            button.BorderBrush = Theme.Brush(WithAlpha(accent, 170));
            button.Foreground = restText;
            button.MouseEnter += (_, _) =>
            {
                button.Background = Theme.Brush(accent);
                button.Foreground = hoverText;
                button.Effect = Glow(accent, 14, 0.85);
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

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DropShadowEffect Glow(WpfColor color, double blur, double opacity) => new()
    {
        Color = color,
        ShadowDepth = 0,
        BlurRadius = blur,
        Opacity = opacity
    };

    private static WpfColor AccentFor(Kind kind) => kind switch
    {
        Kind.Danger or Kind.Error => Theme.IsDark ? WpfColor.FromRgb(255, 70, 100) : WpfColor.FromRgb(196, 43, 28),
        _ => Theme.Accent
    };

    private static WpfColor PrimaryHoverText(Kind kind) =>
        kind is Kind.Danger or Kind.Error
            ? Colors.White
            : (Theme.IsDark ? Colors.Black : Colors.White);

    private static WpfColor WithAlpha(WpfColor c, byte alpha) => WpfColor.FromArgb(alpha, c.R, c.G, c.B);

    private static System.Drawing.Color ToDrawingColor(WpfColor color, byte alpha) =>
        System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static WpfColor PanelBackground =>
        Theme.IsDark ? WpfColor.FromRgb(15, 17, 26) : WpfColor.FromRgb(245, 246, 248);

    private static WpfColor SecondaryButtonBg =>
        Theme.IsDark ? WpfColor.FromRgb(26, 30, 46) : WpfColor.FromRgb(249, 249, 249);

    private static WpfColor SecondaryButtonBorder =>
        Theme.IsDark ? WpfColor.FromArgb(32, 255, 255, 255) : WpfColor.FromArgb(26, 0, 0, 0);
}
