using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace CyberSnap.UI;

/// <summary>
/// CyberPaste-style category tab strip (icon + label + count pills).
/// The collapsed ComboBox stays the selection state holder so the existing
/// SelectionChanged flow (persist filter, reset selection, load) is untouched.
/// </summary>
public partial class HistoryWindow
{
    private static readonly (string IconId, string LabelKey, string PluralKey)[] GalleryTabs =
    [
        ("history", "All", "history items"),
        ("camera", "Images", "screenshots"),
        ("filmstrip", "Videos/GIFs", "videos & GIFs"),
        ("ocr", "Text", "text captures"),
        ("picker", "Colors", "colors"),
        ("scan", "QR & Barcodes", "QR & Barcode scans"),
    ];

    private readonly List<Button> _galleryTabButtons = new();

    private void BuildGalleryTabs()
    {
        GalleryTabStrip.Children.Clear();
        _galleryTabButtons.Clear();

        for (int i = 0; i < GalleryTabs.Length; i++)
        {
            var index = i;
            var btn = new Button
            {
                Style = (Style)FindResource("GalleryTabButton"),
                Tag = index,
                Focusable = true,
            };
            btn.Click += GalleryTab_Click;
            btn.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Left && e.Key != Key.Right)
                    return;
                var next = (index + (e.Key == Key.Left ? -1 : 1) + GalleryTabs.Length) % GalleryTabs.Length;
                _galleryTabButtons[next].Focus();
                e.Handled = true;
            };
            // Hover only for inactive tabs; the active pill keeps its accent fill.
            btn.MouseEnter += (_, _) =>
            {
                if (HistoryCategoryCombo.SelectedIndex != index)
                    btn.Background = Theme.Brush(Theme.TabHoverBg);
            };
            btn.MouseLeave += (_, _) => RefreshGalleryTabVisual(index);
            _galleryTabButtons.Add(btn);
            GalleryTabStrip.Children.Add(btn);
        }

        UpdateGalleryTabCounts();
    }

    private void GalleryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index)
            return;
        if (HistoryCategoryCombo.SelectedIndex == index)
            return;
        // Drives the existing flow: persist filter, reset selection, reload.
        HistoryCategoryCombo.SelectedIndex = index;
    }

    private bool IsGalleryTabActive(int index)
        => HistoryCategoryCombo != null && HistoryCategoryCombo.SelectedIndex == index;

    private void UpdateGalleryTabCounts()
    {
        if (_galleryTabButtons.Count == 0)
            return;

        var lang = _settingsService.Settings.InterfaceLanguage;
        var counts = new[]
        {
            _historyService.ImageEntries.Count
                + _historyService.MediaEntries.Count
                + _historyService.OcrEntries.Count
                + _historyService.ColorEntries.Count
                + _historyService.CodeEntries.Count,
            _historyService.ImageEntries.Count,
            _historyService.MediaEntries.Count,
            _historyService.OcrEntries.Count,
            _historyService.ColorEntries.Count,
            _historyService.CodeEntries.Count,
        };

        for (int i = 0; i < GalleryTabs.Length; i++)
        {
            var btn = _galleryTabButtons[i];
            var (iconId, labelKey, pluralKey) = GalleryTabs[i];
            var active = IsGalleryTabActive(i);
            var label = LocalizationService.Translate(lang, labelKey);
            var count = counts[i];
            var plural = LocalizationService.Translate(lang, pluralKey);

            var fg = active ? Theme.Brush(Theme.Accent) : Theme.Brush(Theme.TextSecondary);
            var iconColor = System.Drawing.Color.FromArgb(fg.Color.A, fg.Color.R, fg.Color.G, fg.Color.B);

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Image
            {
                Source = FluentIcons.RenderWpf(iconId, iconColor, 14),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily(UiChrome.PreferredFamilyName),
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = count.ToString(),
                FontSize = 11,
                FontFamily = new FontFamily(UiChrome.PreferredFamilyName),
                Foreground = fg,
                Opacity = active ? 0.8 : 0.5,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            btn.Content = row;

            RefreshGalleryTabVisual(i);

            var tooltip = $"{count} {plural}";
            btn.ToolTip = tooltip;
            AutomationProperties.SetName(btn, $"{label}, {tooltip}");
            AutomationProperties.SetHelpText(btn, tooltip);
        }
    }

    private void RefreshGalleryTabVisual(int index)
    {
        if (index < 0 || index >= _galleryTabButtons.Count)
            return;

        // Subtle active state: translucent accent pill + accent text (same language as the
        // ComboBoxItem selected style), not a solid glowing fill.
        var btn = _galleryTabButtons[index];
        btn.Background = IsGalleryTabActive(index)
            ? Theme.Brush(Theme.AccentSubtle)
            : System.Windows.Media.Brushes.Transparent;
    }
}
