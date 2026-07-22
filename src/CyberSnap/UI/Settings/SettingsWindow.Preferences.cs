using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private bool _suppressAutoIndexImagesChange;

    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                FileName = "CyberSnap-settings.json"
            };
            if (dlg.ShowDialog(this) != true) return;

            var json = SettingsService.ExportRedactedJson(_settingsService.Settings);
            File.WriteAllText(dlg.FileName, json);
            SetSettingsImportExportStatus($"Settings exported to {Path.GetFileName(dlg.FileName)}.");
            ToastWindow.Show("Settings exported", dlg.FileName);
        }
        catch (Exception ex)
        {
            ShowSettingsExportFailed(ex);
        }
    }

    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AppSettings? previous = null;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json"
            };
            if (dlg.ShowDialog(this) != true) return;

            var json = File.ReadAllText(dlg.FileName);
            if (!SettingsService.TryDeserialize(json, out var imported))
            {
                SetSettingsImportExportStatus("Import failed: invalid settings file.");
                ToastWindow.ShowError("Import failed", "Invalid settings file.");
                return;
            }

            previous = _settingsService.Settings;
            _settingsService.Settings = imported;
            _settingsService.Save();
            HotkeyChanged?.Invoke();
            LoadSettings();
            SetSettingsImportExportStatus("Settings imported and applied.");
            ToastWindow.Show("Settings imported", "Settings have been applied.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.import", ex);
            if (previous is not null)
            {
                _settingsService.Settings = previous;
                try
                {
                    _settingsService.Save();
                }
                catch (Exception rollbackEx)
                {
                    AppDiagnostics.LogError("settings.import-rollback", rollbackEx);
                }

                RestoreSettingsUiAfterFailedReset();
            }

            ShowSettingsImportFailed(previous is not null, ex);
        }
    }

    private void ShowSettingsImportFailed(bool restoredPrevious, Exception ex)
    {
        SetSettingsImportExportStatus(restoredPrevious
            ? "Import failed. Previous settings restored."
            : "Import failed. Check the file and try again.");
        var message = restoredPrevious
            ? $"The imported settings were not saved. Previous settings were restored. Check the file and try again.\n{ex.Message}"
            : $"CyberSnap could not import settings. Check the file and try again.\n{ex.Message}";
        ToastWindow.ShowError("Import failed", message);
    }

    private void SetSettingsImportExportStatus(string message)
    {
        SettingsImportExportStatusText.Text = message;
        SettingsImportExportStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemedConfirmDialog.Confirm(
                this,
                "Reset Settings",
                "Reset all settings to defaults?\n\nThis cannot be undone.",
                "Reset",
                "Cancel"))
        {
            SetSettingsImportExportStatus("Reset canceled. Existing settings kept.");
            return;
        }
        if (!ThemedConfirmDialog.Confirm(
                this,
                "Confirm Reset",
                "Are you sure? All hotkeys, upload configs, and preferences will be lost.",
                "Reset",
                "Cancel"))
        {
            SetSettingsImportExportStatus("Reset canceled. Existing settings kept.");
            return;
        }
        if (!ThemedConfirmDialog.Confirm(
                this,
                "Final Confirmation",
                "Last chance - reset everything?",
                "Reset",
                "Cancel"))
        {
            SetSettingsImportExportStatus("Reset canceled. Existing settings kept.");
            return;
        }

        var previous = _settingsService.Settings;
        try
        {
            _settingsService.Settings = new AppSettings();
            _settingsService.Save();
            HotkeyChanged?.Invoke();
            LoadSettings();
            PopulateToolToggles();
            SetSettingsImportExportStatus("Settings reset to defaults.");
            ToastWindow.Show("Settings reset", "Defaults have been applied.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.reset", ex);
            _settingsService.Settings = previous;
            RestoreSettingsUiAfterFailedReset();
            ShowSettingsResetFailed(ex);
        }
    }

    private void ShowSettingsResetFailed(Exception ex)
    {
        SetSettingsImportExportStatus("Reset failed. Previous settings restored.");
        ToastWindow.ShowError(
            "Reset failed",
            $"Defaults were not saved. Previous settings were restored. Try again after checking file permissions.\n{ex.Message}");
    }

    private void ShowSettingsExportFailed(Exception ex)
    {
        SetSettingsImportExportStatus("Export failed. Choose another folder and try again.");
        ToastWindow.ShowError(
            "Export failed",
            $"CyberSnap could not write the settings export. Choose another folder and try again.\n{ex.Message}");
    }

    private void RestoreSettingsUiAfterFailedReset()
    {
        try
        {
            LoadSettings();
            PopulateToolToggles();
        }
        catch (Exception restoreEx)
        {
            AppDiagnostics.LogError("settings.reset.restore", restoreEx);
        }
    }

    private void CrosshairGuidesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.ShowCrosshairGuides;
        var selected = CrosshairGuidesCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.crosshair-guides",
            "Crosshair guides",
            previous,
            selected,
            value => _settingsService.Settings.ShowCrosshairGuides = value,
            value => CrosshairGuidesCheck.IsChecked = value);
    }

    private void ShowCaptureMagnifierCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.ShowCaptureMagnifier;
        var selected = ShowCaptureMagnifierCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.capture-magnifier",
            "Capture magnifier",
            previous,
            selected,
            value => _settingsService.Settings.ShowCaptureMagnifier = value,
            value => ShowCaptureMagnifierCheck.IsChecked = value);
    }

    private void ShowSelectionSizeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.ShowSelectionSize;
        var selected = ShowSelectionSizeCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.selection-size",
            "Selection size",
            previous,
            selected,
            value => _settingsService.Settings.ShowSelectionSize = value,
            value => ShowSelectionSizeCheck.IsChecked = value);
    }

    private void ConfirmRegionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.ConfirmRegionBeforeCapture;
        var selected = ConfirmRegionCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.confirm-region",
            "Confirm region before capture",
            previous,
            selected,
            value => _settingsService.Settings.ConfirmRegionBeforeCapture = value,
            value => ConfirmRegionCheck.IsChecked = value);
    }

    private void OverlayAllMonitorsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.OverlayCaptureAllMonitors;
        var selected = OverlayAllMonitorsCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.overlay-all-monitors",
            "All-monitor capture",
            previous,
            selected,
            value => _settingsService.Settings.OverlayCaptureAllMonitors = value,
            value => OverlayAllMonitorsCheck.IsChecked = value);
    }

    private void ShowCursorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.ShowCursor;
        var selected = ShowCursorCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.show-cursor",
            "Show cursor",
            previous,
            selected,
            value => _settingsService.Settings.SetCaptureCursorForAll(value),
            value =>
            {
                ShowCursorCheck.IsChecked = value;
                if (WidgetCaptureCursorCheck != null)
                    WidgetCaptureCursorCheck.IsChecked = value;
                SyncRecordingShowCursorChecks(value);
            },
            () => ((App)Application.Current).SyncWidgetCaptureCursorToggle());
    }

    private void AutoCheckUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.AutoCheckForUpdates;
        var selected = AutoCheckUpdateCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.auto-check-updates",
            "Auto check for updates",
            previous,
            selected,
            value => _settingsService.Settings.AutoCheckForUpdates = value,
            value => AutoCheckUpdateCheck.IsChecked = value);
    }

    private void AnnotationStrokeShadowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.AnnotationStrokeShadow;
        var selected = AnnotationStrokeShadowCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.annotation-stroke-shadow",
            "Annotation contrast",
            previous,
            selected,
            value => _settingsService.Settings.AnnotationStrokeShadow = value,
            value => AnnotationStrokeShadowCheck.IsChecked = value);
    }



    private void ToastPositionBlock_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;
        if (sender is not FrameworkElement el || el.Tag is not string tag) return;
        if (!Enum.TryParse<ToastPosition>(tag, out var selected)) return;

        var previous = _settingsService.Settings.ToastPosition;
        UpdateToastPreference(
            "settings.toast-position",
            "Toast position",
            previous,
            selected,
            value => _settingsService.Settings.ToastPosition = value,
            value => SelectToastPositionUi(value),
            value =>
            {
                ToastWindow.SetPosition(value);
                PreviewWindow.SetPosition(value);
            },
            value => SelectToastPositionUi(value));
    }

    private void SelectToastPositionUi(ToastPosition position)
    {
        var accentBrush = (System.Windows.Media.Brush)Resources["ThemeAccentBrush"];
        var defaultBorderBrush = System.Windows.Media.Brushes.Transparent;
        var activeBg = Theme.Brush(Theme.AccentSubtle);
        var defaultBg = System.Windows.Media.Brushes.Transparent;

        var dotActiveBrush = accentBrush;
        var dotInactiveBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(64, 128, 128, 128));

        var blocks = new[]
        {
            (PosBlock_TopLeft, PosDot_TopLeft, ToastPosition.TopLeft),
            (PosBlock_TopCenter, PosDot_TopCenter, ToastPosition.TopCenter),
            (PosBlock_TopRight, PosDot_TopRight, ToastPosition.TopRight),
            (PosBlock_Left, PosDot_Left, ToastPosition.Left),
            (PosBlock_Right, PosDot_Right, ToastPosition.Right),
            (PosBlock_BottomLeft, PosDot_BottomLeft, ToastPosition.BottomLeft),
            (PosBlock_BottomCenter, PosDot_BottomCenter, ToastPosition.BottomCenter),
            (PosBlock_BottomRight, PosDot_BottomRight, ToastPosition.BottomRight)
        };

        foreach (var (block, dot, pos) in blocks)
        {
            if (block is null || dot is null) continue;
            if (pos == position)
            {
                block.BorderBrush = accentBrush;
                block.Background = activeBg;
                dot.Background = dotActiveBrush;
            }
            else
            {
                block.BorderBrush = defaultBorderBrush;
                block.Background = defaultBg;
                dot.Background = dotInactiveBrush;
            }
        }
    }

    private void UiScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        if (UiScaleCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale))
            return;

        scale = UiScale.Normalize(scale);
        var previous = _settingsService.Settings.UiScale;
        UpdateGeneralPreference(
            "settings.ui-scale",
            "UI scale",
            previous,
            scale,
            value => _settingsService.Settings.UiScale = value,
            SelectUiScale,
            value =>
            {
                UiScale.Set(value);
                ApplyThemeColors();
                // Re-sync the floating widget: UiScale.Set only updates UiScale.Current, but the
                // widget's content transform is applied in RefreshLayout. Without this, the next
                // reposition sizes the window to the new scale while the content stays at the old
                // one, clipping part of the surface until the app restarts.
                ((App)Application.Current).RefreshWidgetWindowLayout();
            });
    }

    private void ToastDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;
        if (ToastDurationCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            return;

        var previous = _settingsService.Settings.ToastDurationSeconds;
        UpdateToastPreference(
            "settings.toast-duration",
            "Toast duration",
            previous,
            seconds,
            value => _settingsService.Settings.ToastDurationSeconds = value,
            SelectToastDuration,
            ToastWindow.SetDuration);
    }

    private void SystemToastDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;
        if (SystemToastDurationCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            return;

        var previous = _settingsService.Settings.SystemToastDurationSeconds;
        UpdateToastPreference(
            "settings.system-toast-duration",
            "System toast duration",
            previous,
            seconds,
            value => _settingsService.Settings.SystemToastDurationSeconds = value,
            SelectSystemToastDuration,
            ToastWindow.SetSystemDuration);
    }

    private void CaptureDockSideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.CaptureDockSide;
        var selected = (CaptureDockSide)Math.Clamp(CaptureDockSideCombo.SelectedIndex, 0, 1);
        UpdateCaptureSavePreference(
            "settings.capture-dock-side",
            "Toolbar Position",
            previous,
            selected,
            value => _settingsService.Settings.CaptureDockSide = value,
            value => CaptureDockSideCombo.SelectedIndex = (int)value);
    }

    private void ScrollingCaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.ScrollingCaptureMode;
        var selected = ScrollingCaptureModeCombo.SelectedIndex switch
        {
            1 => ScrollingCaptureMode.AssistAutoscroll,
            _ => ScrollingCaptureMode.Automatic
        };
        UpdateCaptureSavePreference(
            "settings.scrolling-capture-mode",
            "Scrolling capture mode",
            previous,
            selected,
            value => _settingsService.Settings.ScrollingCaptureMode = value,
            value => ScrollingCaptureModeCombo.SelectedIndex = value == ScrollingCaptureMode.AssistAutoscroll ? 1 : 0);
    }

    private void ToastFadeDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;
        if (ToastFadeDurationCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            return;

        var previous = _settingsService.Settings.ToastFadeOutSeconds;
        UpdateToastPreference(
            "settings.toast-fade-duration",
            "Toast fade duration",
            previous,
            seconds,
            value => _settingsService.Settings.ToastFadeOutSeconds = value,
            SelectToastFadeDuration,
            value => ToastWindow.SetFadeOutSeconds(value));
    }

    private void AutoPinPreviewsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;

        var previous = _settingsService.Settings.AutoPinPreviews;
        var enabled = AutoPinPreviewsCheck.IsChecked == true;
        UpdateToastPreference(
            "settings.auto-pin-previews",
            "Auto-pin previews",
            previous,
            enabled,
            value => _settingsService.Settings.AutoPinPreviews = value,
            value => AutoPinPreviewsCheck.IsChecked = value);
    }

    private void ToastPreviewClickActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;

        var previous = _settingsService.Settings.ToastPreviewClickAction;
        var selected = (ToastPreviewClickAction)Math.Clamp(
            ToastPreviewClickActionCombo.SelectedIndex, 0, (int)ToastPreviewClickAction.Close);
        UpdateToastPreference(
            "settings.toast-preview-click-action",
            "Notification preview click action",
            previous,
            selected,
            value => _settingsService.Settings.ToastPreviewClickAction = value,
            value => ToastPreviewClickActionCombo.SelectedIndex = (int)value);
    }

    private void NotificationsEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;

        var previous = _settingsService.Settings.NotificationsEnabled;
        var enabled = NotificationsEnabledCheck.IsChecked == true;
        UpdateToastPreference(
            "settings.notifications-enabled",
            "Show notifications",
            previous,
            enabled,
            value => _settingsService.Settings.NotificationsEnabled = value,
            value => NotificationsEnabledCheck.IsChecked = value,
            ToastWindow.SetNotificationsEnabled,
            value => UpdateNotificationsDependentState(value));
    }

    private void SystemNotificationsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;

        var previous = _settingsService.Settings.SystemNotificationsEnabled;
        var enabled = SystemNotificationsCheck.IsChecked == true;
        UpdateToastPreference(
            "settings.system-notifications-enabled",
            "System messages",
            previous,
            enabled,
            value => _settingsService.Settings.SystemNotificationsEnabled = value,
            value => SystemNotificationsCheck.IsChecked = value,
            ToastWindow.SetSystemNotificationsEnabled);
    }

    private void CelebrationsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;

        var previous = _settingsService.Settings.CelebrationsEnabled;
        var enabled = CelebrationsCheck.IsChecked == true;
        // No runtime setter needed: the celebration trigger reads CelebrationsEnabled
        // live from settings on each capture.
        UpdateToastPreference(
            "settings.celebrations-enabled",
            "Achievements",
            previous,
            enabled,
            value => _settingsService.Settings.CelebrationsEnabled = value,
            value => CelebrationsCheck.IsChecked = value);

        // Reflect the new on/off state in the milestone rail (dims when off, lights up when on).
        RefreshMilestoneRail(reveal: true);
        // Refresh the Achievements tab too, so stats/medals are current if the user visits it next.
        RefreshAchievements();
    }

    /// <summary>
    /// When the master "Show notifications" switch is off, dim and disable the rest of the
    /// Notifications tab (sub-types, placement, timing, and design) so those controls are not
    /// inviting configuration that has no visible effect.
    /// </summary>
    private void UpdateNotificationsDependentState(bool notificationsEnabled)
    {
        UpdateSystemNotificationsRowState(notificationsEnabled);
        ApplyNotificationsDependentVisual(CelebrationsNotificationsRow, notificationsEnabled);
        ApplyNotificationsDependentVisual(NotificationsPlacementSection, notificationsEnabled);
        ApplyNotificationsDependentVisual(NotificationsTimingSection, notificationsEnabled);
        // Confirm destination designer lives under Capture now — not gated by toast master switch.
        ApplyNotificationsDependentVisual(ConfirmPillsMovedBanner, notificationsEnabled);
    }

    // The "System messages" sub-toggle only applies while the master switch is on.
    private void UpdateSystemNotificationsRowState(bool notificationsEnabled)
        => ApplyNotificationsDependentVisual(SystemNotificationsRow, notificationsEnabled);

    private static void ApplyNotificationsDependentVisual(UIElement? element, bool enabled)
    {
        if (element is null)
            return;
        element.IsEnabled = enabled;
        element.Opacity = enabled ? 1.0 : 0.45;
    }

    private void UpdateToastPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action<T>? applyRuntime = null,
        Action<T>? applySuccessUi = null)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            SetToastPreferenceStatus(string.Empty);
            applySuccessUi?.Invoke(current);
            applyRuntime?.Invoke(current);
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

            _suppressToastPreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressToastPreferenceChange = false;
            }

            applyRuntime?.Invoke(previous);
            SetToastPreferenceStatus($"{label} change was not saved. Previous setting restored.");
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous notification setting was restored. Check Config -> Notifications and try again.\n{ex.Message}");
        }
    }

    private void SetToastPreferenceStatus(string message)
    {
        ToastPreferenceStatusText.Text = message;
        ToastPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SelectToastDuration(double seconds)
    {
        ToastDurationCombo.SelectedIndex = seconds switch
        {
            1.5 => 0,
            2.0 => 1,
            2.5 => 2,
            3.0 => 3,
            4.0 => 4,
            5.0 => 5,
            10.0 => 6,
            20.0 => 7,
            40.0 => 8,
            60.0 => 9,
            _ => 2
        };
    }

    private void SelectSystemToastDuration(double seconds)
    {
        SystemToastDurationCombo.SelectedIndex = seconds switch
        {
            1.5 => 0,
            2.0 => 1,
            2.5 => 2,
            3.0 => 3,
            4.0 => 4,
            5.0 => 5,
            10.0 => 6,
            20.0 => 7,
            40.0 => 8,
            60.0 => 9,
            _ => 4
        };
    }

    private void SelectToastFadeDuration(double seconds)
    {
        ToastFadeDurationCombo.SelectedIndex = seconds switch { 1.0 => 0, 2.0 => 1, 3.0 => 2, 5.0 => 3, _ => 2 };
    }

    private void UpdateGeneralPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action<T>? applyRuntime = null)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            SetGeneralPreferenceStatus(string.Empty);
            applyRuntime?.Invoke(current);
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

            _suppressGeneralPreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressGeneralPreferenceChange = false;
            }

            applyRuntime?.Invoke(previous);
            SetGeneralPreferenceStatus($"{label} change was not saved. Previous setting restored.");
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous general setting was restored. Check Config -> General and try again.\n{ex.Message}");
        }
    }

    private void SetGeneralPreferenceStatus(string message)
    {
        if (GeneralPreferenceStatusText != null)
        {
            GeneralPreferenceStatusText.Text = message;
            GeneralPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (SoundsPreferenceStatusText != null)
        {
            SoundsPreferenceStatusText.Text = message;
            SoundsPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void ShowImageSearchBarCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.ShowImageSearchBar;
        var selected = ShowImageSearchBarCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.show-image-search-bar",
            "Image search bar",
            previous,
            selected,
            value => _settingsService.Settings.ShowImageSearchBar = value,
            value => ShowImageSearchBarCheck.IsChecked = value,
            value =>
            {
                ((App)Application.Current).RefreshHistoryWindowIfOpen();
            });
    }

    private void AutoIndexImagesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressAutoIndexImagesChange) return;

        var previous = _settingsService.Settings.AutoIndexImages;
        var enabled = AutoIndexImagesCheck.IsChecked == true;

        try
        {
            _settingsService.Settings.AutoIndexImages = enabled;
            _settingsService.Save();

            if (enabled)
                _imageSearchIndexService.RequestSync(_historyService.ImageEntries, _settingsService.Settings.OcrLanguageTag);

            SetImageIndexMaintenanceStatus(enabled
                ? "Automatic image indexing enabled."
                : "Automatic image indexing disabled.");

            UpdateImageIndexVisibility(enabled);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.auto-index-images", ex);
            _settingsService.Settings.AutoIndexImages = previous;
            try
            {
                _settingsService.Save();
            }
            catch (Exception rollbackEx)
            {
                AppDiagnostics.LogError("settings.auto-index-images-rollback", rollbackEx);
            }

            _suppressAutoIndexImagesChange = true;
            try
            {
                AutoIndexImagesCheck.IsChecked = previous;
            }
            finally
            {
                _suppressAutoIndexImagesChange = false;
            }

            SetImageIndexMaintenanceStatus("Automatic image indexing failed. Previous setting restored.");
            ToastWindow.ShowError(
                "Image indexing setting failed",
                $"The previous image indexing setting was restored. Try again from Settings.\n{ex.Message}");
            return;
        }

        try
        {
            ((App)Application.Current).RefreshHistoryWindowIfOpen();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.auto-index-images-history-refresh", ex);
            SetImageIndexMaintenanceStatus("Image indexing saved, but Gallery did not refresh.");
            ToastWindow.ShowError(
                "Gallery refresh failed",
                $"The image indexing setting was saved, but Gallery did not refresh. Switch tabs or use Retry in Gallery.\n{ex.Message}");
        }
    }

    private void ResetImageIndexesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ImageIndexResetInProgress)
        {
            SetImageIndexMaintenanceStatus("Image index reset is already running.");
            return;
        }

        if (!ThemedConfirmDialog.Confirm(
                this,
                "Reset Image Indexes",
                "Reset the image OCR/search index?\n\nThis rebuilds screenshot search data in the background.",
                "Reset",
                "Cancel"))
        {
            SetImageIndexMaintenanceStatus("Image index reset canceled. Existing search data was left in place.");
            return;
        }

        ImageIndexResetInProgress = true;
        ResetImageIndexesBtn.IsEnabled = false;
        ResetImageIndexesBtn.Content = "Resetting...";

        try
        {
            try
            {
                _imageSearchIndexService.ReindexAll(_historyService.ImageEntries, _settingsService.Settings.OcrLanguageTag);
                SetImageIndexMaintenanceStatus("Image search index reset requested.");
                ToastWindow.Show("Image indexes reset", "Screenshot search will rebuild in the background.");
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("settings.image-index-reset", ex);
                SetImageIndexMaintenanceStatus("Image index reset failed. Existing search data was left in place.");
                ToastWindow.ShowError(
                    "Reset index failed",
                    $"CyberSnap could not reset the image search index. Existing search data was left in place. Try again from Settings.\n{ex.Message}");
                return;
            }

            try
            {
                ((App)Application.Current).RefreshHistoryWindowIfOpen();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("settings.image-index-reset-history-refresh", ex);
                SetImageIndexMaintenanceStatus("Image index reset requested, but Gallery did not refresh.");
                ToastWindow.ShowError(
                    "Gallery refresh failed",
                    $"The image index reset was requested, but Gallery did not refresh. Switch tabs or use Retry in Gallery.\n{ex.Message}");
            }
        }
        finally
        {
            ImageIndexResetInProgress = false;
            ResetImageIndexesBtn.Content = "Reset index";
            ResetImageIndexesBtn.IsEnabled = true;
        }
    }

    private void SetImageIndexMaintenanceStatus(string message)
    {
        ImageIndexMaintenanceStatusText.Text = message;
        ImageIndexMaintenanceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void WindowDetectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = (Mode: _settingsService.Settings.WindowDetection, DetectWindows: _settingsService.Settings.DetectWindows);
        var enabled = WindowDetectionCheck.IsChecked == true;
        var mode = enabled ? WindowDetectionMode.WindowOnly : WindowDetectionMode.Off;
        var selected = (Mode: mode, DetectWindows: enabled);
        UpdateCaptureSavePreference(
            "settings.window-detection",
            "Window detection",
            previous,
            selected,
            value =>
            {
                _settingsService.Settings.WindowDetection = value.Mode;
                _settingsService.Settings.DetectWindows = value.DetectWindows;
            },
            value => WindowDetectionCheck.IsChecked = value.DetectWindows,
            () => WindowDetectionCheck.IsChecked = enabled);
    }

    private void CaptureDelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.CaptureDelaySeconds;
        var selected = CaptureDelayCombo.SelectedIndex switch { 1 => 3, 2 => 5, 3 => 10, _ => 0 };
        UpdateCaptureSavePreference(
            "settings.capture-delay",
            "Capture delay",
            previous,
            selected,
            value => _settingsService.Settings.CaptureDelaySeconds = value,
            value => CaptureDelayCombo.SelectedIndex = value switch { 3 => 1, 5 => 2, 10 => 3, _ => 0 });
    }

    private void ShowCaptureWidgetCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.ShowCaptureWidget;
        var selected = ShowCaptureWidgetCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.show-capture-widget",
            "Floating capture widget",
            previous,
            selected,
            value =>
            {
                _settingsService.Settings.ShowCaptureWidget = value;
                UpdateWidgetOptionsVisibility(value);
            },
            value =>
            {
                ShowCaptureWidgetCheck.IsChecked = value;
                UpdateWidgetOptionsVisibility(value);
            },
            value =>
            {
                if (value)
                    ((App)Application.Current).EnsureWidgetWindowCreated();
                else
                    ((App)Application.Current).CloseWidgetWindow();
            });
    }

    private void WidgetAlwaysOnTopCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.WidgetAlwaysOnTop;
        var selected = WidgetAlwaysOnTopCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.widget-always-on-top",
            "Always on top",
            previous,
            selected,
            value => _settingsService.Settings.WidgetAlwaysOnTop = value,
            value => WidgetAlwaysOnTopCheck.IsChecked = value,
            value => ((App)Application.Current).SyncWidgetAlwaysOnTop(value));
    }

    // Pulls the "Always on top" checkbox back into sync after the change originated on the widget's
    // own toggle. Suppressed so reflecting the value doesn't echo back into a save/sync round-trip.
    public void RefreshAlwaysOnTopCheck()
    {
        if (WidgetAlwaysOnTopCheck is null) return;

        _suppressGeneralPreferenceChange = true;
        try { WidgetAlwaysOnTopCheck.IsChecked = _settingsService.Settings.WidgetAlwaysOnTop; }
        finally { _suppressGeneralPreferenceChange = false; }
    }

    // ── Global auto-copy exclusions (master lives in after-capture outcome pills) ─

    private void AutoCopyExcludeImagesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange || _suppressAutoCopyPreferenceChange) return;

        var previous = _settingsService.Settings.AutoCopyExcludeImages;
        var selected = AutoCopyExcludeImagesCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.auto-copy-exclude-images",
            "Don't auto-copy screenshots",
            previous,
            selected,
            value =>
            {
                AutoCopyPreferences.SetExcluded(_settingsService.Settings, AutoCopyKind.Image, value);
                AutoCopyPreferences.SyncAfterCaptureCopyBits(_settingsService.Settings);
            },
            value => AutoCopyExcludeImagesCheck.IsChecked = value,
            () =>
            {
                RefreshAfterCaptureOutcomeEditor();
                SettingsService.PublishAutoCopyState(_settingsService.Settings);
                ((App)Application.Current).SyncWidgetAutoCopyToggle();
            });
    }

    private void AutoCopyExcludeRecordingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange || _suppressAutoCopyPreferenceChange) return;

        var previous = _settingsService.Settings.AutoCopyExcludeRecording;
        var selected = AutoCopyExcludeRecordingCheck.IsChecked == true;
        UpdateRecordingPreference(
            "settings.auto-copy-exclude-recording",
            "Don't auto-copy video",
            previous,
            selected,
            value => AutoCopyPreferences.SetExcluded(_settingsService.Settings, AutoCopyKind.Video, value),
            value => AutoCopyExcludeRecordingCheck.IsChecked = value,
            value =>
            {
                SettingsService.PublishAutoCopyState(_settingsService.Settings);
                ((App)Application.Current).SyncWidgetAutoCopyToggle();
            });
    }

    private void AutoCopyExcludeGifCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressRecordingPreferenceChange || _suppressAutoCopyPreferenceChange) return;

        var previous = _settingsService.Settings.AutoCopyExcludeGif;
        var selected = AutoCopyExcludeGifCheck.IsChecked == true;
        UpdateRecordingPreference(
            "settings.auto-copy-exclude-gif",
            "Don't auto-copy GIF",
            previous,
            selected,
            value => AutoCopyPreferences.SetExcluded(_settingsService.Settings, AutoCopyKind.Gif, value),
            value => AutoCopyExcludeGifCheck.IsChecked = value,
            value =>
            {
                SettingsService.PublishAutoCopyState(_settingsService.Settings);
                ((App)Application.Current).SyncWidgetAutoCopyToggle();
            });
    }

    public void RefreshAutoCopyChecks()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshAutoCopyChecks);
            return;
        }

        // Re-read from disk cache when the change may have come from the widget / OCR menu.
        try
        {
            var fresh = SettingsService.LoadStatic();
            if (fresh != null)
            {
                _settingsService.Settings.AutoCopyToClipboard = fresh.AutoCopyToClipboard;
                _settingsService.Settings.AutoCopyExcludeImages = fresh.AutoCopyExcludeImages;
                _settingsService.Settings.AutoCopyExcludeOcr = fresh.AutoCopyExcludeOcr;
                _settingsService.Settings.AutoCopyExcludeRecording = fresh.AutoCopyExcludeRecording;
                _settingsService.Settings.AutoCopyExcludeGif = fresh.AutoCopyExcludeGif;
                _settingsService.Settings.OcrAutoCopyToClipboard = fresh.OcrAutoCopyToClipboard;
                _settingsService.Settings.AfterCapture = fresh.AfterCapture;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.refresh-auto-copy", ex.Message, ex);
        }

        ApplyAutoCopyControlsFromSettings(_settingsService.Settings);
        RefreshAfterCaptureOutcomeEditor();
    }

    private void OnAutoCopyToClipboardChanged(bool value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAutoCopyToClipboardChanged(value)));
            return;
        }

        RefreshAutoCopyChecks();
    }

    private void ApplyAutoCopyControlsFromSettings(AppSettings s)
    {
        _suppressAutoCopyPreferenceChange = true;
        _suppressGeneralPreferenceChange = true;
        _suppressCaptureSavePreferenceChange = true;
        _suppressOcrPreferenceChange = true;
        _suppressRecordingPreferenceChange = true;
        try
        {
            if (AutoCopyExcludeImagesCheck != null)
                AutoCopyExcludeImagesCheck.IsChecked = s.AutoCopyExcludeImages;
            if (AutoCopyExcludeOcrCheck != null)
                AutoCopyExcludeOcrCheck.IsChecked = s.AutoCopyExcludeOcr;
            if (AutoCopyExcludeRecordingCheck != null)
                AutoCopyExcludeRecordingCheck.IsChecked = s.AutoCopyExcludeRecording;
            if (AutoCopyExcludeGifCheck != null)
                AutoCopyExcludeGifCheck.IsChecked = s.AutoCopyExcludeGif;
            UpdateAutoCopyExcludeEnabledState();
            AfterCaptureOutcomeEditor?.LoadFromSettings(s);
        }
        finally
        {
            _suppressAutoCopyPreferenceChange = false;
            _suppressGeneralPreferenceChange = false;
            _suppressCaptureSavePreferenceChange = false;
            _suppressOcrPreferenceChange = false;
            _suppressRecordingPreferenceChange = false;
        }
    }

    private void UpdateAutoCopyExcludeEnabledState()
    {
        bool masterOn = _settingsService.Settings.AutoCopyToClipboard;
        double opacity = masterOn ? 1.0 : 0.45;

        void Apply(FrameworkElement? row, System.Windows.Controls.CheckBox? check)
        {
            if (row != null)
            {
                row.IsEnabled = masterOn;
                row.Opacity = opacity;
            }
            if (check != null)
                check.IsEnabled = masterOn;
        }

        Apply(AutoCopyExcludeImagesRow, AutoCopyExcludeImagesCheck);
        Apply(AutoCopyExcludeOcrRow, AutoCopyExcludeOcrCheck);
        Apply(AutoCopyExcludeRecordingRow, AutoCopyExcludeRecordingCheck);
        Apply(AutoCopyExcludeGifRow, AutoCopyExcludeGifCheck);
    }

    private void WidgetCaptureCursorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.ShowCursor;
        var selected = WidgetCaptureCursorCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.show-cursor",
            "Capture cursor",
            previous,
            selected,
            value => _settingsService.Settings.SetCaptureCursorForAll(value),
            value =>
            {
                WidgetCaptureCursorCheck.IsChecked = value;
                if (ShowCursorCheck != null)
                    ShowCursorCheck.IsChecked = value;
                SyncRecordingShowCursorChecks(value);
            },
            value => ((App)Application.Current).SyncWidgetCaptureCursorToggle());
    }

    private void VideoEnableEditorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.OpenVideoTrimmerAfterCapture;
        var selected = VideoEnableEditorCheck.IsChecked == true;
        UpdateGeneralPreference(
            "settings.open-video-trimmer-after-capture",
            "Open trimmer after video",
            previous,
            selected,
            value => _settingsService.Settings.OpenVideoTrimmerAfterCapture = value,
            value => {
                if (VideoEnableEditorCheck != null)
                    VideoEnableEditorCheck.IsChecked = value;
            });

        RefreshEditorPreviewState();
    }

    // Pulls Capture-tab + Widget "Capture cursor" checkboxes into sync after the widget toggle changes.
    // Also refreshes Video/GIF show-cursor boxes (master may have rewritten those flags).
    public void RefreshShowCursorCheck()
    {
        _suppressGeneralPreferenceChange = true;
        _suppressCaptureSavePreferenceChange = true;
        _suppressRecordingPreferenceChange = true;
        try
        {
            var s = _settingsService.Settings;
            if (WidgetCaptureCursorCheck != null)
                WidgetCaptureCursorCheck.IsChecked = s.ShowCursor;
            if (ShowCursorCheck != null)
                ShowCursorCheck.IsChecked = s.ShowCursor;
            if (VideoShowCursorCheck != null)
                VideoShowCursorCheck.IsChecked = s.VideoShowCursor;
            if (GifShowCursorCheck != null)
                GifShowCursorCheck.IsChecked = s.GifShowCursor;
        }
        finally
        {
            _suppressGeneralPreferenceChange = false;
            _suppressCaptureSavePreferenceChange = false;
            _suppressRecordingPreferenceChange = false;
        }
    }

    private void SyncRecordingShowCursorChecks(bool enabled)
    {
        _suppressRecordingPreferenceChange = true;
        try
        {
            if (VideoShowCursorCheck != null)
                VideoShowCursorCheck.IsChecked = enabled;
            if (GifShowCursorCheck != null)
                GifShowCursorCheck.IsChecked = enabled;
        }
        finally
        {
            _suppressRecordingPreferenceChange = false;
        }
    }

    /// <summary>Keeps Video/GIF "open trimmer" checkboxes in sync (Notifications designer / recording bars).</summary>
    public void RefreshEnableEditorCheck()
    {
        _suppressGeneralPreferenceChange = true;
        try
        {
            if (VideoEnableEditorCheck != null)
                VideoEnableEditorCheck.IsChecked = _settingsService.Settings.OpenVideoTrimmerAfterCapture;
            if (GifEnableEditorCheck != null)
                GifEnableEditorCheck.IsChecked = _settingsService.Settings.OpenGifTrimmerAfterCapture;
        }
        finally { _suppressGeneralPreferenceChange = false; }

        RefreshEditorPreviewState();
    }

    private void WidgetDockEdgeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;

        var previous = _settingsService.Settings.WidgetDockEdge;
        var selected = (CaptureDockSide)Math.Clamp(WidgetDockEdgeCombo.SelectedIndex, 0, 3);
        UpdateGeneralPreference(
            "settings.widget-dock-edge",
            "Widget dock edge",
            previous,
            selected,
            value => _settingsService.Settings.WidgetDockEdge = value,
            value => WidgetDockEdgeCombo.SelectedIndex = (int)value,
            value =>
            {
                ((App)Application.Current).RefreshWidgetWindowLayout();
            });
    }

    private void WidgetHoverDelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        if (WidgetHoverDelayCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        if (!int.TryParse(tag, out var delayMs))
            return;

        var previous = _settingsService.Settings.WidgetHoverDelayMs;
        UpdateGeneralPreference(
            "settings.widget-hover-delay",
            "Widget hover delay",
            previous,
            delayMs,
            value => _settingsService.Settings.WidgetHoverDelayMs = value,
            SelectWidgetHoverDelay,
            value =>
            {
                ((App)Application.Current).RefreshWidgetWindowLayout();
            });
    }

    private void SelectWidgetHoverDelay(int delayMs)
    {
        WidgetHoverDelayCombo.SelectedIndex = delayMs switch
        {
            0 => 0,
            100 => 1,
            250 => 2,
            500 => 3,
            1000 => 4,
            _ => 2
        };
    }

    // Builds the widget-monitor dropdown from the live monitor list. With a single monitor the
    // setting stays visible but inert (disabled with a clear placeholder) so users know it exists.
    private void PopulateWidgetMonitors()
    {
        WidgetMonitorCombo.Items.Clear();
        var screens = PopupWindowHelper.GetSortedScreens();

        if (screens.Length <= 1)
        {
            WidgetMonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Translate("Only one monitor detected"),
                Tag = -1
            });
            WidgetMonitorCombo.SelectedIndex = 0;
            WidgetMonitorCombo.IsEnabled = false;
            return;
        }

        WidgetMonitorCombo.IsEnabled = true;
        WidgetMonitorCombo.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.Translate("Follow active monitor"),
            Tag = -1
        });
        for (int i = 0; i < screens.Length; i++)
        {
            WidgetMonitorCombo.Items.Add(new ComboBoxItem
            {
                Content = CaptureWidgetWindow.FormatMonitorLabel(i, screens[i].Primary),
                Tag = i
            });
        }
        SelectWidgetMonitor(_settingsService.Settings.WidgetMonitorIndex);
    }

    private void SelectWidgetMonitor(int monitorIndex)
    {
        for (int i = 0; i < WidgetMonitorCombo.Items.Count; i++)
        {
            if (WidgetMonitorCombo.Items[i] is ComboBoxItem ci && ci.Tag is int tag && tag == monitorIndex)
            {
                WidgetMonitorCombo.SelectedIndex = i;
                return;
            }
        }
        WidgetMonitorCombo.SelectedIndex = 0; // fall back to "Follow active monitor"
    }

    private void WidgetMonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGeneralPreferenceChange) return;
        if (WidgetMonitorCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int idx)
            return;

        var previous = _settingsService.Settings.WidgetMonitorIndex;
        UpdateGeneralPreference(
            "settings.widget-monitor",
            "Widget monitor",
            previous,
            idx,
            value => _settingsService.Settings.WidgetMonitorIndex = value,
            SelectWidgetMonitor,
            value =>
            {
                ((App)Application.Current).RefreshWidgetWindowLayout();
            });
    }

    private void WidgetResetPositionButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.WidgetDockPositionOffset = 0.5;
        _settingsService.Save();
        ((App)Application.Current).RefreshWidgetWindowLayout();
    }

    private void UpdateWidgetOptionsVisibility(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        WidgetOptionsSeparator.Visibility = visibility;
        WidgetAlwaysOnTopRow.Visibility = visibility;
        WidgetAlwaysOnTopSeparator.Visibility = visibility;
        WidgetCaptureCursorRow.Visibility = visibility;
        WidgetCaptureCursorSeparator.Visibility = visibility;
        WidgetDockEdgeRow.Visibility = visibility;
        WidgetHoverDelaySeparator.Visibility = visibility;
        WidgetHoverDelayRow.Visibility = visibility;
        WidgetMonitorSeparator.Visibility = visibility;
        WidgetMonitorRow.Visibility = visibility;
        WidgetResetPositionSeparator.Visibility = visibility;
        WidgetResetPositionRow.Visibility = visibility;
    }

    private void UpdateImageIndexVisibility(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ImageIndexSeparator.Visibility = visibility;
        ImageIndexRow.Visibility = visibility;
    }

    private void TestToastBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Celebration / achievement flourish preview (replaces the old capture-image toast mock).
            // Same visual family as milestone / streak / welcome-back celebrations.
            var settings = _settingsService.Settings;
            int count = Math.Max(1, settings.CelebrationCaptureCount);
            string title;
            string body;
            string? iconId = null;

            if (CelebrationMilestones.IsMilestone(count))
            {
                title = LocalizationService.Translate("Milestone reached!");
                body = string.Format(LocalizationService.Translate("{0} captures and counting"), count);
                iconId = "trophy";
            }
            else if (settings.CurrentStreak >= 3 && CelebrationMilestones.IsStreakMilestone(settings.CurrentStreak))
            {
                title = LocalizationService.Translate("On a roll!");
                body = string.Format(LocalizationService.Translate("{0}-day streak"), settings.CurrentStreak);
            }
            else
            {
                title = LocalizationService.Translate("Welcome back!");
                body = LocalizationService.Translate("Your first capture today");
            }

            ToastWindow.Show(ToastSpec.Standard(title, body) with
            {
                Celebrate = true,
                SuppressSound = false,
                IsSystemMessage = false,
                CelebrationBodyIconId = iconId
            });
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.test-celebration-toast", ex);
            ToastWindow.ShowError(
                LocalizationService.Translate("Test failed"),
                $"{LocalizationService.Translate("Could not show test notification:")}\n{ex.Message}");
        }
    }

    private void TestSystemAlertBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Brief system alert — same family as "Video recorded" / encoding wait.
            // Plays SoundEvent.System (customizable in Sounds) unless muted.
            ToastWindow.Show(ToastSpec.Standard(
                LocalizationService.Translate("Screenshot ready"),
                LocalizationService.Translate("Copied to clipboard")));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.test-system-alert", ex);
            ToastWindow.ShowError(
                LocalizationService.Translate("Test failed"),
                $"{LocalizationService.Translate("Could not show test notification:")}\n{ex.Message}");
        }
    }
}
