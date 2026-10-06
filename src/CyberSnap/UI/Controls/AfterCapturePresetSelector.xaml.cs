using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using WpfFontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HAlign = System.Windows.HorizontalAlignment;
using Panel = System.Windows.Controls.Panel;
using Cursors = System.Windows.Input.Cursors;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Shared capture-preset picker (Basic / Intermediate / Advanced) used by both the
/// SetupWizard (vertical cards + result summary) and Configuration (horizontal cards
/// above the manual chip editor). Selecting a preset raises <see cref="PresetSelected"/>;
/// <see cref="Refresh"/> highlights the preset matching a state, or a read-only
/// Custom badge when the state matches none (manual chip setup).
/// </summary>
public partial class AfterCapturePresetSelector : UserControl
{
    private AfterCapturePreset _selected = AfterCapturePreset.Custom;
    private bool _hasState;

    public AfterCapturePresetSelector()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    /// <summary>Raised when the user picks a selectable preset (never for Custom).</summary>
    public event Action<AfterCapturePreset>? PresetSelected;

    public AfterCapturePreset Selected => _selected;

    public Orientation CardsOrientation { get; set; } = Orientation.Horizontal;

    /// <summary>Two-line cards (title + subtitle on one row, description below)
    /// for narrow hosts like the SetupWizard.</summary>
    public bool CompactText { get; set; }

    /// <summary>Highlight the preset matching <paramref name="state"/>, or Custom.</summary>
    public void Refresh(AfterCaptureOutcomeState state)
    {
        _selected = AfterCaptureOutcomePresets.Match(state);
        _hasState = true;
        Rebuild();
    }

    public void RefreshLocalization() => Rebuild();

    private void Rebuild()
    {
        if (CardsHost is null)
            return;

        CardsHost.Children.Clear();
        CardsHost.Orientation = Orientation.Vertical;

        Panel host = CardsOrientation == Orientation.Horizontal
            // One shared row: three equal thirds, no wrap, no dead space.
            ? new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 }
            : new StackPanel { Orientation = Orientation.Vertical };
        CardsHost.Children.Add(host);

        var presets = AfterCaptureOutcomePresets.Selectable;
        for (int i = 0; i < presets.Length; i++)
        {
            var card = BuildPresetCard(presets[i]);
            if (CardsOrientation == Orientation.Horizontal
                && i == presets.Length - 1
                && card is Border last)
                last.Margin = new Thickness(0);
            host.Children.Add(card);
        }

        if (_hasState && _selected == AfterCapturePreset.Custom)
            host.Children.Add(BuildCustomBadge());
    }

    private FrameworkElement BuildPresetCard(AfterCapturePreset preset)
    {
        string title = LocalizationService.Translate(AfterCaptureOutcomePresets.TitleKey(preset));
        string subtitle = LocalizationService.Translate(AfterCaptureOutcomePresets.SubtitleKey(preset));
        string desc = LocalizationService.Translate(AfterCaptureOutcomePresets.DescriptionKey(preset));
        bool selected = _hasState && _selected == preset;

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text"),
            Foreground = TryBrush("ThemeTextPrimaryBrush", MediaColor(0xEE, 0xE8, 0xEC, 0xF0)),
            IsHitTestVisible = false
        };
        var subtitleBlock = new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text"),
            Foreground = selected
                ? TryBrush("ThemeAccentBrush", MediaColor(0xFF, 0x00, 0xE5, 0xCC))
                : TryBrush("ThemeTextSecondaryBrush", MediaColor(0xCC, 0xB0, 0xB8, 0xC0)),
            Margin = new Thickness(0, 1, 0, 0),
            IsHitTestVisible = false
        };
        var descBlock = new TextBlock
        {
            Text = desc,
            FontSize = 11.5,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text"),
            Foreground = TryBrush("ThemeTextSecondaryBrush", MediaColor(0xCC, 0xB0, 0xB8, 0xC0)),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            Margin = new Thickness(0, 4, 0, 0),
            IsHitTestVisible = false
        };

        var body = new StackPanel { Orientation = Orientation.Vertical };
        if (CompactText)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            titleBlock.Margin = new Thickness(0);
            subtitleBlock.Margin = new Thickness(6, 0, 0, 0);
            subtitleBlock.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(titleBlock);
            header.Children.Add(subtitleBlock);
            body.Children.Add(header);
        }
        else
        {
            body.Children.Add(titleBlock);
            body.Children.Add(subtitleBlock);
        }
        body.Children.Add(descBlock);

        var idleBg = TryBrush("ThemeInputBackgroundBrush", MediaColor(0xFF, 0x2A, 0x2D, 0x33));
        var idleBorder = TryBrush("ThemeInputBorderBrush", MediaColor(0xFF, 0x3A, 0x3E, 0x45));
        var activeBg = TryBrush("ThemeTabActiveBrush", MediaColor(0x28, 0x00, 0xE5, 0xCC));
        var activeBorder = TryBrush("ThemeAccentBrush", MediaColor(0x88, 0x00, 0xE5, 0xCC));

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = CardsOrientation == Orientation.Horizontal
                ? new Thickness(0, 0, 8, 0)
                : new Thickness(0, 0, 0, 8),
            MinWidth = 0,
            HorizontalAlignment = HAlign.Stretch,
            Background = selected ? activeBg : idleBg,
            BorderBrush = selected ? activeBorder : idleBorder,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = body,
            ToolTip = desc
        };
        System.Windows.Automation.AutomationProperties.SetName(card, $"{title} — {subtitle}");

        if (!selected)
        {
            card.MouseEnter += (_, _) => card.BorderBrush = activeBorder;
            card.MouseLeave += (_, _) => card.BorderBrush = idleBorder;
        }

        // MouseLeftButtonDown (not Up): SetupWizard DragMove on bubbling
        // LeftButtonDown otherwise swallows the click, same as outcome pills.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            PresetSelected?.Invoke(preset);
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                PresetSelected?.Invoke(preset);
            }
        };

        return card;
    }

    private static FrameworkElement BuildCustomBadge()
    {
        string label = LocalizationService.Translate("Custom");
        var badge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 26,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HAlign.Left,
            Background = new SolidColorBrush(MediaColor(0x10, 0xFF, 0xFF, 0xFF)),
            BorderBrush = TryBrush("ThemeTextSecondaryBrush", MediaColor(0x77, 0xC0, 0xC8, 0xD0)),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryBrush("ThemeTextPrimaryBrush", MediaColor(0xEE, 0xE8, 0xEC, 0xF0)),
                Opacity = 0.78,
                IsHitTestVisible = false
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(badge, label);
        return badge;
    }

    private static Color MediaColor(byte a, byte r, byte g, byte b) =>
        Color.FromArgb(a, r, g, b);

    private static Brush TryBrush(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }
}
