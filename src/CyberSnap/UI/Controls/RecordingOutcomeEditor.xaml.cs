using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace CyberSnap.UI.Controls;

public partial class RecordingOutcomeEditor : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(RecordingOutcomeKind),
            typeof(RecordingOutcomeEditor),
            new PropertyMetadata(RecordingOutcomeKind.Video, OnKindChanged));

    private RecordingOutcomeState _state = new(Save: true, Notification: true, Clipboard: false, OpenTrimmer: true);
    private bool _suppress;

    public RecordingOutcomeEditor()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    public RecordingOutcomeKind Kind
    {
        get => (RecordingOutcomeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Raised after the user adds or removes a pill (not when LoadFromSettings runs).</summary>
    public event Action? OutcomeChanged;

    public RecordingOutcomeState State => _state;

    public void LoadFromSettings(AppSettings settings)
    {
        _suppress = true;
        try
        {
            _state = RecordingOutcomeModel.FromSettings(settings, Kind);
            Rebuild();
        }
        finally
        {
            _suppress = false;
        }
    }

    public void ApplyToSettings(AppSettings settings) =>
        RecordingOutcomeModel.ApplyToSettings(_state, settings, Kind);

    public void RefreshLocalization() => Rebuild();

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RecordingOutcomeEditor editor)
            editor.Rebuild();
    }

    private void Rebuild()
    {
        if (ActivePanel is null || AvailablePanel is null)
            return;

        if (ActiveLabel != null)
            ActiveLabel.Text = LocalizationService.Translate("Active outcome");
        if (AvailableLabel != null)
            AvailableLabel.Text = LocalizationService.Translate("Available outcome");

        ActivePanel.Children.Clear();
        AvailablePanel.Children.Clear();

        foreach (var pill in RecordingOutcomeModel.AllPills)
        {
            if (RecordingOutcomeModel.IsActive(_state, pill))
                ActivePanel.Children.Add(BuildActivePill(pill));
            else
                AvailablePanel.Children.Add(BuildAvailablePill(pill));
        }

        if (ActivePanel.Children.Count == 0)
        {
            ActivePanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("No active steps"),
                FontSize = 11.5,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2, 4, 2)
            });
        }

        if (AvailablePanel.Children.Count == 0)
        {
            AvailablePanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("All steps are active"),
                FontSize = 11.5,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2, 4, 2)
            });
        }
    }

    private FrameworkElement BuildActivePill(RecordingOutcomePillKind pill)
    {
        bool canRemove = RecordingOutcomeModel.CanRemove(_state, pill);
        string label = LocalizationService.Translate(RecordingOutcomeModel.LabelKey(pill));
        string tip = LocalizationService.Translate(RecordingOutcomeModel.TooltipKey(pill, Kind));

        var root = CreatePillChrome(isActive: true, tip);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Height = PillContentHeight
        };

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = PillFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, GlyphTextGap, 0)
        });

        if (canRemove)
        {
            var remove = CreateActionGlyph(
                text: "\u00D7",
                useIconFont: false,
                automationName: LocalizationService.Translate("Remove outcome step"),
                onClick: () => RemovePill(pill));
            row.Children.Add(remove);
        }

        root.Child = row;
        return root;
    }

    private FrameworkElement BuildAvailablePill(RecordingOutcomePillKind pill)
    {
        string label = LocalizationService.Translate(RecordingOutcomeModel.LabelKey(pill));
        string tip = LocalizationService.Translate(RecordingOutcomeModel.TooltipKey(pill, Kind));

        var root = CreatePillChrome(isActive: false, tip);
        root.Cursor = System.Windows.Input.Cursors.Hand;
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            AddPill(pill);
        };
        root.MouseEnter += (_, _) => root.Opacity = 1.0;
        root.MouseLeave += (_, _) => root.Opacity = AvailablePillOpacity;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Height = PillContentHeight
        };
        var add = CreateActionGlyph(
            text: "+",
            useIconFont: false,
            automationName: LocalizationService.Translate("Add outcome step"),
            onClick: () => AddPill(pill));
        add.Margin = new Thickness(0, 0, GlyphTextGap, 0);
        row.Children.Add(add);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = PillFontSize,
            VerticalAlignment = VerticalAlignment.Center
        });

        root.Child = row;
        ToolTipService.SetToolTip(root, tip);
        return root;
    }

    private const double PillContentHeight = 18;
    private const double PillFontSize = 11.5;
    private const double GlyphHitSize = 14;
    private const double GlyphTextGap = 4;
    private const double AvailablePillOpacity = 0.92;

    private static Border CreatePillChrome(bool isActive, string? toolTip)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = isActive
                ? new Thickness(8, 3, 4, 3)
                : new Thickness(5, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6),
            MinHeight = 26,
            SnapsToDevicePixels = true,
            ClipToBounds = true,
            ToolTip = string.IsNullOrWhiteSpace(toolTip) ? null : toolTip
        };

        if (isActive)
        {
            border.Background = TryBrush("ThemeTabActiveBrush", MediaColor(0x28, 0x00, 0xE5, 0xCC));
            border.BorderBrush = TryBrush("ThemeAccentBrush", MediaColor(0x88, 0x00, 0xE5, 0xCC));
            border.BorderThickness = new Thickness(1);
            border.Opacity = 1.0;
        }
        else
        {
            border.Background = new SolidColorBrush(MediaColor(0x1A, 0x00, 0xE5, 0xCC));
            border.BorderBrush = TryBrush("ThemeAccentBrush", MediaColor(0x66, 0x00, 0xE5, 0xCC));
            border.BorderThickness = new Thickness(1);
            border.Opacity = AvailablePillOpacity;
        }

        return border;
    }

    private static System.Windows.Media.Color MediaColor(byte a, byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromArgb(a, r, g, b);

    private static Border CreateActionGlyph(string text, bool useIconFont, string automationName, Action onClick)
    {
        var glyph = new TextBlock
        {
            Text = text,
            FontSize = useIconFont ? 8.5 : (text == "\u00D7" ? 13 : 12),
            FontWeight = useIconFont ? FontWeights.Normal : FontWeights.SemiBold,
            FontFamily = useIconFont
                ? new WpfFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                : new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = text == "\u00D7" ? new Thickness(0, -1, 0, 0) : new Thickness(0),
            IsHitTestVisible = false
        };

        var idleBg = WpfBrushes.Transparent;
        var hoverBg = new SolidColorBrush(MediaColor(0x33, 0xFF, 0xFF, 0xFF));

        var hit = new Border
        {
            Width = GlyphHitSize,
            Height = GlyphHitSize,
            CornerRadius = new CornerRadius(GlyphHitSize / 2),
            Background = idleBg,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            SnapsToDevicePixels = true,
            ClipToBounds = true,
            ToolTip = automationName,
            Child = glyph,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(hit, automationName);

        hit.MouseEnter += (_, _) => hit.Background = hoverBg;
        hit.MouseLeave += (_, _) => hit.Background = idleBg;
        hit.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            onClick();
        };

        return hit;
    }

    private static System.Windows.Media.Brush TryBrush(string resourceKey, System.Windows.Media.Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is System.Windows.Media.Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private void AddPill(RecordingOutcomePillKind pill)
    {
        if (_suppress) return;
        var next = RecordingOutcomeModel.WithPillAdded(_state, pill);
        if (next.Equals(_state)) return;
        _state = next;
        Rebuild();
        OutcomeChanged?.Invoke();
    }

    private void RemovePill(RecordingOutcomePillKind pill)
    {
        if (_suppress) return;
        var next = RecordingOutcomeModel.WithPillRemoved(_state, pill);
        if (next.Equals(_state)) return;
        _state = next;
        Rebuild();
        OutcomeChanged?.Invoke();
    }
}
