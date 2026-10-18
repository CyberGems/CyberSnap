using System.Windows;
using CheckBox = System.Windows.Controls.CheckBox;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private void DeliveryPreferenceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange || _suppressAutoCopyPreferenceChange) return;
        var check = (CheckBox)sender;
        bool scroll = check == AutoCopyScrollCheck || check == ShowScrollCapturePreviewCheck;
        bool copyOption = check == AutoCopyScrollCheck || check == AutoCopyColorCheck;
        bool selected = check.IsChecked == true;
        _settingsService.RefreshResultDeliveryPreferences();
        var settings = _settingsService.Settings;
        var previous = scroll
            ? new ResultDelivery.Plan(settings.ScrollAutoCopyToClipboard, settings.ShowScrollCapturePreview)
            : new ResultDelivery.Plan(settings.ColorDetailAutoCopy, settings.ShowColorDetailWindow);
        var current = copyOption ? previous with { Copy = selected } : previous with { ShowWindow = selected };
        if (scroll)
        {
            var outcome = AfterCaptureOutcomeModel.Normalize(
                AfterCaptureOutcomeModel.FromSettings(settings, CaptureKind.ScrollCapture) with
                { Clipboard = current.Copy, Preview = current.ShowWindow });
            current = new(outcome.Clipboard, outcome.Preview);
        }
        else if (copyOption && !selected) current = current with { ShowWindow = true };
        else if (!copyOption && !selected) current = current with { Copy = true };

        UpdateCaptureSavePreference("settings.mode-delivery", check.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) as string ?? "",
            previous, current,
            value =>
            {
                if (scroll)
                {
                    settings.ScrollAutoCopyToClipboard = value.Copy;
                    settings.ShowScrollCapturePreview = value.ShowWindow;
                }
                else
                {
                    settings.ColorDetailAutoCopy = value.Copy;
                    settings.ShowColorDetailWindow = value.ShowWindow;
                }
            },
            _ => ApplyAutoCopyControlsFromSettings(settings),
            () =>
            {
                ApplyAutoCopyControlsFromSettings(settings);
                SettingsService.PublishAutoCopyPreferences(settings);
            });
    }
}
