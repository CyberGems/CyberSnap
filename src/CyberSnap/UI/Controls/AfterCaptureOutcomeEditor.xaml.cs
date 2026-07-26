using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Orientation = System.Windows.Controls.Orientation;
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

        ActivePanel.Children.Clear();
        AvailablePanel.Children.Clear();

        int activeCount = 0;
        int availableCount = 0;
        var activePills = new List<AfterCapturePillKind>();
        foreach (var pill in AfterCaptureOutcomeModel.AllPills)
        {
            if (AfterCaptureOutcomeModel.IsActive(_state, pill))
            {
                activePills.Add(pill);
                activeCount++;
            }
            else
            {
                AvailablePanel.Children.Add(BuildAvailablePill(pill));
                availableCount++;
            }
        }

        if (ActiveLabel != null)
            ActiveLabel.Text = $"{LocalizationService.Translate("Active outcome")} ({activeCount})";
        if (AvailableLabel != null)
            AvailableLabel.Text = $"{LocalizationService.Translate("Available outcome")} ({availableCount})";

        if (activePills.Count == 0)
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
        else
        {
            // Source stays left; each wrap unit is "→ pill" so a new row never orphans an arrow.
            var steps = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var pill in activePills)
                steps.Children.Add(BuildFlowStep(BuildActivePill(pill)));

            var source = BuildFlowSourceIcon();
            DockPanel.SetDock(source, Dock.Left);

            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(source);
            dock.Children.Add(steps);
            ActivePanel.Children.Add(dock);
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

    /// <summary>Atomic wrap unit: incoming arrow + pill stay together across rows.</summary>
    private static FrameworkElement BuildFlowStep(FrameworkElement pill)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        row.Children.Add(BuildFlowArrow());
        // Pill owns trailing gap; clear its bottom margin so the unit controls row spacing.
        pill.Margin = new Thickness(0);
        row.Children.Add(pill);
        return row;
    }

    private FrameworkElement BuildActivePill(AfterCapturePillKind pill)
    {
        bool canRemove = AfterCaptureOutcomeModel.CanRemove(_state, pill);
        string label = LocalizationService.Translate(AfterCaptureOutcomeModel.LabelKey(pill));
        string tip = LocalizationService.Translate(AfterCaptureOutcomeModel.TooltipKey(pill));
        string removeName = LocalizationService.Translate("Remove outcome step");

        var root = CreatePillChrome(isActive: true, tip);
        // Flow step unit owns vertical spacing when the strip wraps.
        root.Margin = new Thickness(0);

        // Active hover: brighten the filled chip (mirror of Available wash).
        var idleBg = root.Background;
        var idleBorder = root.BorderBrush;
        var hoverBg = new SolidColorBrush(MediaColor(0x3A, 0x00, 0xE5, 0xCC));
        var hoverBorder = TryBrush("ThemeAccentBrush", MediaColor(0xCC, 0x00, 0xE5, 0xCC));
        root.MouseEnter += (_, _) =>
        {
            root.Background = hoverBg;
            root.BorderBrush = hoverBorder;
        };
        root.MouseLeave += (_, _) =>
        {
            root.Background = idleBg;
            root.BorderBrush = idleBorder;
        };

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
                actionOnLeadingEdge: false,
                muted: false);
        }
        else
        {
            root.Padding = new Thickness(8, 3, 8, 3);
            root.Child = CreatePillLabel(label, muted: false);
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

        // Ghost idle → light accent wash on hover so candidates stay distinct from Active.
        var idleBg = root.Background;
        var idleBorder = root.BorderBrush;
        var hoverBg = new SolidColorBrush(MediaColor(0x14, 0x00, 0xE5, 0xCC));
        var hoverBorder = TryBrush("ThemeAccentBrush", MediaColor(0x55, 0x00, 0xE5, 0xCC));
        root.MouseEnter += (_, _) =>
        {
            root.Background = hoverBg;
            root.BorderBrush = hoverBorder;
        };
        root.MouseLeave += (_, _) =>
        {
            root.Background = idleBg;
            root.BorderBrush = idleBorder;
        };

        root.Child = BuildSplitPillContent(
            label,
            actionGlyph: "+",
            actionOnLeadingEdge: true,
            muted: true);
        System.Windows.Controls.ToolTipService.SetToolTip(root, tip);
        return root;
    }

    // Shared geometry so Active and Available chips read as the same size.
    // Match Settings ComboBox face (rounded rect), not stadium pills.
    private const double PillCornerRadius = 6;
    private const double PillFontSize = 11.5;
    private const double ActionSlotWidth = 22;
    private const double FlowSourceIconSize = 24;
    // Viewfinder (corner brackets + center dot) — same as capture toolbar rect glyph.
    private const string FlowSourceIconId = "captureRect";

    private static FrameworkElement BuildFlowSourceIcon()
    {
        var accent = Theme.Accent;
        var color = System.Drawing.Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
        var source = FluentIcons.RenderWpf(FlowSourceIconId, color, (int)FlowSourceIconSize, active: true);
        var image = new System.Windows.Controls.Image
        {
            Source = source,
            Width = FlowSourceIconSize,
            Height = FlowSourceIconSize,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0.95
        };

        // Short uppercase caption so the lead-in reads as the starting event.
        string caption = LocalizationService.Translate("Capture").ToUpperInvariant();
        var label = new TextBlock
        {
            Text = caption,
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.55,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Foreground = TryBrush("ThemeTextSecondaryBrush", MediaColor(0xCC, 0xB0, 0xB8, 0xC0)),
            IsHitTestVisible = false
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        stack.Children.Add(image);
        stack.Children.Add(label);

        return new Border
        {
            Child = stack,
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            ToolTip = LocalizationService.Translate("Screenshot")
        };
    }

    private static FrameworkElement BuildFlowArrow()
    {
        // Crisp shaft + head (not a washed-out text glyph) with breathing room to chip borders.
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,5 L 9,5 M 5.5,1.5 L 10,5 L 5.5,8.5"),
            Stroke = TryBrush("ThemeTextPrimaryBrush", MediaColor(0xEE, 0xE8, 0xEC, 0xF0)),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 11,
            Height = 10,
            Stretch = Stretch.None,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        return new Border
        {
            Child = arrow,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

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
        }
        else
        {
            // Ghost outline with a touch more presence so chips don't vanish into the tray.
            border.Background = new SolidColorBrush(MediaColor(0x10, 0xFF, 0xFF, 0xFF));
            border.BorderBrush = TryBrush("ThemeTextSecondaryBrush", MediaColor(0x77, 0xC0, 0xC8, 0xD0));
            border.BorderThickness = new Thickness(1);
        }

        return border;
    }

    /// <summary>
    /// Two-zone chip: label | action (or action | label).
    /// </summary>
    private static Grid BuildSplitPillContent(string label, string actionGlyph, bool actionOnLeadingEdge, bool muted)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelCell = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = CreatePillLabel(label, muted)
        };

        var actionCell = new Border
        {
            Width = ActionSlotWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderBrush = muted
                ? TryBrush("ThemeTextSecondaryBrush", MediaColor(0x66, 0xC0, 0xC8, 0xD0))
                : new SolidColorBrush(MediaColor(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = actionOnLeadingEdge
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(1, 0, 0, 0),
            Child = CreateActionGlyph(actionGlyph, muted)
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

    private static TextBlock CreatePillLabel(string label, bool muted)
    {
        var tb = new TextBlock
        {
            Text = label,
            FontSize = PillFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        if (muted)
        {
            // Brighter than pure secondary so Available stays readable in the dark tray.
            tb.Foreground = TryBrush("ThemeTextPrimaryBrush", MediaColor(0xEE, 0xE8, 0xEC, 0xF0));
            tb.Opacity = 0.78;
        }
        return tb;
    }

    private static System.Windows.Media.Color MediaColor(byte a, byte r, byte g, byte b) =>
        System.Windows.Media.Color.FromArgb(a, r, g, b);

    /// <summary>Decorative × / + glyph — clicks are handled by the parent chip.</summary>
    private static TextBlock CreateActionGlyph(string text, bool muted)
    {
        var glyph = new TextBlock
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
        if (muted)
        {
            glyph.Foreground = TryBrush("ThemeTextPrimaryBrush", MediaColor(0xEE, 0xE8, 0xEC, 0xF0));
            glyph.Opacity = 0.78;
        }
        return glyph;
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
