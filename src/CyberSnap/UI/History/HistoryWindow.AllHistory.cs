using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace CyberSnap.UI;

public partial class HistoryWindow
{
    // ── Unified "All" history view ──

    private sealed class UnifiedHistoryItem
    {
        public DateTime CapturedAt { get; init; }
        public object RawEntry { get; init; } = null!;    // HistoryEntry, OcrHistoryEntry, ColorHistoryEntry, or CodeHistoryEntry
        public bool IsImageOrMedia => RawEntry is HistoryEntry;
        public bool IsOcr => RawEntry is OcrHistoryEntry;
        public bool IsCode => RawEntry is CodeHistoryEntry;
        public bool IsColor => RawEntry is ColorHistoryEntry;
    }

    private List<UnifiedHistoryItem> _allUnifiedEntries = new();

    private void LoadAllHistory()
    {
        var sw = Stopwatch.StartNew();
        HistoryStack.Children.Clear();
        HideHistoryEmptyState();

        var unified = new List<UnifiedHistoryItem>();

        foreach (var img in _historyService.ImageEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = img.CapturedAt, RawEntry = img });

        foreach (var media in _historyService.MediaEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = media.CapturedAt, RawEntry = media });

        foreach (var ocr in _historyService.OcrEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = ocr.CapturedAt, RawEntry = ocr });

        foreach (var color in _historyService.ColorEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = color.CapturedAt, RawEntry = color });

        foreach (var code in _historyService.CodeEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = code.CapturedAt, RawEntry = code });

        unified.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt));
        _allUnifiedEntries = unified;

        if (unified.Count == 0)
        {
            ShowHistoryEmptyState("No captures yet", "Screenshots, OCR text, colors, and codes will appear here.");
            HistoryCountText.Text = "0 items";
            UpdateHistoryActionButtons();
            return;
        }

        HistoryCountText.Text = $"{unified.Count} item{(unified.Count == 1 ? "" : "s")}";
        var pageSize = Math.Min(HistoryInitialPageSize, unified.Count);
        var page = unified.GetRange(0, pageSize);
        AppendGroupedUnifiedItems(HistoryStack, page, CreateUnifiedCard);
        _allLastAppendIndex = pageSize;
        UpdateHistoryActionButtons();
        sw.Stop();
        AppDiagnostics.LogInfo("history.load-all", $"items={unified.Count} rendered={pageSize} elapsedMs={sw.ElapsedMilliseconds}");
    }

    // ── Infinite scroll ──

    private int _allLastAppendIndex;

    private void AppendNextAllPage()
    {
        if (_allLastAppendIndex >= _allUnifiedEntries.Count) return;
        var prevOffset = ImagesPanel.VerticalOffset;
        var prevCount = _allLastAppendIndex;
        _allLastAppendIndex = Math.Min(_allLastAppendIndex + HistoryAppendPageSize, _allUnifiedEntries.Count);
        var added = _allUnifiedEntries.GetRange(prevCount, _allLastAppendIndex - prevCount);
        AppendGroupedUnifiedItems(HistoryStack, added, CreateUnifiedCard);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 0)
                ImagesPanel.ScrollToVerticalOffset(prevOffset);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Grouped grid rendering (WrapPanels + date pills, same as image history) ──

    private void AppendGroupedUnifiedItems(System.Windows.Controls.Panel target, IReadOnlyList<UnifiedHistoryItem> items, Func<UnifiedHistoryItem, Border> cardFactory)
    {
        WrapPanel? currentWrap = target.Children.Count > 0 ? target.Children[target.Children.Count - 1] as WrapPanel : null;
        DateTime? currentDate = currentWrap?.Tag is DateTime tagDate ? tagDate : null;
        var updatedWraps = new HashSet<WrapPanel>();

        foreach (var item in items)
        {
            var itemDate = item.CapturedAt.Date;
            if (currentWrap is null || currentDate != itemDate)
            {
                // Date separator line
                if (target.Children.Count > 0)
                {
                    target.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Theme.Brush(Theme.BorderSubtle),
                        Margin = new Thickness(6, 20, 6, 0)
                    });
                }

                // Date label pill
                var dateLabel = new TextBlock
                {
                    Text = FormatHistoryGroupLabel(itemDate).ToUpperInvariant(),
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
                    Foreground = Theme.Brush(Theme.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.9
                };
                target.Children.Add(new Border
                {
                    Background = Theme.Brush(Theme.AccentSubtle),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(6, 14, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = dateLabel
                });

                currentWrap = CreateHistoryWrapPanel(itemDate);
                target.Children.Add(currentWrap);
                currentDate = itemDate;
            }

            var card = cardFactory(item);
            currentWrap!.Children.Add(card);
            updatedWraps.Add(currentWrap);
        }

        foreach (var wrap in updatedWraps)
            UpdateHistoryWrapPanelCardWidths(wrap);
    }

    // ── Card factory ──

    private Border CreateUnifiedCard(UnifiedHistoryItem item)
    {
        if (item.IsImageOrMedia)
            return CreateUnifiedImageCard((HistoryEntry)item.RawEntry);
        if (item.IsOcr)
            return CreateUnifiedOcrCard((OcrHistoryEntry)item.RawEntry);
        if (item.IsCode)
            return CreateUnifiedCodeCard((CodeHistoryEntry)item.RawEntry);
        if (item.IsColor)
            return CreateUnifiedColorCard((ColorHistoryEntry)item.RawEntry);
        return CreateUnifiedFallbackCard(item);
    }

    // ── Image / Media card (reuses existing BuildMediaCardShell) ──

    private Border CreateUnifiedImageCard(HistoryEntry entry)
    {
        var vm = new HistoryItemVM();
        UpdateHistoryItemViewModel(vm, entry, isSelected: false, hydrateSearchMetadata: false);

        var shell = BuildMediaCardShell(vm, () =>
        {
            try
            {
                if (!System.IO.File.Exists(entry.FilePath)) { ShowHistoryFileMissingError(entry.FilePath); return; }
                using var bmp = BitmapPerf.LoadDetached(entry.FilePath);
                ClipboardService.CopyToClipboard(bmp);
                ToastWindow.Show("Copied", "Image copied");
            }
            catch (Exception ex) { ToastWindow.ShowError("Copy failed", ex.Message); }
        });

        // Type badge in the image area
        var badgeLabel = entry.Kind == HistoryKind.Video ? "VID"
            : entry.Kind == HistoryKind.Gif ? "GIF" : "IMG";
        var badgeColor = entry.Kind == HistoryKind.Video ? System.Windows.Media.Color.FromRgb(255, 100, 100)
            : entry.Kind == HistoryKind.Gif ? System.Windows.Media.Color.FromRgb(255, 180, 60) : System.Windows.Media.Color.FromRgb(140, 160, 255);
        AddTypeBadge(shell.ImageContainer, badgeLabel, badgeColor);

        // Add play icon overlay for videos (same as CreateVideoCard)
        if (entry.Kind == HistoryKind.Video)
        {
            var playIcon = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M8,5 L8,19 L19,12 Z"),
                    Fill = Brushes.White,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    Width = 14, Height = 14,
                    Margin = new Thickness(2, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };
            AutomationProperties.SetName(playIcon, "Video play overlay");
            shell.ImageContainer.Children.Add(playIcon);
        }

        var nameBlock = new TextBlock
        {
            Text = entry.FileName,
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        shell.InfoPanel.Children.Add(nameBlock);

        var timeBlock = new TextBlock
        {
            Text = vm.TimeAgo,
            FontSize = 10,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            Opacity = 0.3,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        shell.InfoPanel.Children.Add(timeBlock);

        shell.Card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!System.IO.File.Exists(entry.FilePath)) { ShowHistoryFileMissingError(entry.FilePath); return; }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = entry.FilePath, UseShellExecute = true }); }
            catch { }
        };

        return shell.Card;
    }

    // ── OCR Text card ──

    private Border CreateUnifiedOcrCard(OcrHistoryEntry entry)
    {
        var text = entry.Text ?? "";
        var card = CreateBaseUnifiedCard("Text history item", "Copy this text");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top: the actual text content (replaces the image thumbnail area)
        var textArea = new Grid { Background = Theme.Brush(Theme.BgSecondary), ClipToBounds = true, MaxWidth = HistoryCardPreferredWidth };
        AddTypeBadge(textArea, "TXT", System.Windows.Media.Color.FromRgb(100, 180, 255));
        var displayText = text.Length > 80 ? text[..80] + "…" : text;
        textArea.Children.Add(new TextBlock
        {
            Text = displayText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            Foreground = Theme.Brush(Theme.TextPrimary),
            Margin = new Thickness(10),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(textArea, 0);
        root.Children.Add(textArea);

        // Bottom: just the capture time
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        info.Children.Add(new TextBlock { Text = "Text", FontSize = 9, Opacity = 0.5, FontWeight = FontWeights.Bold });
        info.Children.Add(new TextBlock { Text = FormatTimeAgo(entry.CapturedAt), FontSize = 10, Opacity = 0.3, Margin = new Thickness(0, 2, 0, 0) });

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);

        var capturedText = text;
        card.Child = root;
        card.MouseLeftButtonDown += (_, e) => { e.Handled = true; CopyTextToClipboard(capturedText); };
        return card;
    }

    // ── Color card ──

    private Border CreateUnifiedColorCard(ColorHistoryEntry entry)
    {
        var hex = entry.Hex ?? "000000";
        TryParseHexColor(hex, out var r, out var g, out var b);
        var swatchColor = System.Windows.Media.Color.FromRgb(r, g, b);
        var displayHex = FormatColorHexForDisplay(hex);

        var card = CreateBaseUnifiedCard($"Color {displayHex}", "Copy this color");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var swatchArea = new Grid();
        AddTypeBadge(swatchArea, "CLR", System.Windows.Media.Color.FromRgb(255, 160, 80));
        swatchArea.Children.Add(new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(swatchColor),
            BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(swatchArea, 0);
        root.Children.Add(swatchArea);

        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        info.Children.Add(new TextBlock { Text = "Color", FontSize = 9, Opacity = 0.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        info.Children.Add(new TextBlock { Text = displayHex, FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName) });
        info.Children.Add(new TextBlock { Text = FormatTimeAgo(entry.CapturedAt), FontSize = 10, Opacity = 0.35, Margin = new Thickness(0, 4, 0, 0) });

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);

        card.Child = root;
        card.MouseLeftButtonDown += (_, e) => { e.Handled = true; CopyColorToClipboard(hex); };
        return card;
    }

    // ── Code (QR/Barcode) card ──

    private readonly Dictionary<string, BitmapSource> _allCodePreviewCache = new();

    private Border CreateUnifiedCodeCard(CodeHistoryEntry entry)
    {
        var text = entry.Text ?? "";
        var format = entry.Format ?? "";
        var card = CreateBaseUnifiedCard($"{HumanizeBarcodeFormat(format)} history item", "Copy this code text");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewArea = new Grid { Background = Brushes.White };
        AddTypeBadge(previewArea, "QR", System.Windows.Media.Color.FromRgb(120, 200, 120));
        var previewKey = $"{text}|{format}";
        if (!_allCodePreviewCache.TryGetValue(previewKey, out var previewSrc))
        {
            try
            {
                using var bmp = BarcodeService.RenderPreview(text, ParseBarcodeFormat(format));
                previewSrc = BitmapPerf.ToBitmapSource(bmp);
            }
            catch { previewSrc = null; }
            if (previewSrc is not null) _allCodePreviewCache[previewKey] = previewSrc;
        }

        var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(16), Source = previewSrc };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        previewArea.Children.Add(img);
        Grid.SetRow(previewArea, 0);
        root.Children.Add(previewArea);

        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        info.Children.Add(new TextBlock { Text = "Code", FontSize = 9, Opacity = 0.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        info.Children.Add(new TextBlock { Text = HumanizeBarcodeFormat(format), FontSize = 11, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName) });
        info.Children.Add(new TextBlock { Text = FormatTimeAgo(entry.CapturedAt), FontSize = 10, Opacity = 0.35, Margin = new Thickness(0, 4, 0, 0) });

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);

        var capturedText = text;
        card.Child = root;
        card.MouseLeftButtonDown += (_, e) => { e.Handled = true; CopyTextToClipboard(capturedText); };
        return card;
    }

    // ── Fallback card ──

    private Border CreateUnifiedFallbackCard(UnifiedHistoryItem item)
    {
        var card = CreateBaseUnifiedCard("History item", "");
        card.Child = new TextBlock { Text = FormatTimeAgo(item.CapturedAt), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        return card;
    }

    // ── Helpers ──

    private static void AddTypeBadge(Grid parent, string label, System.Windows.Media.Color color)
    {
        var pill = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName)
            }
        };
        parent.Children.Add(pill);
    }

    private Border CreateBaseUnifiedCard(string automationName, string tooltip)
    {
        var card = new Border
        {
            Width = HistoryCardPreferredWidth,  // initial width; UpdateHistoryWrapPanelCardWidths overrides
            MinWidth = HistoryCardMinWidth,
            MaxWidth = HistoryCardMaxWidth,
            Margin = new Thickness(HistoryCardMargin),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.BgCard),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = tooltip
        };
        AutomationProperties.SetName(card, automationName);
        AutomationProperties.SetHelpText(card, tooltip);

        card.MouseEnter += (_, _) => { card.Background = HistoryCardHoverBrush; card.BorderBrush = HistoryCardFocusBrush; };
        card.MouseLeave += (_, _) => { if (!card.IsKeyboardFocusWithin) { card.Background = Theme.Brush(Theme.BgCard); card.BorderBrush = Brushes.Transparent; } };
        card.GotKeyboardFocus += (_, _) => { card.Background = HistoryCardHoverBrush; card.BorderBrush = HistoryCardFocusBrush; };
        card.LostKeyboardFocus += (_, _) => { if (!card.IsMouseOver) { card.Background = Theme.Brush(Theme.BgCard); card.BorderBrush = Brushes.Transparent; } };

        return card;
    }

    private static void CopyTextToClipboard(string text)
    {
        try { ClipboardService.CopyTextToClipboard(text); ToastWindow.Show("Copied", "Text copied"); }
        catch (Exception ex) { ToastWindow.ShowError("Copy failed", ex.Message); }
    }

    private static void CopyColorToClipboard(string hex)
    {
        try { ClipboardService.CopyTextToClipboard(hex); ToastWindow.Show("Copied", $"{hex} copied"); }
        catch (Exception ex) { ToastWindow.ShowError("Copy failed", ex.Message); }
    }
}
