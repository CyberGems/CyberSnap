using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Image = System.Windows.Controls.Image;
using WpfPoint = System.Windows.Point;

namespace CyberSnap.UI;

public partial class HistoryWindow
{
    private bool _selectMode;
    private List<HistoryItemVM> _historyItems = new();
    private List<HistoryItemVM> _filteredHistoryItems = new();
    private List<HistoryItemVM> _gifItems = new();
    private IReadOnlyList<HistoryEntry> _allImageHistoryEntries = Array.Empty<HistoryEntry>();
    private List<HistoryItemVM> _allHistoryItems = new();
    private Dictionary<string, HistoryItemVM> _allHistoryItemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<HistoryItemVM> _allGifItems = new();
    private List<HistoryItemVM> _filteredGifItems = new();
    private bool _mediaHistoryCacheReady;
    private string? _mediaHistoryCacheKey;
    private string _imageSearchQuery = "";
    private bool _suppressImageSearchSourceEvents;
    private CancellationTokenSource? _searchFilterCts;
    private int _searchFilterVersion;
    private string _lastImmediateSearchQuery = "";
    private ImageSearchSourceOptions _lastImmediateSearchSources = ImageSearchSourceOptions.None;
    private bool _lastImmediateSearchExactMatch;
    private List<HistoryItemVM> _lastImmediateSearchResults = new();
    private int _historyRenderCount;
    private int _gifRenderCount;
    private bool _imageSearchRowAutoHidden;
    private bool _autoPruneRowAutoHidden;
    private bool _suppressHistorySearchBoxTextEvents;
    private const int HistoryPageSize = 60;
    private const int HistoryInitialPageSize = 18;
    private const int ImageHistoryPageSize = HistoryInitialPageSize;
    private const int HistoryAppendPageSize = 18;
    private const int HistoryLookaheadCount = 6;
    private const int HistoryVirtualizationThreshold = 240;
    private const double HistoryCardMargin = 3d;
    private const double HistoryCardPreferredWidth = 192d;
    private const double HistoryCardMinWidth = 164d;
    private const double HistoryCardMaxWidth = 256d;
    private const double HistoryCardHorizontalGap = HistoryCardMargin * 2d;
    private const double HistoryCardFullWidth = HistoryCardPreferredWidth + HistoryCardHorizontalGap;
    private const double HistoryCardImageAspectRatio = 100d / HistoryCardPreferredWidth;
    private const double HistoryVirtualRowHeight = 156d;
    private const int HistoryVirtualRowBuffer = 3;
    private const int HistoryPrefetchRowBuffer = 2;
    private const int HistoryPrefetchLimit = 48;
    private bool _useVirtualizedImageHistory;
    private bool _imageHistoryLoadFailed;
    private int _virtualizedHistoryColumns = 1;
    private int _virtualizedHistoryStartIndex = -1;
    private int _virtualizedHistoryEndIndex = -1;
    private Border? _historyTopSpacer;
    private Border? _historyBottomSpacer;
    private WrapPanel? _historyVirtualizedPanel;
    private readonly Dictionary<OcrHistoryEntry, string> _ocrSearchTextCache = new();
    private readonly Dictionary<ColorHistoryEntry, string> _colorSearchTextCache = new();
    private readonly Dictionary<CodeHistoryEntry, string> _codeSearchTextCache = new();

    private bool ShouldUseVirtualizedImageHistory(IReadOnlyCollection<HistoryItemVM> items)
        => items.Count >= HistoryVirtualizationThreshold;

    private void ShowHistoryEmptyState(string title, string detail, bool showRetry = false)
    {
        var translatedTitle = LocalizationService.Translate(title);
        var translatedDetail = LocalizationService.Translate(detail);
        HistoryEmptyTitle.Text = translatedTitle;
        HistoryEmptyTitle.ToolTip = translatedTitle;
        AutomationProperties.SetHelpText(HistoryEmptyTitle, translatedTitle);
        HistoryEmptyLabel.Text = translatedDetail;
        HistoryEmptyLabel.ToolTip = translatedDetail;
        AutomationProperties.SetHelpText(HistoryEmptyLabel, translatedDetail);
        HistoryEmptyRetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Visibility = Visibility.Visible;
    }

    private void HideHistoryEmptyState()
    {
        HistoryEmptyRetryButton.Visibility = Visibility.Collapsed;
        HistoryEmptyText.Visibility = Visibility.Collapsed;
    }

