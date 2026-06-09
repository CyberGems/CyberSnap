using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class HistoryWindow
{
    private void UpdateImageSearchActionButtons()
    {
        if (!IsLoaded)
            return;

        var isImages = HistoryCategoryCombo.SelectedIndex == 0;
        var status = _imageSearchIndexService.StatusText;
        var isIndexing = status.StartsWith("Indexing screenshots", StringComparison.OrdinalIgnoreCase);

        ReindexAllProgressBar.Visibility = isIndexing ? Visibility.Visible : Visibility.Collapsed;

        var entries = _allImageHistoryEntries.Count > 0
            ? _allImageHistoryEntries
            : _historyService.ImageEntries;
        var ocrTag = _settingsService.Settings.OcrLanguageTag;
        int total = entries.Count;

        if (isIndexing)
        {
            ReindexAllProgressPanel.Visibility = Visibility.Visible;
            ReindexAllBtn.Content = status;
            ReindexAllBtn.IsEnabled = false;
            UpdateReindexAllButtonLabel(status, "Image search indexing is already running.");
        }
        else if (total >= HistoryVirtualizationThreshold)
        {
            ReindexAllProgressPanel.Visibility = Visibility.Collapsed;
            ReindexAllBtn.Content = "Refresh index";
            ReindexAllBtn.IsEnabled = total > 0;
            UpdateReindexAllButtonLabel("Refresh image search index", "Refresh the image search index for all screenshot history items.");
        }
        else
        {
            int indexed = _imageSearchIndexService.CountReadyEntries(entries, ocrTag);
            if (indexed < total)
            {
                ReindexAllProgressPanel.Visibility = Visibility.Visible;
                ReindexAllBtn.Content = $"Index {total - indexed} remaining";
                ReindexAllBtn.IsEnabled = true;
                UpdateReindexAllButtonLabel("Index remaining screenshots", $"Index {total - indexed} screenshots for History search.");
            }
            else
            {
                ReindexAllProgressPanel.Visibility = Visibility.Collapsed;
                ReindexAllBtn.Content = $"{indexed}/{total} indexed";
                ReindexAllBtn.IsEnabled = false;
                UpdateReindexAllButtonLabel("Image search index complete", "All visible screenshot history items are indexed.");
            }
        }
    }

    private void UpdateReindexAllButtonLabel(string automationName, string helpText)
    {
        ReindexAllBtn.ToolTip = helpText;
        AutomationProperties.SetName(ReindexAllBtn, automationName);
        AutomationProperties.SetHelpText(ReindexAllBtn, helpText);
    }

    private void UpdateImageSearchPlaceholderText()
    {
        if (!IsLoaded)
            return;

        string placeholder;
        string automationName;
        string helpText;

        if (HistoryCategoryCombo.SelectedIndex == 1)
        {
            placeholder = "Search text captures";
            automationName = "Text history search";
            helpText = "Search saved OCR text captures.";
        }
        else if (HistoryCategoryCombo.SelectedIndex == 3)
        {
            placeholder = "Search hex, RGB, or color names";
            automationName = "Color history search";
            helpText = "Search saved colors by hex value, RGB values, or color names.";
        }
        else if (HistoryCategoryCombo.SelectedIndex == 4)
        {
            placeholder = "Search QR/barcode text, links, or formats";
            automationName = "Code history search";
            helpText = "Search saved QR and barcode text, links, or code formats.";
        }
        else
        {
            var isIndexing = _imageSearchIndexService.StatusText.StartsWith("Indexing screenshots", StringComparison.OrdinalIgnoreCase);
            placeholder = isIndexing
                ? "Search... (indexing)"
                : "Search...";
            automationName = "History search";
            helpText = isIndexing
                ? "Search your capture history while the index continues updating."
                : "Search your capture history by file name, OCR text, color hex, or code content.";
        }

        ImageSearchPlaceholder.Text = placeholder;
        ImageSearchBox.ToolTip = helpText;
        AutomationProperties.SetName(ImageSearchBox, automationName);
        AutomationProperties.SetHelpText(ImageSearchBox, helpText);
    }

    private void UpdateImageSearchSourceSummary()
    {
        var parts = new List<string>(3);
        if (ImageSearchFileNameCheck.IsChecked)
            parts.Add("Name");
        if (ImageSearchOcrCheck.IsChecked)
            parts.Add("OCR");
        if (ImageSearchExactMatchCheck.IsChecked)
            parts.Add("Exact");

        ImageSearchFiltersSummaryText.Text = parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    private void LoadImageSearchSources()
    {
        var sources = _settingsService.Settings.ImageSearchSources;
        _suppressImageSearchSourceEvents = true;
        try
        {
            ImageSearchFileNameCheck.IsChecked = (sources & ImageSearchSourceOptions.FileName) != 0;
            ImageSearchOcrCheck.IsChecked = (sources & ImageSearchSourceOptions.Ocr) != 0;
            ImageSearchExactMatchCheck.IsChecked = _settingsService.Settings.ImageSearchExactMatch;
        }
        finally
        {
            _suppressImageSearchSourceEvents = false;
        }

        UpdateImageSearchSourceSummary();
    }

    private ImageSearchSourceOptions GetImageSearchSourcesFromUi()
    {
        var sources = ImageSearchSourceOptions.None;
        if (ImageSearchFileNameCheck.IsChecked)
            sources |= ImageSearchSourceOptions.FileName;
        if (ImageSearchOcrCheck.IsChecked)
            sources |= ImageSearchSourceOptions.Ocr;
        return sources;
    }

    private void ImageSearchExactMatchCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressImageSearchSourceEvents)
            return;

        var previous = _settingsService.Settings.ImageSearchExactMatch;
        var selected = ImageSearchExactMatchCheck.IsChecked == true;
        UpdateImageSearchPreference(
            "settings.image-search-exact-match",
            "Search exact match",
            previous,
            selected,
            value => _settingsService.Settings.ImageSearchExactMatch = value,
            value => ImageSearchExactMatchCheck.IsChecked = value);
    }

    private void ImageSearchSourcesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressImageSearchSourceEvents)
            return;

        var previous = _settingsService.Settings.ImageSearchSources;
        var selected = GetImageSearchSourcesFromUi();
        UpdateImageSearchPreference(
            "settings.image-search-sources",
            "Search sources",
            previous,
            selected,
            value => _settingsService.Settings.ImageSearchSources = value,
            RestoreImageSearchSourceChecks);
    }

    private void UpdateImageSearchPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            UpdateImageSearchSourceSummary();
            CancelImageSearchWork();

            if (HistoryCategoryCombo.SelectedIndex == 0)
                ApplyImageSearchFilter();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError(diagnosticKey, ex);
            setValue(previous);
            try
            {
                _settingsService.Save();
            }
            catch (Exception rollbackEx)
            {
                AppDiagnostics.LogError($"{diagnosticKey}-rollback", rollbackEx);
            }

            _suppressImageSearchSourceEvents = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressImageSearchSourceEvents = false;
            }

            UpdateImageSearchSourceSummary();
            HistorySearchStatusText.Text = $"{label} change was not saved. Previous setting restored.";
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous search setting was restored. Check Config -> History and try again.\n{ex.Message}");
        }
    }

    private void RestoreImageSearchSourceChecks(ImageSearchSourceOptions sources)
    {
        ImageSearchFileNameCheck.IsChecked = (sources & ImageSearchSourceOptions.FileName) != 0;
        ImageSearchOcrCheck.IsChecked = (sources & ImageSearchSourceOptions.Ocr) != 0;
    }

    private void ImageSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _suppressHistorySearchBoxTextEvents)
                return;

            SetAutoPruneRowAutoHidden(false);
            var text = ImageSearchBox.Text ?? "";
            ImageSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(text) && !ImageSearchBox.IsKeyboardFocused
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (HistoryCategoryCombo.SelectedIndex == 0)
            {
                _imageSearchQuery = text;
                ImageSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(_imageSearchQuery) && !ImageSearchBox.IsKeyboardFocused
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (string.IsNullOrWhiteSpace(_imageSearchQuery))
                {
                    CancelImageSearchWork();
                    ApplyImageSearchFilter();
                    return;
                }

                SetImageSearchLoading(true);
                QueueImageSearchRefresh();
            }
            else if (HistoryCategoryCombo.SelectedIndex == 1)
            {
                _ocrSearchQuery = text;
                ImageSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(_ocrSearchQuery) && !ImageSearchBox.IsKeyboardFocused
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _ocrSearchDebounceTimer.Stop();
                _ocrSearchDebounceTimer.Tick -= FlushOcrSearchDebounce;
                _ocrSearchDebounceTimer.Tick += FlushOcrSearchDebounce;
                _ocrSearchDebounceTimer.Start();
            }
            else if (HistoryCategoryCombo.SelectedIndex == 3)
            {
                _colorSearchQuery = text;
                ImageSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(_colorSearchQuery) && !ImageSearchBox.IsKeyboardFocused
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _colorSearchDebounceTimer.Stop();
                _colorSearchDebounceTimer.Tick -= FlushColorSearchDebounce;
                _colorSearchDebounceTimer.Tick += FlushColorSearchDebounce;
                _colorSearchDebounceTimer.Start();
            }
            else if (HistoryCategoryCombo.SelectedIndex == 4)
            {
                _codeSearchQuery = text;
                ImageSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(_codeSearchQuery) && !ImageSearchBox.IsKeyboardFocused
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _codeSearchDebounceTimer.Stop();
                _codeSearchDebounceTimer.Tick -= FlushCodeSearchDebounce;
                _codeSearchDebounceTimer.Tick += FlushCodeSearchDebounce;
                _codeSearchDebounceTimer.Start();
            }
        }
        catch (Exception ex)
        {
            SetImageSearchLoading(false, forceIndexed: true);
            HistorySearchStatusText.Text = "Search failed. Edit the query or retry from History.";
            ToastWindow.ShowError(
                "Search failed",
                $"CyberSnap could not update history search. Edit the query or retry from History.\n{ex.Message}");
        }
    }

    private void ImageSearchBox_FocusChanged(object sender, RoutedEventArgs e)
    {
        if (ImageSearchBox.IsKeyboardFocused)
        {
            SetAutoPruneRowAutoHidden(false);
            ImageSearchBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(60, 0, 255, 255));  // very soft cyan
            ImageSearchChevron.Visibility = Visibility.Visible;
        }
        else
        {
            ImageSearchBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("ThemeInputBorderBrush");
            ImageSearchChevron.Visibility = Visibility.Collapsed;
        }

        ImageSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(ImageSearchBox.Text) && !ImageSearchBox.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ImageSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrWhiteSpace(ImageSearchBox.Text))
            return;

        try
        {
            CancelImageSearchWork();
            ImageSearchBox.Clear();
            ImageSearchBox.Focus();
            if (HistoryCategoryCombo.SelectedIndex == 0)
                ApplyImageSearchFilter();
            else if (HistoryCategoryCombo.SelectedIndex == 1)
                LoadOcrHistory();
            else if (HistoryCategoryCombo.SelectedIndex == 3)
                LoadColorHistory();
            else if (HistoryCategoryCombo.SelectedIndex == 4)
                LoadCodeHistory();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            SetImageSearchLoading(false, forceIndexed: true);
            HistorySearchStatusText.Text = "Search failed. Edit the query or retry from History.";
            ToastWindow.ShowError(
                "Search failed",
                $"CyberSnap could not update history search. Edit the query or retry from History.\n{ex.Message}");
        }
    }

    private void ReindexAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ReindexAllBtn.IsEnabled)
            return;

        ReindexAllBtn.IsEnabled = false;
        ReindexAllBtn.Content = "Starting index...";
        ReindexAllProgressPanel.Visibility = Visibility.Visible;
        ReindexAllProgressBar.Visibility = Visibility.Visible;
        HistorySearchStatusText.Text = "Starting image index refresh...";

        try
        {
            _imageSearchIndexService.RequestSync(_historyService.ImageEntries, _settingsService.Settings.OcrLanguageTag);
            UpdateImageSearchStatus();
            UpdateImageSearchActionButtons();
            UpdateImageSearchPlaceholderText();
            QueueImageIndexRefresh();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-reindex-refresh", ex);
            SetImageSearchLoading(false, forceIndexed: true);
            HistorySearchStatusText.Text = "Index refresh failed. Existing search data is still available.";
            UpdateImageSearchActionButtons();
            ToastWindow.ShowError(
                "Index refresh failed",
                $"CyberSnap could not refresh the image search index. Existing search data is still available; try again from History.\n{ex.Message}");
        }
    }

    private void ImageSearchChevron_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ImageSearchFiltersMenu.PlacementTarget = ImageSearchBox;
        ImageSearchFiltersMenu.IsOpen = true;
    }

    private void ImageSearchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        ImageSearchBox.SelectAll();
        ImageSearchBox.Focus();
    }

    private void ImageSearchDeleteText_Click(object sender, RoutedEventArgs e)
    {
        ImageSearchBox.Clear();
        ImageSearchBox.Focus();
    }

    private void ImageSearchCut_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ImageSearchBox.SelectedText))
        {
            System.Windows.Clipboard.SetText(ImageSearchBox.SelectedText);
            ImageSearchBox.SelectedText = "";
        }
    }

    private void ImageSearchCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ImageSearchBox.SelectedText))
            System.Windows.Clipboard.SetText(ImageSearchBox.SelectedText);
        else if (!string.IsNullOrEmpty(ImageSearchBox.Text))
            System.Windows.Clipboard.SetText(ImageSearchBox.Text);
    }

    private void ImageSearchPaste_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Clipboard.ContainsText())
        {
            var pasteText = System.Windows.Clipboard.GetText();
            if (!string.IsNullOrEmpty(pasteText))
            {
                var caret = ImageSearchBox.CaretIndex;
                ImageSearchBox.Text = ImageSearchBox.Text.Insert(caret, pasteText);
                ImageSearchBox.CaretIndex = caret + pasteText.Length;
            }
        }
    }

    private void HistoryPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Auto-hide pruning card on scroll
        var shouldHidePrune = e.VerticalOffset > 18;
        SetAutoPruneRowAutoHidden(shouldHidePrune);

        // All view: infinite scroll
        if (HistoryCategoryCombo.SelectedIndex == 0)
        {
            if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 360) return;
            AppendNextAllPage();
            return;
        }

        if (_useVirtualizedImageHistory)
        {
            UpdateVirtualizedHistoryViewport();
            return;
        }

        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 360)
            return;

        if (HistoryCategoryCombo.SelectedIndex == 1 && string.IsNullOrWhiteSpace(_imageSearchQuery))
        {
            AppendNextImageHistoryPage();
            return;
        }

        if (_historyRenderCount >= _filteredHistoryItems.Count)
            return;

        var previousOffset = ImagesPanel.VerticalOffset;
        var previousCount = _historyRenderCount;
        _historyRenderCount = Math.Min(_historyRenderCount + HistoryAppendPageSize, _filteredHistoryItems.Count);
        var appendCount = _historyRenderCount - previousCount;
        if (appendCount <= 0)
            return;
        var appended = _filteredHistoryItems.GetRange(previousCount, appendCount);

        _historyItems.AddRange(appended);
        AppendGroupedHistoryItems(HistoryStack, appended, CreateHistoryCard);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex == 0)
                ImagesPanel.ScrollToVerticalOffset(previousOffset);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LoadPruneSettings()
    {
        _suppressPrunePreferenceChange = true;
        try
        {
            var s = _settingsService.Settings;
            HistoryRetentionCombo.SelectedIndex = Math.Clamp((int)s.HistoryRetention, 0, 4);
            HistoryCountLimitCombo.SelectedIndex = s.HistoryCountLimit switch
            {
                50 => 1,
                100 => 2,
                250 => 3,
                500 => 4,
                1000 => 5,
                _ => 0
            };
            HistoryDeleteOriginalOnPruneCheck.IsChecked = s.HistoryDeleteOriginalOnPrune;
        }
        finally
        {
            _suppressPrunePreferenceChange = false;
        }

        // Apply pruning on startup so limits take effect immediately
        _historyService.PruneByRetention(_settingsService.Settings.HistoryRetention);
        _historyService.PruneByCount(_settingsService.Settings.HistoryCountLimit, _settingsService.Settings.HistoryDeleteOriginalOnPrune);
    }

    private void HistoryRetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressPrunePreferenceChange) return;

        try
        {
            var selected = (HistoryRetentionPeriod)Math.Clamp(HistoryRetentionCombo.SelectedIndex, 0, 4);
            _settingsService.Settings.HistoryRetention = selected;
            _settingsService.Save();
            _historyService.PruneByRetention(selected);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-retention", ex);
        }
    }

    private void HistoryCountLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressPrunePreferenceChange) return;

        try
        {
            var selected = HistoryCountLimitCombo.SelectedIndex switch
            {
                1 => 50,
                2 => 100,
                3 => 250,
                4 => 500,
                5 => 1000,
                _ => 0
            };
            _settingsService.Settings.HistoryCountLimit = selected;
            _historyService.HistoryCountLimit = selected;
            _settingsService.Save();
            _historyService.PruneByCount(selected, _settingsService.Settings.HistoryDeleteOriginalOnPrune);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-count-limit", ex);
        }
    }

    private void HistoryDeleteOriginalOnPruneCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressPrunePreferenceChange) return;

        try
        {
            var selected = HistoryDeleteOriginalOnPruneCheck.IsChecked == true;
            _settingsService.Settings.HistoryDeleteOriginalOnPrune = selected;
            _historyService.HistoryDeleteOriginalOnPrune = selected;
            _settingsService.Save();
            if (selected && _settingsService.Settings.HistoryCountLimit > 0)
            {
                _historyService.PruneByCount(_settingsService.Settings.HistoryCountLimit, true);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.history-delete-original-on-prune", ex);
        }
    }

    private void DeleteOriginalLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HistoryDeleteOriginalOnPruneCheck.IsChecked = HistoryDeleteOriginalOnPruneCheck.IsChecked != true;
    }
}
