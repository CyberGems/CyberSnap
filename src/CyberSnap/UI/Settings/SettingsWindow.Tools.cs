using System.Windows;
using System.Windows.Controls;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using RadioButton = System.Windows.Controls.RadioButton;
using CheckBox = System.Windows.Controls.CheckBox;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private string _selectedToolKey = "ocr";
    private bool _suppressToolsPreferenceChange;

    public void NavigateToPinSettings()
    {
        SelectSettingsTab("pin");
        ToolsPanel.ScrollToTop();
    }

    private void InitializeToolsSettings()
    {
        ToolsOcrIcon.Data = FluentIcons.GetGeometry("ocr");
        ToolsPickerIcon.Data = FluentIcons.GetGeometry("picker");
        ToolsQrIcon.Data = FluentIcons.GetGeometry("scan");
        ToolsRulerIcon.Data = FluentIcons.GetGeometry("ruler");
        ToolsZoomIcon.Data = FluentIcons.GetGeometry("zoom");
        ToolsPinIcon.Data = FluentIcons.GetGeometry("pin");
        ApplyPinControls();
        ApplyZoomControls();
        ApplyColorFormatControls(_settingsService.Settings);
        RefreshToolShortcut();
        HotkeyChanged += RefreshToolShortcut;
        LocalizationChanged += RefreshToolShortcut;
        LocalizationChanged += ApplyZoomControls;
    }

    private void ToolTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;
        _selectedToolKey = key;
        if (OcrToolPanel is null || PickerToolPanel is null || QrToolPanel is null || RulerToolPanel is null || ZoomToolPanel is null || PinToolPanel is null) return;
        ApplyToolsSelection();
        ToolsPanel.ScrollToTop();
    }

    private void ApplyToolsSelection()
    {
        OcrToolPanel.Visibility = _selectedToolKey == "ocr" ? Visibility.Visible : Visibility.Collapsed;
        PickerToolPanel.Visibility = _selectedToolKey == "picker" ? Visibility.Visible : Visibility.Collapsed;
        QrToolPanel.Visibility = _selectedToolKey == "qr" ? Visibility.Visible : Visibility.Collapsed;
        RulerToolPanel.Visibility = _selectedToolKey == "ruler" ? Visibility.Visible : Visibility.Collapsed;
        PinToolPanel.Visibility = _selectedToolKey == "pin" ? Visibility.Visible : Visibility.Collapsed;
        ZoomToolPanel.Visibility = _selectedToolKey == "zoom" ? Visibility.Visible : Visibility.Collapsed;
        ToolHistoryCard.Visibility = ToolHistoryLabel.Visibility = ToolSoundsButton.Visibility =
            _selectedToolKey is "zoom" or "pin" ? Visibility.Collapsed : Visibility.Visible;
        if (_selectedToolKey == "ocr" && ToolsTab.IsChecked == true)
            LoadOcrTab();
        RefreshToolShortcut();
    }

    private void SelectToolSettings(string key)
    {
        var tab = key switch
        {
            "picker" => ToolPickerTab,
            "qr" or "scan" => ToolQrTab,
            "ruler" => ToolRulerTab,
            "zoom" => ToolZoomTab,
            "pin" => ToolPinTab,
            _ => ToolOcrTab
        };
        tab.IsChecked = true;
        ApplyToolsSelection();
    }

    private string SelectedStandaloneToolId => _selectedToolKey switch
    {
        "picker" => "_standaloneColorPicker",
        "qr" => "_standaloneScan",
        "ruler" => "_standaloneRuler",
        "zoom" => "_standaloneZoom",
        "pin" => "_standalonePin",
        _ => "_standaloneOcr"
    };

    private void RefreshToolShortcut()
    {
        if (ToolShortcutValueText is null) return;
        var (modifiers, key) = _settingsService.Settings.GetToolHotkey(SelectedStandaloneToolId);
        ToolShortcutValueText.Text = HotkeyFormatter.Format(modifiers, key);
    }

    private void ChangeToolShortcut_Click(object sender, RoutedEventArgs e)
    {
        SelectSettingsTab("hotkeys");
        UpdateLayout();
        var row = CaptureToolsPanel.Children.OfType<Border>().FirstOrDefault(card => Equals(card.Tag, SelectedStandaloneToolId));
        if (row is null) return;
        ScrollToElement(row);
        HighlightSettingCard(row);
    }

    private void ToolSounds_Click(object sender, RoutedEventArgs e) => SelectSettingsTab("sounds");

    private static readonly double[] PinScales = { .25, .5, .75, 1, 1.25, 1.5, 2 };
    private void ApplyPinControls()
    {
        bool previous = _suppressToolsPreferenceChange;
        _suppressToolsPreferenceChange = true;
        try
        {
            var settings = _settingsService.Settings;
            PinInitialScaleCombo.Items.Clear();
            var scales = PinScales.Append(Capture.PinImageForm.NormalizeScale(settings.PinInitialScale)).Distinct().OrderBy(scale => scale);
            foreach (double scale in scales) PinInitialScaleCombo.Items.Add(new ComboBoxItem { Content = $"{scale:P0}", Tag = scale });
            PinInitialScaleCombo.SelectedItem = PinInitialScaleCombo.Items.OfType<ComboBoxItem>().First(item => Math.Abs((double)item.Tag - Capture.PinImageForm.NormalizeScale(settings.PinInitialScale)) < .001);
            PinSmoothImageCheck.IsChecked = settings.PinSmoothImage;
            PinAlwaysOnTopCheck.IsChecked = settings.PinAlwaysOnTop;
            PinIncludeInCapturesCheck.IsChecked = settings.PinIncludeInCaptures;
        }
        finally { _suppressToolsPreferenceChange = previous; }
    }
    private void PinInitialScale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange || PinInitialScaleCombo.SelectedItem is not ComboBoxItem { Tag: double selected }) return;
        UpdateToolsPreference("Initial image scale", _settingsService.Settings.PinInitialScale, selected,
            value => _settingsService.Settings.PinInitialScale = value, _ => ApplyPinControls());
    }
    private void PinPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange) return;
        var check = (CheckBox)sender;
        if (ReferenceEquals(check, PinSmoothImageCheck))
            UpdateToolsPreference("Smooth pinned images", _settingsService.Settings.PinSmoothImage, check.IsChecked == true,
                value => _settingsService.Settings.PinSmoothImage = value, _ => ApplyPinControls(), () => (Application.Current as App)?.RefreshPinnedImagePreferences());
        else if (ReferenceEquals(check, PinAlwaysOnTopCheck))
            UpdateToolsPreference("Keep pinned images on top", _settingsService.Settings.PinAlwaysOnTop, check.IsChecked == true,
                value => _settingsService.Settings.PinAlwaysOnTop = value, _ => ApplyPinControls(), () => (Application.Current as App)?.RefreshPinnedImagePreferences());
        else
            UpdateToolsPreference("Include pinned images in captures", _settingsService.Settings.PinIncludeInCaptures, check.IsChecked == true,
                value => _settingsService.Settings.PinIncludeInCaptures = value, _ => ApplyPinControls(), () => (Application.Current as App)?.RefreshPinnedImagePreferences());
    }

    private void ApplyZoomControls()
    {
        bool previous = _suppressToolsPreferenceChange;
        _suppressToolsPreferenceChange = true;
        try
        {
            var settings = _settingsService.Settings;
            for (int index = 0; index < ZoomInitialLevelCombo.Items.Count; index++)
                ((ComboBoxItem)ZoomInitialLevelCombo.Items[index]).Content = $"{1.25 + index * 0.25:0.##}×";
            double level = Capture.ZoomViewport.NormalizeZoom(settings.ZoomInitialLevel);
            ZoomInitialLevelCombo.SelectedIndex = Math.Clamp((int)Math.Round((level - 1.25) / 0.25), 0, 11);
            ZoomSmoothImageCheck.IsChecked = settings.ZoomSmoothImage;
            ZoomAnimateCheck.IsChecked = settings.ZoomAnimate;
            ZoomShowHelpBarCheck.IsChecked = settings.ZoomShowHelpBar;
        }
        finally { _suppressToolsPreferenceChange = previous; }
    }

    private void ZoomInitialLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange || ZoomInitialLevelCombo.SelectedIndex < 0) return;
        double level = 1.25 + ZoomInitialLevelCombo.SelectedIndex * 0.25;
        UpdateToolsPreference("Initial zoom level", _settingsService.Settings.ZoomInitialLevel, level,
            value => _settingsService.Settings.ZoomInitialLevel = value, _ => ApplyZoomControls());
    }

    private void ZoomSmoothImage_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange) return;
        UpdateToolsPreference("Smooth the enlarged image", _settingsService.Settings.ZoomSmoothImage, ZoomSmoothImageCheck.IsChecked == true,
            value => _settingsService.Settings.ZoomSmoothImage = value, _ => ApplyZoomControls());
    }

    private void ZoomAnimate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange) return;
        UpdateToolsPreference("Animate zoom changes", _settingsService.Settings.ZoomAnimate, ZoomAnimateCheck.IsChecked == true,
            value => _settingsService.Settings.ZoomAnimate = value, _ => ApplyZoomControls());
    }

    private void ZoomShowHelpBar_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange) return;
        UpdateToolsPreference("Show help bar", _settingsService.Settings.ZoomShowHelpBar, ZoomShowHelpBarCheck.IsChecked == true,
            value => _settingsService.Settings.ZoomShowHelpBar = value, _ => ApplyZoomControls());
    }

    private void ApplyColorFormatControls(AppSettings settings)
    {
        if (ColorCopyFormatCombo is null || ColorIncludeHashCheck is null) return;
        bool previous = _suppressToolsPreferenceChange;
        _suppressToolsPreferenceChange = true;
        try
        {
            ColorCopyFormatCombo.SelectedIndex = settings.ColorDetailCopyFormat switch
            { ColorDetailCopyFormat.Rgb => 1, ColorDetailCopyFormat.Hsl => 2, _ => 0 };
            ColorIncludeHashCheck.IsChecked = settings.ColorDetailIncludeHash;
            ColorIncludeHashCheck.IsEnabled = settings.ColorDetailCopyFormat == ColorDetailCopyFormat.Hex;
        }
        finally { _suppressToolsPreferenceChange = previous; }
    }

    private void ColorCopyFormatCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange || ColorCopyFormatCombo.SelectedIndex < 0) return;
        var selected = (ColorDetailCopyFormat)ColorCopyFormatCombo.SelectedIndex;
        _settingsService.RefreshResultDeliveryPreferences();
        UpdateToolsPreference("Favorite format", _settingsService.Settings.ColorDetailCopyFormat, selected,
            value => _settingsService.Settings.ColorDetailCopyFormat = value,
            _ => ApplyColorFormatControls(_settingsService.Settings),
            () => SettingsService.PublishAutoCopyPreferences(_settingsService.Settings));
        ApplyColorFormatControls(_settingsService.Settings);
    }

    private void ColorIncludeHashCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToolsPreferenceChange) return;
        bool selected = ColorIncludeHashCheck.IsChecked == true;
        _settingsService.RefreshResultDeliveryPreferences();
        UpdateToolsPreference("Include # in HEX", _settingsService.Settings.ColorDetailIncludeHash, selected,
            value => _settingsService.Settings.ColorDetailIncludeHash = value,
            _ => ApplyColorFormatControls(_settingsService.Settings),
            () => SettingsService.PublishAutoCopyPreferences(_settingsService.Settings));
    }

    private void UpdateToolsPreference<T>(string label, T previous, T current, Action<T> setValue,
        Action<T> restoreUi, Action? publish = null)
    {
        try
        {
            setValue(current);
            PersistToolPreference();
            SetToolsPreferenceStatus(string.Empty);
            publish?.Invoke();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.tools", ex);
            setValue(previous);
            try { PersistToolPreference(); }
            catch (Exception rollbackEx) { AppDiagnostics.LogError("settings.tools-rollback", rollbackEx); }
            _suppressToolsPreferenceChange = true;
            try { restoreUi(previous); }
            finally { _suppressToolsPreferenceChange = false; }
            SetToolsPreferenceStatus(string.Format(LocalizationService.Translate("Could not save {0}. The previous setting was restored."),
                LocalizationService.Translate(label)));
            ToastWindow.ShowError(LocalizationService.Translate("Tool settings were not saved"), ex.Message);
        }
    }

    private void PersistToolPreference()
    {
        string? error = null;
        void OnSaveFailed(string message) => error = message;
        _settingsService.SaveFailed += OnSaveFailed;
        try
        {
            _settingsService.Save();
            _settingsService.FlushPendingWrites();
            if (error is not null) throw new System.IO.IOException(error);
        }
        finally { _settingsService.SaveFailed -= OnSaveFailed; }
    }

    private void SetToolsPreferenceStatus(string message)
    {
        if (ToolsPreferenceStatusText is null) return;
        ToolsPreferenceStatusText.Text = message;
        ToolsPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? ToolKeyForElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, OcrToolPanel)) return "ocr";
            if (ReferenceEquals(element, PickerToolPanel)) return "picker";
            if (ReferenceEquals(element, QrToolPanel)) return "qr";
            if (ReferenceEquals(element, RulerToolPanel)) return "ruler";
            if (ReferenceEquals(element, ZoomToolPanel)) return "zoom";
            if (ReferenceEquals(element, PinToolPanel)) return "pin";
            element = LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static string ToolTitle(string key) => key switch
    {
        "picker" => "Color Picker",
        "qr" => "QR & Barcodes",
        "ruler" => "Ruler",
        "zoom" => "Zoom",
        "pin" => "Pin to screen",
        _ => "OCR"
    };
}
