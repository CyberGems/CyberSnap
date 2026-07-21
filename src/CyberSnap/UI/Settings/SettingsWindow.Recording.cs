using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private void UpdateCaptureFormatControls()
    {
        var isJpeg = (CaptureImageFormat)CaptureFormatCombo.SelectedIndex == CaptureImageFormat.Jpeg;
        JpegQualityPanel.Visibility = isJpeg ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveHistoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressHistoryPreferenceChange) return;

        var previous = _settingsService.Settings.SaveHistory;
        var selected = SaveHistoryCheck.IsChecked == true;
        UpdateHistoryPreference(
            "settings.save-history",
            "Save capture gallery",
            previous,
            selected,
            value => _settingsService.Settings.SaveHistory = value,
            value => SaveHistoryCheck.IsChecked = value);
    }

    private void SaveStandaloneToHistoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressHistoryPreferenceChange) return;

        var previous = _settingsService.Settings.SaveStandaloneToHistory;
        var selected = SaveStandaloneToHistoryCheck.IsChecked == true;
        UpdateHistoryPreference(
            "settings.save-standalone-history",
            "Save standalone tools to Gallery",
            previous,
            selected,
            value => _settingsService.Settings.SaveStandaloneToHistory = value,
            value => SaveStandaloneToHistoryCheck.IsChecked = value);
    }

    private void HistoryRetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressHistoryPreferenceChange) return;

        var previous = _settingsService.Settings.HistoryRetention;
        var selected = (HistoryRetentionPeriod)Math.Clamp(HistoryRetentionCombo.SelectedIndex, 0, 4);
        UpdateHistoryPreference(
            "settings.history-retention",
            "History retention",
            previous,
            selected,
            value => _settingsService.Settings.HistoryRetention = value,
            value =>
            {
                HistoryRetentionCombo.SelectedIndex = (int)value;
                _historyService.RetentionPeriod = value;
            },
            value => _historyService.PruneByRetention(value));
    }

    private void HistoryClickActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressHistoryPreferenceChange) return;

        var previous = _settingsService.Settings.HistoryClickAction;
        var selected = (HistoryClickAction)Math.Clamp(HistoryClickActionCombo.SelectedIndex, 0, 2);
        UpdateHistoryPreference(
            "settings.history-click-action",
            "Click action",
            previous,
            selected,
            value => _settingsService.Settings.HistoryClickAction = value,
            value => HistoryClickActionCombo.SelectedIndex = (int)value);
    }

    private void UpdateHistoryPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action<T>? applySuccess = null)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            applySuccess?.Invoke(current);
            SetHistoryPreferenceStatus(string.Empty);
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

            _suppressHistoryPreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressHistoryPreferenceChange = false;
            }

            SetHistoryPreferenceStatus($"{label} change was not saved. Previous setting restored.");
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous history setting was restored. Check Config -> Recording and try again.\n{ex.Message}");
        }
    }

    private void SetHistoryPreferenceStatus(string message)
    {
        HistoryPreferenceStatusText.Text = message;
        HistoryPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool _suppressingSoundToggles;

    private void MuteSoundsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        // Master switch is an activator: checked = all sounds on, unchecked = all muted.
        var enableAll = MuteSoundsCheck.IsChecked == true;
        var muted = !enableAll;
        _suppressingSoundToggles = true;
        try
        {
            foreach (var (evt, _, _) in SoundEventDefs)
            {
                _settingsService.Settings.MutedSounds[evt] = muted;
                SoundService.SetSoundMuted(evt, muted);
            }
            _settingsService.Settings.MuteSounds = muted;
            SoundService.Muted = muted;
            _settingsService.Save();

            foreach (var child in SoundCustomizationPanel.Children)
            {
                if (child is Border card && card.Child is Grid row)
                {
                    foreach (var element in row.Children)
                    {
                        if (element is CheckBox cb)
                        {
                            cb.IsChecked = enableAll;
                            card.Opacity = enableAll ? 1.0 : 0.4;
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            _suppressingSoundToggles = false;
        }
    }

    private void ShowToolBannersCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.ShowToolBanners;
        var selected = ShowToolBannersCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.show-tool-banners",
            "Show instruction banners",
            previous,
            selected,
            value => _settingsService.Settings.ShowToolBanners = value,
            value => ShowToolBannersCheck.IsChecked = value,
            value => StandaloneToolBanner.Enabled = value);
    }

    private void ConfirmBeforeExitCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.ConfirmBeforeExit;
        var selected = ConfirmBeforeExitCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.confirm-before-exit",
            "Confirm before exit",
            previous,
            selected,
            value => _settingsService.Settings.ConfirmBeforeExit = value,
            value => ConfirmBeforeExitCheck.IsChecked = value);
    }

    private void DisableAnimationsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.DisableAnimations;
        var selected = DisableAnimationsCheck.IsChecked != true; // inverted: checked = enabled = !disabled
        UpdateGeneralPreference(
            "settings.disable-animations",
            "Enable animated effects",
            previous,
            selected,
            value => _settingsService.Settings.DisableAnimations = value,
            value => DisableAnimationsCheck.IsChecked = !value,
            value => Motion.Disabled = value);
    }

    private void SoundPackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Replaced by per-sound customization UI below.
    }

    // ── Sound customization ────────────────────────────────────────────────

    private static readonly (SoundEvent Event, string Label, string IconId)[] SoundEventDefs =
    [
        (SoundEvent.Startup, "Startup", "home"),
        (SoundEvent.Capture, "Capture", "captureRect"),
        (SoundEvent.Color, "Color picker", "picker"),
        (SoundEvent.Text, "OCR Text", "ocr"),
        (SoundEvent.Scan, "QR / Barcode scan", "scan"),
        (SoundEvent.RecordStart, "Recording start", "record"),
        (SoundEvent.RecordStop, "Recording stop", "stop"),
        (SoundEvent.Upload, "Upload / Share", "share"),
        (SoundEvent.Achievement, "Achievement", "trophy"),
        // Brief status toasts: "Sent to the editor", encoding wait, etc. (not capture previews).
        (SoundEvent.System, "System notification", "info"),
        (SoundEvent.Error, "Error", "warning"),
    ];

    private void PopulateSoundCustomizationPanel()
    {
        SoundCustomizationPanel.Children.Clear();
        var s = _settingsService.Settings;

        foreach (var (evt, label, iconId) in SoundEventDefs)
        {
            var isMuted = s.MutedSounds.TryGetValue(evt, out var m) && m;
            var card = new Border { Style = (Style)FindResource("SoundItemCard"), Opacity = isMuted ? 0.4 : 1.0 };

            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });                     // icon
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // label
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // centered action group
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // balancer keeps the group centered at any width
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // enable toggle, pinned right

            // Col 0: Event icon
            var iconFrame = new Border { Style = (Style)FindResource("SoundItemIconFrame") };
            var iconColor = System.Drawing.Color.FromArgb(Theme.TextPrimary.A, Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B);
            var iconSource = Helpers.FluentIcons.RenderWpf(iconId, iconColor, 16);
            var iconImage = new System.Windows.Controls.Image
            {
                Source = iconSource,
                Width = 16,
                Height = 16,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.9
            };
            iconFrame.Child = iconImage;
            Grid.SetColumn(iconFrame, 0);
            row.Children.Add(iconFrame);

            // Col 1: Label
            var labelBlock = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 12, 0),
                Style = (Style)FindResource("SettingTitle")
            };
            LocalizationService.SetSourceText(labelBlock, label);
            labelBlock.Text = LocalizationService.Translate(label);
            Grid.SetColumn(labelBlock, 1);
            row.Children.Add(labelBlock);

            // Col 2: Centered action group (preview + source pill + browse + reset)
            var controlsPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            var translatedLabel = LocalizationService.Translate(label);

            // Preview button
            var previewBtn = new Button
            {
                Content = "\uE768",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = LocalizationService.Translate("Preview this sound."),
                Style = (Style)FindResource("SoundItemPlayButton")
            };
            AutomationProperties.SetName(previewBtn,
                string.Format(LocalizationService.Translate("Preview {0} sound"), translatedLabel));
            previewBtn.Click += (_, _) => SoundService.Play(evt);
            controlsPanel.Children.Add(previewBtn);

            // Source status (not a button): "Built-in" / filename caption between actions.
            var sourcePill = new Border { Style = (Style)FindResource("SoundItemSourcePill"), Margin = new Thickness(10, 0, 8, 0) };
            var sourceLabel = new TextBlock { Style = (Style)FindResource("SoundItemSourceText") };
            sourcePill.Child = sourceLabel;
            controlsPanel.Children.Add(sourcePill);

            // Browse button — same chrome as preview so it reads as an action.
            var browseBtn = new Button
            {
                Content = "\uE8E5", // OpenFile / folder
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = LocalizationService.Translate("Choose a custom sound file."),
                Style = (Style)FindResource("SoundItemActionButton")
            };
            AutomationProperties.SetName(browseBtn,
                string.Format(LocalizationService.Translate("Choose custom sound for {0}"), translatedLabel));
            controlsPanel.Children.Add(browseBtn);

            // Reset button — same ↺ glyph as the master "reset all" control.
            var resetBtn = new Button
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Visibility = Visibility.Hidden,
                ToolTip = LocalizationService.Translate("Revert to default sound."),
                Style = (Style)FindResource("SoundItemResetButton")
            };
            AutomationProperties.SetName(resetBtn,
                string.Format(LocalizationService.Translate("Reset {0} sound to default"), translatedLabel));
            resetBtn.Click += (_, _) => ResetCustomSound(evt, sourceLabel, sourcePill, resetBtn);
            controlsPanel.Children.Add(resetBtn);

            Grid.SetColumn(controlsPanel, 2);
            row.Children.Add(controlsPanel);

            // Col 4: Enable checkbox (checked = this sound plays) — pinned right, aligns with master switch
            var enableCheck = new CheckBox
            {
                Width = 42, Height = 20, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsChecked = !isMuted,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = string.Format(LocalizationService.Translate("Enable the {0} sound."), translatedLabel),
            };
            AutomationProperties.SetName(enableCheck,
                string.Format(LocalizationService.Translate("Enable {0} sound"), translatedLabel));
            enableCheck.Checked += (_, _) => { SetSoundMuted(evt, false); card.Opacity = 1.0; };
            enableCheck.Unchecked += (_, _) => { SetSoundMuted(evt, true); card.Opacity = 0.4; };
            Grid.SetColumn(enableCheck, 4);
            row.Children.Add(enableCheck);

            // Wire browse now that resetBtn exists
            browseBtn.Click += (_, _) => BrowseCustomSound(evt, sourceLabel, sourcePill, resetBtn);

            card.Child = row;

            // Update source label
            UpdateSoundSourceLabel(evt, sourceLabel, sourcePill, resetBtn);

            SoundCustomizationPanel.Children.Add(card);
        }
    }

    private void UpdateSoundSourceLabel(SoundEvent evt, TextBlock label, Border sourcePill, Button resetBtn)
    {
        var customPath = _settingsService.Settings.CustomSounds.TryGetValue(evt, out var p) ? p : null;
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            LocalizationService.SetSourceText(label, "");
            label.Text = System.IO.Path.GetFileName(customPath);
            label.Foreground = (System.Windows.Media.Brush)FindResource("ThemeTextPrimaryBrush");
            label.Opacity = 1.0;
            // Flat status caption — no pill chrome (must not look like a button).
            sourcePill.Background = System.Windows.Media.Brushes.Transparent;
            sourcePill.BorderBrush = System.Windows.Media.Brushes.Transparent;
            resetBtn.Visibility = Visibility.Visible;
        }
        else
        {
            // Dedicated key so ES can say "Predefinido" without changing global "Default".
            LocalizationService.SetSourceText(label, "Built-in");
            label.Text = LocalizationService.Translate("Built-in");
            label.Foreground = (System.Windows.Media.Brush)FindResource("ThemeMutedBrush");
            label.Opacity = 0.9;
            sourcePill.Background = System.Windows.Media.Brushes.Transparent;
            sourcePill.BorderBrush = System.Windows.Media.Brushes.Transparent;
            resetBtn.Visibility = Visibility.Hidden;
        }
    }

    private void SetSoundMuted(SoundEvent evt, bool muted)
    {
        if (_suppressingSoundToggles) return;
        _settingsService.Settings.MutedSounds[evt] = muted;
        SoundService.SetSoundMuted(evt, muted);

        // Sync master switch: if any sound is unmuted, master should be ON
        var allMuted = SoundEventDefs.All(d => _settingsService.Settings.MutedSounds.TryGetValue(d.Event, out var m) && m);
        var masterOn = !allMuted;
        if ((MuteSoundsCheck.IsChecked == true) != masterOn)
        {
            _suppressGeneralPreferenceChange = true;
            try { MuteSoundsCheck.IsChecked = masterOn; }
            finally { _suppressGeneralPreferenceChange = false; }
        }
        _settingsService.Settings.MuteSounds = allMuted;
        SoundService.Muted = allMuted;
        _settingsService.Save();
    }

    private void BrowseCustomSound(SoundEvent evt, TextBlock sourceLabel, Border sourcePill, Button resetBtn)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = string.Format(
                LocalizationService.Translate("Choose custom sound for {0}"),
                LocalizationService.Translate(evt.ToString())),
            Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|MP3 files (*.mp3)|*.mp3|WAV files (*.wav)|*.wav|All files (*.*)|*.*",
            DefaultExt = ".mp3"
        };
        if (dlg.ShowDialog() == true)
        {
            _settingsService.Settings.CustomSounds[evt] = dlg.FileName;
            SoundService.SetCustomSound(evt, dlg.FileName);
            _settingsService.Save();
            UpdateSoundSourceLabel(evt, sourceLabel, sourcePill, resetBtn);
            // Preview the new sound
            SoundService.Play(evt);
        }
    }

    private void ResetCustomSound(SoundEvent evt, TextBlock sourceLabel, Border sourcePill, Button resetBtn)
    {
        _settingsService.Settings.CustomSounds.Remove(evt);
        SoundService.SetCustomSound(evt, null);
        _settingsService.Save();
        UpdateSoundSourceLabel(evt, sourceLabel, sourcePill, resetBtn);
    }

    private void ResetAllSoundsBtn_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.CustomSounds.Clear();
        _settingsService.Settings.MutedSounds.Clear();
        _settingsService.Settings.MuteSounds = false; // reset master to "all on"
        SoundService.Initialize(_settingsService.Settings.CustomSounds, _settingsService.Settings.MutedSounds);
        SoundService.Muted = false;
        _settingsService.Save();

        // Reflect the reset master in the UI without re-triggering the change handler.
        _suppressGeneralPreferenceChange = true;
        try
        {
            MuteSoundsCheck.IsChecked = true;
        }
        finally
        {
            _suppressGeneralPreferenceChange = false;
        }

        PopulateSoundCustomizationPanel();
    }

    private void RecordingQualityCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;

        var previous = _settingsService.Settings.RecordingQuality;
        var selected = (RecordingQuality)Math.Clamp(RecordingQualityCombo.SelectedIndex, 0, 3);
        UpdateRecordingPreference(
            "settings.recording-quality",
            "Recording quality",
            previous,
            selected,
            value => _settingsService.Settings.RecordingQuality = value,
            value => RecordingQualityCombo.SelectedIndex = (int)value);
    }

    private void RecordingFpsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;
        if (RecordingFpsCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!int.TryParse(tag, out int fps))
            return;

        var previous = _settingsService.Settings.RecordingFps;
        UpdateRecordingPreference(
            "settings.recording-fps",
            "Video FPS",
            previous,
            fps,
            value => _settingsService.Settings.RecordingFps = value,
            SelectRecordingFps);
    }

    private void GifFpsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;
        if (GifFpsCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!int.TryParse(tag, out int fps))
            return;

        // GIF only supports 15 (default) and 30.
        fps = fps == 30 ? 30 : 15;
        var previous = _settingsService.Settings.GifFps;
        UpdateRecordingPreference(
            "settings.gif-fps",
            "GIF FPS",
            previous,
            fps,
            value => _settingsService.Settings.GifFps = value,
            SelectGifFps);
    }

    private void SelectRecordingFps(int fps)
    {
        RecordingFpsCombo.SelectedIndex = fps switch
        {
            15 => 0,
            24 => 1,
            30 => 2,
            60 => 3,
            _ => 2
        };
    }

    private void SelectGifFps(int fps)
    {
        GifFpsCombo.SelectedIndex = fps == 30 ? 1 : 0;
    }

    private void VideoShowCursorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;

        var previous = _settingsService.Settings.VideoShowCursor;
        var selected = VideoShowCursorCheck.IsChecked == true;
        UpdateRecordingPreference(
            "settings.video-show-cursor",
            "Show cursor in video",
            previous,
            selected,
            value => _settingsService.Settings.VideoShowCursor = value,
            value => VideoShowCursorCheck.IsChecked = value);
    }

    private void GifShowCursorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;

        var previous = _settingsService.Settings.GifShowCursor;
        var selected = GifShowCursorCheck.IsChecked == true;
        UpdateRecordingPreference(
            "settings.gif-show-cursor",
            "Show cursor in GIF",
            previous,
            selected,
            value => _settingsService.Settings.GifShowCursor = value,
            value => GifShowCursorCheck.IsChecked = value);
    }

    private void GifEnableEditorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.OpenGifTrimmerAfterCapture;
        var selected = GifEnableEditorCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.open-gif-trimmer-after-capture",
            "Open trimmer after GIF",
            previous,
            selected,
            value => _settingsService.Settings.OpenGifTrimmerAfterCapture = value,
            value =>
            {
                if (GifEnableEditorCheck != null)
                    GifEnableEditorCheck.IsChecked = value;
            });

        RefreshEditorPreviewState();
    }

    private void PopulateAudioDevices()
    {
        MicDeviceCombo.Items.Clear();
        var mics = AudioService.GetMicrophones();
        var preferredMicId = _settingsService.Settings.MicrophoneDeviceId
            ?? AudioService.GetDefaultMicrophoneId();
        foreach (var mic in mics)
        {
            var item = CreateAudioDeviceItem(
                mic.Name,
                mic.Id,
                $"Microphone device {mic.Name}",
                $"Use {mic.Name} for microphone recording.");
            MicDeviceCombo.Items.Add(item);
            if (mic.Id == preferredMicId)
                MicDeviceCombo.SelectedItem = item;
        }
        if (MicDeviceCombo.SelectedIndex < 0 && MicDeviceCombo.Items.Count > 0)
            MicDeviceCombo.SelectedIndex = 0;

        DesktopAudioDeviceCombo.Items.Clear();
        var outputs = AudioService.GetDesktopAudioDevices();
        var preferredDesktopAudioId = _settingsService.Settings.DesktopAudioDeviceId
            ?? AudioService.GetDefaultDesktopAudioId();
        foreach (var dev in outputs)
        {
            var item = CreateAudioDeviceItem(
                dev.Name,
                dev.Id,
                $"Desktop audio device {dev.Name}",
                $"Use {dev.Name} for desktop audio recording.");
            DesktopAudioDeviceCombo.Items.Add(item);
            if (dev.Id == preferredDesktopAudioId)
                DesktopAudioDeviceCombo.SelectedItem = item;
        }
        if (DesktopAudioDeviceCombo.SelectedIndex < 0 && DesktopAudioDeviceCombo.Items.Count > 0)
            DesktopAudioDeviceCombo.SelectedIndex = 0;
    }

    private static ComboBoxItem CreateAudioDeviceItem(string name, string id, string automationName, string helpText)
    {
        var item = new ComboBoxItem { Content = name, Tag = id, ToolTip = helpText };
        AutomationProperties.SetName(item, automationName);
        AutomationProperties.SetHelpText(item, helpText);
        return item;
    }

    private void RecordMicCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;

        var previous = _settingsService.Settings.RecordMicrophone;
        var selected = RecordMicCheck.IsChecked == true;
        UpdateRecordingPreference(
            "settings.record-microphone",
            "Microphone recording",
            previous,
            selected,
            value => _settingsService.Settings.RecordMicrophone = value,
            value => RecordMicCheck.IsChecked = value);
    }

    private void RecordDesktopAudioCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;

        var previous = _settingsService.Settings.RecordDesktopAudio;
        var selected = RecordDesktopAudioCheck.IsChecked == true;
        UpdateRecordingPreference(
            "settings.record-desktop-audio",
            "Desktop audio recording",
            previous,
            selected,
            value => _settingsService.Settings.RecordDesktopAudio = value,
            value => RecordDesktopAudioCheck.IsChecked = value);
    }

    private void MicDeviceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;
        if (MicDeviceCombo.SelectedItem is not ComboBoxItem item)
            return;

        var previous = _settingsService.Settings.MicrophoneDeviceId;
        var selected = item.Tag as string;
        UpdateRecordingPreference(
            "settings.microphone-device",
            "Microphone device",
            previous,
            selected,
            value => _settingsService.Settings.MicrophoneDeviceId = value,
            SelectMicDeviceById);
    }

    private void DesktopAudioDeviceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange) return;
        if (DesktopAudioDeviceCombo.SelectedItem is not ComboBoxItem item)
            return;

        var previous = _settingsService.Settings.DesktopAudioDeviceId;
        var selected = item.Tag as string;
        UpdateRecordingPreference(
            "settings.desktop-audio-device",
            "Desktop audio device",
            previous,
            selected,
            value => _settingsService.Settings.DesktopAudioDeviceId = value,
            SelectDesktopAudioDeviceById);
    }

    private void UpdateRecordingPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action<T>? applySuccessUi = null)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            SetRecordingPreferenceStatus(string.Empty);
            if (applySuccessUi != null)
            {
                _suppressRecordingPreferenceChange = true;
                try
                {
                    applySuccessUi(current);
                }
                finally
                {
                    _suppressRecordingPreferenceChange = false;
                }
            }
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

            _suppressRecordingPreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressRecordingPreferenceChange = false;
            }

            SetRecordingPreferenceStatus($"{label} change was not saved. Previous setting restored.");
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous recording setting was restored. Check Config -> Recording and try again.\n{ex.Message}");
        }
    }

    private void SetRecordingPreferenceStatus(string message)
    {
        RecordingPreferenceStatusText.Text = message;
        RecordingPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SelectMicDeviceById(string? deviceId)
    {
        SelectComboItemByTag(MicDeviceCombo, deviceId);
    }

    private void SelectDesktopAudioDeviceById(string? deviceId)
    {
        SelectComboItemByTag(DesktopAudioDeviceCombo, deviceId);
    }

    private static void SelectComboItemByTag(System.Windows.Controls.ComboBox comboBox, string? tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
            comboBox.SelectedIndex = 0;
    }

    private void Hyperlink_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenSupportUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void KoFiSupport_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenSupportUrl("https://ko-fi.com/T6T71X9ZAM");
        e.Handled = true;
    }

    private void KoFiSupport_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        OpenSupportUrlFromKeyboard("https://ko-fi.com/T6T71X9ZAM", e);
    }

    private void PayPalSupport_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OpenSupportUrl("https://www.paypal.com/paypalme/9KGFX");
        e.Handled = true;
    }

    private void PayPalSupport_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        OpenSupportUrlFromKeyboard("https://www.paypal.com/paypalme/9KGFX", e);
    }

    private static void OpenSupportUrlFromKeyboard(string url, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space))
            return;

        OpenSupportUrl(url);
        e.Handled = true;
    }

    private static bool OpenSupportUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ToastWindow.ShowError("Open failed", "No support link is available.");
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ToastWindow.ShowError("Open failed", "The support link is not a valid web link.");
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            if (process is null)
            {
                ToastWindow.ShowError("Open failed", "Windows did not open the support link. Copy the link from Config -> About and open it manually.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Open failed",
                $"CyberSnap could not open the support link. Copy the link from Config -> About and open it manually.\n{ex.Message}");
            return false;
        }
    }
}