    private void HistoryEmptyRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyLoadInProgress)
            return;

        _ = LoadHistoryAsync();
    }

    private sealed record PreparedHistoryItemData(
        HistoryEntry Entry,
        string ThumbPath,
        string Dimensions,
        string TimeAgo,
        string FileNameSearchText,
        string NormalizedFileNameSearchText,
        string SearchText,
        string NormalizedSearchText,
        string ImageSearchStatusText,
        string ImageSearchDiagnosticsText,
        string ImageSearchMatchText,
        bool IsSelected);

    private bool TryRefreshLoadedImageHistoryIncrementally()
    {
        if (!_historyImageCacheReady || _historyLoadInProgress || _pendingHistoryDiskRefresh)
            return false;

        var selectedPaths = _allHistoryItems
            .Where(item => item.IsSelected)
            .Select(item => item.Entry.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = _historyService.ImageEntries;
        
        int oldTotalCount = _allImageHistoryEntries?.Count ?? 0;
        int newTotalCount = entries.Count;
        int diff = newTotalCount - oldTotalCount;
        if (diff > 0)
        {
            _historyRenderCount += diff;
        }
        else if (newTotalCount < _historyRenderCount)
        {
            _historyRenderCount = newTotalCount;
        }

        _allImageHistoryEntries = entries;
        ResetMaterializedImageHistory();
        EnsureMaterializedImageHistoryItems(Math.Min(_historyRenderCount <= 0 ? ImageHistoryPageSize : _historyRenderCount, entries.Count), selectedPaths);
        ApplyImageSearchFilter();

        return true;
    }

    private HistoryItemVM CreateHistoryItemViewModel(HistoryEntry entry, bool isSelected, bool hydrateSearchMetadata)
    {
        var vm = new HistoryItemVM();
        UpdateHistoryItemViewModel(vm, entry, isSelected, hydrateSearchMetadata);
        return vm;
    }

    private void UpdateHistoryItemViewModel(HistoryItemVM vm, HistoryEntry entry, bool isSelected, bool hydrateSearchMetadata)
    {
        var previousEntry = vm.Entry;
        var thumbnailSourceChanged =
            previousEntry is not null &&
            string.Equals(previousEntry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase) &&
            (previousEntry.FileSizeBytes != entry.FileSizeBytes ||
             previousEntry.Width != entry.Width ||
             previousEntry.Height != entry.Height);

        if (thumbnailSourceChanged)
            ResetHistoryItemThumbnail(vm, entry.FilePath);

        vm.Entry = entry;
        vm.ThumbPath = entry.FilePath;
        vm.Dimensions = entry.Width > 0 ? $"{entry.Width} x {entry.Height}" : "";
        vm.TimeAgo = FormatTimeAgo(entry.CapturedAt);
        vm.FileNameSearchText = Path.GetFileNameWithoutExtension(entry.FileName);
        vm.NormalizedFileNameSearchText = ImageSearchQueryMatcher.Normalize(vm.FileNameSearchText);
        if (hydrateSearchMetadata)
            HydrateHistoryItemSearchMetadata(vm);
        else
        {
            vm.SearchText = vm.FileNameSearchText;
            vm.NormalizedSearchText = vm.NormalizedFileNameSearchText;
            vm.ImageSearchStatusText = "";
            vm.ImageSearchDiagnosticsText = "";
            vm.ImageSearchMatchText = "";
            vm.SearchMetadataHydrated = false;
        }
        vm.OcrSearchText = "";
        vm.SemanticSearchText = "";

        vm.IsSelected = isSelected;
        if (vm.ThumbnailLoaded && IsStaleHistoryPlaceholder(vm.ThumbnailSource, entry.Kind))
        {
            vm.ThumbnailLoaded = false;
            vm.ThumbnailSource = null;
        }
        if ((vm.ThumbnailSource is null || !vm.ThumbnailLoaded) &&
            TryGetThumbFromCache(entry.FilePath, out var cachedThumb))
        {
            vm.ThumbnailSource = cachedThumb;
            vm.ThumbnailLoaded = true;
        }

        if (vm.ThumbnailLoaded)
            RememberHistoryItemThumbnailFingerprint(vm, entry);
    }

    private static void ResetHistoryItemThumbnail(HistoryItemVM vm, string filePath)
    {
        RemoveThumbFromCache(filePath);
        vm.ThumbnailLoaded = false;
        vm.ThumbnailSource = null;
        vm.ThumbnailFileSizeBytes = 0;
        vm.ThumbnailWidth = 0;
        vm.ThumbnailHeight = 0;
        if (vm.ThumbnailImage is Image image)
        {
            image.Source = GetHistoryPlaceholder(vm.Entry?.Kind ?? HistoryKind.Image);
            image.Opacity = 1;
        }
    }

    private static void RememberHistoryItemThumbnailFingerprint(HistoryItemVM vm, HistoryEntry entry)
    {
        vm.ThumbnailFileSizeBytes = entry.FileSizeBytes;
        vm.ThumbnailWidth = entry.Width;
        vm.ThumbnailHeight = entry.Height;
    }

    private void HydrateHistoryItemSearchMetadata(HistoryItemVM vm)
    {
        vm.SearchText = _imageSearchIndexService.BuildSearchText(vm.Entry.FilePath, vm.Entry.FileName);
        vm.NormalizedSearchText = ImageSearchQueryMatcher.Normalize(vm.SearchText);
        if (_settingsService.Settings.ShowImageSearchBar)
        {
            var diagnostics = _imageSearchIndexService.GetDiagnostics(
                vm.Entry.FilePath,
                vm.Entry.FileName,
                _imageSearchQuery,
                _settingsService.Settings.ImageSearchSources,
                _settingsService.Settings.ImageSearchExactMatch);
            vm.ImageSearchStatusText = diagnostics.StatusText;
            vm.ImageSearchDiagnosticsText = diagnostics.DetailsText;
            vm.ImageSearchMatchText = diagnostics.MatchText;
        }
        else
        {
            vm.ImageSearchStatusText = "";
            vm.ImageSearchDiagnosticsText = "";
            vm.ImageSearchMatchText = "";
        }

        vm.SearchMetadataHydrated = true;
    }

    private void HydrateHistoryItemSearchMetadataIfNeeded(HistoryItemVM vm)
    {
        if (!vm.SearchMetadataHydrated)
            HydrateHistoryItemSearchMetadata(vm);
    }

    private void HydrateHistoryItemsForSearch(IEnumerable<HistoryItemVM> items)
    {
        foreach (var item in items)
            HydrateHistoryItemSearchMetadataIfNeeded(item);
    }

    private void ResetMaterializedImageHistory()
    {
        _allHistoryItems.Clear();
        _allHistoryItemsByPath.Clear();
        _lastImmediateSearchQuery = "";
        _lastImmediateSearchResults = new List<HistoryItemVM>();
    }

    private void InvalidateHistoryCategoryCaches()
    {
        _mediaHistoryCacheReady = false;
        _mediaHistoryCacheKey = null;
    }

    private void EnsureMaterializedImageHistoryItems(int count, HashSet<string>? selectedPaths = null)
    {
        if (_allImageHistoryEntries.Count == 0)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var startingCount = _allHistoryItems.Count;
        var targetCount = Math.Clamp(count, 0, _allImageHistoryEntries.Count);
        for (var i = _allHistoryItems.Count; i < targetCount; i++)
        {
            var entry = _allImageHistoryEntries[i];
            if (_allHistoryItemsByPath.TryGetValue(entry.FilePath, out var existing))
            {
                _allHistoryItems.Add(existing);
                continue;
            }

            var vm = CreateHistoryItemViewModel(entry, selectedPaths?.Contains(entry.FilePath) == true, hydrateSearchMetadata: false);
            _allHistoryItems.Add(vm);
            _allHistoryItemsByPath[entry.FilePath] = vm;
        }

        sw.Stop();
        if (_allHistoryItems.Count != startingCount)
        {
            AppDiagnostics.LogInfo(
                "history.materialize-images",
                $"added={_allHistoryItems.Count - startingCount} loaded={_allHistoryItems.Count}/{_allImageHistoryEntries.Count} elapsedMs={sw.ElapsedMilliseconds}");
        }
    }

    private void EnsureAllImageHistoryItemsMaterialized()
    {
        EnsureMaterializedImageHistoryItems(_allImageHistoryEntries.Count);
    }

    private async Task LoadHistoryAsync()
    {
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        _historyLoadCts?.Cancel();
        _historyLoadCts?.Dispose();
        _historyLoadCts = new CancellationTokenSource();
        var cancellationToken = _historyLoadCts.Token;
        var version = ++_historyLoadVersion;
        _historyLoadInProgress = true;
        _imageHistoryLoadFailed = false;
        _deferHistoryMonitor = true;
        HistoryStack.Children.Clear();
        HideHistoryEmptyState();
        HistoryCountText.Text = "Loading captures...";
        _imageSearchRowAutoHidden = false;
        _autoPruneRowAutoHidden = false;
        _lastImmediateSearchQuery = "";
        _lastImmediateSearchSources = ImageSearchSourceOptions.None;
        _lastImmediateSearchExactMatch = false;
        _lastImmediateSearchResults = new List<HistoryItemVM>();

        try
        {
            await Task.Yield();
            var entries = _historyService.ImageEntries;
            var selectedPaths = _allHistoryItems
                .Where(i => i.IsSelected)
                .Select(i => i.Entry.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _allImageHistoryEntries = entries;
            ResetMaterializedImageHistory();
            _historyRenderCount = Math.Min(ImageHistoryPageSize, entries.Count);
            EnsureMaterializedImageHistoryItems(_historyRenderCount, selectedPaths);
            ApplyImageSearchFilter();
            _historyImageCacheReady = true;
            PrimeHistoryFingerprint();
            UpdateHistoryActionButtons();
            if (_settingsService.Settings.AutoIndexImages)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 0)
                            _imageSearchIndexService.RequestSync(entries, _settingsService.Settings.OcrLanguageTag);
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("settings.image-search-request", ex);
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-load", ex);
            _imageHistoryLoadFailed = true;
            HistoryStack.Children.Clear();
            _allImageHistoryEntries = Array.Empty<HistoryEntry>();
            _allHistoryItems.Clear();
            _allHistoryItemsByPath.Clear();
            _filteredHistoryItems.Clear();
            _historyItems.Clear();
            ShowHistoryEmptyState("Couldn't load captures", "Retry loading history. If it still fails, check the app log.", showRetry: true);
            HistoryCountText.Text = "History unavailable";
            UpdateHistoryActionButtons();
        }
        finally
        {
            loadSw.Stop();
            AppDiagnostics.LogInfo(
                "history.load-images",
                $"loaded={_allHistoryItems.Count}/{_allImageHistoryEntries.Count} elapsedMs={loadSw.ElapsedMilliseconds}");
            if (version == _historyLoadVersion)
            {
                _historyLoadInProgress = false;
                _deferHistoryMonitor = false;
                UpdateHistoryMonitorState();
                if (_pendingHistoryDiskRefresh || _pendingHistoryUiRefresh)
                {
                    _historyRefreshTimer.Stop();
                    _historyRefreshTimer.Start();
                }

                if (_pendingNavigateToPath != null)
                    TryNavigateToPendingItem();
            }
        }
    }

    private void RenderHistoryItems()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _useVirtualizedImageHistory = ShouldUseVirtualizedImageHistory(_filteredHistoryItems);
        if (_useVirtualizedImageHistory)
        {
            RenderVirtualizedHistoryItems(resetScrollPosition: true);
            return;
        }

        HistoryStack.Children.Clear();
        _historyItems = _filteredHistoryItems.GetRange(0, _historyRenderCount);
        AppendGroupedHistoryItems(HistoryStack, _historyItems, CreateHistoryCard);
        var renderLookahead = Math.Min(HistoryLookaheadCount, _allHistoryItems.Count - _historyRenderCount);
        PrimeHistoryThumbnailLoads(_historyItems, _allHistoryItems, _historyRenderCount, Math.Max(0, renderLookahead));
        sw.Stop();
        AppDiagnostics.LogInfo(
            "history.render-images",
            $"rendered={_historyItems.Count} filtered={_filteredHistoryItems.Count} elapsedMs={sw.ElapsedMilliseconds}");
    }

    private void AppendNextImageHistoryPage()
    {
        if (_historyRenderCount >= _allImageHistoryEntries.Count)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var previousOffset = ImagesPanel.VerticalOffset;
        var previousCount = _allHistoryItems.Count;
        _historyRenderCount = Math.Min(_historyRenderCount + ImageHistoryPageSize, _allImageHistoryEntries.Count);
        EnsureMaterializedImageHistoryItems(_historyRenderCount);

        var appendCount = _historyRenderCount - previousCount;
        if (appendCount <= 0)
            return;
        var appended = _allHistoryItems.GetRange(previousCount, appendCount);

        _filteredHistoryItems.AddRange(appended);
        _historyItems.AddRange(appended);
        AppendGroupedHistoryItems(HistoryStack, appended, CreateHistoryCard);
        var lookaheadCount = Math.Min(HistoryLookaheadCount, _allHistoryItems.Count - _historyRenderCount);
        PrimeHistoryThumbnailLoads(appended, _allHistoryItems, _historyRenderCount, Math.Max(0, lookaheadCount));
        UpdateLoadedImageHistoryCountText();

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 0)
                ImagesPanel.ScrollToVerticalOffset(previousOffset);
        }, System.Windows.Threading.DispatcherPriority.Background);
        sw.Stop();
        AppDiagnostics.LogInfo(
            "history.append-images",
            $"appended={appended.Count} loaded={_historyRenderCount}/{_allImageHistoryEntries.Count} elapsedMs={sw.ElapsedMilliseconds}");
    }

    private void UpdateLoadedImageHistoryCountText()
    {
        var loadedCount = _filteredHistoryItems.Count;
        var totalCount = _allImageHistoryEntries.Count;
        long visibleBytes = 0;
        foreach (var item in _filteredHistoryItems)
            visibleBytes += GetHistoryItemFileSize(item);

        var loadedPrefix = totalCount > loadedCount
            ? $"{loadedCount} {LocalizationService.Translate("of")} {totalCount} {LocalizationService.Translate("captures loaded")}"
            : $"{loadedCount} {LocalizationService.Translate(loadedCount == 1 ? "capture" : "captures")}";
        HistoryCountText.Text = $"{loadedPrefix} · {FormatStorageSize(visibleBytes)}";
    }

    private void RenderVirtualizedHistoryItems(bool resetScrollPosition)
    {
        EnsureHistoryVirtualizedElements();
        _historyItems.Clear();
        _virtualizedHistoryStartIndex = -1;
        _virtualizedHistoryEndIndex = -1;

        if (resetScrollPosition)
            ImagesPanel.ScrollToVerticalOffset(0);

        UpdateVirtualizedHistoryViewport();
    }

    private void EnsureHistoryVirtualizedElements()
    {
        if (_historyTopSpacer is not null &&
            _historyBottomSpacer is not null &&
            _historyVirtualizedPanel is not null &&
            HistoryStack.Children.Count == 3 &&
            ReferenceEquals(HistoryStack.Children[0], _historyTopSpacer) &&
            ReferenceEquals(HistoryStack.Children[1], _historyVirtualizedPanel) &&
            ReferenceEquals(HistoryStack.Children[2], _historyBottomSpacer))
            return;

        _historyTopSpacer = new Border { Height = 0 };
        _historyVirtualizedPanel = new WrapPanel();
        _historyBottomSpacer = new Border { Height = 0 };

        HistoryStack.Children.Clear();
        HistoryStack.Children.Add(_historyTopSpacer);
        HistoryStack.Children.Add(_historyVirtualizedPanel);
        HistoryStack.Children.Add(_historyBottomSpacer);
    }

    private void UpdateVirtualizedHistoryViewport()
    {
        if (!_useVirtualizedImageHistory || _historyVirtualizedPanel is null || _historyTopSpacer is null || _historyBottomSpacer is null)
            return;

        var totalCount = _filteredHistoryItems.Count;
        if (totalCount == 0)
        {
            _historyVirtualizedPanel.Children.Clear();
            _historyTopSpacer.Height = 0;
            _historyBottomSpacer.Height = 0;
            _historyItems.Clear();
            UpdateHistoryActionButtons();
            return;
        }

        var availableWidth = ImagesPanel.ViewportWidth > 0 ? ImagesPanel.ViewportWidth : ImagesPanel.ActualWidth;
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(HistoryCardFullWidth, availableWidth - 6) / HistoryCardFullWidth));
        _virtualizedHistoryColumns = columns;

        var totalRows = (int)Math.Ceiling(totalCount / (double)columns);
        var viewportHeight = ImagesPanel.ViewportHeight > 0 ? ImagesPanel.ViewportHeight : 600d;
        var visibleRows = Math.Max(1, (int)Math.Ceiling(viewportHeight / HistoryVirtualRowHeight));
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(ImagesPanel.VerticalOffset / HistoryVirtualRowHeight));
        var startRow = Math.Max(0, firstVisibleRow - HistoryVirtualRowBuffer);
        var endRowExclusive = Math.Min(totalRows, firstVisibleRow + visibleRows + HistoryVirtualRowBuffer);
        var startIndex = Math.Min(totalCount, startRow * columns);
        var endIndex = Math.Min(totalCount, endRowExclusive * columns);

        if (startIndex == _virtualizedHistoryStartIndex && endIndex == _virtualizedHistoryEndIndex)
            return;

        _virtualizedHistoryStartIndex = startIndex;
        _virtualizedHistoryEndIndex = endIndex;
        _historyTopSpacer.Height = startRow * HistoryVirtualRowHeight;
        _historyBottomSpacer.Height = Math.Max(0, (totalRows - endRowExclusive) * HistoryVirtualRowHeight);

        var visibleCount = endIndex - startIndex;
        var visibleItems = _filteredHistoryItems.GetRange(startIndex, visibleCount);
        _historyItems = visibleItems;
        _historyVirtualizedPanel.Children.Clear();
        for (int i = 0; i < visibleItems.Count; i++)
            _historyVirtualizedPanel.Children.Add(GetOrCreateHistoryCard(visibleItems[i]));

        var prefetchAfter = Math.Min(columns * HistoryPrefetchRowBuffer, _filteredHistoryItems.Count - endIndex);
        PrimeHistoryThumbnailLoads(visibleItems, _filteredHistoryItems, endIndex, Math.Max(0, prefetchAfter));
        UpdateHistoryActionButtons();
    }

    private static void PrimeHistoryThumbnailLoads(IEnumerable<HistoryItemVM> items)
    {
        int queued = 0;
        foreach (var item in items)
        {
            if (queued >= HistoryPrefetchLimit)
                break;

            if (item.ThumbnailLoaded && item.ThumbnailSource != null)
                continue;

            queued++;
            PrimeThumbLoad(item);
        }
    }

    private static void PrimeHistoryThumbnailLoads(
        IReadOnlyList<HistoryItemVM> primary,
        IReadOnlyList<HistoryItemVM> secondary,
        int secondaryStart,
        int secondaryCount)
    {
        int queued = 0;
        var seen = secondaryCount > 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;

        for (int i = 0; i < primary.Count; i++)
        {
            if (queued >= HistoryPrefetchLimit)
                return;

            var item = primary[i];
            seen?.Add(item.Entry.FilePath);

            if (item.ThumbnailLoaded && item.ThumbnailSource != null)
                continue;

            queued++;
            PrimeThumbLoad(item);
        }

        if (seen is null || secondaryCount <= 0)
            return;

        var end = secondaryStart + secondaryCount;
        for (int i = secondaryStart; i < end; i++)
        {
            if (queued >= HistoryPrefetchLimit)
                return;

            var item = secondary[i];
            if (!seen.Add(item.Entry.FilePath))
                continue;

            if (item.ThumbnailLoaded && item.ThumbnailSource != null)
                continue;

            queued++;
            PrimeThumbLoad(item);
        }
    }

    private Border CreateHistoryCard(HistoryItemVM vm)
    {
        if (ShouldShowHistoryCardStatus(vm.ImageSearchStatusText))
            HydrateHistoryItemSearchMetadataIfNeeded(vm);

        var shell = BuildMediaCardShell(vm, () =>
        {
            try
            {
                if (!File.Exists(vm.Entry.FilePath))
                {
                    ShowHistoryFileMissingError(vm.Entry.FilePath);
                    return;
                }

                using var bmp = BitmapPerf.LoadDetached(vm.Entry.FilePath);
                Services.ClipboardService.CopyToClipboard(bmp);
                ToastWindow.Show("Copied", $"{vm.Dimensions} screenshot copied");
            }
            catch (Exception ex)
            {
                const string recovery = "Try again from Config -> History, or open the saved screenshot manually.";
                ToastWindow.ShowError(
                    "Copy failed",
                    $"CyberSnap could not copy this history item. {recovery}\n{ex.Message}",
                    vm.Entry.FilePath);
            }
        });

        var fileNameBlock = new TextBlock
        {
            Text = vm.Entry.FileName,
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        vm.FileNameTextBlock = fileNameBlock;
        shell.InfoPanel.Children.Add(fileNameBlock);

        var visibleStatus = ShouldShowHistoryCardStatus(vm.ImageSearchStatusText) ? vm.ImageSearchStatusText : "";
        var timeAndStatus = string.IsNullOrWhiteSpace(visibleStatus)
            ? vm.TimeAgo
            : $"{vm.TimeAgo} · {visibleStatus}";
        var timeStatusBlock = new TextBlock
        {
            Text = timeAndStatus,
            FontSize = 10,
            FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
            Foreground = Theme.Brush(Theme.TextMuted),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        vm.TimeStatusTextBlock = timeStatusBlock;
        shell.InfoPanel.Children.Add(timeStatusBlock);

        RefreshHistoryCardTextMetadata(vm);
        AddCategoryTint(shell.Root, System.Windows.Media.Color.FromRgb(100, 180, 255));
        return shell.Card;
    }

    private Border GetOrCreateHistoryCard(HistoryItemVM vm)
    {
        if (vm.Card is Border existing)
        {
            DetachElementFromParent(existing);
            if (ShouldShowHistoryCardStatus(vm.ImageSearchStatusText))
                HydrateHistoryItemSearchMetadataIfNeeded(vm);
            RefreshHistoryCardTextMetadata(vm);
            UpdateCardSelection(vm);
            RefreshCardThumbnail(vm);
            return existing;
        }

        return CreateHistoryCard(vm);
    }

    private void RefreshHistoryCardTextMetadata(HistoryItemVM vm)
    {
        if (vm.FileNameTextBlock != null)
        {
            vm.FileNameTextBlock.Text = vm.Entry.FileName;
            vm.FileNameTextBlock.ToolTip = vm.Entry.FileName;
            AutomationProperties.SetName(vm.FileNameTextBlock, "History file name");
            AutomationProperties.SetHelpText(vm.FileNameTextBlock, vm.Entry.FileName);
        }

        if (vm.TimeStatusTextBlock != null)
        {
            var visibleStatus = ShouldShowHistoryCardStatus(vm.ImageSearchStatusText) ? vm.ImageSearchStatusText : "";
            var timeAndStatus = string.IsNullOrWhiteSpace(visibleStatus)
                ? vm.TimeAgo
                : $"{vm.TimeAgo} · {visibleStatus}";
            vm.TimeStatusTextBlock.Text = timeAndStatus;
            vm.TimeStatusTextBlock.ToolTip = timeAndStatus;
            AutomationProperties.SetName(vm.TimeStatusTextBlock, string.IsNullOrWhiteSpace(visibleStatus)
                ? "History capture time"
                : "History capture time and search status");
            AutomationProperties.SetHelpText(vm.TimeStatusTextBlock, timeAndStatus);
        }

        if (vm.Card != null)
        {
            vm.Card.ToolTip = LocalizationService.Translate("Open in Editor");
        }
    }

    private static void RefreshCardThumbnail(HistoryItemVM vm)
    {
        if (vm.ThumbnailImage is not Image image)
            return;

        var thumbnailFingerprintChanged =
            vm.ThumbnailLoaded &&
            (vm.ThumbnailFileSizeBytes != vm.Entry.FileSizeBytes ||
             vm.ThumbnailWidth != vm.Entry.Width ||
             vm.ThumbnailHeight != vm.Entry.Height);
        if (thumbnailFingerprintChanged)
            ResetHistoryItemThumbnail(vm, vm.Entry.FilePath);

        if (vm.ThumbnailLoaded && IsStaleHistoryPlaceholder(vm.ThumbnailSource, vm.Entry.Kind))
        {
            vm.ThumbnailLoaded = false;
            vm.ThumbnailSource = null;
        }

        if ((vm.ThumbnailSource is null || !vm.ThumbnailLoaded) &&
            TryGetThumbFromCache(vm.Entry.FilePath, out var cachedThumb))
        {
            vm.ThumbnailSource = cachedThumb;
            vm.ThumbnailLoaded = true;
            RememberHistoryItemThumbnailFingerprint(vm, vm.Entry);
        }

        image.Source = vm.ThumbnailSource ?? GetHistoryPlaceholder(vm.Entry.Kind);
        image.Opacity = 1;

        if (!vm.ThumbnailLoaded || vm.ThumbnailSource is null || IsStaleHistoryPlaceholder(vm.ThumbnailSource, vm.Entry.Kind))
            LoadThumbAsync(image, vm);
    }

    private static bool ShouldShowHistoryCardStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("OCR ready", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("Indexed", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("No text", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("OCR error", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("OCR failed", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCardSelection(HistoryItemVM vm)
    {
        if (vm.Card is null)
            return;

        if (vm.SelectionBadge != null)
        {
            vm.SelectionBadge.Visibility = _selectMode || vm.IsSelected ? Visibility.Visible : Visibility.Collapsed;
            vm.SelectionBadge.Opacity = vm.IsSelected ? 1 : 0.45;
            UpdateSelectionBadgeAccessibility(vm.SelectionBadge, vm.IsSelected);
            if (vm.SelectionBadge is FrameworkElement { Tag: UIElement check })
                check.Visibility = vm.IsSelected ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void ToggleSelectMode(object sender, RoutedEventArgs e)
    {
        _selectMode = !_selectMode;
        if (!_selectMode)
            ClearCurrentHistorySelections();

        UpdateSelectModeControls();
        RefreshVisibleCardSelections();
        UpdateImageSearchActionButtons();
    }

    private void UpdateSelectModeControls()
    {
        SelectBtn.Content = LocalizationService.Translate(_selectMode ? "Done" : "Select");
        UpdateHistoryActionButtons();
    }

    private void UpdateHistoryActionButtons()
    {
        if (!IsLoaded)
            return;

        var visibleCount = GetCurrentVisibleHistoryItemCount();
        var totalCount = GetCurrentTotalHistoryItemCount();
        var selectedCount = GetCurrentSelectedHistoryItemCount();
        var historyUnavailable = HistoryCategoryCombo.SelectedIndex == 0 && _imageHistoryLoadFailed;
        var categoryLabel = GetCurrentHistoryCategoryLabel(2);
        var totalCategoryLabel = GetCurrentHistoryCategoryLabel(totalCount);
        var selectedCategoryLabel = GetCurrentHistoryCategoryLabel(selectedCount);

        SelectBtn.IsEnabled = !historyUnavailable && (visibleCount > 0 || _selectMode);
        DeleteAllBtn.IsEnabled = !historyUnavailable && totalCount > 0;
        DeleteSelectedBtn.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        DeleteSelectedBtn.IsEnabled = !historyUnavailable && _selectMode && selectedCount > 0;
        DeleteSelectedBtn.Content = selectedCount > 0
            ? LocalizationService.Translate("Delete selected") + $" ({selectedCount})"
            : LocalizationService.Translate("Delete selected");

        var selectHelp = _selectMode
            ? string.Format(LocalizationService.Translate("Finish selecting {0}"), categoryLabel)
            : string.Format(LocalizationService.Translate("Select {0}"), categoryLabel);
        var selectName = selectHelp;
        var deleteAllHelp = totalCount > 0
            ? string.Format(LocalizationService.Translate("Delete all {0} {1} from history. Source files on disk will also be deleted."), totalCount, totalCategoryLabel)
            : string.Format(LocalizationService.Translate("No {0} to clear in the current category"), categoryLabel);
        var deleteAllName = totalCount > 0
            ? string.Format(LocalizationService.Translate("Delete all {0} {1} in the current history category"), totalCount, totalCategoryLabel)
            : string.Format(LocalizationService.Translate("Clear {0}"), categoryLabel);
        var deleteSelectedHelp = selectedCount > 0
            ? string.Format(LocalizationService.Translate("Delete {0} selected {1} from history. Source files on disk will also be deleted."), selectedCount, selectedCategoryLabel)
            : string.Format(LocalizationService.Translate("Select {0} before removing selected items"), categoryLabel);
        var deleteSelectedName = selectedCount > 0
            ? string.Format(LocalizationService.Translate("Delete {0} selected {1}"), selectedCount, selectedCategoryLabel)
            : string.Format(LocalizationService.Translate("Remove selected {0}"), categoryLabel);

        SelectBtn.ToolTip = selectHelp;
        DeleteAllBtn.ToolTip = deleteAllHelp;
        DeleteSelectedBtn.ToolTip = deleteSelectedHelp;
        AutomationProperties.SetName(SelectBtn, selectName);
        AutomationProperties.SetName(DeleteAllBtn, deleteAllName);
        AutomationProperties.SetName(DeleteSelectedBtn, deleteSelectedName);
        AutomationProperties.SetHelpText(SelectBtn, selectHelp);
        AutomationProperties.SetHelpText(DeleteAllBtn, deleteAllHelp);
        AutomationProperties.SetHelpText(DeleteSelectedBtn, deleteSelectedHelp);
    }

    private string GetCurrentHistoryCategoryLabel(int count)
        => LocalizationService.Translate(HistoryCategoryCombo.SelectedIndex switch
        {
            0 => count == 1 ? "history item" : "history items",
            1 => count == 1 ? "screenshot" : "screenshots",
            2 => count == 1 ? "video/GIF" : "videos & GIFs",
            3 => count == 1 ? "text capture" : "text captures",
            4 => count == 1 ? "color" : "colors",
            5 => count == 1 ? "QR & Barcode scan" : "QR & Barcode scans",
            _ => count == 1 ? "history item" : "history items"
        });

    private int GetCurrentVisibleHistoryItemCount()
    {
        return HistoryCategoryCombo.SelectedIndex switch
        {
            0 => CountAllCardsInVisualTree(HistoryStack),
            1 => _filteredOcrEntries.Count,
            2 => _filteredGifItems.Count,
            3 => _filteredColorEntries.Count,
            4 => _filteredCodeEntries.Count,
            _ => 0
        };
    }

    private int GetCurrentTotalHistoryItemCount()
    {
        return HistoryCategoryCombo.SelectedIndex switch
        {
            0 => _allImageHistoryEntries.Count > 0 ? _allImageHistoryEntries.Count : _historyService.ImageEntries.Count,
            1 => _allImageHistoryEntries.Count > 0 ? _allImageHistoryEntries.Count : _historyService.ImageEntries.Count,
            2 => _allGifItems.Count > 0 ? _allGifItems.Count : _historyService.MediaEntries.Count,
            3 => _historyService.OcrEntries.Count,
            4 => _historyService.ColorEntries.Count,
            5 => _historyService.CodeEntries.Count,
            _ => 0
        };
    }

    private int GetCurrentSelectedHistoryItemCount()
    {
        return HistoryCategoryCombo.SelectedIndex switch
        {
            0 => CountAllSelectedCardsInVisualTree(HistoryStack),
            1 => CountAllSelectedCardsInVisualTree(HistoryStack),
            2 => CountSelectedCardsInVisualTree(GifsPanel),
            3 => OcrStack.Children.OfType<Border>().Count(card => card.Tag is true),
            4 => ColorStack.Children.OfType<Border>().Count(card => card.Tag is ColorHistoryEntry),
            5 => CodeStack.Children.OfType<Border>().Count(card => card.Tag is CodeHistoryEntry),
            _ => 0
        };
    }

    private static int CountAllCardsInVisualTree(System.Windows.DependencyObject root)
    {
        var count = 0;
        WalkVisualBorders(root, border =>
        {
            if (border.Tag is HistoryItemVM || border.Tag is bool)
                count++;
        });
        return count;
    }

    private static int CountAllSelectedCardsInVisualTree(System.Windows.DependencyObject root)
    {
        var count = 0;
        WalkVisualBorders(root, border =>
        {
            if (border.Tag is HistoryItemVM vm && vm.IsSelected)
                count++;
            else if (border.Tag is true)
                count++;
        });
        return count;
    }

    private static int CountSelectedCardsInVisualTree(System.Windows.DependencyObject root)
    {
        var count = 0;
        WalkVisualBorders(root, border =>
        {
            if (border.Tag is HistoryItemVM vm && vm.IsSelected)
                count++;
        });
        return count;
    }

    private static void WalkVisualBorders(System.Windows.DependencyObject parent, Action<Border> action)
    {
        if (parent is Border b)
        {
            action(b);
            if (b.Child != null)
                WalkVisualBorders(b.Child, action);
            return;
        }
        var childrenCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++)
            WalkVisualBorders(System.Windows.Media.VisualTreeHelper.GetChild(parent, i), action);
    }

    private static List<HistoryEntry> GetSelectedEntriesFromVisualTree(System.Windows.DependencyObject root)
    {
        var entries = new List<HistoryEntry>();
        WalkVisualBorders(root, border =>
        {
            if (border.Tag is HistoryItemVM vm && vm.IsSelected)
                entries.Add(vm.Entry);
        });
        return entries;
    }

    private void ClearCurrentHistorySelections()
    {
        foreach (var item in GetCurrentHistorySelectionItems())
            item.IsSelected = false;

        if (HistoryCategoryCombo.SelectedIndex is 0 or 2)
        {
            var root = HistoryCategoryCombo.SelectedIndex == 0
                ? (System.Windows.DependencyObject)HistoryStack
                : GifsPanel;
            WalkVisualBorders(root, border =>
            {
                if (border.Tag is HistoryItemVM vm)
                    vm.IsSelected = false;
                else if (border.Tag is bool)
                {
                    border.Tag = false;
                    UpdateUnifiedCardSelectionVisual(border, false);
                }
            });
        }

        foreach (var card in GetCurrentSelectableCards())
            ClearSelectableCardSelection(card);
    }

    private void RefreshVisibleCardSelections()
    {
        foreach (var item in GetCurrentHistorySelectionItems())
            UpdateCardSelection(item);

        if (HistoryCategoryCombo.SelectedIndex is 0 or 2)
        {
            var root = HistoryCategoryCombo.SelectedIndex == 0
                ? (System.Windows.DependencyObject)HistoryStack
                : GifsPanel;
            WalkVisualBorders(root, border =>
            {
                if (border.Tag is HistoryItemVM vm)
                    UpdateCardSelection(vm);
                else if (border.Tag is bool selected)
                    UpdateUnifiedCardSelectionVisual(border, selected);
            });
        }

        foreach (var card in GetCurrentSelectableCards())
            RefreshSelectableCardSelection(card);
    }

    private IEnumerable<HistoryItemVM> GetCurrentHistorySelectionItems()
    {
        return HistoryCategoryCombo.SelectedIndex switch
        {
            0 => _filteredHistoryItems,
            2 => _filteredGifItems,
            _ => Enumerable.Empty<HistoryItemVM>()
        };
    }

    private IEnumerable<Border> GetCurrentSelectableCards()
    {
        return HistoryCategoryCombo.SelectedIndex switch
        {
            2 => OcrStack.Children.OfType<Border>().Where(IsSelectableHistoryCard),
            4 => ColorStack.Children.OfType<Border>().Where(IsSelectableHistoryCard),
            5 => CodeStack.Children.OfType<Border>().Where(IsSelectableHistoryCard),
            _ => Enumerable.Empty<Border>()
        };
    }

    private static bool IsSelectableHistoryCard(Border card)
    {
        return card.Child is Grid root &&
               root.Children.OfType<Border>().Any(badge => badge.Tag is UIElement);
    }

    private void ClearSelectableCardSelection(Border card)
    {
        if (HistoryCategoryCombo.SelectedIndex == 3)
            card.Tag = false;
        else if (HistoryCategoryCombo.SelectedIndex == 4)
            card.Tag = null;
        else if (HistoryCategoryCombo.SelectedIndex == 5)
            card.Tag = null;

        RefreshSelectableCardSelection(card);
    }

    private void RefreshSelectableCardSelection(Border card)
    {
        if (card.Child is not Grid root)
            return;

        var badge = root.Children.OfType<Border>().FirstOrDefault(candidate => candidate.Tag is UIElement);
        if (badge is null)
            return;

        var selected = HistoryCategoryCombo.SelectedIndex switch
        {
            1 => card.Tag is true,
            3 => card.Tag is ColorHistoryEntry,
            4 => card.Tag is CodeHistoryEntry,
            _ => false
        };

        UpdateSelectableCardSelection(card, badge, selected);
        UpdateHistoryActionButtons();
    }

    private void DeleteAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var totalCount = GetCurrentTotalHistoryItemCount();
            var tab = GetCurrentHistoryCategoryLabel(totalCount);
            if (totalCount <= 0)
            {
                SetHistoryDeleteStatus($"No {tab} to delete.");
                UpdateHistoryActionButtons();
                return;
            }

            if (!ConfirmDeleteAllStep(1, totalCount, tab)) return;
            if (!ConfirmDeleteAllStep(2, totalCount, tab)) return;

            CancelImageSearchWork();
            if (HistoryCategoryCombo.SelectedIndex == 0) _historyService.ClearImages();
            else if (HistoryCategoryCombo.SelectedIndex == 2) DeleteMediaItems(_allGifItems);
            else if (HistoryCategoryCombo.SelectedIndex == 3) _historyService.ClearOcr();
            else if (HistoryCategoryCombo.SelectedIndex == 4) _historyService.ClearColors();
            else if (HistoryCategoryCombo.SelectedIndex == 5) _historyService.ClearCodes();

            _selectMode = false;
            UpdateSelectModeControls();

            LoadCurrentHistoryTab();
            UpdateImageSearchActionButtons();
            UpdateHistoryActionButtons();
            SetHistoryDeleteStatus($"Deleted all {tab}.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-delete-all", ex);
            SetHistoryDeleteStatus($"Delete failed for {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.");
            ToastWindow.ShowError(
                "Delete failed",
                $"CyberSnap could not finish deleting {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.\n{ex.Message}");
        }
    }

    private void DeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedCount = GetCurrentSelectedHistoryItemCount();
            var selectedLabel = GetCurrentHistoryCategoryLabel(selectedCount);
            if (selectedCount <= 0)
            {
                SetHistoryDeleteStatus($"Select {GetCurrentHistoryCategoryLabel(2)} to delete.");
                UpdateHistoryActionButtons();
                return;
            }

            if (!ConfirmDeleteSelected(selectedCount, selectedLabel))
                return;

            CancelImageSearchWork();
            _selectMode = false;
            UpdateSelectModeControls();

            if (HistoryCategoryCombo.SelectedIndex == 0)
            {
                var toDelete = GetSelectedEntriesFromVisualTree(HistoryStack);
                _historyService.RemoveEntries(toDelete);

                // Also delete selected unified entries (text, color, code)
                var unifiedToDelete = new List<Border>();
                WalkVisualBorders(HistoryStack, border =>
                {
                    if (border.Tag is true && _unifiedCardEntries.TryGetValue(border, out var rawEntry))
                        unifiedToDelete.Add(border);
                });
                foreach (var card in unifiedToDelete)
                {
                    if (_unifiedCardEntries.TryGetValue(card, out var rawEntry))
                    {
                        if (rawEntry is OcrHistoryEntry ocr) _historyService.DeleteOcrEntry(ocr);
                        else if (rawEntry is ColorHistoryEntry color) _historyService.DeleteColorEntry(color);
                        else if (rawEntry is CodeHistoryEntry code) _historyService.DeleteCodeEntry(code);
                    }
                    _unifiedCardEntries.Remove(card);
                }
            }
            else if (HistoryCategoryCombo.SelectedIndex == 2)
            {
                DeleteMediaItems(_filteredGifItems.Where(i => i.IsSelected).ToList());
            }
            else if (HistoryCategoryCombo.SelectedIndex == 3)
            {
                var entriesToDelete = OcrStack.Children.OfType<Border>()
                    .Where(b => b.Tag is true)
                    .Select(card => card.DataContext)
                    .OfType<OcrHistoryEntry>()
                    .ToList();
                _historyService.DeleteOcrEntries(entriesToDelete);
            }
            else if (HistoryCategoryCombo.SelectedIndex == 4)
            {
                var toDelete = ColorStack.Children.OfType<Border>()
                    .Select(s => s.Tag).OfType<ColorHistoryEntry>().ToList();
                _historyService.DeleteColorEntries(toDelete);
            }
            else if (HistoryCategoryCombo.SelectedIndex == 5)
            {
                var toDelete = CodeStack.Children.OfType<Border>()
                    .Select(s => s.Tag).OfType<CodeHistoryEntry>().ToList();
                _historyService.DeleteCodeEntries(toDelete);
            }

            LoadCurrentHistoryTab();
            UpdateImageSearchActionButtons();
            UpdateHistoryActionButtons();
            SetHistoryDeleteStatus($"Deleted {selectedCount} selected {selectedLabel}.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-delete-selected", ex);
            SetHistoryDeleteStatus($"Delete failed for selected {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.");
            ToastWindow.ShowError(
                "Delete failed",
                $"CyberSnap could not finish deleting the selected {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.\n{ex.Message}");
        }
    }

    private void SetHistoryDeleteStatus(string message)
    {
        HistorySearchStatusText.Text = message;
    }

    private bool ConfirmDeleteSelected(int selectedCount, string categoryLabel)
    {
        var del = LocalizationService.Translate("Delete");
        var sel = LocalizationService.Translate("selected");
        if (ThemedConfirmDialog.Confirm(
                this,
                $"{del} {selectedCount} {sel} {categoryLabel}",
                $"{del} {selectedCount} {sel} {categoryLabel}? {LocalizationService.Translate("This cannot be undone.")}",
                del,
                LocalizationService.Translate("Cancel")))
            return true;

        SetHistoryDeleteStatus($"{LocalizationService.Translate("Delete canceled")}. {LocalizationService.Translate("Kept")} {selectedCount} {sel} {categoryLabel}.");
        UpdateHistoryActionButtons();
        return false;
    }

    private bool ConfirmDeleteAllStep(int step, int totalCount, string categoryLabel)
    {
        if (ThemedConfirmDialog.Confirm(this, BuildDeleteAllConfirmationTitle(step, totalCount, categoryLabel), BuildDeleteAllConfirmationMessage(step, totalCount, categoryLabel), "Delete", "Cancel"))
            return true;

        var cancelMsg = LocalizationService.Translate("Delete canceled");
        var kept = LocalizationService.Translate("Kept");
        SetHistoryDeleteStatus($"{cancelMsg}. {kept} {totalCount} {categoryLabel}.");
        UpdateHistoryActionButtons();
        return false;
    }

    private static string BuildDeleteAllConfirmationTitle(int step, int totalCount, string categoryLabel)
    {
        var del = LocalizationService.Translate("Delete");
        return $"{del} {totalCount} {categoryLabel}";
    }

    private static string BuildDeleteAllConfirmationMessage(int step, int totalCount, string categoryLabel)
    {
        var confirmation = LocalizationService.Translate("Confirmation");
        var of = LocalizationService.Translate("of");
        var confirmStep = $"({confirmation} {step} {of} 2)";
        return step switch
        {
            1 => LocalizationService.Translate("This will permanently delete ALL capture thumbnails and physical files from disk.") +
                 $"\n\n{confirmStep}",
            2 => LocalizationService.Translate("This cannot be undone. Everything will be permanently deleted from disk and the Gallery.") +
                 $"\n\n{confirmStep}",
            _ => ""
        };
    }

    private void AppendGroupedHistoryItems(System.Windows.Controls.Panel target, IEnumerable<HistoryItemVM> items, Func<HistoryItemVM, Border> cardFactory)
    {
        WrapPanel? currentWrap = target.Children.Count > 0 ? target.Children[target.Children.Count - 1] as WrapPanel : null;
        DateTime? currentDate = currentWrap?.Tag is DateTime tagDate ? tagDate : null;

        var updatedWraps = new HashSet<WrapPanel>();
        foreach (var item in items)
        {
            var itemDate = item.Entry.CapturedAt.Date;
            if (currentWrap is null || currentDate != itemDate)
            {
                if (target.Children.Count > 0)
                {
                    target.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Theme.Brush(Theme.BorderSubtle),
                        Margin = new Thickness(6, 26, 6, 0)
                    });
                }

                var dateLabelText = new TextBlock
                {
                    Text = FormatHistoryGroupLabel(itemDate).ToUpperInvariant(),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName),
                    Foreground = Theme.Brush(Theme.Accent),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Opacity = 0.9,
                };
                var dateLabelPill = new Border
                {
                    Background = Theme.Brush(Theme.AccentSubtle),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(6, 18, 0, 12),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Child = dateLabelText
                };
                target.Children.Add(dateLabelPill);

                currentWrap = CreateHistoryWrapPanel(itemDate);
                target.Children.Add(currentWrap);
                currentDate = itemDate;
            }

            currentWrap.Children.Add(cardFactory(item));
            updatedWraps.Add(currentWrap);
        }

        foreach (var wrap in updatedWraps)
            UpdateHistoryWrapPanelCardWidths(wrap);
    }

    private WrapPanel CreateHistoryWrapPanel(DateTime itemDate)
    {
        var wrap = new WrapPanel
        {
            Tag = itemDate,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        wrap.Loaded += (_, _) => UpdateHistoryWrapPanelCardWidths(wrap);
        wrap.SizeChanged += (_, _) => UpdateHistoryWrapPanelCardWidths(wrap);
        return wrap;
    }

    private static void UpdateHistoryWrapPanelCardWidths(WrapPanel wrap)
    {
        var availableWidth = wrap.ActualWidth;
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
            return;

        var minimumOuterWidth = HistoryCardMinWidth + HistoryCardHorizontalGap;
        var maxColumns = Math.Max(1, (int)Math.Floor(availableWidth / minimumOuterWidth));
        var columns = Math.Max(1, (int)Math.Round(availableWidth / HistoryCardFullWidth));
        columns = Math.Min(columns, maxColumns);

        var targetWidth = Math.Floor(availableWidth / columns) - HistoryCardHorizontalGap;
        if (availableWidth >= minimumOuterWidth)
            targetWidth = Math.Clamp(targetWidth, HistoryCardMinWidth, HistoryCardMaxWidth);
        else
            targetWidth = Math.Max(0, availableWidth - HistoryCardHorizontalGap);

        for (int i = 0; i < wrap.Children.Count; i++)
        {
            if (wrap.Children[i] is not Border card)
                continue;
            // Accept image-history cards (HistoryItemVM tag) and unified cards (bool tag)
            if (card.Tag is not HistoryItemVM && card.Tag is not bool)
                continue;

            if (Math.Abs(card.Width - targetWidth) > 0.5)
                card.Width = targetWidth;
        }
    }

    private static double GetHistoryCardImageHeight(double cardWidth)
    {
        if (double.IsNaN(cardWidth) || double.IsInfinity(cardWidth) || cardWidth <= 0)
            return Math.Round(HistoryCardPreferredWidth * HistoryCardImageAspectRatio);

        return Math.Clamp(
            Math.Round(cardWidth * HistoryCardImageAspectRatio),
            88d,
            132d);
    }

    private static string FormatHistoryGroupLabel(DateTime date)
    {
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (date == DateTime.Today) return lang == "es" ? "Hoy" : "Today";
        if (date == DateTime.Today.AddDays(-1)) return lang == "es" ? "Ayer" : "Yesterday";
        return date.ToString("MMMM d, yyyy");
    }

    private static void ShowHistoryFileMissingError(string? filePath = null)
    {
        var fileName = string.IsNullOrWhiteSpace(filePath) ? "" : Path.GetFileName(filePath);
        var detail = string.IsNullOrWhiteSpace(fileName)
            ? "The saved file is no longer on disk."
            : $"The saved file is no longer on disk: {fileName}";
        ToastWindow.ShowError("File missing", $"{detail}\nRestore the file or capture it again from History.", filePath);
    }

    private static long TryGetFileLength(string filePath)
    {
        try { return new FileInfo(filePath).Length; }
        catch { return 0; }
    }

    private static long GetHistoryItemFileSize(HistoryItemVM item) =>
        item.Entry.FileSizeBytes > 0 ? item.Entry.FileSizeBytes : TryGetFileLength(item.Entry.FilePath);

    private static string FormatFileBackedHistoryCountText(
        int visibleCount,
        int totalCount,
        string singularLabel,
        string pluralLabel,
        string sizeText,
        bool filterActive)
    {
        if (filterActive)
        {
            var totalLabel = totalCount == 1 ? singularLabel : pluralLabel;
            return $"{visibleCount} of {totalCount} {totalLabel} shown by filter · {sizeText}";
        }

        var visibleLabel = visibleCount == 1 ? singularLabel : pluralLabel;
        return $"{visibleCount} {visibleLabel} · {sizeText}";
    }
}
