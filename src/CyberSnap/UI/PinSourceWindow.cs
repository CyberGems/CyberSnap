using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CyberSnap.UI;

public sealed class PinSourceWindow : Window
{
    private IntPtr _captureHandle;
    public event Action<string>? SourceRequested;
    public PinSourceWindow()
    {
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        Theme.ApplyTo(Application.Current.Resources);
        FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName);
        Title = LocalizationService.Translate("Pin to screen");
        Width = 620; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        CyberSnapWindowChrome.Apply(this);
        var panel = new StackPanel();
        var title = new CyberSnapTitleBar { Title = Title };
        title.CloseRequested += (_, _) => Close();
        panel.Children.Add(title);
        var body = new StackPanel { Margin = new Thickness(22, 12, 22, 22) };
        body.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate("Keep an image visible while you work."),
            Foreground = Theme.Brush(Theme.TextSecondary), FontSize = 14,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18)
        });
        var sources = new Grid();
        foreach (var _ in new[] { 0, 1, 2 }) sources.ColumnDefinitions.Add(new ColumnDefinition());
        var entries = new[] { ("region", "Select region...", "captureRect"), ("clipboard", "From clipboard", "paste"), ("file", "Select image...", "folder") };
        for (int i = 0; i < entries.Length; i++)
        {
            var (id, text, icon) = entries[i];
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new System.Windows.Shapes.Path
            {
                Data = FluentIcons.GetGeometry(icon), Fill = Theme.Brush(Theme.TextPrimary),
                Width = 25, Height = 25, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(new TextBlock { Text = LocalizationService.Translate(text), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            var button = new Button { Content = content, MinHeight = 105, Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0), Padding = new Thickness(10) };
            button.SetResourceReference(StyleProperty, "CyberSnapAccentButton");
            System.Windows.Automation.AutomationProperties.SetName(button, LocalizationService.Translate(text));
            button.Click += (_, _) => SourceRequested?.Invoke(id);
            Grid.SetColumn(button, i); sources.Children.Add(button);
        }
        body.Children.Add(sources); panel.Children.Add(body);
        Content = new Border
        {
            Background = Theme.Brush(Theme.BgPrimary), BorderBrush = Theme.Brush(Theme.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Child = panel
        };
        UiScale.ApplyToWindow(this, panel, scaleWindowBounds: true);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => PopupWindowHelper.CenterWindowOnPhysicalMonitor(this);
        SourceInitialized += (_, _) =>
        {
            _captureHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Capture.CaptureWindowExclusion.Apply(_captureHandle);
        };
        Closed += (_, _) => Capture.CaptureWindowExclusion.Unregister(_captureHandle);
    }
}
