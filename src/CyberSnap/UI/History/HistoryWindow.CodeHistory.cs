using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Services;
using ZXing;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;

namespace CyberSnap.UI;

public partial class HistoryWindow
{
    private string _codeSearchQuery = "";
    private List<CodeHistoryEntry> _filteredCodeEntries = new();
    private int _codeRenderCount;

    private readonly Dictionary<CodeHistoryEntry, Border> _codeHistoryCardCache = new();
    private readonly Dictionary<CodeHistoryEntry, BitmapSource> _codePreviewCache = new();
    private readonly System.Windows.Threading.DispatcherTimer _codeSearchDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };

    private void LoadCodeHistory()
    {
        var sw = Stopwatch.StartNew();
        CodeStack.Children.Clear();

        var allEntries = _historyService.CodeEntries;
        PruneCodeSearchCache(allEntries);

        var query = _codeSearchQuery.Trim();
        var queryTerms = SplitHistorySearchTerms(query);
        List<CodeHistoryEntry> entries = string.IsNullOrWhiteSpace(query)
            ? new List<CodeHistoryEntry>(allEntries)
            : allEntries.Where(entry => CodeMatchesCachedTerms(entry, queryTerms)).ToList();

        if (entries.Count == 0)
        {
            if (allEntries.Count == 0)
                ShowHistoryEmptyState("No QR & Barcode scans yet", "Scanned codes will appear here.");
            else
                ShowHistoryEmptyState("No QR & Barcode scans match your search", "Search matched 0 saved codes.");
        }
        else
        {
            HideHistoryEmptyState();
        }
        HistoryCountText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{entries.Count} {LocalizationService.Translate(entries.Count == 1 ? "code" : "codes")}"
            : $"{entries.Count} {LocalizationService.Translate("of")} {allEntries.Count} {LocalizationService.Translate(allEntries.Count == 1 ? "code" : "codes")}";
        _filteredCodeEntries = entries;
        _codeRenderCount = Math.Min(HistoryInitialPageSize, _filteredCodeEntries.Count);

        AppendCodeHistoryEntries(_filteredCodeEntries, 0, _codeRenderCount);
        UpdateHistoryActionButtons();
        sw.Stop();
        AppDiagnostics.LogInfo(
            "history.load-codes",
            $"items={_filteredCodeEntries.Count} rendered={_codeRenderCount} query={!string.IsNullOrWhiteSpace(query)} elapsedMs={sw.ElapsedMilliseconds}");
    }

    private void CodesPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 360) return;
        AppendNextCodeHistoryPage();
    }

    private void AppendNextCodeHistoryPage()
    {
        if (_codeRenderCount >= _filteredCodeEntries.Count)
            return;

        var previousOffset = CodesPanel.VerticalOffset;
        var previousCount = _codeRenderCount;
        _codeRenderCount = Math.Min(_codeRenderCount + HistoryAppendPageSize, _filteredCodeEntries.Count);
        AppendCodeHistoryEntries(_filteredCodeEntries, previousCount, _codeRenderCount - previousCount);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 5)
                CodesPanel.ScrollToVerticalOffset(previousOffset);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushCodeSearchDebounce(object? sender, EventArgs e)
    {
        _codeSearchDebounceTimer.Stop();
        if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 5)
            LoadCodeHistory();
    }

    private void AppendCodeHistoryEntries(IReadOnlyList<CodeHistoryEntry> entries, int start, int count)
    {
        var end = start + count;
        WrapPanel? currentWrap = CodeStack.Children.Count > 0
            ? CodeStack.Children[CodeStack.Children.Count - 1] as WrapPanel
            : null;
        DateTime? currentDate = currentWrap?.Tag is DateTime tagDate ? tagDate : null;
        var updatedWraps = new HashSet<WrapPanel>();

        for (int i = start; i < end; i++)
        {
            var entry = entries[i];
            var itemDate = entry.CapturedAt.Date;
            if (currentWrap is null || currentDate != itemDate)
            {
                // Date separator line
                if (CodeStack.Children.Count > 0)
                {
                    CodeStack.Children.Add(new Border
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
                CodeStack.Children.Add(new Border
                {
                    Background = Theme.Brush(Theme.AccentSubtle),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(6, 18, 0, 12),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Child = dateLabel
                });

                currentWrap = CreateHistoryWrapPanel(itemDate);
                CodeStack.Children.Add(currentWrap);
                currentDate = itemDate;
            }

            var card = GetOrCreateCodeHistoryCard(entry);
            currentWrap!.Children.Add(card);
            updatedWraps.Add(currentWrap);
        }

        foreach (var wrap in updatedWraps)
            UpdateHistoryWrapPanelCardWidths(wrap);
    }

    private Border GetOrCreateCodeHistoryCard(CodeHistoryEntry entry)
    {
        if (_codeHistoryCardCache.TryGetValue(entry, out var existing))
        {
            DetachElementFromParent(existing);
            if (!_selectMode)
                existing.Tag = null;
            RefreshSelectableCardSelection(existing);
            return existing;
        }

        var card = CreateCodeHistoryCard(entry);
        _codeHistoryCardCache[entry] = card;
        return card;
    }

    private Border CreateCodeHistoryCard(CodeHistoryEntry entry)
    {
        var text = entry.Text ?? "";
        var format = entry.Format ?? "";
        var card = CreateBaseUnifiedCard($"{HumanizeBarcodeFormat(format)} history item", "Copy this QR & Barcode text");
        card.DataContext = entry;
        card.Tag = null;

        var root = new Grid();
        var imageRow = new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) };
        root.RowDefinitions.Add(imageRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewArea = new Grid { Background = Brushes.White, MaxWidth = HistoryCardPreferredWidth };
        var selBadge = CreateSelectionBadge(false);
        previewArea.Children.Add(selBadge);
        
        var previewSrc = GetOrCreateCodePreview(entry);

        var img = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, Margin = new Thickness(16), Source = previewSrc };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        previewArea.Children.Add(img);  // add image BEFORE AttachCardMenu so button is on top
        AttachCardMenu(card, root, () => { ClipboardService.CopyTextToClipboard(text); ToastWindow.Show("Copied", "Text copied"); }, () => DeleteCodeEntryFromCodesTab(entry), System.Windows.Media.Color.FromRgb(176, 136, 240));
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
        SetupUnifiedCardHoverAndClip(card, root, imageRow, System.Windows.Media.Color.FromRgb(176, 136, 240));
        var translatedCodeTooltip = LocalizationService.Translate("Copy this QR & Barcode text");
        previewArea.ToolTip = translatedCodeTooltip;
        previewArea.Cursor = System.Windows.Input.Cursors.Hand;
        previewArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            e.Handled = true;
            if (_selectMode)
            {
                ToggleSelection();
                return;
            }
            ClipboardService.CopyTextToClipboard(capturedText);
            ToastWindow.Show("Copied", "Text copied");
        };

        void ToggleSelection()
        {
            var selected = card.Tag is CodeHistoryEntry;
            selected = !selected;
            card.Tag = selected ? entry : null;
            UpdateSelectableCardSelection(card, selBadge, selected);
            UpdateHistoryActionButtons();
        }

        card.KeyDown += (_, e) =>
        {
            if (!IsHistoryCardActivationKey(e))
                return;

            e.Handled = true;
            if (_selectMode)
            {
                ToggleSelection();
            }
            else
            {
                ClipboardService.CopyTextToClipboard(capturedText);
                ToastWindow.Show("Copied", "Text copied");
            }
        };

        UpdateSelectableCardSelection(card, selBadge, selected: false);
        return card;
    }

    private void DeleteCodeEntryFromCodesTab(CodeHistoryEntry entry)
    {
        _historyService.DeleteCodeEntry(entry);
        LoadCodeHistory();
    }

    private BitmapSource GetOrCreateCodePreview(CodeHistoryEntry entry)
    {
        if (_codePreviewCache.TryGetValue(entry, out var cached))
            return cached;

        try
        {
            var format = ParseBarcodeFormat(entry.Format);
            using var bmp = BarcodeService.RenderPreview(entry.Text, format);
            var src = BitmapPerf.ToBitmapSource(bmp);
            _codePreviewCache[entry] = src;
            return src;
        }
        catch
        {
            using var fallback = new Bitmap(64, 64);
            using var g = Graphics.FromImage(fallback);
            g.Clear(System.Drawing.Color.Transparent);
            var src = BitmapPerf.ToBitmapSource(fallback);
            _codePreviewCache[entry] = src;
            return src;
        }
    }

    private void PruneCodeSearchCache(IReadOnlyCollection<CodeHistoryEntry> currentEntries)
    {
        if (_codeSearchTextCache.Count <= currentEntries.Count + 64 &&
            _codeHistoryCardCache.Count <= currentEntries.Count + 64 &&
            _codePreviewCache.Count <= currentEntries.Count + 64)
            return;

        var current = currentEntries.ToHashSet();
        foreach (var entry in _codeSearchTextCache.Keys.Where(entry => !current.Contains(entry)).ToList())
            _codeSearchTextCache.Remove(entry);
        foreach (var entry in _codeHistoryCardCache.Keys.Where(entry => !current.Contains(entry)).ToList())
            _codeHistoryCardCache.Remove(entry);
        foreach (var entry in _codePreviewCache.Keys.Where(entry => !current.Contains(entry)).ToList())
            _codePreviewCache.Remove(entry);
    }

    private bool CodeMatchesCachedTerms(CodeHistoryEntry entry, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return true;

        var searchable = GetCodeSearchText(entry);
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private string GetCodeSearchText(CodeHistoryEntry entry)
    {
        if (_codeSearchTextCache.TryGetValue(entry, out var cached))
            return cached;

        var searchText = BuildCodeSearchText(entry);
        _codeSearchTextCache[entry] = searchText;
        return searchText;
    }

    private static string BuildCodeSearchText(CodeHistoryEntry entry)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            entry.Text,
            entry.Format,
            HumanizeBarcodeFormat(entry.Format)
        };

        if (TryNormalizeUrl(entry.Text, out var url) && !string.IsNullOrEmpty(url))
        {
            tokens.Add(url);
            tokens.Add("link");
            tokens.Add("url");
            try
            {
                var parsed = new Uri(url);
                tokens.Add(parsed.Host);
                tokens.Add(parsed.Scheme);
            }
            catch { }
        }

        switch (entry.Format?.ToUpperInvariant())
        {
            case "QR_CODE":
                tokens.Add("qr");
                tokens.Add("qrcode");
                break;
            case "AZTEC":
            case "DATA_MATRIX":
            case "PDF_417":
                tokens.Add("2d");
                tokens.Add("barcode");
                break;
            default:
                tokens.Add("barcode");
                tokens.Add("1d");
                break;
        }

        return string.Join(' ', tokens);
    }

    private static string HumanizeBarcodeFormat(string? format)
    {
        return LocalizationService.Translate(format?.ToUpperInvariant() switch
        {
            "QR_CODE" => "QR Code",
            "AZTEC" => "Aztec",
            "DATA_MATRIX" => "Data Matrix",
            "PDF_417" => "PDF 417",
            "CODE_128" => "Code 128",
            "CODE_39" => "Code 39",
            "CODE_93" => "Code 93",
            "CODABAR" => "Codabar",
            "ITF" => "ITF",
            "EAN_13" => "EAN-13",
            "EAN_8" => "EAN-8",
            "UPC_A" => "UPC-A",
            "UPC_E" => "UPC-E",
            _ => string.IsNullOrWhiteSpace(format) ? "Code" : format
        });
    }

    private static BarcodeFormat ParseBarcodeFormat(string? format)
    {
        if (Enum.TryParse<BarcodeFormat>(format, ignoreCase: true, out var parsed))
            return parsed;
        return BarcodeFormat.QR_CODE;
    }

    private static bool TryNormalizeUrl(string text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Contains(' ') || trimmed.Contains('\n'))
            return false;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = uri.AbsoluteUri;
            return true;
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var withScheme))
        {
            url = withScheme.AbsoluteUri;
            return true;
        }

        return false;
    }

    private static bool TryOpenExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ToastWindow.ShowError("Open failed", "No code URL is available.");
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ToastWindow.ShowError("Open failed", "The code URL is not a valid web link.");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            if (process is null)
            {
                ToastWindow.ShowError("Open failed", "Windows did not open the code URL. Copy it from Config -> History and open it manually.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Open failed",
                $"CyberSnap could not open the code URL. Copy it from Config -> History and open it manually.\n{ex.Message}");
            return false;
        }
    }
}
