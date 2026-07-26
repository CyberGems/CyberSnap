using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using UserControl = System.Windows.Controls.UserControl;
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
        string removeName = LocalizationService.Translate("Remove outcome step");

        var root = CreatePillChrome(isActive: true, tip);
        if (canRemove)
        {
            // Whole chip removes — same hit model as Available add chips.
            root.Cursor = System.Windows.Input.Cursors.Hand;
            System.Windows.Automation.AutomationProperties.SetName(root, removeName);
            root.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;
                RemovePill(pill);
            };
            root.Child = BuildSplitPillContent(
                label,
                actionGlyph: "\u00D7",
                actionOnLeadingEdge: false);
        }
        else
        {
            root.Padding = new Thickness(8, 3, 8, 3);
            root.Child = CreatePillLabel(label);
        }

        return root;
    }

    private FrameworkElement BuildAvailablePill(RecordingOutcomePillKind pill)
    {
        string label = LocalizationService.Translate(RecordingOutcomeModel.LabelKey(pill));
        string tip = LocalizationService.Translate(RecordingOutcomeModel.TooltipKey(pill, Kind));
        string addName = LocalizationService.Translate("Add outcome step");

        var root = CreatePillChrome(isActive: false, tip);
        root.Cursor = System.Windows.Input.Cursors.Hand;
        System.Windows.Automation.AutomationProperties.SetName(root, addName);
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            AddPill(pill);
        };
        root.MouseEnter += (_, _) => root.Opacity = 1.0;
        root.MouseLeave += (_, _) => root.Opacity = AvailablePillOpacity;
        root.Child = BuildSplitPillContent(
            label,
            actionGlyph: "+",
            actionOnLeadingEdge: true);
        ToolTipService.SetToolTip(root, tip);
        return root;
    }

    // Match Settings ComboBox face (rounded rect), not stadium pills.
    private const double PillCornerRadius = 6;
    private const double PillFontSize = 11.5;
    private const double ActionSlotWidth = 22;
    private const double AvailablePillOpacity = 0.92;

    private static Border CreatePillChrome(bool isActive, string? toolTip)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(PillCornerRadius),
            // Section padding lives inside the split layout; chrome stays flush so
            // the divider can run edge-to-edge like the example chip.
            Padding = new Thickness(0),
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

    /// <summary>
    /// Two-zone chip: label | action (or action | label). Shape only — colors stay themed.
    /// </summary>
    private static Grid BuildSplitPillContent(string label, string actionGlyph, bool actionOnLeadingEdge)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelCell = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = CreatePillLabel(label)
        };

        var actionCell = new Border
        {
            Width = ActionSlotWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderBrush = new SolidColorBrush(MediaColor(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = actionOnLeadingEdge
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(1, 0, 0, 0),
            Child = CreateActionGlyph(actionGlyph)
        };

        if (actionOnLeadingEdge)
        {
            Grid.SetColumn(actionCell, 0);
            Grid.SetColumn(labelCell, 1);
        }
        else
        {
            Grid.SetColumn(labelCell, 0);
            Grid.SetColumn(actionCell, 1);
        }

        grid.Children.Add(labelCell);
        grid.Children.Add(actionCell);
        return grid;
    }

    private static TextBlock CreatePillLabel(string label) => new()
    {
        Text = label,
        FontSize = PillFontSize,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    private static System.Windows.Media.Color MediaColor(byte a, byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromArgb(a, r, g, b);

    /// <summary>Decorative × / + glyph — clicks are handled by the parent chip.</summary>
    private static TextBlock CreateActionGlyph(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = text == "\u00D7" ? 13 : 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = text == "\u00D7" ? new Thickness(0, -1, 0, 0) : new Thickness(0),
            IsHitTestVisible = false
        };
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
