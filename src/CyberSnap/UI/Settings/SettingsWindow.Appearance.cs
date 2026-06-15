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
        Icon = ThemedLogo.Square(32);
        Foreground = Theme.Brush(Theme.TextPrimary);
        UiScale.ApplyToWindow(this, OuterBorder, scaleWindowBounds: true);

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
            [CaptureTab] = "\uE7C2", // Capture
            [RecordingTab] = "\uE768", // Video
            [OcrTab] = "\uE8C8", // OCR
            [HotkeysTab] = "\uE765", // Hotkeys
            [HistoryTab] = "\uEB9F", // History
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
            CenterAspectRatioCombo.SelectedIndex = Enum.IsDefined(typeof(CenterSelectionAspectRatio), s.CenterSelectionAspectRatio)
                ? (int)s.CenterSelectionAspectRatio
                : 0;

            var afterCapture = Enum.IsDefined(typeof(AfterCaptureAction), s.AfterCapture)
                ? s.AfterCapture
                : AfterCaptureAction.PreviewAndCopy;
            AfterCaptureCombo.SelectedIndex = GetAfterCaptureSelectedIndex(
                new AfterCapturePreference(afterCapture, s.OpenEditorAfterCapture));
            SaveToFileCheck.IsChecked = s.SaveToFile;
            AskFileNameCheck.IsChecked = s.AskForFileNameOnSave;
            AutoOpenCapturedImagesCheck.IsChecked = s.AutoOpenCapturedImages;
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

            SaveHistoryCheck.IsChecked = s.SaveHistory;
            HistoryRetentionCombo.SelectedIndex = (int)s.HistoryRetention;
            ShowImageSearchBarCheck.IsChecked = s.ShowImageSearchBar;
            AutoIndexImagesCheck.IsChecked = s.AutoIndexImages;
            UpdateImageIndexVisibility(s.AutoIndexImages);
            DisableAnimationsCheck.IsChecked = s.DisableAnimations;
            SelectUiScale(s.UiScale);
            OcrAutoCopyCheck.IsChecked = s.OcrAutoCopyToClipboard;
            CrosshairGuidesCheck.IsChecked = s.ShowCrosshairGuides;
            ShowCaptureMagnifierCheck.IsChecked = s.ShowCaptureMagnifier;
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
            WidgetDockEdgeCombo.SelectedIndex = (int)s.WidgetDockEdge;
            SelectWidgetHoverDelay(s.WidgetHoverDelayMs);
            UpdateWidgetOptionsVisibility(s.ShowCaptureWidget);
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
        ToolListBuilder.Build(CaptureToolsPanel, AnnotationToolsPanel, _settingsService, this, () => HotkeyChanged?.Invoke());

    private void PopulateInterfaceLanguageOptions()
    {
        _languageItemSources.Clear();
        InterfaceLanguageCombo.Items.Clear();

        var autoLabel = "Auto (system language)";
        var autoToolTip = "Uses Windows language when CyberSnap has translations for it.";
        var autoAutomationName = "Auto interface language";
        var autoAutomationHelp = "Use the Windows language when CyberSnap has app translations for it.";
        _languageItemSources[LocalizationService.AutoLanguageCode] =
            (autoLabel, autoToolTip, autoAutomationName, autoAutomationHelp);

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
            var label = string.Equals(language.EnglishName, language.NativeName, StringComparison.OrdinalIgnoreCase)
                ? language.EnglishName
                : $"{language.EnglishName} - {language.NativeName}";
            var contentLabel = available ? label : $"{label} (not translated yet)";
            var toolTip = available
                ? $"Use {label} for the CyberSnap interface."
                : "This language is recognized, but CyberSnap does not have app translations for it yet.";
            var automationName = $"{label} interface language";
            var automationHelp = available
                ? $"Use {label} for CyberSnap menus, settings, and prompts."
                : $"{label} is recognized, but CyberSnap does not have app translations for it yet.";
            _languageItemSources[language.Code] = (contentLabel, toolTip, automationName, automationHelp);

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

        // English (added first by LocalizationService.Languages) stays at the top as the
        // default; insert Auto right after it so auto-detection is second until every
        // language is fully translated.
        int autoIndex = InterfaceLanguageCombo.Items.Count > 0 ? 1 : 0;
        InterfaceLanguageCombo.Items.Insert(autoIndex, autoLanguageItem);
    }

    private readonly Dictionary<string, (string Label, string ToolTip, string AutomationName, string AutomationHelp)> _languageItemSources = new();

    private void RefreshLanguageComboDisplay()
    {
        foreach (var item in InterfaceLanguageCombo.Items.OfType<ComboBoxItem>())
        {
            var code = item.Tag?.ToString();
            if (code == null || !_languageItemSources.TryGetValue(code, out var info))
                continue;

            item.Content = LocalizationService.Translate(info.Label);
            item.ToolTip = LocalizationService.Translate(info.ToolTip);
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
        RefreshLanguageComboDisplay();
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var title = $"CyberSnap {UpdateService.GetCurrentVersionLabel()} - Configuration";
        Title = title;
        SettingsTitleBar.Title = title;
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

    private void TabChanged(object sender, RoutedEventArgs e)
    {
        ApplyMainTabSelection();
    }

    private void ApplyMainTabSelection()
    {
        SettingsPanel.Visibility = SettingsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ToastPanel.Visibility = ToastTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPanel.Visibility = HotkeysTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CapturePanel.Visibility = CaptureTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RecordingPanel.Visibility = RecordingTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OcrPanel.Visibility = OcrTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = HistoryTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = AboutTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = LocalizationService.Translate(GetSelectedSettingsPageTitle());

        if (OcrTab.IsChecked == true)
            LoadOcrTab();

        // Reveal the milestone rail when the Notifications tab is shown, so a newly reached
        // milestone gets its one-shot flourish at the moment the user actually sees it.
        if (ToastTab.IsChecked == true)
            RefreshMilestoneRail(reveal: true);
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
        if (CaptureTab.IsChecked == true) return "Capture";
        if (RecordingTab.IsChecked == true) return "Video";
        if (OcrTab.IsChecked == true) return "OCR";
        if (HotkeysTab.IsChecked == true) return "Hotkeys";
        if (HistoryTab.IsChecked == true) return "Gallery";
        if (AboutTab.IsChecked == true) return "About";
        return "General";
    }
}
