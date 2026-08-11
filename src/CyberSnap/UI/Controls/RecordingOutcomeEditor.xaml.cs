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
        // Rebuild the active pills whenever the available width actually changes. WrapPanel
        // inside a DockPanel last-child-fill sometimes measures with infinite width during
        // the first pass (especially when hosted inside a Card Border with its own padding),
        // so pills can be laid out past the visible edge. Re-running Rebuild forces a
        // re-measure once the real width is known.
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                Rebuild();
        };
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

        ActivePanel.Children.Clear();
        AvailablePanel.Children.Clear();

        int activeCount = 0;
        int availableCount = 0;
        var activePills = new List<RecordingOutcomePillKind>();
        foreach (var pill in RecordingOutcomeModel.AllPills)
        {
            if (RecordingOutcomeModel.IsActive(_state, pill))
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
        pill.Margin = new Thickness(0);
        row.Children.Add(pill);
        return row;
    }

    private FrameworkElement BuildActivePill(RecordingOutcomePillKind pill)
    {
        bool canRemove = RecordingOutcomeModel.CanRemove(_state, pill);
        string label = LocalizationService.Translate(RecordingOutcomeModel.LabelKey(pill, Kind));
        string tip = LocalizationService.Translate(RecordingOutcomeModel.TooltipKey(pill, Kind));
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

    private FrameworkElement BuildAvailablePill(RecordingOutcomePillKind pill)
    {
        string label = LocalizationService.Translate(RecordingOutcomeModel.LabelKey(pill, Kind));
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
        ToolTipService.SetToolTip(root, tip);
        return root;
    }

    // Match Settings ComboBox face (rounded rect), not stadium pills.
    private const double PillCornerRadius = 6;
    private const double PillFontSize = 11.5;
    private const double ActionSlotWidth = 22;
    private const double FlowSourceIconSize = 24;

    private FrameworkElement BuildFlowSourceIcon()
    {
        // Same glyphs as the widget toolbar: record (MP4) / recordGif.
        string iconId = Kind == RecordingOutcomeKind.Gif ? "recordGif" : "record";
        string tipKey = Kind == RecordingOutcomeKind.Gif ? "GIF recording" : "Video";
        string captionKey = Kind == RecordingOutcomeKind.Gif ? "GIF" : "Video";
        var accent = Theme.Accent;
        var color = System.Drawing.Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
        var source = FluentIcons.RenderWpf(iconId, color, (int)FlowSourceIconSize, active: true);
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

        var label = new TextBlock
        {
            Text = LocalizationService.Translate(captionKey).ToUpperInvariant(),
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
            ToolTip = LocalizationService.Translate(tipKey)
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
            FontSize = text == "\u00D7" ? 13 : 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
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
