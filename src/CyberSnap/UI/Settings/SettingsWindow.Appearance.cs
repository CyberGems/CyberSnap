using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
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
        Icon = WindowIcons.Wpf(WindowIconKind.Settings);
        Foreground = Theme.Brush(Theme.TextPrimary);
        // SettingsWindow manages its own size via LoadWindowBounds/SaveWindowBounds,
        // so don't let UiScale rescale window bounds — only apply the LayoutTransform.
        UiScale.ApplyToWindow(this, OuterBorder, scaleWindowBounds: false);

        ApplyThemeToVisualTree(OuterBorder);
        UpdateSectionIcons();
        RefreshToastButtonLayoutDesigner();
    }

    private void UpdateSectionIcons()
    {
        // Determine foreground color for icons based on theme darkness
        var foreground = Theme.IsDark ? Colors.White : Colors.Black;

        var iconMap = new System.Collections.Generic.Dictionary<System.Windows.Controls.RadioButton, string>
        {
            [SettingsTab] = "\uE713", // General
            [ToastTab] = "\uEA8F", // Notifications
            [SoundsTab] = "\uE767", // Sounds
            [CaptureTab] = "\uE7C2", // Capture
            [RecordingTab] = "\uE768", // Video
            [OcrTab] = "\uE8C8", // OCR
            [HotkeysTab] = "\uE765", // Hotkeys
            [HistoryTab] = "\uEB9F", // History
            [AchievementsTab] = "\uE735", // Achievements (star)
            [AboutTab] = "\uE946" // About
        };

        foreach (var kvp in iconMap)
        {
            var radio = kvp.Key;
            var glyph = kvp.Value;
            // Ensure template is applied before searching for named parts
            radio.ApplyTemplate();
            if (radio.Template.FindName("Icon", radio) is TextBlock iconTextBlock)
            {
                iconTextBlock.Text = glyph;
                iconTextBlock.Foreground = new SolidColorBrush(foreground);
            }
        }
    }

    private void ApplyThemeToVisualTree(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            switch (child)
            {
                case System.Windows.Controls.TextBox textBox:
                    textBox.Background = (MediaBrush)Resources["ThemeInputBackgroundBrush"];
                    textBox.Foreground = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    textBox.BorderBrush = (MediaBrush)Resources["ThemeInputBorderBrush"];
                    textBox.CaretBrush = (MediaBrush)Resources["ThemeTextPrimaryBrush"];
                    break;
                case System.Windows.Controls.ComboBox comboBox:
                    comboBox.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ThemeInputBackgroundBrush");
                    comboBox.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ThemeTextPrimaryBrush");
                    comboBox.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "ThemeInputBorderBrush");
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

    private void LoadSettings()
    {
        _suppressGeneralPreferenceChange = true;
        try
        {
            var s = _settingsService.Settings;


            TryLoadSettingsSection("settings.load-ocr-languages", LoadOcrLanguageOptions);

            PopulateInterfaceLanguageOptions();
            SelectInterfaceLanguage(s.InterfaceLanguage);
            DefaultCaptureModeCombo.SelectedIndex = s.DefaultCaptureMode switch
            {
                CaptureMode.Center => 1,
                CaptureMode.Freeform => 2,
                _ => 0
            };
            var isCenterDefault = DefaultCaptureModeCombo.SelectedIndex == 1;
            CenterAspectRatioRow.Visibility = isCenterDefault ? Visibility.Visible : Visibility.Collapsed;
            CenterAspectRatioSeparator.Visibility = isCenterDefault ? Visibility.Visible : Visibility.Collapsed;
            CenterAspectRatioCombo.SelectedIndex = Enum.IsDefined(typeof(CenterSelectionAspectRatio), s.CenterSelectionAspectRatio)
                ? (int)s.CenterSelectionAspectRatio
                : 0;

            var afterCaptureView = GetAfterCaptureViewPreference();
            AfterCaptureCombo.SelectedIndex = afterCaptureView.WindowIndex;
            AfterCaptureCopyCheck.IsChecked = afterCaptureView.Copy;
            RefreshAfterCaptureSummary(afterCaptureView);

            SaveToFileCheck.IsChecked = s.SaveToFile;
            UpdateSaveToFileState();
            AskFileNameCheck.IsChecked = s.AskForFileNameOnSave;
            CaptureFormatCombo.SelectedIndex = (int)s.CaptureImageFormat;
            JpegQualityCombo.SelectedIndex = s.JpegQuality switch
            {
                >= 95 => 0,
                >= 90 => 1,
                >= 85 => 2,
                >= 75 => 3,
                _ => 4
            };
            CaptureSizeCombo.SelectedIndex = s.CaptureMaxLongEdge switch
            {
                2160 => 1,
                1440 => 2,
                1080 => 3,
                720 => 4,
                480 => 5,
                _ => 0
            };
            SetSaveDirectoryPath(s.SaveDirectory);
            SaveDirPanel.Visibility = s.SaveToFile ? Visibility.Visible : Visibility.Collapsed;
            StartWithWindowsCheck.IsChecked = s.StartWithWindows;
            RulerCaptureAllScreensCheck.IsChecked = s.RulerCaptureAllScreens;
            RulerContextMenuEnabledCheck.IsChecked = s.RulerContextMenuEnabled;

            SaveHistoryCheck.IsChecked = s.SaveHistory;
            SaveStandaloneToHistoryCheck.IsChecked = s.SaveStandaloneToHistory;
            HistoryRetentionCombo.SelectedIndex = (int)s.HistoryRetention;
            HistoryClickActionCombo.SelectedIndex = (int)s.HistoryClickAction;
            ShowImageSearchBarCheck.IsChecked = s.ShowImageSearchBar;
            AutoIndexImagesCheck.IsChecked = s.AutoIndexImages;
            UpdateImageIndexVisibility(s.AutoIndexImages);
            DisableAnimationsCheck.IsChecked = !s.DisableAnimations; // inverted: checked = enabled
            ShowToolBannersCheck.IsChecked = s.ShowToolBanners;
            ConfirmBeforeExitCheck.IsChecked = s.ConfirmBeforeExit;
            SelectAppTheme(s.ThemeMode);
            SelectUiScale(s.UiScale);
            OcrAutoCopyCheck.IsChecked = s.OcrAutoCopyToClipboard;
            CrosshairGuidesCheck.IsChecked = s.ShowCrosshairGuides;
            ShowCaptureMagnifierCheck.IsChecked = s.ShowCaptureMagnifier;
            ShowSelectionSizeCheck.IsChecked = s.ShowSelectionSize;
            ConfirmRegionCheck.IsChecked = s.ConfirmRegionBeforeCapture;
            OverlayAllMonitorsCheck.IsChecked = s.OverlayCaptureAllMonitors;
            AutoCheckUpdateCheck.IsChecked = s.AutoCheckForUpdates;
            ShowToolNumberBadgesCheck.IsChecked = s.ShowToolNumberBadges;
            MonthlyFoldersCheck.IsChecked = s.SaveInMonthlyFolders;
            LoadFileNameTemplate(s.FileNameTemplate);
            SelectToastPositionUi(s.ToastPosition);
            PopulateToastMonitorOptions();
            SelectToastMonitor(s.ToastMonitorIndex);
            CaptureDockSideCombo.SelectedIndex = (int)s.CaptureDockSide;
            ScrollingCaptureModeCombo.SelectedIndex = s.ScrollingCaptureMode switch
            {
                ScrollingCaptureMode.AssistAutoscroll => 1,
                ScrollingCaptureMode.Manual => 1,
                _ => 0
            };
            ScrollCaptureModeIcon.Source = Helpers.FluentIcons.RenderWpf("scrollCapture",
                System.Drawing.Color.FromArgb(Theme.TextPrimary.A, Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B), 22);
            WindowDetectionCheck.IsChecked = s.WindowDetection != WindowDetectionMode.Off;
            ShowCursorCheck.IsChecked = s.ShowCursor;
            AnnotationStrokeShadowCheck.IsChecked = s.AnnotationStrokeShadow;
            CaptureDelayCombo.SelectedIndex = s.CaptureDelaySeconds switch { 3 => 1, 5 => 2, 10 => 3, _ => 0 };
            AutoPinPreviewsCheck.IsChecked = s.AutoPinPreviews;
            NotificationsEnabledCheck.IsChecked = s.NotificationsEnabled;
            SystemNotificationsCheck.IsChecked = s.SystemNotificationsEnabled;
            CelebrationsCheck.IsChecked = s.CelebrationsEnabled;
            RefreshMilestoneRail(reveal: false);
            UpdateSystemNotificationsRowState(s.NotificationsEnabled);
            MuteSoundsCheck.IsChecked = !s.MuteSounds; // activator: checked = all sounds on
            PopulateSoundCustomizationPanel();
            ShowCaptureWidgetCheck.IsChecked = s.ShowCaptureWidget;
            WidgetEnableEditorCheck.IsChecked = s.OpenEditorAfterCapture;
            if (VideoEnableEditorCheck != null)
                VideoEnableEditorCheck.IsChecked = s.OpenVideoTrimmerAfterCapture;
            WidgetDockEdgeCombo.SelectedIndex = (int)s.WidgetDockEdge;
            SelectWidgetHoverDelay(s.WidgetHoverDelayMs);
            PopulateWidgetMonitors();
            WidgetAlwaysOnTopCheck.IsChecked = s.WidgetAlwaysOnTop;
            UpdateWidgetOptionsVisibility(s.ShowCaptureWidget);
            EditorFitCheck.IsChecked = s.EditorFitToWindowOnOpen;
            EditorShowFrameCheck.IsChecked = s.EditorShowFrame;
            EditorShowBannersCheck.IsChecked = s.EditorShowBanners;
            EditorShowWelcomeBannerCheck.IsChecked = s.EditorShowWelcomeBanner;
            EditorShowHintsCheck.IsChecked = s.EditorShowHints;
            EditorShowRulersCheck.IsChecked = s.EditorShowRulers;
            EditorAutoCropCheck.IsChecked = s.EditorAutoCropControls;
            EditorShowResizeHandlesCheck.IsChecked = s.EditorShowResizeHandles;
            EditorResizeScaleContentCheck.IsChecked = s.EditorResizeHandlesScaleContent;
            EditorPanModeLockCheck.IsChecked = s.EditorPanModeLockObjects;
            SelectUndoLimit(s.EditorUndoLimit);
            EditorExportFormatCombo.SelectedIndex = s.EditorExportFormat;
            RecordingQualityCombo.SelectedIndex = (int)s.RecordingQuality;
            SelectRecordingFps(s.RecordingFormat == RecordingFormat.GIF ? s.GifFps : s.RecordingFps);
            RecordShowCursorCheck.IsChecked = s.ShowCursor;
            RecordMicCheck.IsChecked = s.RecordMicrophone;
            RecordDesktopAudioCheck.IsChecked = s.RecordDesktopAudio;
            TryLoadSettingsSection("settings.populate-audio-devices", PopulateAudioDevices);

            double dur = s.ToastDurationSeconds;
            SelectToastDuration(dur);
            double sysDur = s.SystemToastDurationSeconds;
            SelectSystemToastDuration(sysDur);
            double fadeDur = s.ToastFadeOutSeconds;
            int fadeDurIdx = fadeDur switch { 1.0 => 0, 2.0 => 1, 3.0 => 2, 5.0 => 3, _ => 2 };
            ToastFadeDurationCombo.SelectedIndex = fadeDurIdx;
            LoadToastButtonLayoutDesigner();

            AboutVersionText.Text = $"Version {UpdateService.GetCurrentVersionLabel()}";

            TryLoadSettingsSection("settings.populate-tool-toggles", PopulateToolToggles);
            TryLoadSettingsSection("settings.update-capture-format-controls", UpdateCaptureFormatControls);
            ApplyLocalization();
        }
        finally
        {
            _suppressGeneralPreferenceChange = false;
        }
    }

    private static void TryLoadSettingsSection(string logKey, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError(logKey, ex);
        }
    }

    internal static readonly (string id, string label, char icon)[] ExtraTools =
        ToolListBuilder.ExtraTools;

    private void PopulateToolToggles() =>
        ToolListBuilder.Build(CaptureToolsPanel, AnnotationToolsPanel, _settingsService, this, () => HotkeyChanged?.Invoke(), EditorToolsPanel, ToolbarUtilitiesPanel);

    private void PopulateInterfaceLanguageOptions()
    {
        _languageItemSources.Clear();
        InterfaceLanguageCombo.Items.Clear();

        var autoLabel = "Auto (system language)";
        var autoToolTip = "Uses Windows language when CyberSnap has translations for it.";
        var autoAutomationName = "Auto interface language";
        var autoAutomationHelp = "Use the Windows language when CyberSnap has app translations for it.";
        _languageItemSources[LocalizationService.AutoLanguageCode] =
            (autoLabel, autoToolTip, null, autoAutomationName, autoAutomationHelp);

        var autoLanguageItem = new ComboBoxItem
        {
            Content = autoLabel,
            Tag = LocalizationService.AutoLanguageCode,
            ToolTip = autoToolTip,
        };
        AutomationProperties.SetName(autoLanguageItem, autoAutomationName);
        AutomationProperties.SetHelpText(autoLanguageItem, autoAutomationHelp);

        foreach (var language in LocalizationService.Languages)
        {
            bool available = LocalizationService.HasInterfaceTranslations(language.Code);
            var label = $"{language.NativeName} - {language.EnglishName}";
            var contentLabel = available ? label : $"{label} (not translated yet)";
            // Store the tooltip template key and label separately so RefreshLanguageComboDisplay
            // can re-translate and re-format the tooltip when the language changes.
            var toolTipTemplate = available
                ? "Use {0} for the CyberSnap interface."
                : "This language is recognized, but CyberSnap does not have app translations for it yet.";
            var toolTip = available
                ? string.Format(LocalizationService.Translate(toolTipTemplate), label)
                : LocalizationService.Translate(toolTipTemplate);
            var automationName = $"{label} interface language";
            var automationHelp = available
                ? $"Use {label} for CyberSnap menus, settings, and prompts."
                : $"{label} is recognized, but CyberSnap does not have app translations for it yet.";
            _languageItemSources[language.Code] = (contentLabel, toolTipTemplate, label, automationName, automationHelp);

            var item = new ComboBoxItem
            {
                Content = contentLabel,
                Tag = language.Code,
                IsEnabled = available,
                ToolTip = toolTip,
            };
            AutomationProperties.SetName(item, automationName);
            AutomationProperties.SetHelpText(item, automationHelp);
            InterfaceLanguageCombo.Items.Add(item);
        }

        // Auto always goes first, then all languages in native-name alphabetical order.
        InterfaceLanguageCombo.Items.Insert(0, autoLanguageItem);
    }

    private readonly Dictionary<string, (string Label, string ToolTipTemplate, string? LanguageLabel, string AutomationName, string AutomationHelp)> _languageItemSources = new();

    private void RefreshLanguageComboDisplay()
    {
        foreach (var item in InterfaceLanguageCombo.Items.OfType<ComboBoxItem>())
        {
            var code = item.Tag?.ToString();
            if (code == null || !_languageItemSources.TryGetValue(code, out var info))
                continue;

            item.Content = LocalizationService.Translate(info.Label);

            // Re-translate and re-format the tooltip using the stored template and language label.
            var translatedTemplate = LocalizationService.Translate(info.ToolTipTemplate);
            item.ToolTip = info.LanguageLabel != null
                ? string.Format(translatedTemplate, info.LanguageLabel)
                : translatedTemplate;

            AutomationProperties.SetName(item, LocalizationService.Translate(info.AutomationName));
            AutomationProperties.SetHelpText(item, LocalizationService.Translate(info.AutomationHelp));
        }

        // The ComboBox selection box caches the selected item's content as a snapshot
        // taken at selection time; mutating item.Content above does not refresh it.
        // Re-assign the selection (under the suppress guard so the handler is a no-op)
        // to force the closed combo to display the freshly translated label.
        var selected = InterfaceLanguageCombo.SelectedItem;
        if (selected != null)
        {
            var wasSuppressed = _suppressGeneralPreferenceChange;
            _suppressGeneralPreferenceChange = true;
            try
            {
                InterfaceLanguageCombo.SelectedItem = null;
                InterfaceLanguageCombo.SelectedItem = selected;
            }
            finally
            {
                _suppressGeneralPreferenceChange = wasSuppressed;
            }
        }
    }

    private void ShowToolNumberBadgesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.ShowToolNumberBadges;
        var selected = ShowToolNumberBadgesCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.tool-number-badges",
            "Tool number badges",
            previous,
            selected,
            value => _settingsService.Settings.ShowToolNumberBadges = value,
            value => ShowToolNumberBadgesCheck.IsChecked = value);
    }

    private void SelectInterfaceLanguage(string languageCode)
    {
        var normalized = LocalizationService.NormalizeLanguageSetting(languageCode);
        foreach (var item in InterfaceLanguageCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                InterfaceLanguageCombo.SelectedItem = item;
                return;
            }
        }

        InterfaceLanguageCombo.SelectedIndex = 0;
    }

    private void InterfaceLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var selected = InterfaceLanguageCombo.SelectedItem as ComboBoxItem;
        var languageCode = selected?.Tag?.ToString() ?? LocalizationService.AutoLanguageCode;
        if (!string.Equals(languageCode, LocalizationService.AutoLanguageCode, StringComparison.OrdinalIgnoreCase) &&
            !LocalizationService.HasInterfaceTranslations(languageCode))
        {
            ToastWindow.Show("Language not available", "CyberSnap does not have translations for that language yet.");
            SelectInterfaceLanguage(_settingsService.Settings.InterfaceLanguage);
            return;
        }

        var previous = _settingsService.Settings.InterfaceLanguage;
        var normalized = LocalizationService.NormalizeLanguageSetting(languageCode);
        UpdateGeneralPreference(
            "settings.interface-language",
            "Interface language",
            previous,
            normalized,
            value => _settingsService.Settings.InterfaceLanguage = value,
            SelectInterfaceLanguage,
            _ =>
            {
                ApplyLocalization();
                LocalizationChanged?.Invoke();
            });
    }

    private void PopulateToastMonitorOptions()
    {
        while (ToastMonitorCombo.Items.Count > 1)
            ToastMonitorCombo.Items.RemoveAt(1);

        var screens = PopupWindowHelper.GetSortedScreens();
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var label = $"Monitor {i + 1} ({s.Bounds.Width}x{s.Bounds.Height})";
            if (s.Primary) label += " [Primary]";
            ToastMonitorCombo.Items.Add(new ComboBoxItem { Content = label, Tag = i.ToString() });
        }
    }

    private void SelectToastMonitor(int index)
    {
        foreach (var item in ToastMonitorCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), index.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ToastMonitorCombo.SelectedItem = item;
                return;
            }
        }
        ToastMonitorCombo.SelectedIndex = 0;
    }

    private void ToastMonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var selected = ToastMonitorCombo.SelectedItem as ComboBoxItem;
        if (selected?.Tag is not string tag || !int.TryParse(tag, out var index))
            index = -1;

        var previous = _settingsService.Settings.ToastMonitorIndex;
        UpdateGeneralPreference(
            "settings.toast-monitor",
            "Toast monitor",
            previous,
            index,
            value =>
            {
                _settingsService.Settings.ToastMonitorIndex = value;
                ToastWindow.SetMonitorIndex(value);
            },
            SelectToastMonitor);
    }

    private void ApplyLocalization()
    {
        LocalizationService.ApplyCurrentCulture(_settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        // Re-apply the default theme tooltip after ApplyTo re-translates it from SourceToolTip.
        ThemeDarkRadio?.ApplyDefaultTooltip();
        // Second pass on AboutPanel specifically — ensures SourceText/SourceToolTip/SourceContent
        // are processed even if the first pass missed them (e.g., due to template timing).
        LocalizationService.ApplyTo(AboutPanel, _settingsService.Settings.InterfaceLanguage);
        RefreshLanguageComboDisplay();
        PopulateToolToggles();
        PopulateSoundCustomizationPanel();
        UpdateWindowTitle();
        RefreshAboutLocalization();
    }

    /// <summary>Explicitly translates all About tab texts, tooltips, and button labels.</summary>
    private void RefreshAboutLocalization()
    {
        try
        {
            AboutSectionLabel.Text = LocalizationService.Translate("About CyberSnap");
            AboutDescriptionText.Text = LocalizationService.Translate("CyberSnap is a professional-grade screen capture and productivity suite designed for seamless workflows. Built with performance in mind, it combines rapid image capture with advanced features like local OCR, instant translation, and comprehensive gallery management.");
            AboutUpdatesSectionLabel.Text = LocalizationService.Translate("Updates & Maintenance");
            AboutAutoUpdateTitle.Text = LocalizationService.Translate("Check for updates on startup");
            AboutAutoUpdateDesc.Text = LocalizationService.Translate("Automatically check for new versions when CyberSnap starts.");
            AutoCheckUpdateCheck.ToolTip = LocalizationService.Translate("Automatically check for new versions when CyberSnap starts.");
            AboutUpdateTitle.Text = LocalizationService.Translate("Check for updates");
            AboutUpdateDesc.Text = LocalizationService.Translate("Check for the latest version and download updates directly.");
            UpdateBtn.Content = LocalizationService.Translate("Check Now");
            UpdateBtn.ToolTip = LocalizationService.Translate("Check for the latest version");
            UpdateProgressText.Text = LocalizationService.Translate("Downloading update...");
            AboutResourcesSectionLabel.Text = LocalizationService.Translate("Resources");
            AboutRepoTitle.Text = LocalizationService.Translate("Project Repository");
            AboutRepoDesc.Text = LocalizationService.Translate("View the source code on GitHub, report issues, and contribute.");
            GithubBtn.Content = LocalizationService.Translate("View GitHub");
            GithubBtn.ToolTip = LocalizationService.Translate("View the source code on GitHub");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.about-localization", ex);
        }
    }

    private void UpdateWindowTitle()
    {
        var configLabel = LocalizationService.Translate("Configuration");
        SettingsTitleBar.Title = $"CyberSnap {UpdateService.GetCurrentVersionLabel()} - {configLabel}";
        WindowTitles.ApplyTaskbar(this, WindowTitles.Settings, _settingsService.Settings.InterfaceLanguage);
    }

    private void SelectUiScale(double scale)
    {
        var normalized = UiScale.Normalize(scale);
        foreach (var item in UiScaleCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag &&
                double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var itemScale) &&
                Math.Abs(itemScale - normalized) < 0.001)
            {
                UiScaleCombo.SelectedItem = item;
                return;
            }
        }

        SelectComboByTag(UiScaleCombo, "1.0");
    }

    private void SelectAppTheme(Models.AppThemeMode mode)
    {
        ThemeSystemRadio.IsChecked = mode == Models.AppThemeMode.System;
        ThemeDarkRadio.IsChecked = mode == Models.AppThemeMode.Dark;
        ThemeLightRadio.IsChecked = mode == Models.AppThemeMode.Light;
        ThemeGrayscaleRadio.IsChecked = mode == Models.AppThemeMode.Grayscale;
    }

    private void AppThemeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var selected = ThemeDarkRadio.IsChecked == true ? Models.AppThemeMode.Dark
            : ThemeLightRadio.IsChecked == true ? Models.AppThemeMode.Light
            : ThemeGrayscaleRadio.IsChecked == true ? Models.AppThemeMode.Grayscale
            : Models.AppThemeMode.System;

        var previous = _settingsService.Settings.ThemeMode;
        if (selected == previous) return;

        UpdateGeneralPreference(
            "settings.app-theme",
            "App theme",
            previous,
            selected,
            value => _settingsService.Settings.ThemeMode = value,
            SelectAppTheme,
            value =>
            {
                Theme.SetMode(value);
                Theme.ApplyTo(Application.Current.Resources);

                ApplyThemeColors();

                // Live-refresh all open windows.
                foreach (Window w in Application.Current.Windows)
                {
                    if (w == this || !w.IsLoaded) continue;
                    try
                    {
                        Theme.ApplyTo(Application.Current.Resources);
                        w.InvalidateVisual();

                        if (w is HistoryWindow hw && hw.IsLoaded)
                            hw.ApplyThemeColors();

                        if (w is CaptureWidgetWindow cww && cww.IsLoaded)
                            cww.RefreshLayout();

                        if (w is VideoTrimmerWindow vtw && vtw.IsLoaded)
                            vtw.ApplyTheme();
                    }
                    catch (Exception ex)
                    {
                        Services.AppDiagnostics.LogWarning("settings.theme-refresh-window", $"Failed to refresh {w.GetType().Name}", ex);
                    }
                }

                // Refresh Windows Forms editor if active
                if (CyberSnap.UI.Editor.EditorForm.ActiveInstance is { } editor)
                {
                    try
                    {
                        editor.ApplyTheme();
                    }
                    catch (Exception ex)
                    {
                        Services.AppDiagnostics.LogWarning("settings.theme-refresh-editor", "Failed to refresh EditorForm theme", ex);
                    }
                }
            });
    }

    private void TabChanged(object sender, RoutedEventArgs e)
    {
        ApplyMainTabSelection();
    }

    private void ApplyMainTabSelection()
    {
        if (_isSearching)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SoundsPanel.Visibility = Visibility.Collapsed;
            ToastPanel.Visibility = Visibility.Collapsed;
            HotkeysPanel.Visibility = Visibility.Collapsed;
            CapturePanel.Visibility = Visibility.Collapsed;
            WidgetPanel.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Collapsed;
            RecordingPanel.Visibility = Visibility.Collapsed;
            OcrPanel.Visibility = Visibility.Collapsed;
            HistoryPanel.Visibility = Visibility.Collapsed;
            AchievementsPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            SearchResultsPanel.Visibility = Visibility.Visible;

            PageTitleText.Text = LocalizationService.Translate("Search Results");
        }
        else
        {
            SettingsPanel.Visibility = SettingsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SoundsPanel.Visibility = SoundsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ToastPanel.Visibility = ToastTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            HotkeysPanel.Visibility = HotkeysTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            CapturePanel.Visibility = CaptureTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            WidgetPanel.Visibility = WidgetTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            EditorPanel.Visibility = EditorTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            RecordingPanel.Visibility = RecordingTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            OcrPanel.Visibility = OcrTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            HistoryPanel.Visibility = HistoryTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AchievementsPanel.Visibility = AchievementsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AboutPanel.Visibility = AboutTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SearchResultsPanel.Visibility = Visibility.Collapsed;

            PageTitleText.Text = LocalizationService.Translate(GetSelectedSettingsPageTitle());

            if (OcrTab.IsChecked == true)
                LoadOcrTab();

            // Reveal the milestone rail + refresh stats/medals when the Achievements tab is shown,
            // so a newly reached milestone gets its one-shot flourish at the moment the user sees it.
            if (AchievementsTab.IsChecked == true)
            {
                RefreshAchievements();
                RefreshMilestoneRail(reveal: true);
            }
        }
    }

    private void ResetHotkeysBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsService.Settings.ResetToDefaultHotkeys();
            _settingsService.Save();
            PopulateToolToggles();
            HotkeyChanged?.Invoke();
            ToastWindow.Show("Hotkeys reset", "All hotkeys have been reset to defaults.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.reset-hotkeys", ex);
            ToastWindow.ShowError("Reset failed", $"Failed to reset hotkeys:\n{ex.Message}");
        }
    }

    private string GetSelectedSettingsPageTitle()
    {
        if (ToastTab.IsChecked == true) return "Notifications";
        if (SoundsTab.IsChecked == true) return "Sounds";
        if (CaptureTab.IsChecked == true) return "Capture";
        if (WidgetTab.IsChecked == true) return "Widget";
        if (EditorTab.IsChecked == true) return "Editor";
        if (RecordingTab.IsChecked == true) return "Video";
        if (OcrTab.IsChecked == true) return "OCR";
        if (HotkeysTab.IsChecked == true) return "Hotkeys";
        if (HistoryTab.IsChecked == true) return "Gallery";
        if (AchievementsTab.IsChecked == true) return "Achievements";
        if (AboutTab.IsChecked == true) return "About";
        return "General";
    }

    private void EditorFitCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorFitToWindowOnOpen;
        var selected = EditorFitCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-fit",
            "Editor fit to window on open",
            previous,
            selected,
            value => _settingsService.Settings.EditorFitToWindowOnOpen = value,
            value => EditorFitCheck.IsChecked = value);
    }

    private void EditorShowFrameCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorShowFrame;
        var selected = EditorShowFrameCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-show-frame",
            "Editor show frame",
            previous,
            selected,
            value => _settingsService.Settings.EditorShowFrame = value,
            value => EditorShowFrameCheck.IsChecked = value);
    }

    private void EditorShowBannersCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorShowBanners;
        var selected = EditorShowBannersCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-show-banners",
            "Editor show banners",
            previous,
            selected,
            value => _settingsService.Settings.EditorShowBanners = value,
            value => EditorShowBannersCheck.IsChecked = value);
    }

    private void EditorShowWelcomeBannerCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorShowWelcomeBanner;
        var selected = EditorShowWelcomeBannerCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-show-welcome-banner",
            "Editor show welcome banner",
            previous,
            selected,
            value => _settingsService.Settings.EditorShowWelcomeBanner = value,
            value => EditorShowWelcomeBannerCheck.IsChecked = value,
            applyRuntime: value => CyberSnap.UI.Editor.EditorForm.ActiveInstance?.SetShowWelcomeBanner(value));
    }

    private void EditorShowHintsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorShowHints;
        var selected = EditorShowHintsCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-show-hints",
            "Editor show hints",
            previous,
            selected,
            value => _settingsService.Settings.EditorShowHints = value,
            value => EditorShowHintsCheck.IsChecked = value);
    }

    private void EditorShowRulersCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorShowRulers;
        var selected = EditorShowRulersCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-show-rulers",
            "Editor show rulers",
            previous,
            selected,
            value => _settingsService.Settings.EditorShowRulers = value,
            value => EditorShowRulersCheck.IsChecked = value);
    }

    private void EditorAutoCropCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorAutoCropControls;
        var selected = EditorAutoCropCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-auto-crop",
            "Editor auto-crop controls",
            previous,
            selected,
            value => _settingsService.Settings.EditorAutoCropControls = value,
            value => EditorAutoCropCheck.IsChecked = value);
    }

    private void EditorShowResizeHandlesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorShowResizeHandles;
        var selected = EditorShowResizeHandlesCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-resize-handles",
            "Editor resize handles",
            previous,
            selected,
            value => _settingsService.Settings.EditorShowResizeHandles = value,
            value => EditorShowResizeHandlesCheck.IsChecked = value);
    }

    private void EditorResizeScaleContentCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorResizeHandlesScaleContent;
        var selected = EditorResizeScaleContentCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-resize-scale",
            "Editor resize scales content",
            previous,
            selected,
            value => _settingsService.Settings.EditorResizeHandlesScaleContent = value,
            value => EditorResizeScaleContentCheck.IsChecked = value);
    }

    private void EditorPanModeLockCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorPanModeLockObjects;
        var selected = EditorPanModeLockCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.editor-pan-lock-objects",
            "Editor pan lock objects",
            previous,
            selected,
            value => _settingsService.Settings.EditorPanModeLockObjects = value,
            value => EditorPanModeLockCheck.IsChecked = value);
    }

    private void EditorUndoLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        if (EditorUndoLimitCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tagStr || !int.TryParse(tagStr, out int limit)) return;
        var previous = _settingsService.Settings.EditorUndoLimit;
        UpdateGeneralPreference(
            "settings.editor-undo-limit",
            "Editor undo limit",
            previous,
            limit,
            value => _settingsService.Settings.EditorUndoLimit = value,
            value =>
            {
                for (int i = 0; i < EditorUndoLimitCombo.Items.Count; i++)
                {
                    if (EditorUndoLimitCombo.Items[i] is ComboBoxItem cbi && cbi.Tag is string t && int.TryParse(t, out int v) && v == value)
                    {
                        EditorUndoLimitCombo.SelectedIndex = i;
                        return;
                    }
                }
            });
    }

    private void EditorExportFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        var previous = _settingsService.Settings.EditorExportFormat;
        var selected = EditorExportFormatCombo.SelectedIndex;
        if (selected < 0) return;
        UpdateGeneralPreference(
            "settings.editor-export-format",
            "Editor export format",
            previous,
            selected,
            value => _settingsService.Settings.EditorExportFormat = value,
            value => EditorExportFormatCombo.SelectedIndex = value);
    }

    private void ResetSuppressedDialogsButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.EditorSuppressResizeConfirm = false;
        _settingsService.Settings.EditorSuppressPasteConfirm = false;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("settings.reset-suppressed-dialogs", ex); }
        var original = ResetSuppressedDialogsButton.Content?.ToString() ?? "Reset";
        ResetSuppressedDialogsButton.Content = Services.LocalizationService.Translate("Suppressed dialogs restored");
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ResetSuppressedDialogsButton.Content = original;
        };
        timer.Start();
    }

    private void SelectUndoLimit(int limit)
    {
        for (int i = 0; i < EditorUndoLimitCombo.Items.Count; i++)
        {
            if (EditorUndoLimitCombo.Items[i] is ComboBoxItem item &&
                item.Tag is string tag && int.TryParse(tag, out int val) && val == limit)
            {
                EditorUndoLimitCombo.SelectedIndex = i;
                return;
            }
        }
        EditorUndoLimitCombo.SelectedIndex = 4; // default to 100
    }
}
