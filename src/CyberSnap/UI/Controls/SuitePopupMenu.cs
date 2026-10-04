using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CyberSnap.UI.Controls;

// Shared using aliases to avoid clashes with WinForms names in consuming files.
using WpfButton = System.Windows.Controls.Button;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using WpfImage = System.Windows.Controls.Image;
using WpfPopup = System.Windows.Controls.Primitives.Popup;

/// <summary>
/// Deterministic suite popup menu: a StaysOpen <see cref="Popup"/> (no mouse capture)
/// with fully explicit dismissal. A <see cref="ContextMenu"/> captures the mouse while
/// open, which blinds its trigger to the second press and freezes hover — and StaysOpen
/// on ContextMenu does NOT disable that capture. Every toggle menu in the suite is
/// built on this helper instead.
/// </summary>
public sealed class SuitePopupMenu
{
    public sealed class Entry
    {
        public string Header { get; set; } = "";
        public string? ToolTip { get; set; }
        public bool ShowToolTipWhenDisabled { get; set; }
        /// <summary>Either an <see cref="ImageSource"/> or an <see cref="Image"/> (uses its Source).</summary>
        public object? Icon { get; set; }
        public bool IsCheckable { get; set; }
        public bool IsChecked { get; set; }
        public bool IsEnabled { get; set; } = true;
        public Action<bool>? Toggled { get; set; }
        public Action? Clicked { get; set; }
        public SuitePopupMenu? Submenu { get; set; }
    }

    private static readonly HashSet<SuitePopupMenu> s_open = new();
    private static readonly object s_gate = new();

    public static bool AnyOpen
    {
        get { lock (s_gate) return s_open.Count > 0; }
    }

    /// <summary>Closes every open suite popup (outside press, Esc, deactivation).</summary>
    public static void CloseAll()
    {
        SuitePopupMenu[] open;
        lock (s_gate)
        {
            if (s_open.Count == 0)
                return;
            open = new SuitePopupMenu[s_open.Count];
            s_open.CopyTo(open);
        }

        foreach (var menu in open)
        {
            try { menu.Close(); }
            catch { }
        }
    }

    /// <summary>
    /// Hooks explicit dismissal on an owner window. Presses inside an open popup never
    /// reach the owner (separate visual tree); <paramref name="isTrigger"/> marks the
    /// presses owned by toggle buttons, which close deterministically on Click instead.
    /// </summary>
    public static void AttachDismiss(Window owner, Func<DependencyObject, bool> isTrigger)
    {
        owner.PreviewMouseDown += (s, e) =>
        {
            try
            {
                if (!AnyOpen)
                    return;
                if (e.OriginalSource is DependencyObject source && isTrigger(source))
                    return;
                CloseAll();
            }
            catch { }
        };
        owner.PreviewKeyDown += (s, e) =>
        {
            try
            {
                if (e.Key == Key.Escape && AnyOpen)
                {
                    CloseAll();
                    e.Handled = true;
                }
            }
            catch { }
        };
        owner.Deactivated += (s, e) =>
        {
            // Replaces the implicit auto-dismiss: never leave an orphan menu floating.
            try { CloseAll(); }
            catch { }
        };
    }

    public static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(element);

