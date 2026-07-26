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

public partial class AfterCaptureOutcomeEditor : UserControl
{
    private AfterCaptureOutcomeState _state = new(
        Save: true,
        AfterCaptureDestination.Notification,
        SystemViewer: false,
        Clipboard: true,
        Preview: true,
        Share: false);
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
        string tip = LocalizationService.Translate(AfterCaptureOutcomeModel.TooltipKey(pill));
        string removeName = LocalizationService.Translate("Remove outcome step");

        var root = CreatePillChrome(isActive: true, tip);
        if (canRemove)
        {
            // Whole chip removes — same hit model as Available add chips.
            // MouseLeftButtonDown (not Up): SetupWizard DragMove otherwise swallows the click.
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

    private FrameworkElement BuildAvailablePill(AfterCapturePillKind pill)
    {
        string label = LocalizationService.Translate(AfterCaptureOutcomeModel.LabelKey(pill));
        string tip = LocalizationService.Translate(AfterCaptureOutcomeModel.TooltipKey(pill));
        string addName = LocalizationService.Translate("Add outcome step");

        var root = CreatePillChrome(isActive: false, tip);
        root.Cursor = System.Windows.Input.Cursors.Hand;
        System.Windows.Automation.AutomationProperties.SetName(root, addName);
        // Must handle MouseLeftButtonDown (not Up): host windows that DragMove on
        // bubbling LeftButtonDown (SetupWizard) otherwise swallow the click.
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
        System.Windows.Controls.ToolTipService.SetToolTip(root, tip);
        return root;
    }

    // Shared geometry so Active and Available chips read as the same size.
    // Match Settings ComboBox face (rounded rect), not stadium pills.
    private const double PillCornerRadius = 6;
    private const double PillFontSize = 11.5;
    private const double ActionSlotWidth = 22;
    // Available stays fully legible; only a mild opacity drop + quieter fill.
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
            // Read as "ready to add", not disabled: solid fill + clear accent edge.
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
            // × needs a touch more size than + to match optical weight.
            FontSize = text == "\u00D7" ? 13 : 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Nudge × up slightly — multiplication sign sits low in the em box.
            Margin = text == "\u00D7" ? new Thickness(0, -1, 0, 0) : new Thickness(0),
            IsHitTestVisible = false
        };
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
