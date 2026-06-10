using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MediaBrush = System.Windows.Media.Brush;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using WpfPoint = System.Windows.Point;

namespace CyberSnap.UI;

public partial class HistoryWindow : Window
{
    private static readonly SemaphoreSlim ThumbDecodeGate = new(4);

    private readonly System.Windows.Threading.DispatcherTimer _historyMonitorTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2.5)
    };
    private readonly System.Windows.Threading.DispatcherTimer _imageIndexRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1.25)
    };
    private readonly System.Windows.Threading.DispatcherTimer _historyRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    private readonly System.Windows.Threading.DispatcherTimer _imageSearchDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };

    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly ImageSearchIndexService _imageSearchIndexService;

    private string? _lastHistoryFingerprint;
    private bool _pendingImageSearchTextRefresh;
    private bool _pendingHistoryDiskRefresh;
    private bool _pendingHistoryUiRefresh;
    private bool _pendingHistoryDataRefresh;
    private bool _historyRefreshInProgress;
    private CancellationTokenSource? _historyLoadCts;
    private int _historyLoadVersion;
    private bool _historyLoadInProgress;
    private bool _historyImageCacheReady;
    private bool _deferHistoryMonitor;
    private int _historyTabLoadVersion;
    private bool _historyTabLoadScheduled;
    private bool _historyTabLoadPreserveTransientState;
    private bool _suppressPrunePreferenceChange;
    private string? _pendingNavigateToPath;

    public HistoryWindow(SettingsService settingsService, HistoryService historyService, ImageSearchIndexService imageSearchIndexService)
    {
        _settingsService = settingsService;
        _historyService = historyService;
        _imageSearchIndexService = imageSearchIndexService;

        InitializeComponent();
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        CyberSnapWindowChrome.Apply(this);
        Theme.Refresh();
        Theme.ApplyTo(Application.Current.Resources);
        ApplyThemeColors();
        LoadStaticFluentIcons();
        LoadPruneSettings();

        Loaded += (_, _) =>
        {
            // Restore persisted category filter
            var savedFilter = _settingsService.Settings.HistoryCategoryFilter;
            if (savedFilter >= 0 && savedFilter < HistoryCategoryCombo.Items.Count)
            {
                HistoryCategoryCombo.SelectionChanged -= HistoryCategoryCombo_Changed;
                HistoryCategoryCombo.SelectedIndex = savedFilter;
                HistoryCategoryCombo.SelectionChanged += HistoryCategoryCombo_Changed;
            }
            ApplyMicaBackdrop();
        };
        StateChanged += (_, _) => SettingsTitleBar.RefreshIcons();

        _historyService.Changed += HistoryService_Changed;
        _imageSearchIndexService.Changed += ImageSearchIndexService_Changed;
        _imageSearchIndexService.StatusChanged += ImageSearchIndexService_StatusChanged;

        _historyMonitorTimer.Tick += (_, _) => PollHistoryChanges();
        _historyRefreshTimer.Tick += async (_, _) => await FlushQueuedHistoryRefreshAsync();
        _imageIndexRefreshTimer.Tick += (_, _) => FlushQueuedImageIndexRefresh();
        _imageSearchDebounceTimer.Tick += (_, _) => FlushQueuedImageSearchRefresh();

        Activated += (_, _) =>
        {
            ApplyThemeColors();
            LoadStaticFluentIcons();
        };

        SizeChanged += (_, _) =>
        {
            if (IsLoaded && HistoryTab.IsChecked == true && HistoryCategoryCombo.SelectedIndex <= 1)
                UpdateVirtualizedHistoryViewport();
        };

        Closed += (_, _) =>
        {
            _historyService.Changed -= HistoryService_Changed;
            _imageSearchIndexService.Changed -= ImageSearchIndexService_Changed;
            _imageSearchIndexService.StatusChanged -= ImageSearchIndexService_StatusChanged;
            _historyLoadCts?.Cancel();
            _historyLoadCts?.Dispose();
            CancelImageSearchWork();
            _imageIndexRefreshTimer.Stop();
            _historyRefreshTimer.Stop();
            _imageSearchDebounceTimer.Stop();
            _ocrSearchDebounceTimer.Stop();
            _colorSearchDebounceTimer.Stop();
            _codeSearchDebounceTimer.Stop();
            _historyMonitorTimer.Stop();
        };

        // Immediately load history
        ScheduleHistoryTabLoad();
    }

    public void RequestRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RequestRefresh);
            return;
        }

        // Handle settings propagation (e.g. search bar toggle)
        if (!_settingsService.Settings.ShowImageSearchBar)
        {
            if (!string.IsNullOrEmpty(ImageSearchBox.Text))
                ImageSearchBox.Clear();
            _imageSearchQuery = "";
        }

        ApplyThemeColors();
        LoadStaticFluentIcons();
        ScheduleHistoryTabLoad(preserveTransientState: true);
    }

    /// <summary>Lightweight refresh: only updates search bar and auto-prune visibility without reloading.</summary>
    public void RefreshVisibility()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshVisibility);
            return;
        }
        UpdateImageSearchUi();
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    public void NavigateToItem(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => NavigateToItem(filePath));
            return;
        }

        _pendingNavigateToPath = filePath;

        // Don't force-switch the filter — respect whatever the user has selected.
        // If the current filter can't show this item, just refresh the current view.
        if (HistoryCategoryCombo.SelectedIndex == 0)
        {
            // All view: refresh to pick up the new item
            if (IsLoaded && HistoryTab.IsChecked == true)
                LoadAllHistory();
            return;
        }

        _imageSearchQuery = "";
        if (ImageSearchBox != null) ImageSearchBox.Text = "";
        _ocrSearchQuery = "";
        _colorSearchQuery = "";
        _codeSearchQuery = "";

        if (!_historyLoadInProgress && _historyImageCacheReady)
            TryNavigateToPendingItem();
    }

    private void TryNavigateToPendingItem()
    {
        var path = _pendingNavigateToPath;
        if (path == null) return;

        if (_historyLoadInProgress || !_historyImageCacheReady || _allImageHistoryEntries.Count == 0)
        {
            Dispatcher.BeginInvoke(new Action(() => TryNavigateToPendingItem()),
                System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        _pendingNavigateToPath = null;

        EnsureAllImageHistoryItemsMaterialized();

        if (!_allHistoryItemsByPath.TryGetValue(path, out var vm))
        {
            AppDiagnostics.LogInfo("history.navigate-to-item", $"Item not found: {path}");
            return;
        }

        var sources = _settingsService.Settings.ImageSearchSources;
        var exactMatch = _settingsService.Settings.ImageSearchExactMatch;
        ApplyImmediateImageFilter("", sources, exactMatch);

        var index = _filteredHistoryItems.IndexOf(vm);
        if (index < 0) return;

        ClearCurrentHistorySelections();
        vm.IsSelected = true;
        UpdateCardSelection(vm);
        UpdateImageSearchActionButtons();
        UpdateHistoryActionButtons();

        if (_useVirtualizedImageHistory)
        {
            var col = Math.Max(1, _virtualizedHistoryColumns);
            var row = index / col;
            ImagesPanel.ScrollToVerticalOffset(row * HistoryVirtualRowHeight);
        }
        else
        {
            while (_historyRenderCount <= index && _historyRenderCount < _filteredHistoryItems.Count)
            {
                var prevCount = _historyRenderCount;
                _historyRenderCount = Math.Min(_historyRenderCount + HistoryAppendPageSize, _filteredHistoryItems.Count);
                var appendCount = _historyRenderCount - prevCount;
                if (appendCount <= 0) break;
                var appended = _filteredHistoryItems.GetRange(prevCount, appendCount);
                _historyItems.AddRange(appended);
                AppendGroupedHistoryItems(HistoryStack, appended, CreateHistoryCard);
            }
            vm.Card?.BringIntoView();
        }

        vm.Card?.Focus();
    }

    private void ApplyMicaBackdrop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.Dwm.DisableBackdrop(hwnd);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("history.apply-backdrop", ex.Message, ex);
        }
        ApplyThemeColors();
    }

    private void ApplyThemeColors()
    {
        Theme.Refresh();
        Theme.ApplyTo(Application.Current.Resources);
        Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
        Resources["ThemeMutedBrush"] = Theme.Brush(Theme.TextMuted);
        Resources["ThemeCardBrush"] = Theme.Brush(Theme.CardBg);
        Resources["ThemeTabActiveBrush"] = Theme.Brush(Theme.TabActiveBg);
        Resources["ThemeTabHoverBrush"] = Theme.Brush(Theme.TabHoverBg);
        Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
        Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        Resources["ThemeWindowBorderBrush"] = Theme.Brush(Theme.WindowBorder);
        Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);

        OuterBorder.Background = Theme.Brush(Theme.BgPrimary);
        OuterBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        Icon = ThemedLogo.Square(32);
        Foreground = Theme.Brush(Theme.TextPrimary);
        UiScale.ApplyToWindow(this, OuterBorder, scaleWindowBounds: true);

        ApplyThemeToVisualTree(OuterBorder);
        UpdateImageSearchUi();
    }

    private void ApplyThemeToVisualTree(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            switch (child)
            {
                case TextBox textBox:
                    textBox.Background = (MediaBrush)Resources["ThemeInputBackgroundBrush"];
                    textBox.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    textBox.BorderBrush = (MediaBrush)Resources["ThemeInputBorderBrush"];
                    textBox.CaretBrush = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    break;
                case ComboBox comboBox:
                    comboBox.Background = (MediaBrush)Resources["ThemeInputBackgroundBrush"];
                    comboBox.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    comboBox.BorderBrush = (MediaBrush)Resources["ThemeInputBorderBrush"];
                    break;
                case Button button when button.Style == null:
                    button.Background = Theme.Brush(Theme.AccentSubtle);
                    button.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    button.BorderBrush = (MediaBrush)Resources["ThemeInputBorderBrush"];
                    break;
                case CheckBox checkBox:
                    checkBox.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    break;
            }

            ApplyThemeToVisualTree(child);
        }
    }

    private void LoadStaticFluentIcons()
    {
        var accentColor = System.Drawing.Color.FromArgb(Theme.Accent.A, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B);
        ImageSearchIcon.Source = Helpers.FluentIcons.RenderWpf("search", accentColor, 18);
        ImageSearchIcon.Opacity = 0.55;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<Native.User32.MINMAXINFO>(lParam);
        var monitor = Native.User32.MonitorFromWindow(hwnd, Native.User32.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new Native.User32.MONITORINFO { cbSize = Marshal.SizeOf<Native.User32.MONITORINFO>() };
            Native.User32.GetMonitorInfo(monitor, ref monitorInfo);
            var rcWork = monitorInfo.rcWork;
            var rcMonitor = monitorInfo.rcMonitor;
            mmi.ptMaxPosition.X = Math.Abs(rcWork.Left - rcMonitor.Left);
            mmi.ptMaxPosition.Y = Math.Abs(rcWork.Top - rcMonitor.Top);
            mmi.ptMaxSize.X = Math.Abs(rcWork.Right - rcWork.Left);
            mmi.ptMaxSize.Y = Math.Abs(rcWork.Bottom - rcWork.Top);
        }

        // Enforce the window's minimum size while the user drags a resize border. This window uses
        // WindowStyle=None + AllowsTransparency and marks WM_GETMINMAXINFO as handled, which bypasses
        // WPF's own MinWidth/MinHeight enforcement, so we populate ptMinTrackSize (physical pixels) here.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void EnsureWindowFitsWorkArea()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)Left, (int)Top));
        var phys = screen.WorkingArea;
        var hwnd = new WindowInteropHelper(this).Handle;

        if (hwnd == IntPtr.Zero)
        {
            var workArea = SystemParameters.WorkArea;
            if (Left + 100 > workArea.Right || Left + Width - 100 < workArea.Left ||
                Top + 50 > workArea.Bottom || Top < workArea.Top)
            {
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
            return;
        }

        var topLeft = PointFromScreen(new WpfPoint(phys.Left, phys.Top));
        var bottomRight = PointFromScreen(new WpfPoint(phys.Right, phys.Bottom));
        var wa = new Rect(
            this.Left + topLeft.X,
            this.Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);

        const double screenMargin = 12d;
        var minLeft = wa.Left + screenMargin;
        var minTop = wa.Top + screenMargin;
        var maxLeft = wa.Right - Width - screenMargin;
        var maxTop = wa.Bottom - Height - screenMargin;

        Left = Math.Min(Math.Max(Left, minLeft), Math.Max(minLeft, maxLeft));
        Top = Math.Min(Math.Max(Top, minTop), Math.Max(minTop, maxTop));
    }

    private void HistoryService_Changed()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            try
            {
                InvalidateHistoryCategoryCaches();
                _pendingHistoryDataRefresh = true;
                QueueHistoryRefresh(reloadFromDisk: false);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("history.history-service-changed", ex);
                _pendingHistoryDataRefresh = false;
                _pendingHistoryUiRefresh = false;
                _pendingHistoryDiskRefresh = false;
                _historyRefreshTimer.Stop();
                if (IsLoaded && HistoryTab.IsChecked == true)
                    ShowHistoryEmptyState("Couldn't refresh history", "Retry loading history. If it still fails, check the app log.", showRetry: true);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ImageSearchIndexService_Changed()
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateImageSearchStatus();
                UpdateImageSearchActionButtons();
                UpdateImageSearchPlaceholderText();
                QueueImageIndexRefresh();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("history.image-search-index-changed", ex);
                SetImageSearchLoading(false, forceIndexed: true);
                HistorySearchStatusText.Text = "Search failed";
            }
        });
    }

    private void ImageSearchIndexService_StatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!IsLoaded || HistoryTab.IsChecked != true || HistoryCategoryCombo.SelectedIndex > 1)
                    return;

                UpdateImageSearchStatus();
                UpdateImageSearchActionButtons();
                UpdateImageSearchPlaceholderText();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("history.image-search-status", ex);
                SetImageSearchLoading(false, forceIndexed: true);
                HistorySearchStatusText.Text = "Search failed";
            }
        });
    }

    private void QueueImageIndexRefresh()
    {
        _pendingImageSearchTextRefresh = true;

        if (!IsLoaded || HistoryTab.IsChecked != true || HistoryCategoryCombo.SelectedIndex > 1)
            return;

        _imageIndexRefreshTimer.Stop();
        _imageIndexRefreshTimer.Start();
    }

    private void QueueImageSearchRefresh()
    {
        if (!IsLoaded || HistoryTab.IsChecked != true || HistoryCategoryCombo.SelectedIndex > 1)
            return;

        if (!_settingsService.Settings.ShowImageSearchBar)
            return;

        _imageSearchDebounceTimer.Stop();
        _imageSearchDebounceTimer.Start();
    }

    private void FlushQueuedImageIndexRefresh()
    {
        _imageIndexRefreshTimer.Stop();

        if (!IsLoaded || HistoryTab.IsChecked != true || HistoryCategoryCombo.SelectedIndex > 1)
            return;

        if (_pendingImageSearchTextRefresh)
        {
            var relevantItems = _historyItems.Count > 0
                ? _historyItems
                : _filteredHistoryItems.Count > 0
                    ? _filteredHistoryItems
                    : _allHistoryItems;
            RefreshImageSearchTexts(relevantItems);
            _pendingImageSearchTextRefresh = false;
        }

        ApplyImageSearchFilter();
    }

    private void FlushQueuedImageSearchRefresh()
    {
        _imageSearchDebounceTimer.Stop();

        if (!IsLoaded || HistoryTab.IsChecked != true || HistoryCategoryCombo.SelectedIndex > 1)
            return;

        if (!_settingsService.Settings.ShowImageSearchBar)
            return;

        ApplyImageSearchFilter();
    }

    private void PollHistoryChanges()
    {
        if (!IsLoaded || HistoryTab.IsChecked != true || _deferHistoryMonitor || _historyLoadInProgress)
        {
            _historyMonitorTimer.Stop();
            return;
        }

        var fingerprint = _historyService.GetDiskFingerprint(_settingsService.Settings.SaveDirectory);
        if (fingerprint == _lastHistoryFingerprint)
            return;

        QueueHistoryRefresh(reloadFromDisk: true);
    }

    private void RefreshHistoryFromDisk()
    {
        _historyService.RecoverFromDirectories(_settingsService.Settings.SaveDirectory);
        _historyService.PruneByRetention(_settingsService.Settings.HistoryRetention);
        _historyService.PruneByCount(_settingsService.Settings.HistoryCountLimit, _settingsService.Settings.HistoryDeleteOriginalOnPrune);
    }

    private void PrimeHistoryFingerprint()
    {
        _lastHistoryFingerprint = _historyService.GetDiskFingerprint(_settingsService.Settings.SaveDirectory);
    }

    private bool CanReuseLoadedImageHistory()
    {
        if (!_historyImageCacheReady || _historyLoadInProgress || _pendingHistoryDiskRefresh)
            return false;

        if (_allHistoryItems.Count == 0)
            return false;

        var fingerprint = _historyService.GetDiskFingerprint(_settingsService.Settings.SaveDirectory);
        return string.Equals(fingerprint, _lastHistoryFingerprint, StringComparison.Ordinal);
    }

    private void UpdateHistoryMonitorState()
    {
        if (HistoryTab.IsChecked == true)
        {
            if (_deferHistoryMonitor || _historyLoadInProgress)
                _historyMonitorTimer.Stop();
            else
            {
                PrimeHistoryFingerprint();
                if (!_historyMonitorTimer.IsEnabled)
                    _historyMonitorTimer.Start();
            }
        }
        else
        {
            _historyMonitorTimer.Stop();
            _lastHistoryFingerprint = null;
        }
    }

    private void QueueHistoryRefresh(bool reloadFromDisk)
    {
        if (!IsLoaded || HistoryTab.IsChecked != true)
            return;

        if (reloadFromDisk)
            _historyImageCacheReady = false;

        _pendingHistoryDiskRefresh |= reloadFromDisk;
        _pendingHistoryUiRefresh = true;
        _historyRefreshTimer.Stop();
        _historyRefreshTimer.Start();
    }

    private void ScheduleHistoryTabLoad(bool preserveTransientState = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ScheduleHistoryTabLoad(preserveTransientState), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        _historyTabLoadVersion++;
        _historyTabLoadPreserveTransientState |= preserveTransientState;
        if (_historyTabLoadScheduled)
            return;

        _historyTabLoadScheduled = true;
        var scheduledVersion = _historyTabLoadVersion;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _historyTabLoadScheduled = false;
            if (!IsLoaded || HistoryTab.IsChecked != true || scheduledVersion != _historyTabLoadVersion)
                return;

            var preserveState = _historyTabLoadPreserveTransientState;
            _historyTabLoadPreserveTransientState = false;
            LoadCurrentHistoryTab(preserveTransientState: preserveState);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private async Task FlushQueuedHistoryRefreshAsync()
    {
        _historyRefreshTimer.Stop();

        if (!IsLoaded || HistoryTab.IsChecked != true)
            return;

        if (_historyRefreshInProgress || _historyLoadInProgress)
        {
            _historyRefreshTimer.Start();
            return;
        }

        _historyRefreshInProgress = true;
        try
        {
            var reloadFromDisk = _pendingHistoryDiskRefresh;
            var refreshLoadedData = _pendingHistoryDataRefresh;
            _pendingHistoryDiskRefresh = false;
            _pendingHistoryDataRefresh = false;
            _pendingHistoryUiRefresh = false;

            if (reloadFromDisk)
            {
                await Task.Run(RefreshHistoryFromDisk);
            }

            if (!reloadFromDisk && refreshLoadedData &&
                HistoryCategoryCombo.SelectedIndex == 1 && // Only incremental for Images view
                TryRefreshLoadedImageHistoryIncrementally())
            {
                PrimeHistoryFingerprint();
                UpdateCategoryCounts();
            }
            else
            {
                ScheduleHistoryTabLoad(preserveTransientState: true);
                PrimeHistoryFingerprint();
            }
        }
        finally
        {
            _historyRefreshInProgress = false;
            if (_pendingHistoryDiskRefresh || _pendingHistoryDataRefresh || _pendingHistoryUiRefresh)
                _historyRefreshTimer.Start();
        }
    }

    private void HistoryCategoryCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // Persist the selected filter
        _settingsService.Settings.HistoryCategoryFilter = HistoryCategoryCombo.SelectedIndex;
        _settingsService.Save();
        UpdateImageSearchUi();
        ScheduleHistoryTabLoad(preserveTransientState: true);
    }

    private void HistoryCategoryCombo_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox)
            return;

        if (comboBox.IsDropDownOpen)
            return;

        comboBox.IsDropDownOpen = true;
        e.Handled = true;
    }

    private void LoadCurrentHistoryTab(bool preserveTransientState = false)
    {
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var selectedCategory = HistoryCategoryCombo.SelectedIndex;
        if (!preserveTransientState)
        {
            _selectMode = false;
            UpdateSelectModeControls();
            _ocrSearchQuery = "";
            _colorSearchQuery = "";
            _codeSearchQuery = "";
            _imageSearchQuery = "";
            if (ImageSearchBox != null) ImageSearchBox.Text = "";
        }

        ImagesPanel.Visibility = Visibility.Collapsed;
        GifsPanel.Visibility = Visibility.Collapsed;
        TextPanel.Visibility = Visibility.Collapsed;
        ColorsPanel.Visibility = Visibility.Collapsed;
        CodesPanel.Visibility = Visibility.Collapsed;
        UpdateImageSearchUi();

        if (HistoryCategoryCombo.SelectedIndex > 1) // not All or Images
            CancelImageSearchWork();

        switch (HistoryCategoryCombo.SelectedIndex)
        {
            case 0: // All
                ImagesPanel.Visibility = Visibility.Visible;
                LoadAllHistory();
                break;
            case 1: // Images
                ImagesPanel.Visibility = Visibility.Visible;
                if (CanReuseLoadedImageHistory())
                    ApplyImageSearchFilter();
                else
                    _ = LoadHistoryAsync();
                break;
            case 2: TextPanel.Visibility = Visibility.Visible; LoadOcrHistory(); break;
            case 3: GifsPanel.Visibility = Visibility.Visible; LoadMediaHistory(); break;
            case 4: ColorsPanel.Visibility = Visibility.Visible; LoadColorHistory(); break;
            case 5: CodesPanel.Visibility = Visibility.Visible; LoadCodeHistory(); break;
        }

        UpdateHistoryMonitorState();
        UpdateHistoryActionButtons();
        UpdateCategoryCounts();
        loadSw.Stop();
        AppDiagnostics.LogInfo(
            "history.tab-load",
            $"category={selectedCategory} preserve={preserveTransientState} elapsedMs={loadSw.ElapsedMilliseconds}");
    }

    private void UpdateCategoryCounts()
    {
        if (HistoryCategoryCombo == null || HistoryCategoryCombo.Items.Count < 6)
            return;

        var lang = _settingsService.Settings.InterfaceLanguage;
        var allBase = LocalizationService.Translate(lang, "All");
        var imagesBase = LocalizationService.Translate(lang, "Images");
        var textBase = LocalizationService.Translate(lang, "Text");
        var mediaBase = LocalizationService.Translate(lang, "Videos/GIFs");
        var colorsBase = LocalizationService.Translate(lang, "Colors");
        var codesBase = LocalizationService.Translate(lang, "QR & Barcodes");

        var imagesCount = _historyService.ImageEntries.Count;
        var textCount = _historyService.OcrEntries.Count;
        var mediaCount = _historyService.MediaEntries.Count;
        var colorsCount = _historyService.ColorEntries.Count;
        var codesCount = _historyService.CodeEntries.Count;
        var totalCount = imagesCount + textCount + mediaCount + colorsCount + codesCount;

        if (HistoryCategoryCombo.Items[0] is ComboBoxItem item0)
            item0.Content = $"{allBase} ({totalCount})";
        if (HistoryCategoryCombo.Items[1] is ComboBoxItem item1)
            item1.Content = $"{imagesBase} ({imagesCount})";
        if (HistoryCategoryCombo.Items[2] is ComboBoxItem item2)
            item2.Content = $"{textBase} ({textCount})";
        if (HistoryCategoryCombo.Items[3] is ComboBoxItem item3)
            item3.Content = $"{mediaBase} ({mediaCount})";
        if (HistoryCategoryCombo.Items[4] is ComboBoxItem item4)
            item4.Content = $"{colorsBase} ({colorsCount})";
        if (HistoryCategoryCombo.Items[5] is ComboBoxItem item5)
            item5.Content = $"{codesBase} ({codesCount})";

        var selectedIndex = HistoryCategoryCombo.SelectedIndex;
        if (selectedIndex >= 0)
        {
            HistoryCategoryCombo.SelectionChanged -= HistoryCategoryCombo_Changed;
            HistoryCategoryCombo.SelectedIndex = -1;
            HistoryCategoryCombo.SelectedIndex = selectedIndex;
            HistoryCategoryCombo.SelectionChanged += HistoryCategoryCombo_Changed;
        }
    }
}
