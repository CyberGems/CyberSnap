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
        public HistoryKind? MediaKind { get; init; }   // for Image/Media entries
        public string? FilePath { get; init; }          // for Image/Media entries
        public string? Text { get; init; }              // for OCR/Code entries
        public string? Format { get; init; }            // for Code entries
        public string? Hex { get; init; }               // for Color entries
        public bool IsImageOrMedia => MediaKind.HasValue;
        public bool IsOcr => Text is not null && Format is null && Hex is null;
        public bool IsCode => Format is not null;
        public bool IsColor => Hex is not null;
    }

    private List<UnifiedHistoryItem> _allUnifiedEntries = new();
    private int _allRenderCount;
    private DateTime? _allLastRenderedDate;

    private void LoadAllHistory()
    {
        var sw = Stopwatch.StartNew();
        HistoryStack.Children.Clear();
        HideHistoryEmptyState();

        // Merge all entry types into a single sorted list
        var unified = new List<UnifiedHistoryItem>();

        foreach (var img in _historyService.ImageEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = img.CapturedAt, MediaKind = img.Kind, FilePath = img.FilePath });

        foreach (var media in _historyService.MediaEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = media.CapturedAt, MediaKind = media.Kind, FilePath = media.FilePath });

        foreach (var ocr in _historyService.OcrEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = ocr.CapturedAt, Text = ocr.Text });

        foreach (var color in _historyService.ColorEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = color.CapturedAt, Hex = color.Hex });

        foreach (var code in _historyService.CodeEntries)
            unified.Add(new UnifiedHistoryItem { CapturedAt = code.CapturedAt, Text = code.Text, Format = code.Format });

        unified.Sort((a, b) => b.CapturedAt.CompareTo(a.CapturedAt)); // newest first
        _allUnifiedEntries = unified;

        if (unified.Count == 0)
        {
            ShowHistoryEmptyState("No captures yet", "Screenshots, OCR text, colors, and codes will appear here.");
            HistoryCountText.Text = "0 items";
            UpdateHistoryActionButtons();
            return;
        }

        HistoryCountText.Text = $"{unified.Count} item{(unified.Count == 1 ? "" : "s")}";
        _allRenderCount = Math.Min(HistoryInitialPageSize, unified.Count);
        _allLastRenderedDate = null;
        AppendAllPage(unified, 0, _allRenderCount);
        UpdateHistoryActionButtons();
        sw.Stop();
        AppDiagnostics.LogInfo("history.load-all", $"items={unified.Count} rendered={_allRenderCount} elapsedMs={sw.ElapsedMilliseconds}");
    }

    private void AllPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 360) return;
        AppendNextAllPage();
    }

    private void AppendNextAllPage()
    {
        if (_allRenderCount >= _allUnifiedEntries.Count) return;
        var prevOffset = ImagesPanel.VerticalOffset;
        var prevCount = _allRenderCount;
        _allRenderCount = Math.Min(_allRenderCount + HistoryAppendPageSize, _allUnifiedEntries.Count);
        AppendAllPage(_allUnifiedEntries, prevCount, _allRenderCount - prevCount);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 0)
                ImagesPanel.ScrollToVerticalOffset(prevOffset);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AppendAllPage(IReadOnlyList<UnifiedHistoryItem> entries, int start, int count)
    {
        var end = start + count;
        for (int i = start; i < end; i++)
        {
            var entry = entries[i];
            AppendSectionHeaderIfNeeded(HistoryStack, entry.CapturedAt.Date, ref _allLastRenderedDate);
            HistoryStack.Children.Add(CreateUnifiedCard(entry));
        }
    }

    private Border CreateUnifiedCard(UnifiedHistoryItem item)
    {
        if (item.IsImageOrMedia)
            return CreateUnifiedImageCard(item);
        if (item.IsOcr)
            return CreateUnifiedOcrCard(item);
        if (item.IsCode)
            return CreateUnifiedCodeCard(item);
        if (item.IsColor)
            return CreateUnifiedColorCard(item);
        return CreateUnifiedFallbackCard(item);
    }

    // ── Image / Media card ──

    private Border CreateUnifiedImageCard(UnifiedHistoryItem item)
    {
        var entry = _historyService.ImageEntries
            .FirstOrDefault(e => e.FilePath == item.FilePath && e.CapturedAt == item.CapturedAt)
            ?? (HistoryEntry?)_historyService.MediaEntries
            .FirstOrDefault(e => e.FilePath == item.FilePath && e.CapturedAt == item.CapturedAt);

        if (entry is null) return CreateUnifiedFallbackCard(item);

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

        // File name + time
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

    private Border CreateUnifiedOcrCard(UnifiedHistoryItem item)
    {
        var text = item.Text ?? "";
        var card = CreateBaseUnifiedCard("Text history item", "Copy this text");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top: large text icon on dark surface
        var iconArea = new Grid { Background = Theme.Brush(Theme.BgSecondary) };
        var iconText = new TextBlock
        {
            Text = "\uE8F1", // Segoe Fluent "Text" icon
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = 36,
            Foreground = Theme.Brush(Theme.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.5
        };
        iconArea.Children.Add(iconText);
        Grid.SetRow(iconArea, 0);
        root.Children.Add(iconArea);

        // Bottom: text preview + time
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        var preview = text.Length > 60 ? text[..60] + "..." : text;
        info.Children.Add(new TextBlock { Text = preview, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName) });
        info.Children.Add(new TextBlock { Text = FormatTimeAgo(item.CapturedAt), FontSize = 10, Opacity = 0.35, Margin = new Thickness(0, 4, 0, 0) });

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);

        var capturedText = text;
        card.Child = root;
        card.MouseLeftButtonDown += (_, e) => { e.Handled = true; CopyTextToClipboard(capturedText); };
        return card;
    }

    // ── Color card ──

    private Border CreateUnifiedColorCard(UnifiedHistoryItem item)
    {
        var hex = item.Hex ?? "000000";
        TryParseHexColor(hex, out var r, out var g, out var b);
        var swatchColor = System.Windows.Media.Color.FromRgb(r, g, b);
        var displayHex = FormatColorHexForDisplay(hex);

        var card = CreateBaseUnifiedCard($"Color {displayHex}", "Copy this color");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top: large color swatch
        var swatchArea = new Grid();
        var swatch = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(swatchColor),
            BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        swatchArea.Children.Add(swatch);
        Grid.SetRow(swatchArea, 0);
        root.Children.Add(swatchArea);

        // Bottom: hex + time
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        info.Children.Add(new TextBlock { Text = displayHex, FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName) });
        info.Children.Add(new TextBlock { Text = FormatTimeAgo(item.CapturedAt), FontSize = 10, Opacity = 0.35, Margin = new Thickness(0, 4, 0, 0) });

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);

        card.Child = root;
        card.MouseLeftButtonDown += (_, e) => { e.Handled = true; CopyColorToClipboard(hex); };
        return card;
    }

    // ── Code (QR/Barcode) card ──

    private readonly Dictionary<string, BitmapSource> _allCodePreviewCache = new();

    private Border CreateUnifiedCodeCard(UnifiedHistoryItem item)
    {
        var text = item.Text ?? "";
        var format = item.Format ?? "";
        var card = CreateBaseUnifiedCard($"{HumanizeBarcodeFormat(format)} history item", "Copy this code text");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top: barcode preview
        var previewArea = new Grid { Background = Brushes.White };
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

        // Bottom: format + time
        var info = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        info.Children.Add(new TextBlock { Text = HumanizeBarcodeFormat(format), FontSize = 11, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName) });
        info.Children.Add(new TextBlock { Text = FormatTimeAgo(item.CapturedAt), FontSize = 10, Opacity = 0.35, Margin = new Thickness(0, 4, 0, 0) });

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

    private Border CreateBaseUnifiedCard(string automationName, string tooltip)
    {
        var card = new Border
        {
            Width = HistoryCardPreferredWidth,
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