        return LogicalTreeHelper.GetParent(element);
    }

    private readonly List<Entry?> _entries = new();
    private Popup? _popup;
    private FrameworkElement? _trigger;
    private SuitePopupMenu? _openChild;
    private SuitePopupMenu? _parent;

    /// <summary>Fired before rows are built, so check states can refresh per open.</summary>
    public event Action? Opening;

    public event Action? Opened;

    public event Action? Closed;

    public bool IsOpen => _popup?.IsOpen == true;

    public Entry AddAction(string header, Action onClick, object? icon = null, string? toolTip = null, bool enabled = true)
    {
        var entry = new Entry { Header = header, Clicked = onClick, Icon = icon, ToolTip = toolTip, IsEnabled = enabled };
        _entries.Add(entry);
        return entry;
    }

    public Entry AddCheck(string header, bool isChecked, Action<bool> onToggle, object? icon = null, string? toolTip = null)
    {
        var entry = new Entry { Header = header, IsCheckable = true, IsChecked = isChecked, Toggled = onToggle, Icon = icon, ToolTip = toolTip };
        _entries.Add(entry);
        return entry;
    }

    public Entry AddSubmenu(string header, SuitePopupMenu submenu, object? icon = null, string? toolTip = null)
    {
        var entry = new Entry { Header = header, Submenu = submenu, Icon = icon, ToolTip = toolTip };
        _entries.Add(entry);
        return entry;
    }

    public void AddDivider() => _entries.Add(null);

    /// <summary>Deterministic toggle: works because no capture ever hides the press.</summary>
    public void Toggle(FrameworkElement trigger)
    {
        if (IsOpen)
            Close();
        else
            Open(trigger, PlacementMode.Bottom, 0, 2);
    }

    public void Open(FrameworkElement trigger, PlacementMode placement, double horizontalOffset, double verticalOffset)
    {
        try
        {
            Close();
            _trigger = trigger;
            try { Opening?.Invoke(); }
            catch { }

            var stack = new StackPanel();
            foreach (var entry in _entries)
            {
                if (entry is null)
                    stack.Children.Add(Divider());
                else
                    stack.Children.Add(entry.IsCheckable ? BuildCheckRow(entry) : BuildActionRow(entry));
            }

            var shell = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 180,
                SnapsToDevicePixels = true,
                BorderThickness = new Thickness(1),
                Child = stack,
            };
            shell.SetResourceReference(Border.BackgroundProperty, "ThemeCardBrush");
            shell.SetResourceReference(Border.BorderBrushProperty, "ThemeInputBorderBrush");

            var popup = new Popup
            {
                PlacementTarget = trigger,
                Placement = placement,
                HorizontalOffset = horizontalOffset,
                VerticalOffset = verticalOffset,
                AllowsTransparency = true,
                StaysOpen = true,
                Child = shell,
            };
            popup.Opened += (_, _) =>
            {
                lock (s_gate) s_open.Add(this);
                try { Opened?.Invoke(); }
                catch { }
            };
            popup.Closed += (_, _) =>
            {
                try { _openChild?.Close(); }
                catch { }
                _openChild = null;
                lock (s_gate) s_open.Remove(this);
                try { Closed?.Invoke(); }
                catch { }
            };
            _popup = popup;
            popup.IsOpen = true;
        }
        catch { }
    }

    public void Close()
    {
        try
        {
            try { _openChild?.Close(); }
            catch { }
            _openChild = null;
            if (_popup?.IsOpen == true)
                _popup.IsOpen = false;
            _popup = null;
            _trigger = null;
        }
        catch { }
    }

    /// <summary>Closes this menu and every ancestor up to the root (action drill-down).</summary>
    private void CloseChain()
    {
        SuitePopupMenu? current = this;
        while (current != null)
        {
            var parent = current._parent;
            current.Close();
            current = parent;
        }
    }

    private static Border Divider()
    {
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            SnapsToDevicePixels = true,
        };
        divider.SetResourceReference(Border.BackgroundProperty, "ThemeSeparatorBrush");
        return divider;
    }

    private static System.Windows.Controls.Control BuildCheckRow(Entry entry)
    {
        var row = new WpfToggleButton
        {
            Content = entry.Header,
            IsChecked = entry.IsChecked,
            IsEnabled = entry.IsEnabled,
        };
        if (entry.ToolTip != null)
            row.ToolTip = entry.ToolTip;
        if (entry.ShowToolTipWhenDisabled)
            ToolTipService.SetShowOnDisabled(row, true);
        row.SetResourceReference(FrameworkElement.StyleProperty, "PanelMenuCheckRow");
        row.Click += (_, _) =>
        {
            try
            {
                entry.IsChecked = row.IsChecked == true;
                entry.Toggled?.Invoke(entry.IsChecked);
            }
            catch { }
        };
        return row;
    }

    private System.Windows.Controls.Control BuildActionRow(Entry entry)
    {
        var content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconHost = new Grid { Width = 24, Margin = new Thickness(0, 0, 4, 0) };
        var iconSource = entry.Icon is WpfImage iconImage ? iconImage.Source : entry.Icon as ImageSource;
        if (iconSource != null)
        {
            iconHost.Children.Add(new WpfImage
            {
                Source = iconSource,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        content.Children.Add(iconHost);

        if (entry.Submenu != null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = entry.Header, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new Path
            {
                Data = Geometry.Parse("M 0 0 L 4 4 L 0 8 Z"),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.SetResourceReference(Shape.FillProperty, "ThemeTextPrimaryBrush");
            Grid.SetColumn(label, 0);
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(label);
            grid.Children.Add(chevron);
            content.Children.Add(grid);
        }
        else
        {
            content.Children.Add(new TextBlock { Text = entry.Header, VerticalAlignment = VerticalAlignment.Center });
        }

        var row = new WpfButton
        {
            Content = content,
            IsEnabled = entry.IsEnabled,
        };
        if (entry.ToolTip != null)
            row.ToolTip = entry.ToolTip;
        if (entry.ShowToolTipWhenDisabled)
            ToolTipService.SetShowOnDisabled(row, true);
        row.SetResourceReference(FrameworkElement.StyleProperty, "PanelMenuActionRow");
            row.Click += (_, _) =>
            {
                try
                {
                    if (entry.Submenu != null)
                    {
                        try { _openChild?.Close(); }
                        catch { }
                        _openChild = entry.Submenu;
                        entry.Submenu._parent = this;
                        entry.Submenu.Open(row, PlacementMode.Right, 4, 0);
                        return;
                    }

                    entry.Clicked?.Invoke();
                    // Mirror MenuItem default (StaysOpenOnClick=false): actions dismiss.
                    CloseChain();
                }
                catch { }
            };
        return row;
    }
}
