using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.AppModel.Settings;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    // ── Search model ──
    private enum SearchEntrySource { Schema, VisualTree }

    private sealed class SettingsSearchEntry
    {
        public string PageKey { get; init; } = "";
        public string PageTitle { get; init; } = "";
        public string? SectionTitle { get; init; }
        public string MatchText { get; init; } = "";
        public string ContextText { get; init; } = "";
        public SearchEntrySource Source { get; init; }
        public FrameworkElement? TargetElement { get; init; }
        public string? TargetSettingKey { get; init; }
    }

    // ── Search state ──
    private List<SettingsSearchEntry> _searchIndex = [];
    private List<SettingsSearchEntry> _filteredResults = [];
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _suppressSearchTextEvents;
    private bool _isSearching;
    private sealed class MovedElementInfo
    {
        public FrameworkElement Element { get; set; } = null!;
        public System.Windows.Controls.Panel OriginalParent { get; set; } = null!;
        public int OriginalIndex { get; set; }
    }
    private readonly List<MovedElementInfo> _movedElements = [];

    // ── Panel → PageKey mapping ──
    private readonly Dictionary<ScrollViewer, (string PageKey, string PageTitle)> _panelMap = [];

    // ── Initialization (called from constructor after LoadSettings) ──
    private void InitializeSearch()
    {
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplySettingsSearch();
        };

        // Ctrl+F to open search
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SettingsSearchBar.Visibility == Visibility.Visible)
            {
                HideSearchBar();
                e.Handled = true;
            }
        };

        // Rebuild index when localization changes
        LocalizationChanged += () =>
        {
            if (IsLoaded)
            {
                BuildSearchIndex();
                RefreshSearchTooltips();
            }
        };

        // Apply translated tooltips immediately (before ApplyTo may run)
        RefreshSearchTooltips();

        // Build on first load, then again after a short delay to capture
        // any late-applied localization (e.g., TextBlock.Text set after Loaded)
        Loaded += (_, _) =>
        {
            BuildSearchIndex();
            RefreshSearchTooltips();
            // Second pass: catch localized TextBlocks that were set after Loaded
            var deferredTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            deferredTimer.Tick += (s, args) =>
            {
                ((DispatcherTimer)s!).Stop();
                BuildSearchIndex();
            };
            deferredTimer.Start();
        };
    }

    // ── Index building ──
    private void BuildSearchIndex()
    {
        var entries = new List<SettingsSearchEntry>();

        // ─── Source 1: SchemaCatalog ───
        foreach (var page in SettingsSchemaCatalog.Pages)
        {
            var translatedPageTitle = LocalizationService.Translate(page.Title);
            var translatedPageDesc = LocalizationService.Translate(page.Description);

            entries.Add(new SettingsSearchEntry
            {
                PageKey = page.Key,
                PageTitle = translatedPageTitle,
                MatchText = translatedPageTitle,
                ContextText = translatedPageDesc,
                Source = SearchEntrySource.Schema
            });

            foreach (var section in page.Sections)
            {
                var translatedSectionTitle = LocalizationService.Translate(section.Title);
                var translatedSectionDesc = LocalizationService.Translate(section.Description);

                entries.Add(new SettingsSearchEntry
                {
                    PageKey = page.Key,
                    PageTitle = translatedPageTitle,
                    SectionTitle = translatedSectionTitle,
                    MatchText = translatedSectionTitle,
                    ContextText = translatedSectionDesc,
                    Source = SearchEntrySource.Schema
                });

                foreach (var setting in section.Items)
                {
                    var translatedLabel = LocalizationService.Translate(setting.Label);
                    var translatedDesc = LocalizationService.Translate(setting.Description);

                    entries.Add(new SettingsSearchEntry
                    {
                        PageKey = page.Key,
                        PageTitle = translatedPageTitle,
                        SectionTitle = translatedSectionTitle,
                        MatchText = translatedLabel,
                        ContextText = translatedDesc,
                        Source = SearchEntrySource.Schema,
                        TargetSettingKey = setting.Key
                    });

                    if (!string.IsNullOrWhiteSpace(translatedDesc) && translatedDesc.Length < 150)
                    {
                        entries.Add(new SettingsSearchEntry
                        {
                            PageKey = page.Key,
                            PageTitle = translatedPageTitle,
                            SectionTitle = translatedSectionTitle,
                            MatchText = translatedDesc,
                            ContextText = translatedLabel,
                            Source = SearchEntrySource.Schema,
                            TargetSettingKey = setting.Key
                        });
                    }
                }
            }
        }

        // ─── Source 2: Visual tree discovery ───
        var panelDefs = new (ScrollViewer Panel, string PageKey, string PageTitle)[]
        {
            (SettingsPanel,   "general",       "General"),
            (SoundsPanel,     "sounds",        "Sounds"),
            (WidgetPanel,     "widget",        "Widget"),
            (ToastPanel,      "notifications", "Notifications"),
            (CapturePanel,    "capture",       "Capture"),
            (EditorPanel,     "editor",        "Editor"),
            (RecordingPanel,  "recording",     "Video"),
            (OcrPanel,        "ocr",           "OCR & Translation"),
            (HotkeysPanel,    "hotkeys",       "Hotkeys"),
            (HistoryPanel,    "history",       "Gallery"),
            (AchievementsPanel, "achievements", "Achievements"),
            (AboutPanel,      "about",         "About"),
        };

        _panelMap.Clear();
        foreach (var (panel, pageKey, pageTitle) in panelDefs)
        {
            _panelMap[panel] = (pageKey, pageTitle);

            // Force template expansion so children exist in the logical tree
            panel.ApplyTemplate();

            string? currentSection = null;
            DiscoverTextsInTree(panel, pageKey, pageTitle, ref currentSection, entries);
        }

        // ─── Deduplicate ───
        _searchIndex = entries
            .GroupBy(e => (e.PageKey, NormalizeForDedup(e.MatchText)))
            .Select(g => g.First())
            .OrderBy(e => e.PageTitle)
            .ThenBy(e => e.SectionTitle ?? "")
            .ThenBy(e => e.MatchText)
            .ToList();
    }

    private static void DiscoverTextsInTree(
        DependencyObject root, string pageKey, string pageTitle,
        ref string? currentSection, List<SettingsSearchEntry> entries)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject depChild)
                continue;

            if (depChild is TextBlock tb)
            {
                var text = tb.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length is >= 2 and <= 200)
                {
                    string? kind = ClassifyTextBlock(tb);

                    if (kind == "SectionLabel")
                        currentSection = text;

                    // Index all meaningful TextBlocks
                    entries.Add(new SettingsSearchEntry
                    {
                        PageKey = pageKey,
                        PageTitle = pageTitle,
                        SectionTitle = kind == "SectionLabel" ? text : currentSection,
                        MatchText = text,
                        ContextText = kind ?? "",
                        Source = SearchEntrySource.VisualTree,
                        TargetElement = tb
                    });
                }
            }

            // Recurse
            DiscoverTextsInTree(depChild, pageKey, pageTitle, ref currentSection, entries);
        }
    }

    /// <summary>
    /// Heuristic classification of a TextBlock based on its styled appearance.
    /// This lets us show section headers differently in results.
    /// </summary>
    private static string? ClassifyTextBlock(TextBlock tb)
    {
        if (tb.FontSize >= 13 && tb.FontWeight == FontWeights.SemiBold && Math.Abs(tb.Opacity - 1.0) < 0.05)
            return "SectionLabel";
        if (tb.FontSize >= 12.5 && tb.FontSize <= 13.5 && tb.FontWeight == FontWeights.Normal)
            return "SettingTitle";
        if (tb.FontSize is >= 11 and <= 12 && tb.Opacity is >= 0.55 and <= 0.80)
            return "SettingDescription";
        return null;
    }

    private static string NormalizeForDedup(string s) =>
        s.ToLowerInvariant().Trim();

    // ── Filtering & scoring ──
    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchTextEvents || !IsLoaded)
            return;

        var text = SettingsSearchBox.Text ?? "";
        SettingsSearchClearBtn.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible : Visibility.Collapsed;

        _searchDebounceTimer.Stop();
        if (string.IsNullOrWhiteSpace(text))
        {
            _filteredResults.Clear();
            SettingsSearchCount.Text = "";
            RestoreMovedElements();
            if (_isSearching)
            {
                _isSearching = false;
                ApplyMainTabSelection();
            }
            return;
        }

        _searchDebounceTimer.Start();
    }

    private void ApplySettingsSearch()
    {
        var query = (SettingsSearchBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query) || _searchIndex.Count == 0)
        {
            _filteredResults.Clear();
            SettingsSearchCount.Text = "";
            RestoreMovedElements();
            if (_isSearching)
            {
                _isSearching = false;
                ApplyMainTabSelection();
            }
            return;
        }

        var normalized = query.ToLowerInvariant();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        _filteredResults = _searchIndex
            .Select(e => new { Entry = e, Score = ScoreEntry(e, normalized, tokens) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.PageTitle)
            .ThenBy(x => x.Entry.MatchText.Length)
            .Take(25)
            .Select(x => x.Entry)
            .ToList();

        if (_filteredResults.Count == 0 && normalized.Length >= 3)
        {
            _filteredResults = _searchIndex
                .Select(e => new { Entry = e, Score = ScoreEntryFuzzy(e, normalized) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Entry.MatchText.Length)
                .Take(15)
                .Select(x => x.Entry)
                .ToList();
        }

        _isSearching = true;
        ApplyMainTabSelection();
        PerformFilterSearch();
    }

    private void PerformFilterSearch()
    {
        RestoreMovedElements();

        if (_filteredResults.Count == 0)
        {
            SettingsSearchCount.Text = LocalizationService.Translate("No results");
            return;
        }

        var seenContainers = new HashSet<FrameworkElement>();
        var containerEntries = new List<(FrameworkElement Container, SettingsSearchEntry Entry)>();

        foreach (var entry in _filteredResults)
        {
            var container = FindSettingContainer(entry);
            if (container == null || seenContainers.Contains(container))
                continue;

            var parent = GetParentPanel(container);
            if (parent != null)
            {
                seenContainers.Add(container);
                containerEntries.Add((container, entry));
            }
        }

        foreach (var (container, entry) in containerEntries)
        {
            var originalParent = GetParentPanel(container);
            if (originalParent != null)
            {
                int originalIndex = originalParent.Children.IndexOf(container);
                if (originalIndex >= 0)
                {
                    _movedElements.Add(new MovedElementInfo
                    {
                        Element = container,
                        OriginalParent = originalParent,
                        OriginalIndex = originalIndex
                    });

                    originalParent.Children.Remove(container);

                    var cardBorder = new Border
                    {
                        Style = (Style)FindResource("Card"),
                        Margin = new Thickness(0, 0, 0, 12),
                        Padding = new Thickness(16, 12, 16, 12)
                    };

                    var cardStack = new StackPanel();

                    var pageTitle = LocalizationService.Translate(entry.PageTitle);
                    var sectionTitle = !string.IsNullOrEmpty(entry.SectionTitle)
                        ? LocalizationService.Translate(entry.SectionTitle)
                        : "";
                    var categoryText = string.IsNullOrEmpty(sectionTitle)
                        ? pageTitle
                        : $"{pageTitle} › {sectionTitle}";

                    var categoryLabel = new TextBlock
                    {
                        Text = categoryText.ToUpperInvariant(),
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = (System.Windows.Media.Brush)FindResource("ThemeAccentBrush"),
                        Opacity = 0.75,
                        Margin = new Thickness(0, 0, 0, 8),
                        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display")
                    };
                    cardStack.Children.Add(categoryLabel);
                    cardStack.Children.Add(container);

                    cardBorder.Child = cardStack;
                    SearchResultsStack.Children.Add(cardBorder);
                }
            }
        }

        int count = containerEntries.Count;
        SettingsSearchCount.Text = count > 0
            ? $"{count} {LocalizationService.Translate(count == 1 ? "result" : "results")}"
            : LocalizationService.Translate("No results");
    }

    private void RestoreMovedElements()
    {
        if (_movedElements.Count == 0) return;

        for (int i = _movedElements.Count - 1; i >= 0; i--)
        {
            var info = _movedElements[i];
            try
            {
                if (info.Element.Parent is System.Windows.Controls.Panel tempParent)
                {
                    tempParent.Children.Remove(info.Element);
                }
                info.OriginalParent.Children.Insert(info.OriginalIndex, info.Element);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning("settings.search-restore", $"Failed to restore search element: {ex.Message}");
            }
        }
        _movedElements.Clear();

        while (SearchResultsStack.Children.Count > 1)
        {
            SearchResultsStack.Children.RemoveAt(1);
        }
    }

    private FrameworkElement? FindSettingContainer(SettingsSearchEntry entry)
    {
        var element = entry.TargetElement;
        if (element == null && entry.TargetSettingKey != null)
        {
            element = FindSettingControl(entry.TargetSettingKey);
        }

        if (element == null) return null;

        var rowStyle = FindResource("SettingRow") as Style;
        var compactCardStyle = FindResource("CompactItemCard") as Style;
        var soundItemStyle = FindResource("SoundItemCard") as Style;

        DependencyObject? parent = element;
        FrameworkElement? container = null;

        while (parent != null)
        {
            if (parent is FrameworkElement fe)
            {
                if (rowStyle != null && fe is Grid g && g.Style == rowStyle)
                {
                    container = g;
                    break;
                }
                if (compactCardStyle != null && fe is Border cb && cb.Style == compactCardStyle)
                {
                    container = cb;
                    break;
                }
                if (soundItemStyle != null && fe is Border sb && sb.Style == soundItemStyle)
                {
                    container = sb;
                    break;
                }
            }
            parent = GetParent(parent);
        }

        if (container != null)
            return container;

        if (element is System.Windows.Controls.Control && element is not TextBlock)
        {
            return element;
        }

        return null;
    }

    private DependencyObject? GetParent(DependencyObject element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        if (parent == null && element is FrameworkElement fe)
        {
            parent = LogicalTreeHelper.GetParent(fe) ?? fe.Parent;
        }
        return parent;
    }

    private System.Windows.Controls.Panel? GetParentPanel(DependencyObject element)
    {
        return GetParent(element) as System.Windows.Controls.Panel;
    }

    private static double ScoreEntry(SettingsSearchEntry entry, string normalized, string[] tokens)
    {
        var matchText = NormalizeForDedup(entry.MatchText);
        double score = 0;

        if (matchText.Equals(normalized, StringComparison.Ordinal))
            score += 10;
        else if (matchText.StartsWith(normalized, StringComparison.Ordinal))
            score += 7;
        else
        {
            foreach (var token in tokens)
            {
                if (matchText.Contains(token, StringComparison.Ordinal))
                    score += 3;
                else if (NormalizeForDedup(entry.ContextText).Contains(token, StringComparison.Ordinal))
                    score += 1;
            }
        }

        if (entry.SectionTitle != null &&
            NormalizeForDedup(entry.SectionTitle).Contains(normalized, StringComparison.Ordinal))
            score += 2;

        if (NormalizeForDedup(entry.PageTitle).Contains(normalized, StringComparison.Ordinal))
            score += 1.5;

        return score;
    }

    private static double ScoreEntryFuzzy(SettingsSearchEntry entry, string normalized)
    {
        var matchText = NormalizeForDedup(entry.MatchText);
        double score = DensitySubsequenceScore(normalized, matchText);
        if (score <= 0 && !string.IsNullOrWhiteSpace(entry.ContextText))
            score = DensitySubsequenceScore(normalized, NormalizeForDedup(entry.ContextText)) * 0.6;
        if (score <= 0 && entry.SectionTitle != null)
            score = DensitySubsequenceScore(normalized, NormalizeForDedup(entry.SectionTitle)) * 0.4;
        return score;
    }

    private static double DensitySubsequenceScore(string query, string target)
    {
        if (query.Length == 0 || target.Length == 0) return 0;

        int maxSpan = query.Length * 4;
        int qi = 0;
        int firstMatch = -1;
        int lastMatch = -1;

        for (int ti = 0; ti < target.Length && qi < query.Length; ti++)
        {
            if (target[ti] == query[qi])
            {
                if (firstMatch < 0) firstMatch = ti;
                lastMatch = ti;
                qi++;
            }
        }

        if (qi != query.Length) return 0;

        int span = lastMatch - firstMatch;
        if (span > maxSpan) return 0;

        return 1.0 - (double)span / maxSpan;
    }

    private void SettingsSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideSearchBar();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Open the dedicated Widget tab. Used by the capture widget's Config menu
    /// to jump straight to all of its settings.</summary>
    public void NavigateToWidgetSettings()
    {
        try
        {
            SelectSettingsTab("widget");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.widget-navigate", $"Navigation failed: {ex.Message}");
        }
    }

    /// <summary>Open the dedicated Editor tab. Used by the editor's burger menu
    /// to jump straight to editor settings.</summary>
    public void NavigateToEditorSettings()
    {
        try
        {
            SelectSettingsTab("editor");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.editor-navigate", $"Navigation failed: {ex.Message}");
        }
    }

    /// <summary>Open the dedicated About tab. Used by the burger menu
    /// to jump straight to the About section.</summary>
    public void NavigateToGallerySettings()
    {
        try
        {
            SelectSettingsTab("history");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.gallery-navigate", $"Navigation failed: {ex.Message}");
        }
    }

    public void NavigateToAboutSettings()
    {
        try
        {
            SelectSettingsTab("about");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.about-navigate", $"Navigation failed: {ex.Message}");
        }
    }

    private void SelectSettingsTab(string pageKey)
    {
        var tabMap = new Dictionary<string, System.Windows.Controls.RadioButton>
        {
            ["general"]       = SettingsTab,
            ["sounds"]        = SoundsTab,
            ["widget"]        = WidgetTab,
            ["notifications"] = ToastTab,
            ["capture"]       = CaptureTab,
            ["recording"]     = RecordingTab,
            ["ocr"]           = OcrTab,
            ["hotkeys"]       = HotkeysTab,
            ["history"]       = HistoryTab,
            ["achievements"]  = AchievementsTab,
            ["runtimes"]      = AboutTab,
            ["about"]         = AboutTab,
            ["editor"]        = EditorTab,
        };

        if (tabMap.TryGetValue(pageKey, out var tab))
        {
            tab.IsChecked = true;
            ApplyMainTabSelection();
        }
    }

    private static void ScrollToElement(FrameworkElement element)
    {
        // Walk up to find the containing ScrollViewer and bring the element into view
        element.BringIntoView();

        // Fine-tune: center the element vertically in the viewport
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null)
        {
            if (parent is ScrollViewer sv)
            {
                var transform = element.TransformToAncestor(sv);
                var pos = transform.Transform(new System.Windows.Point(0, 0));
                var elementHeight = element.ActualHeight > 0 ? element.ActualHeight : 52;
                var centerOffset = Math.Max(0, pos.Y - (sv.ViewportHeight / 2) + (elementHeight / 2));
                sv.ScrollToVerticalOffset(centerOffset);
                break;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
    }

    private void HighlightSettingCard(FrameworkElement element)
    {
        try
        {
            // Priority: nearest SettingRow > nearest Card > parent Border > element itself
            FrameworkElement? settingRow = null;
            FrameworkElement? card = null;
            DependencyObject? parent = element;

            while (parent != null)
            {
                if (settingRow == null &&
                    parent is Grid grid &&
                    grid.Style == FindResource("SettingRow") as Style)
                {
                    settingRow = grid;
                }
                if (card == null &&
                    parent is Border border &&
                    border.Style == FindResource("Card") as Style)
                {
                    card = border;
                }
                // Once both found, stop walking
                if (settingRow != null && card != null)
                    break;
                parent = VisualTreeHelper.GetParent(parent);
            }

            // Prefer the most granular target
            FrameworkElement? target = settingRow ?? card;

            // Fallback: use the element itself or its immediate parent Border
            target ??= element is Border b ? b : FindParentBorder(element) ?? element;

            AnimateHighlight(target);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.search-highlight", $"Highlight failed: {ex.Message}");
        }
    }

    private static Border? FindParentBorder(DependencyObject element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null)
        {
            if (parent is Border b) return b;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void AnimateHighlight(FrameworkElement element)
    {
        if (element == null) return;

        var accentColor = ((SolidColorBrush)FindResource("ThemeAccentBrush")).Color;
        var highlightBrush = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(50, accentColor.R, accentColor.G, accentColor.B));

        System.Windows.Media.Brush originalBg;

        Action<System.Windows.Media.Brush> setBg;
        Func<System.Windows.Media.Brush> getBg;

        if (element is System.Windows.Controls.Panel panel)
        {
            getBg = () => panel.Background;
            setBg = b => panel.Background = b;
        }
        else if (element is Border border)
        {
            getBg = () => border.Background;
            setBg = b => border.Background = b;
        }
        else if (element is System.Windows.Controls.Control control)
        {
            getBg = () => control.Background;
            setBg = b => control.Background = b;
        }
        else
        {
            return;
        }

        originalBg = getBg();

        // Triple pulse: highlight → original → highlight → original → highlight → original
        // Each phase = 160ms, total = 6 phases = 960ms
        int pulseStep = 0;
        const int totalSteps = 6;
        var pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        pulseTimer.Tick += (_, _) =>
        {
            pulseStep++;
            if (pulseStep >= totalSteps)
            {
                pulseTimer.Stop();
                setBg(originalBg);
                return;
            }
            // Odd steps = highlighted, even steps = original
            setBg(pulseStep % 2 == 1 ? highlightBrush : originalBg);
        };
        pulseTimer.Start();
    }

    private FrameworkElement? FindSettingControl(string settingKey)
    {
        return settingKey switch
        {
            // General / Startup
            "StartWithWindows"          => StartWithWindowsCheck,
            "AfterCapture"              => AfterCaptureCombo,
            // General / Output
            "SaveToFile"                => SaveToFileCheck,
            "SaveDirectory"             => SaveDirBox,
            "SaveInMonthlyFolders"      => MonthlyFoldersCheck,
            "CaptureImageFormat"        => CaptureFormatCombo,
            "FileNameTemplate"          => FileNameTemplateBox,
            // Capture / Overlay
            "ShowCrosshairGuides"       => CrosshairGuidesCheck,
            "ShowCaptureMagnifier"      => ShowCaptureMagnifierCheck,
            "DetectWindows"             => WindowDetectionCheck,
            "CaptureDockSide"           => CaptureDockSideCombo,
            // Capture / Screenshot styling
            "StyleScreenshots"          => FindName("StyleScreenshotsCheck") as FrameworkElement,
            "AddScreenshotShadow"       => FindName("AddScreenshotShadowCheck") as FrameworkElement,
            "AddScreenshotStroke"       => FindName("AddScreenshotStrokeCheck") as FrameworkElement,
            // Recording
            "RecordingFormat"           => FindName("RecordingFormatCombo") as FrameworkElement,
            "RecordingQuality"          => RecordingQualityCombo,
            "RecordingFps"              => RecordingFpsCombo,
            "RecordMicrophone"          => RecordMicCheck,
            "RecordDesktopAudio"        => RecordDesktopAudioCheck,
            // OCR
            "OcrLanguageTag"            => OcrLanguageCombo,
            "OcrDefaultTranslateFrom"   => TranslateFromCombo,
            "OcrDefaultTranslateTo"     => TranslateToCombo,
            "TranslationModel"          => FindName("TranslationModelCombo") as FrameworkElement,
            // History
            "SaveHistory"               => SaveHistoryCheck,
            "HistoryRetention"          => HistoryRetentionCombo,
            "CompressHistory"           => FindName("CompressHistoryCheck") as FrameworkElement,
            "AutoIndexImages"           => AutoIndexImagesCheck,
            "ShowImageSearchBar"        => ShowImageSearchBarCheck,
            "ImageSearchSources"        => FindName("ImageSearchSourcesCombo") as FrameworkElement,
            // Interface language
            "InterfaceLanguage"         => InterfaceLanguageCombo,
            // General / Widgets
            "ShowCaptureWidget"         => ShowCaptureWidgetCheck,
            "WidgetEnableEditor"        => WidgetEnableEditorCheck,
            "VideoEnableEditor"         => VideoEnableEditorCheck,
            "WidgetDockEdge"            => WidgetDockEdgeCombo,
            "WidgetHoverDelay"          => WidgetHoverDelayCombo,
            "WidgetAlwaysOnTop"         => WidgetAlwaysOnTopCheck,
            _ => null
        };
    }

    // ── Show / Hide ──
    /// <summary>Public toggle for the search bar, called from the burger menu.</summary>
    public void ToggleSearchBar()
    {
        if (SettingsSearchBar.Visibility == Visibility.Visible)
            HideSearchBar();
        else
            ShowSearchBar();
    }

    /// <summary>Returns whether the search bar is currently visible.</summary>
    public bool IsSearchBarVisible() => SettingsSearchBar.Visibility == Visibility.Visible;

    private void SettingsSearchToggle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (SettingsSearchBar.Visibility == Visibility.Visible)
            HideSearchBar();
        else
            ShowSearchBar();
    }

    /// <summary>Re-applies translated tooltips for all search bar elements.</summary>
    private void RefreshSearchTooltips()
    {
        SettingsSearchToggleBtn.ToolTip = LocalizationService.Translate("Search settings (Ctrl+F)");
        SettingsSearchBox.ToolTip = LocalizationService.Translate("Search settings — type a setting name, section, or keyword (Ctrl+F)");
        SettingsSearchClearBtn.ToolTip = LocalizationService.Translate("Clear search");
        SettingsSearchPlaceholder.Text = LocalizationService.Translate("Search settings...");
    }

    private void ShowSearchBar()
    {
        SettingsSearchBar.Visibility = Visibility.Visible;
        SettingsSearchToggleBtn.Text = "\uE711";         // ✕ cancel icon
        SettingsSearchToggleBtn.Opacity = 0.85;
        SettingsSearchToggleBtn.ToolTip = LocalizationService.Translate("Close search (Esc)");
        PageTitleText.Margin = new Thickness(18, 50, 18, 0);
        AdjustPanelTopPadding(42 + 44);
        SettingsSearchBox.Focus();
        SettingsSearchBox.SelectAll();
    }

    private void HideSearchBar()
    {
        _suppressSearchTextEvents = true;
        SettingsSearchBox.Clear();
        _suppressSearchTextEvents = false;

        SettingsSearchBar.Visibility = Visibility.Collapsed;
        SettingsSearchToggleBtn.Text = "\uE721";         // 🔍 search icon
        SettingsSearchToggleBtn.Opacity = 0.55;
        SettingsSearchToggleBtn.ToolTip = LocalizationService.Translate("Search settings (Ctrl+F)");
        _filteredResults.Clear();
        SettingsSearchCount.Text = "";
        SettingsSearchPlaceholder.Visibility = Visibility.Visible;
        SettingsSearchClearBtn.Visibility = Visibility.Collapsed;

        PageTitleText.Margin = new Thickness(18, 10, 18, 0);
        AdjustPanelTopPadding(42);

        RestoreMovedElements();
        _isSearching = false;
        ApplyMainTabSelection();

        Focus();
    }

    private void AdjustPanelTopPadding(double topPadding)
    {
        var panels = new ScrollViewer[]
        {
            SettingsPanel, SoundsPanel, WidgetPanel, ToastPanel,
            CapturePanel, EditorPanel, RecordingPanel, OcrPanel,
            HotkeysPanel, HistoryPanel, AchievementsPanel, AboutPanel,
            SearchResultsPanel
        };
        foreach (var panel in panels)
        {
            var current = panel.Padding;
            panel.Padding = new Thickness(current.Left, topPadding, current.Right, current.Bottom);
        }
    }

    private void SettingsSearchClear_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _suppressSearchTextEvents = true;
        SettingsSearchBox.Clear();
        _suppressSearchTextEvents = false;
        _filteredResults.Clear();
        SettingsSearchCount.Text = "";
        SettingsSearchPlaceholder.Visibility = Visibility.Visible;
        SettingsSearchClearBtn.Visibility = Visibility.Collapsed;

        RestoreMovedElements();
        _isSearching = false;
        ApplyMainTabSelection();

        SettingsSearchBox.Focus();
    }

    private void SettingsSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
    }
}
