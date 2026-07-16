using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace CyberSnap.UI.Controls;

public partial class AfterCaptureOutcomeEditor : UserControl
{
    private AfterCaptureOutcomeState _state = new(Save: true, AfterCaptureDestination.Notification, Clipboard: true);
    private bool _suppress;

    public AfterCaptureOutcomeEditor()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    /// <summary>Raised after the user adds or removes a pill (not when LoadFromSettings runs).</summary>
    public event Action? OutcomeChanged;

    public AfterCaptureOutcomeState State => _state;

    public void LoadFromSettings(AppSettings settings)
    {
        _suppress = true;
        try
        {
            _state = AfterCaptureOutcomeModel.FromSettings(settings);
            Rebuild();
        }
        finally
        {
            _suppress = false;
        }
    }

    public void SetState(AfterCaptureOutcomeState state, bool raiseChanged = false)
    {
        _suppress = true;
        try
        {
            _state = AfterCaptureOutcomeModel.Normalize(state);
            Rebuild();
        }
        finally
        {
            _suppress = false;
        }

        if (raiseChanged)
            OutcomeChanged?.Invoke();
    }

    public void ApplyToSettings(AppSettings settings) =>
        AfterCaptureOutcomeModel.ApplyToSettings(_state, settings);

    public void RefreshLocalization() => Rebuild();

    private void Rebuild()
    {
        if (ActivePanel is null || AvailablePanel is null)
            return;

        // Explicit translate: attached SourceText on nested UserControls can miss a
        // parent ApplyTo pass depending on load order.
        if (ActiveLabel != null)
            ActiveLabel.Text = LocalizationService.Translate("Active outcome");
        if (AvailableLabel != null)
            AvailableLabel.Text = LocalizationService.Translate("Available outcome");

        ActivePanel.Children.Clear();
        AvailablePanel.Children.Clear();

        foreach (var pill in AfterCaptureOutcomeModel.AllPills)
        {
            if (AfterCaptureOutcomeModel.IsActive(_state, pill))
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

    private FrameworkElement BuildActivePill(AfterCapturePillKind pill)
    {
        bool canRemove = AfterCaptureOutcomeModel.CanRemove(_state, pill);
        string label = LocalizationService.Translate(AfterCaptureOutcomeModel.LabelKey(pill));
        string tip = canRemove
            ? LocalizationService.Translate(AfterCaptureOutcomeModel.TooltipKey(pill))
            : LocalizationService.Translate(AfterCaptureOutcomeModel.ForcedSaveTooltipKey);

        var root = CreatePillChrome(isActive: true, tip);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Height = PillContentHeight
        };

        // No extra margin before the trailing action — the action itself is tight.
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = PillFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, canRemove ? 0 : 2, 0)
        });

        if (canRemove)
        {
            var remove = CreateActionGlyph(
                text: "\uE711",
                useIconFont: true,
                automationName: LocalizationService.Translate("Remove outcome step"),
                onClick: () => RemovePill(pill));
            row.Children.Add(remove);
        }
        else
        {
            // Locked save: subtle lock glyph so forced state is visible.
            row.Children.Add(new TextBlock
            {
                Text = "\uE72E",
                FontFamily = new WpfFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Width = GlyphHitSize,
                TextAlignment = TextAlignment.Center,
                ToolTip = tip
            });
        }

        root.Child = row;
        return root;
    }

    private FrameworkElement BuildAvailablePill(AfterCapturePillKind pill)
    {
        string label = LocalizationService.Translate(AfterCaptureOutcomeModel.LabelKey(pill));
        string tip = LocalizationService.Translate(AfterCaptureOutcomeModel.TooltipKey(pill));

        var root = CreatePillChrome(isActive: false, tip);
        root.Cursor = System.Windows.Input.Cursors.Hand;
        // Whole chip remains clickable; + also has its own hover glyph like ×.
        root.MouseLeftButtonUp += (_, e) =>
        {
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
        add.Margin = new Thickness(0, 0, 2, 0);
        row.Children.Add(add);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = PillFontSize,
            VerticalAlignment = VerticalAlignment.Center
        });

        root.Child = row;
        System.Windows.Controls.ToolTipService.SetToolTip(root, tip);
        return root;
    }

    // Shared geometry so Active and Available chips read as the same size.
    private const double PillContentHeight = 18;
    private const double PillFontSize = 11.5;
    // Compact hit target for × / + — keeps chips narrow even on hover.
    private const double GlyphHitSize = 12;
    // Available stays fully legible; only a mild opacity drop + quieter fill.
    private const double AvailablePillOpacity = 0.92;

    private static Border CreatePillChrome(bool isActive, string? toolTip)
    {
        // Active chips: tight trailing padding so × sits close to the label.
        // Available chips: balanced padding around +label.
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = isActive
                ? new Thickness(8, 3, 3, 3)
                : new Thickness(5, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6),
            MinHeight = 26,
            SnapsToDevicePixels = true,
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
            // Read as "ready to add", not disabled: solid fill + clear accent edge.
            border.Background = new SolidColorBrush(MediaColor(0x1A, 0x00, 0xE5, 0xCC));
            border.BorderBrush = TryBrush("ThemeAccentBrush", MediaColor(0x66, 0x00, 0xE5, 0xCC));
            border.BorderThickness = new Thickness(1);
            border.Opacity = AvailablePillOpacity;
        }

        return border;
    }

    private static System.Windows.Media.Color MediaColor(byte a, byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromArgb(a, r, g, b);

    /// <summary>
    /// Compact × / + control. Hover only fills the small glyph circle (not a wide pad).
    /// </summary>
    private static Border CreateActionGlyph(string text, bool useIconFont, string automationName, Action onClick)
    {
        var glyph = new TextBlock
        {
            Text = text,
            FontSize = useIconFont ? 8.5 : 12,
            FontWeight = useIconFont ? FontWeights.Normal : FontWeights.SemiBold,
            FontFamily = useIconFont
                ? new WpfFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                : new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
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
            ToolTip = automationName,
            Child = glyph,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(hit, automationName);

        hit.MouseEnter += (_, _) => hit.Background = hoverBg;
        hit.MouseLeave += (_, _) => hit.Background = idleBg;
        hit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };

        return hit;
    }

    private static System.Windows.Media.Brush TryBrush(string resourceKey, System.Windows.Media.Color fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(resourceKey) is System.Windows.Media.Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private void AddPill(AfterCapturePillKind pill)
    {
        if (_suppress) return;
        var next = AfterCaptureOutcomeModel.WithPillAdded(_state, pill);
        if (next.Equals(_state)) return;
        _state = next;
        Rebuild();
        OutcomeChanged?.Invoke();
    }

    private void RemovePill(AfterCapturePillKind pill)
    {
        if (_suppress) return;
        var next = AfterCaptureOutcomeModel.WithPillRemoved(_state, pill);
        if (next.Equals(_state)) return;
        _state = next;
        Rebuild();
        OutcomeChanged?.Invoke();
    }
}
