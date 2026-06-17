using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CyberSnap.AppModel.Jobs;
using CyberSnap.AppModel.Settings;

namespace CyberSnap.WinUI.Views;

public sealed partial class MainPage : Page
{
    private readonly IReadOnlyList<PageEntry> _pages =
    [
        .. SettingsSchemaCatalog.Pages.Select(page => new PageEntry(page.Key, page.Title, page.Description, page)),
        new PageEntry(
            "jobs",
            "Jobs",
            "Shared job contracts that the WinUI shell will consume once runtime, upload, and indexing work is bridged over.",
            null)
    ];

    public MainPage()
    {
        InitializeComponent();
        PageList.ItemsSource = _pages;
        PageList.DisplayMemberPath = nameof(PageEntry.Title);
        PageList.SelectedIndex = 0;
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageList.SelectedItem is PageEntry entry)
            RenderPage(entry);
    }

    private void RenderPage(PageEntry entry)
    {
        ContentHost.Children.Clear();
        ContentHost.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontSize = 30,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        ContentHost.Children.Add(new TextBlock
        {
            Text = entry.Description,
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
            Foreground = new SolidColorBrush(Colors.Gray)
        });

        if (entry.SettingsPage is not null)
        {
            foreach (var section in entry.SettingsPage.Sections)
                ContentHost.Children.Add(BuildSection(section));
            return;
        }

        ContentHost.Children.Add(BuildJobsPanel());
    }

    private static UIElement BuildSection(SettingsSectionDefinition section)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = section.Description,
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
            Foreground = new SolidColorBrush(Colors.Gray)
        });

        foreach (var item in section.Items)
            stack.Children.Add(BuildSettingRow(item));

        return new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private static UIElement BuildSettingRow(SettingDefinition item)
    {
        var row = new Grid
        {
            ColumnSpacing = 18,
            Padding = new Thickness(0, 8, 0, 8),
            MinHeight = 56
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium
        });
        copy.Children.Add(new TextBlock
        {
            Text = item.Description,
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
            Foreground = new SolidColorBrush(Colors.Gray)
        });
        if (!string.IsNullOrWhiteSpace(item.BindingPath))
        {
            copy.Children.Add(new TextBlock
            {
                Text = item.BindingPath,
                Foreground = new SolidColorBrush(Colors.DarkGray),
                FontSize = 12
            });
        }

        Grid.SetColumn(copy, 0);
        row.Children.Add(copy);

        var control = BuildSettingControl(item);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static FrameworkElement BuildSettingControl(SettingDefinition item)
    {
        FrameworkElement control = item.ValueKind switch
        {
            SettingsValueKind.Toggle => BuildToggleSwitch(item),
            SettingsValueKind.Choice => BuildChoiceComboBox(item),
            SettingsValueKind.Text or SettingsValueKind.Folder => BuildTextBox(item),
            SettingsValueKind.Number or SettingsValueKind.Duration => BuildNumberBox(item),
            SettingsValueKind.Action => BuildActionButton(item),
            _ => new TextBlock { Text = item.ValueKind.ToString() }
        };

        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, item.Label);
        AutomationProperties.SetHelpText(control, item.Description);
        return control;
    }

    private static ToggleSwitch BuildToggleSwitch(SettingDefinition item) => new()
    {
        MinWidth = 96,
        OffContent = "Off",
        OnContent = "On",
        IsOn = false
    };

    private static ComboBox BuildChoiceComboBox(SettingDefinition item)
    {
        var combo = new ComboBox
        {
            MinWidth = 180,
            PlaceholderText = "Choose"
        };

        if (item.Options is { Count: > 0 })
        {
            foreach (var option in item.Options)
                combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
            combo.SelectedIndex = 0;
        }

        return combo;
    }

    private static TextBox BuildTextBox(SettingDefinition item) => new()
    {
        MinWidth = 220,
        PlaceholderText = item.BindingPath ?? item.Label
    };

    private static NumberBox BuildNumberBox(SettingDefinition item) => new()
    {
        MinWidth = 140,
        SmallChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
    };

    private static Button BuildActionButton(SettingDefinition item) => new()
    {
        Content = item.Label,
        MinWidth = 132
    };

    private static UIElement BuildJobsPanel()
    {
        var examples = new[]
        {
            new AppJobSnapshot("runtime:sticker-rembg:Cpu", "Sticker runtime (CPU)", AppJobArea.Runtime, false, "Ready", true, null),
            new AppJobSnapshot("runtime:upscale-onnx:Gpu", "Upscale runtime (GPU)", AppJobArea.Runtime, false, "Ready", true, null),
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = "Current migration target",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = "The existing WPF app already persists runtime jobs. The next migration step is to feed those snapshots into this WinUI shell through a shared application layer.",
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
            Foreground = new SolidColorBrush(Colors.Gray)
        });

        foreach (var job in examples)
        {
            stack.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = job.Label, FontWeight = Microsoft.UI.Text.FontWeights.Medium },
                        new TextBlock { Text = job.Key, Foreground = new SolidColorBrush(Colors.Gray), FontSize = 12 },
                        new TextBlock { Text = $"Area: {job.Area}  Status: {job.Status}", Foreground = new SolidColorBrush(Colors.Gray), FontSize = 12 }
                    }
                }
            });
        }

        return new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private sealed record PageEntry(string Key, string Title, string Description, SettingsPageDefinition? SettingsPage);
}
