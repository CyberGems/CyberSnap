using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CyberSnap.UI;

public partial class OcrResultWindow : Window
{
    private const int SearchHighlightMinLength = 2;
    private const double WindowCascadeOffset = 28;

    private readonly SettingsService _settingsService;
    private readonly OcrResultWindowLifecycle _lifecycle = new();
    private CancellationTokenSource? _translateCts;

    // Store full item lists for filtering
    private readonly List<ComboBoxItem> _fromLanguageItems = new();
    private readonly List<ComboBoxItem> _toLanguageItems = new();
    private bool _suppressTranslationPreferenceChange;
    private bool _isPinned;
    private readonly List<int> _searchMatchStarts = new();
    private int _currentMatchIndex = -1;
    private bool _restoreOcrSelectionOnContextMenuClose;
    private ScrollViewer? _ocrTextScrollViewer;
    private readonly SolidColorBrush _searchHighlightBrush = new(System.Windows.Media.Color.FromArgb(86, 255, 224, 0));
    private readonly SolidColorBrush _activeSearchHighlightBrush = new(System.Windows.Media.Color.FromArgb(145, 255, 224, 0));

    public OcrResultWindow(string ocrText, SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        CyberSnapWindowChrome.Apply(this);
        UiScale.Set(settingsService.Settings.UiScale);
        UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);

        Theme.Refresh();
        ApplyTheme();
        LocalizationService.ApplyCurrentCulture(settingsService.Settings.InterfaceLanguage);

        OcrTextBox.Text = ocrText;
        OcrTextBox.TextChanged += OcrTextBox_TextChanged;
        UpdateCharCount();

        SetupOcrContextMenu();

        OcrTextBoxBorder.PreviewMouseRightButtonDown += OcrTextBoxBorder_PreviewMouseRightButtonDown;
        OcrTextBox.SizeChanged += (_, _) => RedrawSearchHighlights();
        OcrSearchHighlightCanvas.SizeChanged += (_, _) => RedrawSearchHighlights();

        // Use a composite font family so CJK / Arabic / Cyrillic glyphs render correctly
        var fontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI, Malgun Gothic, Yu Gothic UI, Arial Unicode MS, Segoe UI Symbol");
        OcrTextBox.FontFamily = fontFamily;
        TranslatedTextBox.FontFamily = fontFamily;

