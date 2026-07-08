using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CyberSnap.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private bool _ocrTabLoaded;

    private readonly List<ComboBoxItem> _ocrLanguageItems = new();
    private readonly List<ComboBoxItem> _translateFromItems = new();
    private readonly List<ComboBoxItem> _translateToItems = new();

    private void LoadOcrTab()
    {
        if (_ocrTabLoaded) return;
        _ocrTabLoaded = true;

        _suppressOcrPreferenceChange = true;
        try
        {
            LoadOcrLanguageOptions();
            LoadTranslateLanguageCombos();
            OcrAutoCopyCheck.IsChecked = _settingsService.Settings.OcrAutoCopyToClipboard;
            OcrPinByDefaultCheck.IsChecked = _settingsService.Settings.OcrResultWindowPinnedByDefault;
            GoogleApiKeyBox.Password = _settingsService.Settings.GoogleTranslateApiKey ?? "";
        }
        finally
        {
            _suppressOcrPreferenceChange = false;
        }

        UpdateTranslationModelUi();
    }

    private void LoadOcrLanguageOptions()
    {
        _ocrLanguageItems.Clear();
        OcrLanguageCombo.Items.Clear();

        // Auto at top â€” uses Windows system language
        var autoHelp = LocalizationService.Translate(
            "Automatically detect the text language from the image. Uses the Windows display language when available, falls back to English if not supported.");
        var autoItem = CreateOcrLanguageItem(
            LocalizationService.Translate("Auto (system language)"),
            "auto",
            LocalizationService.Translate("Auto OCR language"),
            autoHelp);
        _ocrLanguageItems.Add(autoItem);
        OcrLanguageCombo.Items.Add(autoItem);

        // Show all installed Windows OCR languages
        var languages = OcrService.GetAvailableRecognizerLanguages();
        foreach (var tag in languages)
        {
            try
            {
                var lang = new Windows.Globalization.Language(tag);
                var label = $"{lang.DisplayName} ({tag})";
                var langHelp = LocalizationService.Translate(
                    $"Always recognize text as {lang.DisplayName}. Choose this when you know the language of the text in advance for better accuracy.");
                var item = CreateOcrLanguageItem(
                    label,
                    tag,
                    $"{label} OCR language",
                    langHelp);
                _ocrLanguageItems.Add(item);
                OcrLanguageCombo.Items.Add(item);
            }
            catch
            {
                var langHelp = LocalizationService.Translate(
                    $"Always recognize text as {tag}. Choose this when you know the language of the text in advance for better accuracy.");
                var item = CreateOcrLanguageItem(
                    tag,
                    tag,
                    $"{tag} OCR language",
                    langHelp);
                _ocrLanguageItems.Add(item);
                OcrLanguageCombo.Items.Add(item);
            }
        }

        var targetTag = _settingsService.Settings.OcrLanguageTag ?? "auto";
        var selectedItem = OcrLanguageCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, targetTag, StringComparison.OrdinalIgnoreCase))
            ?? OcrLanguageCombo.Items.OfType<ComboBoxItem>().First();

        OcrLanguageCombo.SelectedItem = selectedItem;
        var langCount = languages.Count;
        var langLabel = langCount == 1
            ? LocalizationService.Translate("{0} language available from Windows")
            : LocalizationService.Translate("{0} languages available from Windows");
        OcrLanguageStatusText.Text = string.Format(langLabel, langCount);
    }

    private void OcrLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OcrLanguageCombo.SelectedItem is not ComboBoxItem item) return;
        if (!IsLoaded || _suppressOcrPreferenceChange) return;

        var previous = _settingsService.Settings.OcrLanguageTag;
        var code = item.Tag as string ?? "auto";
        UpdateOcrPreference(
            "settings.ocr-language",
            "OCR language",
            previous,
            code,
            value => _settingsService.Settings.OcrLanguageTag = value,
            value => SelectComboByTag(OcrLanguageCombo, value),
            SetOcrPreferenceStatus);
    }

    private void OcrAutoCopyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressOcrPreferenceChange) return;

        var previous = _settingsService.Settings.OcrAutoCopyToClipboard;
        var selected = OcrAutoCopyCheck.IsChecked == true;
        UpdateOcrPreference(
            "settings.ocr-auto-copy",
            "OCR auto-copy",
            previous,
            selected,
            value => {
                _settingsService.Settings.OcrAutoCopyToClipboard = value;
                SettingsService.SetOcrAutoCopyToClipboard(value);
            },
            value => OcrAutoCopyCheck.IsChecked = value,
            SetOcrPreferenceStatus);
    }

    private void OcrPinByDefaultCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressOcrPreferenceChange) return;

        var previous = _settingsService.Settings.OcrResultWindowPinnedByDefault;
        var selected = OcrPinByDefaultCheck.IsChecked == true;
        UpdateOcrPreference(
            "settings.ocr-pin-by-default",
            "OCR result window pin",
            previous,
            selected,
            value => _settingsService.Settings.OcrResultWindowPinnedByDefault = value,
            value => OcrPinByDefaultCheck.IsChecked = value,
            SetOcrPreferenceStatus);
    }

    private static string GetLanguageLabel(string languageTag)
    {
        try
        {
            var lang = new Windows.Globalization.Language(languageTag);
            return $"{lang.DisplayName} ({languageTag})";
        }
        catch
        {
            return languageTag;
        }
    }

    private void LoadTranslateLanguageCombos()
    {
        _translateFromItems.Clear();
        _translateToItems.Clear();
        TranslateFromCombo.Items.Clear();
        TranslateToCombo.Items.Clear();

        foreach (var (code, name) in TranslationService.SupportedLanguages)
        {
            var fromItem = CreateTranslationLanguageItem(
                name,
                code,
                $"{name} source language",
                $"Use {name} as the default translation source.");
            _translateFromItems.Add(fromItem);
            TranslateFromCombo.Items.Add(fromItem);

            var toName = name;
            var toItem = CreateTranslationLanguageItem(
                toName,
                code,
                $"{toName} target language",
                $"Use {toName} as the default translation target.");
            _translateToItems.Add(toItem);
            TranslateToCombo.Items.Add(toItem);
        }

        SelectComboByTag(TranslateFromCombo, _settingsService.Settings.OcrDefaultTranslateFrom);
        SelectComboByTag(TranslateToCombo, _settingsService.Settings.OcrDefaultTranslateTo);
    }

    private static ComboBoxItem CreateOcrLanguageItem(string text, string tag, string automationName, string helpText)
    {
        var item = new ComboBoxItem { Content = text, Tag = tag, ToolTip = helpText };
        AutomationProperties.SetName(item, automationName);
        AutomationProperties.SetHelpText(item, helpText);
        return item;
    }

    private static ComboBoxItem CreateTranslationLanguageItem(string text, string tag, string automationName, string helpText)
    {
        var item = new ComboBoxItem { Content = text, Tag = tag, ToolTip = helpText };
        AutomationProperties.SetName(item, automationName);
        AutomationProperties.SetHelpText(item, helpText);
        return item;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (item != null) combo.SelectedItem = item;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void TranslateFromCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TranslateFromCombo.SelectedItem is not ComboBoxItem item) return;
        if (!IsLoaded || _suppressOcrPreferenceChange) return;

        var previous = _settingsService.Settings.OcrDefaultTranslateFrom;
        var selected = TranslationService.ResolveSourceLanguage(item.Tag as string);
        UpdateOcrPreference(
            "settings.translation-source-language",
            "Source language",
            previous,
            selected,
            value => _settingsService.Settings.OcrDefaultTranslateFrom = value,
            value => SelectComboByTag(TranslateFromCombo, value),
            SetTranslationPreferenceStatus,
            _ => UpdateTranslationModelUi());
    }

    private void TranslateToCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TranslateToCombo.SelectedItem is not ComboBoxItem item) return;
        if (!IsLoaded || _suppressOcrPreferenceChange) return;

        var previous = _settingsService.Settings.OcrDefaultTranslateTo;
        var selected = item.Tag as string ?? "auto";
        UpdateOcrPreference(
            "settings.translation-target-language",
            "Target language",
            previous,
            selected,
            value => _settingsService.Settings.OcrDefaultTranslateTo = value,
            value => SelectComboByTag(TranslateToCombo, value),
            SetTranslationPreferenceStatus);
    }

    private void UpdateTranslationModelUi()
    {
        var activeModel = _settingsService.Settings.TranslationModel;
        GoogleApiKeyBox.Opacity = activeModel == (int)TranslationModel.Google ? 1.0 : 0.6;
        SelectTranslationEngineUi(activeModel);
    }

    private void SelectTranslationEngineUi(int modelValue)
    {
        var accentBrush = (System.Windows.Media.Brush)FindResource("ThemeAccentBrush");
        var activeBg = Theme.Brush(Theme.AccentSubtle);
        var transparentBrush = System.Windows.Media.Brushes.Transparent;

        var myMemoryActive = modelValue == (int)TranslationModel.MyMemory;
        var googleActive = modelValue == (int)TranslationModel.Google;

        // MyMemory row
        EngineRow_MyMemory.BorderBrush = myMemoryActive ? accentBrush : transparentBrush;
        EngineRow_MyMemory.Background = myMemoryActive ? activeBg : transparentBrush;

        // Google row
        EngineRow_Google.BorderBrush = googleActive ? accentBrush : transparentBrush;
        EngineRow_Google.Background = googleActive ? activeBg : transparentBrush;

        // API Key row follows Google selection
        ApiKeyRow.BorderBrush = googleActive ? accentBrush : transparentBrush;
        ApiKeyRow.Background = googleActive ? activeBg : transparentBrush;

        // DEFAULT badge: only visible when MyMemory is the active engine
        MyMemoryDefaultBadge.Visibility = myMemoryActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EngineRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
            return;

        if (!int.TryParse(tag, out var modelValue) || !Enum.IsDefined(typeof(TranslationModel), modelValue))
            return;

        var previous = _settingsService.Settings.TranslationModel;
        if (previous == modelValue)
            return;

        UpdateOcrPreference(
            "settings.translation-engine",
            "Translation engine",
            previous,
            modelValue,
            value => _settingsService.Settings.TranslationModel = value,
            value => _settingsService.Settings.TranslationModel = value,
            SetTranslationPreferenceStatus,
            _ => UpdateTranslationModelUi());
    }

    private void GoogleApiKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressOcrPreferenceChange) return;
        var previous = _settingsService.Settings.GoogleTranslateApiKey;
        var key = GoogleApiKeyBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(key) ? null : key;
        UpdateOcrPreference(
            "settings.google-translate-api-key",
            "Google Translate API key",
            previous,
            selected,
            value => _settingsService.Settings.GoogleTranslateApiKey = value,
            value => GoogleApiKeyBox.Password = value ?? "",
            SetTranslationPreferenceStatus,
            value =>
            {
                TranslationService.SetGoogleApiKey(value);
                UpdateTranslationModelUi();
            });
    }

    private void OpenGoogleApiConsoleBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://console.cloud.google.com/apis/library/translate.googleapis.com",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void UpdateOcrPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action<string> setStatus,
        Action<T>? applyRuntime = null)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            setStatus(string.Empty);
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

            _suppressOcrPreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressOcrPreferenceChange = false;
            }

            applyRuntime?.Invoke(previous);
            setStatus($"{label} change was not saved. Previous setting restored.");
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous OCR setting was restored. Check Config -> OCR and try again.\n{ex.Message}");
        }
    }

    private void SetOcrPreferenceStatus(string message)
    {
        OcrPreferenceStatusText.Text = message;
        OcrPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetTranslationPreferenceStatus(string message)
    {
        TranslationPreferenceStatusText.Text = message;
        TranslationPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OcrCombo_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.IsDropDownOpen = true;
        Dispatcher.BeginInvoke(new Action(() => FilterSettingsComboItems(combo)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OcrCombo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            if (sender is ComboBox combo)
                Dispatcher.BeginInvoke(new Action(() => FilterSettingsComboItems(combo)),
                    System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void FilterSettingsComboItems(ComboBox combo)
    {
        var editText = combo.Text?.Trim() ?? "";

        List<ComboBoxItem>? allItems = null;
        if (combo == OcrLanguageCombo) allItems = _ocrLanguageItems;
        else if (combo == TranslateFromCombo) allItems = _translateFromItems;
        else if (combo == TranslateToCombo) allItems = _translateToItems;
        if (allItems == null) return;

        combo.Items.Clear();

        if (string.IsNullOrEmpty(editText))
        {
            foreach (var item in allItems)
                combo.Items.Add(item);
        }
        else
        {
            var lower = editText.ToLowerInvariant();
            foreach (var item in allItems)
            {
                var content = (item.Content as string ?? "").ToLowerInvariant();
                var tag = (item.Tag as string ?? "").ToLowerInvariant();
                if (content.Contains(lower) || tag.Contains(lower))
                    combo.Items.Add(item);
            }
        }

        combo.IsDropDownOpen = true;
    }

    private void OnOcrAutoCopyToClipboardChanged(bool value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnOcrAutoCopyToClipboardChanged(value)));
            return;
        }

        _suppressOcrPreferenceChange = true;
        try
        {
            _settingsService.Settings.OcrAutoCopyToClipboard = value;
            OcrAutoCopyCheck.IsChecked = value;
        }
        finally
        {
            _suppressOcrPreferenceChange = false;
        }
    }
}
