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
using CheckBox = System.Windows.Controls.CheckBox;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace CyberSnap.UI;

public enum SavePromptResult { Save, DontSave, Cancel }

// CyberGems-styled confirmation / alert dialog: dark slate panel, neon glow
// border, glowing icon badge and uppercase neon buttons. Theme-aware (cyan in
// dark mode, blue in light mode) to stay consistent with the rest of CyberSnap.
internal sealed class ThemedConfirmDialog : Window
{
    private enum Kind { Confirm, Danger, Info, Error, SavePrompt }

    // Outer transparent margin so the neon glow has room to render.
    private const double GlowMargin = 16;
    private const double PanelWidth = 390;

    private bool _confirmed;
    private bool _suppress;
    private SavePromptResult _saveResult = SavePromptResult.Cancel;

    private ThemedConfirmDialog(string title, string message, string primaryText, string? secondaryText, Kind kind, string? iconId = null, bool showSuppressCheck = false)
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

        var content = BuildContent(title, message, primaryText, secondaryText, kind, iconId, showSuppressCheck);
        Content = content;
        UiScale.ApplyToWindow(this, content, scaleWindowBounds: true);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        Loaded += (s, e) =>
        {
            try
            {
                var editor = CyberSnap.UI.Editor.EditorForm.ActiveInstance;
                if (editor != null && !editor.IsDisposed && editor.Visible)
                {
                    var helper = new WindowInteropHelper(this);
                    if (helper.Owner == editor.Handle)
                    {
                        double editorLeft = editor.Left;
                        double editorTop = editor.Top;
                        double editorWidth = editor.Width;
                        double editorHeight = editor.Height;

                        double sidebarWidth = 332;
                        double canvasAreaCenterX = editorLeft + sidebarWidth + (editorWidth - sidebarWidth) / 2;
                        double canvasAreaCenterY = editorTop + (editorHeight / 2);

                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = canvasAreaCenterX - (ActualWidth / 2);
                        Top = canvasAreaCenterY - (ActualHeight / 2);
                    }
                }
            }
            catch
            {
                // Ignore and fall back to default behavior if centering fails
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
        bool danger = true,
        string? iconId = null)
        => Show(owner, IntPtr.Zero, title, message, primaryText, secondaryText, danger ? Kind.Danger : Kind.Confirm, iconId);

    // Overload for WinForms callers (e.g. the annotation EditorForm), which own
    // an HWND rather than a WPF Window.
    public static bool Confirm(
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText = "Yes",
        string secondaryText = "No",
        bool danger = true,
        string? iconId = null)
        => Show(null, ownerHandle, title, message, primaryText, secondaryText, danger ? Kind.Danger : Kind.Confirm, iconId, false, out _);

    /// <summary>Like <see cref="Confirm(IntPtr, string, string, string, string, bool, string?)"/>,
    /// but renders a "Don't show again" checkbox. <paramref name="dontShowAgain"/> is set to
    /// <c>true</c> when the user both confirmed AND checked the box.</summary>
    public static bool Confirm(
        IntPtr ownerHandle,
        string title,
        string message,
        out bool dontShowAgain,
        string primaryText = "Yes",
        string secondaryText = "No",
        bool danger = true,
        string? iconId = null)
        => Show(null, ownerHandle, title, message, primaryText, secondaryText, danger ? Kind.Danger : Kind.Confirm, iconId, true, out dontShowAgain);

    // Single-button informational / error dialog (replacement for MessageBox.Show with one OK button).
    public static void Alert(Window? owner, string title, string message, bool error = false, string okText = "OK")
        => Show(owner, IntPtr.Zero, title, message, okText, null, error ? Kind.Error : Kind.Info);

    public static void Alert(IntPtr ownerHandle, string title, string message, bool error = false, string okText = "OK")
        => Show(null, ownerHandle, title, message, okText, null, error ? Kind.Error : Kind.Info);

    // 3-button Save Prompt: Save / Don't Save / Cancel
    public static SavePromptResult SavePrompt(Window? owner, string title, string message)
        => ShowSavePrompt(owner, IntPtr.Zero, title, message);

    public static SavePromptResult SavePrompt(IntPtr ownerHandle, string title, string message)
        => ShowSavePrompt(null, ownerHandle, title, message);

    private static SavePromptResult ShowSavePrompt(
        Window? owner,
        IntPtr ownerHandle,
        string title,
        string message)
    {
        var dialog = new ThemedConfirmDialog(title, message, "Yes", null, Kind.SavePrompt);
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

        dialog.ShowDialog();
        return dialog._saveResult;
    }

    private static bool Show(
        Window? owner,
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        Kind kind,
        string? iconId = null)
    {
        return Show(owner, ownerHandle, title, message, primaryText, secondaryText, kind, iconId, false, out _);
    }

    private static bool Show(
        Window? owner,
        IntPtr ownerHandle,
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        Kind kind,
        string? iconId,
        bool showSuppressCheck,
        out bool dontShowAgain)
    {
        dontShowAgain = false;
        var dialog = new ThemedConfirmDialog(title, message, primaryText, secondaryText, kind, iconId, showSuppressCheck);
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

        bool result = dialog.ShowDialog() == true && dialog._confirmed;
        dontShowAgain = dialog._suppress;
        return result;
    }

    // ── Content ────────────────────────────────────────────────────────────

    private FrameworkElement BuildContent(string title, string message, string primaryText, string? secondaryText, Kind kind, string? iconId, bool showSuppressCheck = false)
    {
        var accent = AccentFor(kind);

        var shell = new Border
        {
            Margin = new Thickness(GlowMargin),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(PanelBackground),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
            BorderThickness = new Thickness(1),
            Effect = Glow(accent, Theme.IsDark ? 10 : 7, Theme.IsDark ? 0.18 : 0.11)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });      // accent strip
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // icon (centered)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // title + message
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });      // separator
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // suppress checkbox (optional)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // buttons footer

        // Row 0: thin accent strip — CornerRadius matches the shell outer radius so corners blend cleanly.
        var accentBar = new Border
        {
            CornerRadius = new CornerRadius(topLeft: 10, topRight: 10, bottomRight: 0, bottomLeft: 0),
            Background = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)155 : (byte)115))
        };
        Grid.SetRow(accentBar, 0);
        root.Children.Add(accentBar);

        // Row 1: icon centered.
        var icon = BuildIcon(kind, accent, iconId);
        icon.HorizontalAlignment = WpfHorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 20, 0, 0);
        Grid.SetRow(icon, 1);
        root.Children.Add(icon);

        // Row 2: title + message, both center-aligned.
        var textPanel = new StackPanel
        {
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(28, 10, 28, 18)
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0)
        });
        Grid.SetRow(textPanel, 2);
        root.Children.Add(textPanel);

        // Row 3: hairline separator.
        var separator = new Border
        {
            Background = Theme.Brush(
                Theme.IsDark ? WpfColor.FromArgb(28, 255, 255, 255) : WpfColor.FromArgb(18, 0, 0, 0))
        };
        Grid.SetRow(separator, 3);
        root.Children.Add(separator);

        // Row 4: centered buttons.
        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(10, 18, 10, 14)
        };
        if (kind == Kind.SavePrompt)
        {
            buttons.Children.Add(BuildButton(Services.LocalizationService.Translate("Cancel"), isPrimary: false, kind, () =>
            {
                _saveResult = SavePromptResult.Cancel;
                Close();
            }));
            buttons.Children.Add(BuildButton(Services.LocalizationService.Translate("No"), isPrimary: false, kind, () =>
            {
                _saveResult = SavePromptResult.DontSave;
                Close();
            }));
            buttons.Children.Add(BuildButton(Services.LocalizationService.Translate("Yes"), isPrimary: true, kind, () =>
            {
                _saveResult = SavePromptResult.Save;
                DialogResult = true;
                Close();
            }));
        }
        else
        {
            if (secondaryText is not null)
                buttons.Children.Add(BuildButton(secondaryText, isPrimary: false, kind, () => Close()));
            buttons.Children.Add(BuildButton(primaryText, isPrimary: true, kind, () =>
            {
                _confirmed = true;
                DialogResult = true;
                Close();
            }));
        }
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        // Row 5: "Don't show again" checkbox (only when requested by the caller).
        if (showSuppressCheck)
        {
            var suppressRow = new StackPanel
            {
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                Margin = new Thickness(10, 2, 10, 10)
            };
            var check = new CheckBox
            {
                Content = Services.LocalizationService.Translate("Don't show again"),
                FontSize = 11.5,
                Foreground = Theme.Brush(Theme.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center
            };
            check.Checked += (_, _) => _suppress = true;
            check.Unchecked += (_, _) => _suppress = false;
            suppressRow.Children.Add(check);
            Grid.SetRow(suppressRow, 5);
            root.Children.Add(suppressRow);
        }

        // Close affordance overlaid at the top-right, spanning the accent + icon rows.
        var close = BuildClose();
        close.Margin = new Thickness(0, 8, 8, 0);
        Grid.SetRow(close, 1);
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
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        return close;
    }

    private static FrameworkElement BuildIcon(Kind kind, WpfColor accent, string? iconId)
    {
        iconId ??= kind switch
        {
            Kind.Danger => "trash",
            Kind.Error => "warning",
            _ => "question_plain"
        };

        // Question/SavePrompt: draw the icon inside a neon-glow ring for a bigger, cleaner look.
        if (kind is Kind.Confirm or Kind.SavePrompt or Kind.Info)
        {
            const double ringSize = 54;

            UIElement child;
            if (iconId == "question_plain")
            {
                child = new TextBlock
                {
                    Text = kind == Kind.Info ? "i" : "?",
                    FontSize = kind == Kind.Info ? 30 : 32,
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.Brush(WithAlpha(accent, 235)),
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = kind == Kind.Info ? new Thickness(0, -2, 0, 0) : new Thickness(0, -3, 0, 0)
                };
            }
            else
            {
                const double iconSize = 30;
                child = new System.Windows.Controls.Image
                {
                    Source = FluentIcons.RenderWpf(iconId, ToDrawingColor(accent, 235), (int)iconSize),
                    Width = iconSize,
                    Height = iconSize,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var ring = new Border
            {
                Width = ringSize,
                Height = ringSize,
                CornerRadius = new CornerRadius(ringSize / 2),
                BorderBrush = Theme.Brush(accent),
                BorderThickness = new Thickness(2.4),
                Background = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)18 : (byte)12)),
                Effect = Glow(accent, 12, 0.35),
                Child = child
            };
            return ring;
        }

        // Danger / Error: plain icon, no ring.
        return new System.Windows.Controls.Image
        {
            Source = FluentIcons.RenderWpf(iconId, ToDrawingColor(accent, 235), 48),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform
        };
    }

    private Button BuildButton(string text, bool isPrimary, Kind kind, Action click)
    {
        var accent = AccentFor(kind);
        var button = new Button
        {
            Content = text,
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
            var hoverText = Theme.Brush(PrimaryHoverText(kind));

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
        Kind.SavePrompt => Theme.Accent,
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