        PopulateLanguageCombos();
        SelectTranslationModelCombo(settingsService.Settings.TranslationModel);
        SetTranslationPanelExpanded(settingsService.Settings.OcrTranslationPanelExpanded, animate: false);
        SetPinned(settingsService.Settings.OcrResultWindowPinnedByDefault);
        LocalizationService.ApplyTo(this, settingsService.Settings.InterfaceLanguage);
        var lang = settingsService.Settings.InterfaceLanguage;
        WindowTitles.ApplyTaskbar(this, WindowTitles.Ocr, lang);
        OcrTitleBar.Title = LocalizationService.Translate("Text extraction (OCR)");
        OcrTitleBar.MinimizeBtn.Visibility = Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            OffsetFromOpenOcrWindows();
            ApplyMicaBackdrop();
            HookOcrTextScrollViewer();
            RedrawSearchHighlights();
            OcrTextBox.Focus();
            OcrTextBox.CaretIndex = OcrTextBox.Text.Length;
        };

        TranslationService.SetGoogleApiKey(settingsService.Settings.GoogleTranslateApiKey);
    }

    private void CloseWindow()
    {
        if (!_lifecycle.TryBeginClose())
            return;

        _translateCts?.Cancel();
        StopTranslateTimer();
        Close();
    }

    private void OffsetFromOpenOcrWindows()
    {
        var previousOcrWindows = Application.Current.Windows
            .OfType<OcrResultWindow>()
            .Where(window => !ReferenceEquals(window, this) && window.IsVisible)
            .ToList();
        if (previousOcrWindows.Count == 0)
            return;

        var offset = WindowCascadeOffset * previousOcrWindows.Count;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Min(Left + offset, Math.Max(workArea.Left, workArea.Right - ActualWidth));
        Top = Math.Min(Top + offset, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
    }

    private void ApplyTheme()
    {
        RootBorder.Background = Theme.Brush(Theme.BgPrimary);
        RootBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
        RootBorder.BorderThickness = new Thickness(1);

        Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
        Resources["ThemeMutedBrush"] = Theme.Brush(Theme.TextMuted);
        Resources["ThemeCardBrush"] = Theme.Brush(Theme.BgCard);
        Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
        Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        Resources["ThemeWindowBorderBrush"] = Theme.Brush(Theme.WindowBorder);
        Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        Resources["TranslationTintBrush"] = Theme.Brush(Theme.AccentSubtle);
        Resources["TranslationTintBorderBrush"] = Theme.Brush(Theme.AccentHover);
        Icon = WindowIcons.Wpf(WindowIconKind.Ocr);
    }

    private void ApplyMicaBackdrop()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.Dwm.DisableBackdrop(hwnd);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("ocr-result.backdrop", ex.Message, ex);
        }
    }

    private void PopulateLanguageCombos()
    {
        _fromLanguageItems.Clear();
        _toLanguageItems.Clear();
        FromLanguageCombo.Items.Clear();
        ToLanguageCombo.Items.Clear();

        // Order: Auto-detect first, then English & Spanish, then rest alphabetically
        var ordered = TranslationService.SupportedLanguages
            .Select(lang => (lang.Code, Name: LocalizeLanguageName(lang.Code, lang.Name)))
            .ToList();

        // Extract and sort: auto, en, es first, then rest by name
        var auto = ordered.First(l => l.Code == "auto");
        var en = ordered.First(l => l.Code == "en");
        var es = ordered.First(l => l.Code == "es");
        var rest = ordered
            .Where(l => l.Code != "auto" && l.Code != "en" && l.Code != "es")
            .OrderBy(l => l.Name)
            .ToList();

        var sorted = new List<(string Code, string Name)> { auto, en, es };
        sorted.AddRange(rest);

        foreach (var (code, name) in sorted)
        {
            var fromItem = new ComboBoxItem { Content = name, Tag = code };
            _fromLanguageItems.Add(fromItem);
            FromLanguageCombo.Items.Add(fromItem);

            var toName = code == "auto"
                ? LocalizationService.Translate("Auto (interface/system language)")
                : name;
            var toItem = new ComboBoxItem { Content = toName, Tag = code };
            _toLanguageItems.Add(toItem);
            ToLanguageCombo.Items.Add(toItem);
        }

        var settings = _settingsService.Settings;
        SelectComboByTag(FromLanguageCombo, settings.OcrDefaultTranslateFrom);
        SelectComboByTag(ToLanguageCombo, settings.OcrDefaultTranslateTo);
    }

    /// <summary>Returns the localized name for a language code, falling back to the English name.</summary>
    private static string LocalizeLanguageName(string code, string englishName)
    {
        // Use the English name as the localization key
        return LocalizationService.Translate(englishName);
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (item != null) combo.SelectedItem = item;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void ToggleTranslationBtn_Click(object sender, RoutedEventArgs e)
    {
        var expand = TranslationPanel.Visibility != Visibility.Visible;
        SetTranslationPanelExpanded(expand, animate: true);
        PersistTranslationPanelExpanded(expand);
    }

    private void SetTranslationPanelExpanded(bool expand, bool animate)
    {
        TranslationPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        TranslatedSection.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;

        var targetAngle = expand ? 90.0 : 0.0;
        if (animate)
        {
            var spin = new DoubleAnimation(targetAngle, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            TranslationChevronRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            // Clear any running animation before setting the value directly.
            TranslationChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            TranslationChevronRotation.Angle = targetAngle;
        }
    }

    private void PersistTranslationPanelExpanded(bool expanded)
    {
        if (_settingsService.Settings.OcrTranslationPanelExpanded == expanded)
            return;

        try
        {
            _settingsService.Settings.OcrTranslationPanelExpanded = expanded;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("ocr-result.translation-panel-expanded", ex.Message, ex);
        }
    }

    private void UpdateCharCount()
    {
        var text = OcrTextBox.Text ?? "";
        CharCountText.Text = $"{text.Length} {LocalizationService.Translate("characters")}";
    }

    private void OcrTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
        RefreshSearchMatches(keepCurrentMatch: true);

        if (!IsLoaded)
            return;

        ResetTranslationForSourceEdit();
    }

    private void ResetTranslationForSourceEdit() => ResetTranslationForTranslationInputChange();

    private void ResetTranslationForTranslationOptionChange() => ResetTranslationForTranslationInputChange();

    private void ResetTranslationForTranslationInputChange()
    {
        if (_translateCts is not null)
        {
            _translateCts.Cancel();
            _translateCts = null;
        }

        StopTranslationConfigurationCheck();
        StopTranslationLoading(keepStatusVisible: false);
        TranslatedTextBox.Text = string.Empty;
        CopyTranslationBtn.Visibility = Visibility.Collapsed;
    }

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => CloseWindow();

    private async void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ToastWindow.Show(ToastSpec.Standard("Nothing to copy", "OCR text is empty.") with { SuppressSound = true });
            return;
        }

        try
        {
            ClipboardService.CopyTextToClipboard(text);
            SoundService.PlayTextSound();
            ToastWindow.Show(ToastSpec.Standard(LocalizationService.Translate("Text copied"), FormatCopyToastPreview(text)) with { SuppressSound = true });
            CopyBtn.IsHitTestVisible = false;
            await Task.Delay(120);
            CloseWindow();
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Copy failed",
                $"CyberSnap could not copy the OCR text. Keep the result window open and try again.\n{ex.Message}");
        }
    }

    private void CopyTranslationBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslatedTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ToastWindow.Show(ToastSpec.Standard("No translation to copy", "Translate text first.") with { SuppressSound = true });
            return;
        }

        try
        {
            ClipboardService.CopyTextToClipboard(text);
            SoundService.PlayTextSound();
            ToastWindow.Show(ToastSpec.Standard("Copied translation", FormatCopyToastPreview(text)) with { SuppressSound = true });
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Copy failed",
                $"CyberSnap could not copy the translated text. Keep the result window open and try again.\n{ex.Message}");
        }
    }

    private static string FormatCopyToastPreview(string text)
    {
        var preview = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return preview.Length > 80 ? preview[..80] + "..." : preview;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CloseWindow();
    }


    protected override void OnClosed(EventArgs e)
    {
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = null;
        StopTranslateTimer();
        if (_ocrTextScrollViewer != null)
            _ocrTextScrollViewer.ScrollChanged -= OcrTextScrollViewer_ScrollChanged;
        base.OnClosed(e);
    }

    private void FromLanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationPreferenceChange) return;
        if (FromLanguageCombo.SelectedItem is not ComboBoxItem item) return;

        var previous = _settingsService.Settings.OcrDefaultTranslateFrom;
        var selected = TranslationService.ResolveSourceLanguage(item.Tag as string);
        if (string.Equals(previous, selected, StringComparison.OrdinalIgnoreCase))
        {
            SetTranslationPreferenceStatus(string.Empty);
            return;
        }

        if (UpdateTranslationPreference(
            "ocr-result.translation-source-language",
            "Source language",
            previous,
            selected,
            value => _settingsService.Settings.OcrDefaultTranslateFrom = value,
            value => SelectComboByTag(FromLanguageCombo, value)))
        {
            ResetTranslationForTranslationOptionChange();
        }
    }

    private void ToLanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationPreferenceChange) return;
        if (ToLanguageCombo.SelectedItem is not ComboBoxItem item) return;

        var previous = _settingsService.Settings.OcrDefaultTranslateTo;
        var selected = item.Tag as string ?? "auto";
        if (string.Equals(previous, selected, StringComparison.OrdinalIgnoreCase))
        {
            SetTranslationPreferenceStatus(string.Empty);
            return;
        }

        if (UpdateTranslationPreference(
            "ocr-result.translation-target-language",
            "Target language",
            previous,
            selected,
            value => _settingsService.Settings.OcrDefaultTranslateTo = value,
            value => SelectComboByTag(ToLanguageCombo, value)))
        {
            ResetTranslationForTranslationOptionChange();
        }
    }

    private void ModelCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressTranslationPreferenceChange) return;

        var previous = _settingsService.Settings.TranslationModel;
        var selected = (int)GetSelectedModel();
        if (previous == selected)
        {
            SetTranslationPreferenceStatus(string.Empty);
            return;
        }

        if (UpdateTranslationPreference(
            "ocr-result.translation-model",
            "Translation model",
            previous,
            selected,
            value => _settingsService.Settings.TranslationModel = value,
            SelectTranslationModelCombo))
        {
            ResetTranslationForTranslationOptionChange();
        }
    }

    private bool UpdateTranslationPreference<T>(
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
            SetTranslationPreferenceStatus(string.Empty);
            return true;
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

            _suppressTranslationPreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressTranslationPreferenceChange = false;
            }

            SetTranslationPreferenceStatus($"{label} failed. Previous option restored.");
            ToastWindow.ShowError(
                $"{label} failed",
                $"The previous translation preference was restored. Keep the result window open and try again.\n{ex.Message}");
            return false;
        }
    }

    private void SetTranslationPreferenceStatus(string message)
    {
        TranslationPreferenceStatusText.Text = message;
        TranslationPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private TranslationModel GetSelectedModel()
    {
        if (ModelCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var raw) &&
            Enum.IsDefined(typeof(TranslationModel), raw))
        {
            return (TranslationModel)raw;
        }

        return TranslationModel.MyMemory;
    }

    private void SelectTranslationModelCombo(int rawValue)
    {
        var selected = ModelCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                item.Tag is string tag &&
                int.TryParse(tag, out var parsed) &&
                parsed == rawValue);

        if (selected is not null)
            ModelCombo.SelectedItem = selected;
        else if (ModelCombo.Items.Count > 0)
            ModelCombo.SelectedIndex = 0;
    }

    private void FilterCombo_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.IsDropDownOpen = true;
        Dispatcher.BeginInvoke(new Action(() => FilterComboItems(combo)), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FilterCombo_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            if (sender is ComboBox combo)
                Dispatcher.BeginInvoke(new Action(() => FilterComboItems(combo)), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void FilterComboItems(ComboBox combo)
    {
        var editText = combo.Text?.Trim() ?? "";
        var allItems = combo == FromLanguageCombo ? _fromLanguageItems : _toLanguageItems;

        var currentTag = GetFilteredComboSelectionTag(combo);
        var matchCount = 0;
        var wasSuppressingPreferenceChange = _suppressTranslationPreferenceChange;

        _suppressTranslationPreferenceChange = true;
        try
        {
            combo.Items.Clear();

            if (string.IsNullOrEmpty(editText))
            {
                foreach (var item in allItems)
                {
                    combo.Items.Add(item);
                    matchCount++;
                }
            }
            else
            {
                var lower = editText.ToLowerInvariant();
                foreach (var item in allItems)
                {
                    var content = (item.Content as string ?? "").ToLowerInvariant();
                    var tag = (item.Tag as string ?? "").ToLowerInvariant();
                    if (content.Contains(lower) || tag.Contains(lower))
                    {
                        combo.Items.Add(item);
                        matchCount++;
                    }
                }
            }

            RestoreFilteredComboSelection(combo, currentTag);
        }
        finally
        {
            _suppressTranslationPreferenceChange = wasSuppressingPreferenceChange;
        }

        if (matchCount == 0)
            SetTranslationPreferenceStatus("No languages match that filter.");
        else if (TranslationPreferenceStatusText.Text == "No languages match that filter.")
            SetTranslationPreferenceStatus(string.Empty);

        combo.IsDropDownOpen = true;
    }

    private static void RestoreFilteredComboSelection(ComboBox combo, string? selectedTag)
    {
        if (string.IsNullOrWhiteSpace(selectedTag))
            return;

        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, selectedTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private string? GetFilteredComboSelectionTag(ComboBox combo)
    {
        if ((combo.SelectedItem as ComboBoxItem)?.Tag is string selectedTag &&
            !string.IsNullOrWhiteSpace(selectedTag))
        {
            return selectedTag;
        }

        return combo == FromLanguageCombo
            ? _settingsService.Settings.OcrDefaultTranslateFrom
            : _settingsService.Settings.OcrDefaultTranslateTo;
    }

    private System.Windows.Threading.DispatcherTimer? _translateTimer;
    private DateTime _translateStartTime;

    private void StartTranslateTimer()
    {
        _translateStartTime = DateTime.Now;
        _translateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _translateTimer.Tick += (_, _) =>
        {
            var elapsed = (int)(DateTime.Now - _translateStartTime).TotalSeconds;
            UpdateTranslateStatusText(elapsed);
        };
        _translateTimer.Start();
        UpdateTranslateStatusText(0);
    }

    private void StopTranslateTimer()
    {
        _translateTimer?.Stop();
        _translateTimer = null;
    }

    private void StartTranslationConfigurationCheck()
    {
        TranslatedTextBox.Text = string.Empty;
        TranslateStatus.Visibility = Visibility.Visible;
        TranslateStatus.Text = "Checking translation setup...";
        CopyTranslationBtn.Visibility = Visibility.Collapsed;
        TranslateBtn.IsEnabled = false;
        TranslateBtn.Content = LocalizationService.Translate("Checking...");
    }

    private void StopTranslationConfigurationCheck()
    {
        TranslateBtn.IsEnabled = true;
        TranslateBtn.Content = LocalizationService.Translate("Translate");
    }

    private void StartTranslationLoading(TranslationModel model)
    {
        TranslatedTextBox.Text = "";
        TranslateStatus.Visibility = Visibility.Visible;
        TranslationLoadingOverlay.Visibility = Visibility.Visible;
        CopyTranslationBtn.Visibility = Visibility.Collapsed;
        TranslateBtn.IsEnabled = false;
        TranslateBtn.Content = LocalizationService.Translate("Translating...");
        FromLanguageCombo.IsEnabled = false;
        ToLanguageCombo.IsEnabled = false;
        ModelCombo.IsEnabled = false;
        TranslateProgressBar.IsIndeterminate = true;
        TranslateStatus.Text = GetTranslationStatusLabel(model, 0);
        LoadingTextShimmer.Start(TranslateStatus, Colors.White, opacity: 0.7);

    }

    private void StopTranslationLoading(bool keepStatusVisible)
    {
        StopTranslateTimer();
        TranslationLoadingOverlay.Visibility = Visibility.Collapsed;
        TranslateProgressBar.IsIndeterminate = false;
        TranslateBtn.IsEnabled = true;
        TranslateBtn.Content = LocalizationService.Translate("Translate");
        FromLanguageCombo.IsEnabled = true;
        ToLanguageCombo.IsEnabled = true;
        ModelCombo.IsEnabled = true;
        LoadingTextShimmer.Stop(TranslateStatus, Theme.Brush(Theme.TextPrimary), 0.25);
        if (!keepStatusVisible)
            TranslateStatus.Visibility = Visibility.Collapsed;
    }

    private void ShowTranslateError(string message)
    {
        StopTranslationLoading(keepStatusVisible: true);
        TranslateStatus.Text = $"Error: {message}";
    }

    private void UpdateTranslateStatusText(int elapsedSeconds)
    {
        var model = GetSelectedModel();
        TranslateStatus.Text = $"{GetTranslationStatusLabel(model, elapsedSeconds)} ({elapsedSeconds}s)";
    }

    private void SetTranslationIdleStatus(string message)
    {
        StopTranslateTimer();
        TranslationLoadingOverlay.Visibility = Visibility.Collapsed;
        TranslateProgressBar.IsIndeterminate = false;
        LoadingTextShimmer.Stop(TranslateStatus, Theme.Brush(Theme.TextPrimary), 0.25);
        TranslateStatus.Visibility = Visibility.Visible;
        TranslateStatus.Text = message;
    }

    private bool IsActiveTranslationRequest(CancellationTokenSource requestCts)
    {
        return ReferenceEquals(_translateCts, requestCts);
    }

    private static string GetTranslationStatusLabel(TranslationModel model, int elapsedSeconds)
    {
        if (elapsedSeconds <= 1)
            return model == TranslationModel.MyMemory ? "Sending to MyMemory..." : "Starting translation...";
        if (elapsedSeconds <= 3)
            return model == TranslationModel.MyMemory ? "Translating via web..." : "Sending text...";
        if (elapsedSeconds <= 6)
            return model == TranslationModel.MyMemory ? "Waiting for response..." : "Translating...";
        return model == TranslationModel.MyMemory ? "Finishing up..." : "Finishing translation...";
    }

    private async void TranslateBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetTranslationIdleStatus("No text to translate.");
            return;
        }

        var fromItem = FromLanguageCombo.SelectedItem as ComboBoxItem;
        var toItem = ToLanguageCombo.SelectedItem as ComboBoxItem;
        if (fromItem == null || toItem == null)
        {
            SetTranslationIdleStatus("Choose translation languages first.");
            return;
        }

        var fromCode = TranslationService.ResolveSourceLanguage(fromItem.Tag as string);
        var toCode = TranslationService.ResolveTargetLanguage(
            toItem.Tag as string,
            _settingsService.Settings.InterfaceLanguage);

        _translateCts?.Cancel();
        _translateCts = new CancellationTokenSource();
        var requestCts = _translateCts;
        var token = requestCts.Token;
        var model = GetSelectedModel();

        StartTranslationConfigurationCheck();

        try
        {
            var configurationError = await TranslationService.GetConfigurationErrorAsync(fromCode, model, token);
            if (_lifecycle.IsCloseRequested)
                return;
            if (!IsActiveTranslationRequest(requestCts))
                return;
            if (token.IsCancellationRequested)
            {
                StopTranslationConfigurationCheck();
                TranslateStatus.Visibility = Visibility.Collapsed;
                return;
            }

            if (!string.IsNullOrWhiteSpace(configurationError))
            {
                ShowTranslateError(configurationError);
                return;
            }

            StopTranslationConfigurationCheck();
            StartTranslationLoading(model);
            StartTranslateTimer();

            await TranslationService.EnsureReadyAsync(fromCode, model, token);
            if (_lifecycle.IsCloseRequested)
                return;
            if (!IsActiveTranslationRequest(requestCts))
                return;
            if (token.IsCancellationRequested)
            {
                StopTranslationLoading(keepStatusVisible: false);
                return;
            }

            var result = await TranslationService.TranslateAsync(text, fromCode, toCode, model, token);
            if (_lifecycle.IsCloseRequested)
                return;
            if (!IsActiveTranslationRequest(requestCts))
                return;
            if (token.IsCancellationRequested)
            {
                StopTranslationLoading(keepStatusVisible: false);
                return;
            }

            StopTranslationLoading(keepStatusVisible: false);
            TranslatedTextBox.Text = result;
            CopyTranslationBtn.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (_lifecycle.IsCloseRequested)
                return;

            if (IsActiveTranslationRequest(requestCts))
                StopTranslationLoading(keepStatusVisible: false);
        }
        catch (Exception ex)
        {
            if (_lifecycle.IsCloseRequested)
                return;
            if (!IsActiveTranslationRequest(requestCts))
                return;

            ShowTranslateError(ex.Message);
        }
        finally
        {
            if (IsActiveTranslationRequest(requestCts))
            {
                _translateCts = null;
            }

            requestCts.Dispose();
        }
    }

    private void SetupOcrContextMenu()
    {
        var menu = new ContextMenu();
        if (TryFindResource("OcrContextMenuStyle") is Style menuStyle)
            menu.Style = menuStyle;
        menu.Focusable = false;

        void AddItem(string header, string gesture, string iconId, Action action)
        {
            var item = new MenuItem
            {
                Header = LocalizationService.Translate(header),
                InputGestureText = gesture,
                Focusable = false,
                Icon = CreateMenuIcon(iconId)
            };
            if (TryFindResource("OcrMenuItemStyle") is Style itemStyle)
                item.Style = itemStyle;
            item.Click += (_, _) =>
            {
                _restoreOcrSelectionOnContextMenuClose = false;
                action();
            };
            menu.Items.Add(item);
        }

        AddItem("Undo", "Ctrl+Z", "undo", () => OcrTextBox.Undo());
        menu.Items.Add(new Separator { Style = (TryFindResource("OcrSeparatorStyle") as Style) });
        AddItem("Cut", "Ctrl+X", "cut", () => OcrTextBox.Cut());
        AddItem("Copy", "Ctrl+C", "copy", () => OcrTextBox.Copy());
        AddItem("Paste", "Ctrl+V", "paste", () => OcrTextBox.Paste());
        menu.Items.Add(new Separator { Style = (TryFindResource("OcrSeparatorStyle") as Style) });
        AddItem("Select All", "Ctrl+A", "select", () => OcrTextBox.SelectAll());
        AddItem("Delete", "Del", "trash", () => OcrTextBox.SelectedText = "");

        OcrTextBox.ContextMenu = menu;
    }

    private static System.Windows.Controls.Image CreateMenuIcon(string iconId)
    {
        var iconColor = Theme.IsDark
            ? System.Drawing.Color.FromArgb(225, 255, 255, 255)
            : System.Drawing.Color.FromArgb(225, 24, 24, 24);
        return new System.Windows.Controls.Image
        {
            Source = Helpers.FluentIcons.RenderWpf(iconId, iconColor, 16),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Opacity = 0.88
        };
    }

    private void TitleBar_PinRequested(object? sender, EventArgs e)
    {
        SetPinned(!_isPinned);
    }

    private void SetPinned(bool pinned)
    {
        _isPinned = pinned;
        _lifecycle.SetPinned(_isPinned);
        OcrTitleBar.IsPinActive = _isPinned;
        Topmost = pinned;
        _settingsService.Settings.OcrResultWindowPinnedByDefault = pinned;
        _settingsService.Save();
    }

    private int _savedOcrSelectionStart;
    private int _savedOcrSelectionLength;

    private void OcrTextBoxBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenOcrContextMenu();
    }

    private void OpenOcrContextMenu()
    {
        if (OcrTextBox.ContextMenu == null) return;

        _savedOcrSelectionStart = OcrTextBox.SelectionStart;
        _savedOcrSelectionLength = OcrTextBox.SelectionLength;
        _restoreOcrSelectionOnContextMenuClose = true;

        var pos = Mouse.GetPosition(OcrTextBox);
        OcrTextBox.ContextMenu.PlacementTarget = OcrTextBox;
        OcrTextBox.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
        OcrTextBox.ContextMenu.PlacementRectangle = new Rect(pos, new System.Windows.Size(0, 0));
        OcrTextBox.ContextMenu.Closed += OnOcrContextMenuClosed;
        OcrTextBox.ContextMenu.IsOpen = true;
    }

    private void OnOcrContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (OcrTextBox.ContextMenu != null)
            OcrTextBox.ContextMenu.Closed -= OnOcrContextMenuClosed;
        if (_restoreOcrSelectionOnContextMenuClose)
            OcrTextBox.Select(_savedOcrSelectionStart, _savedOcrSelectionLength);
        _restoreOcrSelectionOnContextMenuClose = false;
        OcrTextBox.Focus();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchTextBox.Text ?? "";
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(query) ? Visibility.Visible : Visibility.Collapsed;
        SearchClearBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
        RefreshSearchMatches(keepCurrentMatch: false);
    }

    private void SearchClearBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SearchTextBox.Clear();
        SearchTextBox.Focus();
    }

    private void SearchBoxShell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SearchClearBtn)
            return;

        SearchTextBox.Focus();
        e.Handled = true;
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            MoveToSearchMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            SearchTextBox.Text = "";
            OcrTextBox.Focus();
        }
    }

    private void PrevMatchBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        MoveToSearchMatch(-1);
    }

    private void NextMatchBtn_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        MoveToSearchMatch(1);
    }

    private void MoveToSearchMatch(int direction)
    {
        if (_searchMatchStarts.Count == 0)
            return;

        _currentMatchIndex = _currentMatchIndex < 0
            ? 0
            : (_currentMatchIndex + direction + _searchMatchStarts.Count) % _searchMatchStarts.Count;
        ScrollTextBoxToMatch(_currentMatchIndex);
        UpdateSearchStatus();
        RedrawSearchHighlights();
    }

    private void ScrollTextBoxToMatch(int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= _searchMatchStarts.Count)
            return;

        try
        {
            var matchStart = _searchMatchStarts[matchIndex];
            var lineIndex = OcrTextBox.GetLineIndexFromCharacterIndex(matchStart);
            OcrTextBox.ScrollToLine(Math.Max(0, lineIndex - 2));
            OcrTextBox.UpdateLayout();

            var rect = OcrTextBox.GetRectFromCharacterIndex(matchStart);
            if (rect.IsEmpty) return;
            var sv = FindVisualChild<ScrollViewer>(OcrTextBox);
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset + rect.Top - sv.ViewportHeight / 2 + rect.Height / 2);
            }
        }
        catch { }
    }

    private void RefreshSearchMatches(bool keepCurrentMatch)
    {
        var previousStart = keepCurrentMatch && _currentMatchIndex >= 0 && _currentMatchIndex < _searchMatchStarts.Count
            ? _searchMatchStarts[_currentMatchIndex]
            : -1;

        _searchMatchStarts.Clear();
        _currentMatchIndex = -1;

        var query = SearchTextBox.Text ?? "";
        if (query.Length < SearchHighlightMinLength)
        {
            MatchCountText.Text = "";
            UpdateSearchNavigationButtons();
            OcrSearchHighlightCanvas.Children.Clear();
            return;
        }

        var text = OcrTextBox.Text ?? "";
        var idx = 0;
        while (idx <= text.Length - query.Length)
        {
            var pos = text.IndexOf(query, idx, StringComparison.CurrentCultureIgnoreCase);
            if (pos < 0) break;
            _searchMatchStarts.Add(pos);
            idx = pos + 1;
        }

        if (_searchMatchStarts.Count > 0)
        {
            _currentMatchIndex = previousStart >= 0
                ? Math.Max(0, _searchMatchStarts.FindIndex(start => start >= previousStart))
                : 0;
            if (_currentMatchIndex >= _searchMatchStarts.Count)
                _currentMatchIndex = _searchMatchStarts.Count - 1;
            ScrollTextBoxToMatch(_currentMatchIndex);
        }

        UpdateSearchStatus();
        RedrawSearchHighlights();
    }

    private void UpdateSearchStatus()
    {
        MatchCountText.Text = _searchMatchStarts.Count == 0
            ? (SearchTextBox.Text.Length >= SearchHighlightMinLength ? "0/0" : "")
            : $"{_currentMatchIndex + 1}/{_searchMatchStarts.Count}";
        UpdateSearchNavigationButtons();
    }

    private void UpdateSearchNavigationButtons()
    {
        var visibility = _searchMatchStarts.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        PrevMatchBtn.Visibility = visibility;
        NextMatchBtn.Visibility = visibility;
    }

    private void HookOcrTextScrollViewer()
    {
        if (_ocrTextScrollViewer != null)
            return;

        _ocrTextScrollViewer = FindVisualChild<ScrollViewer>(OcrTextBox);
        if (_ocrTextScrollViewer != null)
            _ocrTextScrollViewer.ScrollChanged += OcrTextScrollViewer_ScrollChanged;
    }

    private void OcrTextScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(RedrawSearchHighlights, DispatcherPriority.Background);
    }

    private void RedrawSearchHighlights()
    {
        OcrSearchHighlightCanvas.Children.Clear();

        var queryLength = SearchTextBox.Text.Length;
        if (queryLength < SearchHighlightMinLength || _searchMatchStarts.Count == 0)
            return;

        OcrTextBox.UpdateLayout();
        for (var i = 0; i < _searchMatchStarts.Count; i++)
        {
            foreach (var rect in GetSearchHighlightRects(_searchMatchStarts[i], queryLength))
            {
                var highlight = new System.Windows.Shapes.Rectangle
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = i == _currentMatchIndex ? _activeSearchHighlightBrush : _searchHighlightBrush
                };
                Canvas.SetLeft(highlight, rect.Left);
                Canvas.SetTop(highlight, rect.Top);
                OcrSearchHighlightCanvas.Children.Add(highlight);
            }
        }
    }

    private IEnumerable<Rect> GetSearchHighlightRects(int start, int length)
    {
        var textLength = OcrTextBox.Text?.Length ?? 0;
        if (length <= 0 || start < 0 || start >= textLength)
            yield break;

        var end = Math.Min(start + length, textLength);
        var segmentStart = start;
        while (segmentStart < end)
        {
            Rect highlightRect;
            try
            {
                var startRect = OcrTextBox.GetRectFromCharacterIndex(segmentStart);
                var segmentEnd = Math.Min(end, GetLineEndCharacterIndex(segmentStart));
                var endRect = OcrTextBox.GetRectFromCharacterIndex(segmentEnd - 1, trailingEdge: true);
                if (startRect.IsEmpty || endRect.IsEmpty)
                    yield break;

                highlightRect = new Rect(
                    startRect.Left,
                    startRect.Top + 1,
                    Math.Max(2, endRect.Right - startRect.Left),
                    Math.Max(2, startRect.Height - 2));

                segmentStart = segmentEnd;
            }
            catch
            {
                yield break;
            }

            yield return highlightRect;
        }
    }

    private int GetLineEndCharacterIndex(int characterIndex)
    {
        var lineIndex = OcrTextBox.GetLineIndexFromCharacterIndex(characterIndex);
        var lineStart = OcrTextBox.GetCharacterIndexFromLineIndex(lineIndex);
        return Math.Max(characterIndex + 1, lineStart + OcrTextBox.GetLineLength(lineIndex));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
