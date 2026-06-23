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
    private List<UnifiedHistoryItem> _filteredUnifiedEntries = new();
    private readonly Dictionary<Border, object> _unifiedCardEntries = new();  // card → raw entry (OcrHistoryEntry, etc.)

    private void LoadAllHistory()
    {
        var sw = Stopwatch.StartNew();
        HistoryStack.Children.Clear();
        _unifiedCardEntries.Clear();
        HideHistoryEmptyState();

        // Clear and rebuild image cache so switching to Images is instant
        _allHistoryItems.Clear();
        _allHistoryItemsByPath.Clear();
        _filteredHistoryItems.Clear();

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

        // Mark image cache ready so Images tab is instant
        _allImageHistoryEntries = unified
            .Where(i => i.IsImageOrMedia)
            .Select(i => (HistoryEntry)i.RawEntry)
            .ToList();
        _historyImageCacheReady = true;
        PrimeHistoryFingerprint();

        if (string.IsNullOrWhiteSpace(_imageSearchQuery))
        {
            _filteredUnifiedEntries = _allUnifiedEntries;
            RenderUnifiedHistoryItems();
        }
        else
        {
            ApplyImageSearchFilter();
        }

        sw.Stop();
        AppDiagnostics.LogInfo("history.load-all", $"items={unified.Count} elapsedMs={sw.ElapsedMilliseconds}");
    }

    private void ApplyUnifiedFilter(string query, HashSet<string> matchingImagePaths)
    {
        var terms = SplitHistorySearchTerms(query);
        var filtered = new List<UnifiedHistoryItem>();

        foreach (var item in _allUnifiedEntries)
        {
            if (item.IsImageOrMedia)
            {
                var img = (HistoryEntry)item.RawEntry;
                if (matchingImagePaths.Contains(img.FilePath))
                    filtered.Add(item);
            }
            else if (item.IsOcr)
            {
                var ocr = (OcrHistoryEntry)item.RawEntry;
                if (GetOcrSearchText(ocr).Contains(query, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(item);
            }
            else if (item.IsColor)
            {
                var color = (ColorHistoryEntry)item.RawEntry;
                if (ColorMatchesCachedTerms(color, terms))
                    filtered.Add(item);
            }
            else if (item.IsCode)
            {
                var code = (CodeHistoryEntry)item.RawEntry;
                if (CodeMatchesCachedTerms(code, terms))
                    filtered.Add(item);
            }
        }

        _filteredUnifiedEntries = filtered;
        RenderUnifiedHistoryItems();
    }

    private void RenderUnifiedHistoryItems()
    {
        HistoryStack.Children.Clear();
        _unifiedCardEntries.Clear();

        long visibleBytes = 0;
        foreach (var item in _filteredUnifiedEntries)
        {
            if (item.IsImageOrMedia)
            {
                var img = (HistoryEntry)item.RawEntry;
                visibleBytes += img.FileSizeBytes;
            }
        }

        var sizeStr = FormatStorageSize(visibleBytes);
        var totalCount = _allUnifiedEntries.Count;
        var usingSearch = !string.IsNullOrWhiteSpace(_imageSearchQuery);

        if (usingSearch)
        {
            HistoryCountText.Text = $"{_filteredUnifiedEntries.Count} {LocalizationService.Translate("search matches")} · {sizeStr}";
        }
        else
        {
            var loadedCount = _filteredUnifiedEntries.Count;
            var loadedPrefix = totalCount > loadedCount
                ? $"{loadedCount} {LocalizationService.Translate("of")} {totalCount} {LocalizationService.Translate("items loaded")}"
                : $"{loadedCount} {LocalizationService.Translate(loadedCount == 1 ? "item" : "items")}";
            HistoryCountText.Text = $"{loadedPrefix} · {sizeStr}";
        }

        if (_filteredUnifiedEntries.Count == 0)
        {
            if (usingSearch)
                ShowHistoryEmptyState("No items match your search", "Search matched 0 history items.");
            else
                ShowHistoryEmptyState("No captures yet", "Screenshots, OCR text, colors, and codes will appear here.");
        }
        else
        {
            HideHistoryEmptyState();
        }

        var pageSize = Math.Min(HistoryInitialPageSize, _filteredUnifiedEntries.Count);
        var page = _filteredUnifiedEntries.GetRange(0, pageSize);
        AppendGroupedUnifiedItems(HistoryStack, page, CreateUnifiedCard);
        _allLastAppendIndex = pageSize;
        UpdateHistoryActionButtons();
    }

    // ── Infinite scroll ──

    private int _allLastAppendIndex;

    private void AppendNextAllPage()
    {
        if (_allLastAppendIndex >= _filteredUnifiedEntries.Count) return;
        var prevOffset = ImagesPanel.VerticalOffset;
        var prevCount = _allLastAppendIndex;
        _allLastAppendIndex = Math.Min(_allLastAppendIndex + HistoryAppendPageSize, _filteredUnifiedEntries.Count);
        var added = _filteredUnifiedEntries.GetRange(prevCount, _allLastAppendIndex - prevCount);
        AppendGroupedUnifiedItems(HistoryStack, added, CreateUnifiedCard);
        // Update fingerprint after appending more items
        PrimeHistoryFingerprint();
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
                        Margin = new Thickness(6, 26, 6, 0)
                    });
                }

                // Date label pill
                var dateLabel = new TextBlock
                {
                    Text = FormatHistoryGroupLabel(itemDate).ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
                    Foreground = Theme.Brush(Theme.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.9
                };
                target.Children.Add(new Border
                {
                    Background = Theme.Brush(Theme.AccentSubtle),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(6, 18, 0, 12),
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

        // Register VM so Images view can reuse cached thumbnails
        _allHistoryItems.Add(vm);
        if (!string.IsNullOrEmpty(entry.FilePath))
            _allHistoryItemsByPath[entry.FilePath] = vm;

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

        var badgeLabel = entry.Kind == HistoryKind.Video ? "VID"
            : entry.Kind == HistoryKind.Gif ? "GIF" : "IMG";
        var badgeColor = entry.Kind == HistoryKind.Video ? System.Windows.Media.Color.FromRgb(255, 100, 100)
            : entry.Kind == HistoryKind.Gif ? System.Windows.Media.Color.FromRgb(255, 180, 60) : System.Windows.Media.Color.FromRgb(100, 180, 255);

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

        shell.InfoPanel.Children.Add(CreateBadgeTimeText(badgeLabel, badgeColor, vm.TimeAgo));

        AddCategoryTint(shell.Root, badgeColor);

        return shell.Card;
    }

    // ── OCR Text card ──

    private Border CreateUnifiedOcrCard(OcrHistoryEntry entry)
    {
        var text = entry.Text ?? "";
        var card = CreateBaseUnifiedCard("Text history item", "Copy this OCR text");
        _unifiedCardEntries[card] = entry;

        var root = new Grid();
        var imageRow = new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) };
        root.RowDefinitions.Add(imageRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top: the actual text content (replaces the image thumbnail area)
        var textArea = new Grid { Background = Theme.Brush(Theme.BgSecondary), ClipToBounds = true, MaxWidth = HistoryCardPreferredWidth };
        var selBadge = CreateUnifiedSelectionBadge();
        textArea.Children.Add(selBadge);
        var displayText = text.Length > 80 ? text[..80] + "…" : text;
        var ocrTextBlock = new TextBlock
        {
            Text = displayText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = Theme.Brush(Theme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Top
        };
        var ocrContainer = new Border
        {
            Background = Theme.Brush(Theme.IsDark ? Theme.BgElevated : Theme.BgPrimary),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(4),
            Margin = new System.Windows.Thickness(8, 6, 8, 8),
            Padding = new System.Windows.Thickness(8, 6, 8, 6),
            Child = ocrTextBlock
        };
        // Add container BEFORE AttachCardMenu so the action button sits on top (Z-order)
        textArea.Children.Add(ocrContainer);
        AttachCardMenu(card, root, () => CopyTextToClipboard(text), () => DeleteOcrEntry(entry), System.Windows.Media.Color.FromRgb(80, 190, 180));
        Grid.SetRow(textArea, 0);
        root.Children.Add(textArea);

        // Bottom: just the capture time
        var info = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        info.Children.Add(new TextBlock { Text = LocalizationService.Translate("OCR Text"), FontSize = 11, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName), TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(CreateBadgeTimeText("OCR", System.Windows.Media.Color.FromRgb(80, 190, 180), FormatTimeAgo(entry.CapturedAt)));

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        infoBorder.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; };
        infoBorder.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);
        AddCategoryTint(root, System.Windows.Media.Color.FromRgb(80, 190, 180), alphaOverride: Theme.IsDark ? (byte)40 : (byte)55);

        var capturedText = text;
        card.Child = root;
        SetupUnifiedCardHoverAndClip(card, root, imageRow);
        textArea.ToolTip = LocalizationService.Translate("Copy this OCR text");
        textArea.Cursor = Cursors.Hand;
        textArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            e.Handled = true;
            if (_selectMode)
            {
                var selected = card.Tag is not true;
                card.Tag = selected;
                UpdateUnifiedCardSelectionVisual(card, selected);
                UpdateHistoryActionButtons();
                return;
            }
            CopyTextToClipboard(capturedText);
        };
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
        _unifiedCardEntries[card] = entry;

        var root = new Grid();
        var imageRow = new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) };
        root.RowDefinitions.Add(imageRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var swatchArea = new Grid { MaxWidth = HistoryCardPreferredWidth };
        var selBadge = CreateUnifiedSelectionBadge();
        swatchArea.Children.Add(selBadge);
        AttachCardMenu(card, root, () => CopyColorToClipboard(hex), () => DeleteColorEntry(entry), System.Windows.Media.Color.FromRgb(255, 160, 80));
        swatchArea.Children.Add(new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(swatchColor),
            BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(swatchArea, 0);
        root.Children.Add(swatchArea);

        var info = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        // Single line: "Color  #FF8844"
        info.Children.Add(new TextBlock
        {
            Text = $"Color  {displayHex}",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(CreateBadgeTimeText("CLR", System.Windows.Media.Color.FromRgb(255, 160, 80), FormatTimeAgo(entry.CapturedAt)));

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        infoBorder.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; };
        infoBorder.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);
        AddCategoryTint(root, System.Windows.Media.Color.FromRgb(255, 160, 80));

        card.Child = root;
        SetupUnifiedCardHoverAndClip(card, root, imageRow);
        swatchArea.ToolTip = LocalizationService.Translate("Copy this color");
        swatchArea.Cursor = Cursors.Hand;
        swatchArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            e.Handled = true;
            if (_selectMode)
            {
                var selected = card.Tag is not true;
                card.Tag = selected;
                UpdateUnifiedCardSelectionVisual(card, selected);
                UpdateHistoryActionButtons();
                return;
            }
            CopyColorToClipboard(hex);
        };
        return card;
    }

    // ── Code (QR & Barcode) card ──

    private readonly Dictionary<string, BitmapSource> _allCodePreviewCache = new();

    private Border CreateUnifiedCodeCard(CodeHistoryEntry entry)
    {
        var text = entry.Text ?? "";
        var format = entry.Format ?? "";
        var card = CreateBaseUnifiedCard($"{HumanizeBarcodeFormat(format)} history item", "Copy this QR & Barcode text");
        _unifiedCardEntries[card] = entry;

        var root = new Grid();
        var imageRow = new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) };
        root.RowDefinitions.Add(imageRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewArea = new Grid { Background = Brushes.White, MaxWidth = HistoryCardPreferredWidth };
        var selBadge = CreateUnifiedSelectionBadge();
        previewArea.Children.Add(selBadge);
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
        previewArea.Children.Add(img);  // add image BEFORE AttachCardMenu so button is on top
        AttachCardMenu(card, root, () => CopyTextToClipboard(text), () => DeleteCodeEntry(entry), System.Windows.Media.Color.FromRgb(176, 136, 240));
        Grid.SetRow(previewArea, 0);
        root.Children.Add(previewArea);

        var info = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        // Single line: "Code  QR Code" (type + format)
        info.Children.Add(new TextBlock
        {
            Text = $"Code  {HumanizeBarcodeFormat(format)}",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(CreateBadgeTimeText("QR", System.Windows.Media.Color.FromRgb(176, 136, 240), FormatTimeAgo(entry.CapturedAt)));

        var infoBorder = new Border { BorderBrush = Theme.Brush(Theme.BorderSubtle), BorderThickness = new Thickness(0, 1, 0, 0), Background = Theme.Brush(Theme.BgSecondary), Child = info };
        infoBorder.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; };
        infoBorder.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);
        AddCategoryTint(root, System.Windows.Media.Color.FromRgb(176, 136, 240));

        var capturedText = text;
        card.Child = root;
        SetupUnifiedCardHoverAndClip(card, root, imageRow);
        var translatedCodeTooltip = LocalizationService.Translate("Copy this QR & Barcode text");
        previewArea.ToolTip = translatedCodeTooltip;
        previewArea.Cursor = Cursors.Hand;
        previewArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            e.Handled = true;
            if (_selectMode)
            {
                var selected = card.Tag is not true;
                card.Tag = selected;
                UpdateUnifiedCardSelectionVisual(card, selected);
                UpdateHistoryActionButtons();
                return;
            }
            CopyTextToClipboard(capturedText);
        };
        return card;
    }

    // ── Fallback card ──

    private Border CreateUnifiedFallbackCard(UnifiedHistoryItem item)
    {
        var card = CreateBaseUnifiedCard("History item", "");
        card.Child = new TextBlock { Text = FormatTimeAgo(item.CapturedAt), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        return card;
    }

    private static void AddCategoryTint(Grid root, System.Windows.Media.Color accentColor, byte? alphaOverride = null)
    {
        var alpha = alphaOverride ?? (Theme.IsDark ? (byte)28 : (byte)40);
        var overlay = new Border
        {
            IsHitTestVisible = false,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, accentColor.R, accentColor.G, accentColor.B)),
            CornerRadius = new CornerRadius(0, 0, 8, 8)
        };
        Grid.SetRow(overlay, 1);
        root.Children.Add(overlay);
    }

    // ── Helpers ──

    private static TextBlock CreateBadgeTimeText(string badgeLabel, System.Windows.Media.Color badgeColor, string timeAgo)
    {
        var tb = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        tb.Inlines.Add(new System.Windows.Documents.Run
        {
            Text = badgeLabel,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, badgeColor.R, badgeColor.G, badgeColor.B)),
            FontWeight = FontWeights.Bold,
            FontSize = 10
        });
        tb.Inlines.Add(new System.Windows.Documents.Run
        {
            Text = $" \u00B7 {timeAgo}",
            FontSize = 10,
            Foreground = Theme.Brush(Theme.IsDark
                ? System.Windows.Media.Color.FromArgb(76, 255, 255, 255)
                : System.Windows.Media.Color.FromArgb(100, 0, 0, 0))
        });
        return tb;
    }

    /// <summary>Creates a centered checkmark badge for select mode (same style as image cards).</summary>
    private static Border CreateUnifiedSelectionBadge()
    {
        var checkPath = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M6,14 L11,19 L22,8"),
            Stroke = Brushes.White,
            StrokeThickness = 2.6,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8),
            Visibility = Visibility.Hidden
        };

        var badge = new Border
        {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = checkPath,
            Tag = checkPath  // for easy access to the checkmark
        };
        System.Windows.Controls.Panel.SetZIndex(badge, 20);
        return badge;
    }

    /// <summary>Updates a unified card's selection badge and Tag to match selection state.</summary>
    private static void UpdateUnifiedCardSelectionVisual(Border card, bool selected)
    {
        if (card.Child is not Grid root) return;
        // Find the selection badge in the card's visual tree
        var badge = FindUnifiedSelectionBadge(root);
        if (badge is null) return;
        var checkPath = badge.Tag as UIElement;
        badge.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (checkPath is not null)
            checkPath.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
    }

    private static Border? FindUnifiedSelectionBadge(Grid root)
    {
        foreach (var child in root.Children)
        {
            if (child is Border b && System.Windows.Controls.Panel.GetZIndex(b) == 20)
                return b;
            if (child is Grid g)
            {
                var found = FindUnifiedSelectionBadge(g);
                if (found is not null) return found;
            }
        }
        return null;
    }

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
            BorderBrush = Theme.Brush(Theme.IsDark ? Theme.BorderSubtle : Theme.Border),
            BorderThickness = new Thickness(1),
            Focusable = true,
        };
        AutomationProperties.SetName(card, automationName);

        card.Tag = false;

        return card;
    }

    /// <summary>Adds a hover overlay border and rounded-corner clip, matching image card behavior.</summary>
    private void SetupUnifiedCardHoverAndClip(Border card, Grid root, RowDefinition imageRow)
    {
        var hoverBorder = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            IsHitTestVisible = false,
            Background = System.Windows.Media.Brushes.Transparent
        };
        Grid.SetRow(hoverBorder, 0);
        Grid.SetRowSpan(hoverBorder, 2);
        root.Children.Add(hoverBorder);

        card.SizeChanged += (_, _) =>
        {
            imageRow.Height = new GridLength(GetHistoryCardImageHeight(card.ActualWidth));
            card.Clip = new System.Windows.Media.RectangleGeometry(
                new System.Windows.Rect(0, 0, card.ActualWidth, card.ActualHeight), 8.5, 8.5);
        };

        card.MouseEnter += (_, _) =>
        {
            card.Background = HistoryCardHoverBrush;
            hoverBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        };
        card.MouseLeave += (_, _) =>
        {
            if (!card.IsKeyboardFocusWithin)
            {
                card.Background = Theme.Brush(Theme.BgCard);
                hoverBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
        };
        card.GotKeyboardFocus += (_, _) =>
        {
            card.Background = HistoryCardHoverBrush;
            hoverBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        };
        card.LostKeyboardFocus += (_, _) =>
        {
            if (!card.IsMouseOver)
            {
                card.Background = Theme.Brush(Theme.BgCard);
                hoverBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
        };
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

    // ── Hover action menu (matches existing card menu style) ──

    private void AttachCardMenu(Border card, Grid rootGrid, Action onCopy, Action? onDelete = null, System.Windows.Media.Color? badgeColor = null)
    {
        var menu = CreateCardActionMenu();
        menu.Items.Add(CreateCardActionMenuItem("Copy", onCopy, null, "copy"));
        if (onDelete is not null)
            menu.Items.Add(CreateCardActionMenuItem("Delete", onDelete, null, "trash", danger: true));

        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            menu.IsOpen = true;
        };

        var defaultChevronBrush = new SolidColorBrush(Theme.IsDark
            ? System.Windows.Media.Color.FromArgb(80, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(80, 0, 0, 0));
        var badgeHoverBrush = badgeColor.HasValue ? new SolidColorBrush(badgeColor.Value) : defaultChevronBrush;
        var chevronHoverBg = new SolidColorBrush(Theme.IsDark
            ? System.Windows.Media.Color.FromArgb(40, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(40, 0, 0, 0));
        var chevronIdleBg = new SolidColorBrush(Theme.IsDark
            ? System.Windows.Media.Color.FromArgb(12, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(12, 0, 0, 0));

        var chevronPath = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 0 0 L 6 0 L 3 4.5 Z"),
            Fill = defaultChevronBrush,
            Width = 7, Height = 5,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new Border
        {
            Width = 20, Height = 18,
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0),
            Cursor = Cursors.Hand,
            IsHitTestVisible = true,
            Visibility = Visibility.Collapsed,
            Child = chevronPath,
            ToolTip = new System.Windows.Controls.ToolTip { Content = LocalizationService.Translate("Actions") }
        };
        System.Windows.Controls.Panel.SetZIndex(chevron, 999);

        bool chevronHovered = false;

        void UpdateChevronVisibility()
        {
            if (menu.IsOpen)
            {
                chevron.Visibility = Visibility.Visible;
                chevron.Background = chevronHoverBg;
                chevronPath.Fill = badgeHoverBrush;
                return;
            }

            if (card.IsMouseOver || card.IsKeyboardFocusWithin)
            {
                chevron.Visibility = Visibility.Visible;
                if (chevronHovered)
                {
                    chevron.Background = chevronHoverBg;
                    chevronPath.Fill = badgeHoverBrush;
                }
                else
                {
                    chevron.Background = chevronIdleBg;
                    chevronPath.Fill = defaultChevronBrush;
                }
            }
            else
            {
                chevron.Visibility = Visibility.Collapsed;
                chevron.Background = Brushes.Transparent;
                chevronPath.Fill = defaultChevronBrush;
                chevronHovered = false;
            }
        }

        void DismissChevronToolTip()
        {
            if (chevron.ToolTip is System.Windows.Controls.ToolTip tt && tt.IsOpen)
                tt.IsOpen = false;
        }

        chevron.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            DismissChevronToolTip();
            if (menu.IsOpen)
            {
                menu.IsOpen = false;
                UpdateChevronVisibility();
            }
            else
            {
                menu.PlacementTarget = chevron;
                menu.IsOpen = true;
                UpdateChevronVisibility();
            }
            chevron.Background = new SolidColorBrush(Theme.IsDark
                ? System.Windows.Media.Color.FromArgb(60, 255, 255, 255)
                : System.Windows.Media.Color.FromArgb(60, 0, 0, 0));
        };
        chevron.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
        };
        chevron.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Space) { e.Handled = true; if (menu.IsOpen) { menu.IsOpen = false; } else { menu.IsOpen = true; } } };
        chevron.GotKeyboardFocus += (_, _) => UpdateChevronVisibility();
        chevron.LostKeyboardFocus += (_, _) => UpdateChevronVisibility();
        chevron.MouseEnter += (_, _) => { chevronHovered = true; UpdateChevronVisibility(); };
        chevron.MouseLeave += (_, _) => { chevronHovered = false; UpdateChevronVisibility(); };
        menu.Closed += (_, _) => UpdateChevronVisibility();
        card.MouseEnter += (_, _) => UpdateChevronVisibility();
        card.MouseLeave += (_, _) => UpdateChevronVisibility();

        Grid.SetRow(chevron, 1);
        rootGrid.Children.Add(chevron);
    }

    private void DeleteOcrEntry(OcrHistoryEntry entry)
    {
        _historyService.DeleteOcrEntry(entry);
        LoadAllHistory();
    }

    private void DeleteColorEntry(ColorHistoryEntry entry)
    {
        _historyService.DeleteColorEntry(entry);
        LoadAllHistory();
    }

    private void DeleteCodeEntry(CodeHistoryEntry entry)
    {
        _historyService.DeleteCodeEntry(entry);
        LoadAllHistory();
    }
}
