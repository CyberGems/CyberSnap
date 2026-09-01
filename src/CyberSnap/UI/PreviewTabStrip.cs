using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Services;
using UserControl = System.Windows.Controls.UserControl;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CyberSnap.UI;

public readonly record struct PreviewTabInfo(string Title, bool Active);

/// <summary>
/// Compact capture tabs under the preview title bar when more than one
/// capture is open. Hidden with a single capture so chrome stays unchanged.
/// </summary>
public sealed class PreviewTabStrip : UserControl
{
    public const double PreferredHeight = 28;

    private readonly ScrollViewer _scroller;
    private readonly StackPanel _host;
    private readonly List<PreviewTabInfo> _tabs = new();
    private int _hoverIndex = -1;
    private int _hoverCloseIndex = -1;

    public event EventHandler<int>? TabSelected;
    public event EventHandler<int>? TabCloseRequested;
    public event EventHandler<int>? TabContextMenuRequested;
    public event EventHandler? BarContextMenuRequested;

    public PreviewTabStrip()
    {
        Height = PreferredHeight;
        Focusable = false;
        _host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 2, 6, 2)
        };
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.HorizontalOnly,
            Focusable = false,
            Content = _host
        };
        var edge = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _scroller
        };
        edge.SetResourceReference(Border.BorderBrushProperty, "ThemeSeparatorBrush");
        Content = edge;
        PreviewMouseWheel += OnMouseWheel;
        MouseRightButtonUp += OnRightButtonUp;
    }

    public void SetTabs(IReadOnlyList<PreviewTabInfo> tabs)
    {
        _tabs.Clear();
        _tabs.AddRange(tabs);
        _hoverIndex = -1;
        _hoverCloseIndex = -1;
        Rebuild();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroller.ExtentWidth <= _scroller.ViewportWidth)
            return;
        _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - Math.Sign(e.Delta) * 48);
        e.Handled = true;
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        for (int i = 0; i < _tabs.Count; i++)
        {
            int index = i;
            var tab = _tabs[i];
            _host.Children.Add(BuildTab(tab, index));
        }
    }

    private FrameworkElement BuildTab(PreviewTabInfo tab, int index)
    {
        var title = new TextBlock
        {
            Text = tab.Title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
            MaxWidth = 160
        };
        title.SetResourceReference(
            TextBlock.ForegroundProperty,
            tab.Active ? "ThemeTextPrimaryBrush" : "ThemeTextSecondaryBrush");

        var close = new TextBlock
        {
            Text = "×",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Width = 16,
            Height = 16,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        close.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMutedBrush");
        close.MouseEnter += (_, _) =>
        {
            _hoverCloseIndex = index;
            close.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
        };
        close.MouseLeave += (_, _) =>
        {
            if (_hoverCloseIndex == index)
                _hoverCloseIndex = -1;
            close.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMutedBrush");
        };
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            TabCloseRequested?.Invoke(this, index);
        };
        ToolTipService.SetToolTip(close, LocalizationService.Translate("Close tab"));

        var header = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(8, 4, 4, 4)
        };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        var underline = new Border
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 6, 2),
            Visibility = tab.Active ? Visibility.Visible : Visibility.Collapsed
        };
        underline.SetResourceReference(Border.BackgroundProperty, "ThemeAccentBrush");

        var body = new Grid();
        body.Children.Add(header);
        body.Children.Add(underline);

        var card = new Border
        {
            MinWidth = 96,
            MaxWidth = 210,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 3, 0),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Child = body,
            Tag = index
        };
        if (tab.Active)
            card.SetResourceReference(Border.BackgroundProperty, "ThemeCardBrush");
        else
            card.Background = Brushes.Transparent;

        card.MouseEnter += (_, _) =>
        {
            _hoverIndex = index;
            if (!tab.Active)
                card.SetResourceReference(Border.BackgroundProperty, "ThemeAccentHoverBrush");
        };
        card.MouseLeave += (_, _) =>
        {
            if (_hoverIndex == index)
                _hoverIndex = -1;
            if (!tab.Active)
                card.Background = Brushes.Transparent;
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == close || IsDescendantOf(e.OriginalSource as DependencyObject, close))
                return;
            TabSelected?.Invoke(this, index);
        };
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            TabContextMenuRequested?.Invoke(this, index);
        };
        ToolTipService.SetToolTip(card, tab.Title);
        return card;
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled)
            return;
        e.Handled = true;
        BarContextMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
