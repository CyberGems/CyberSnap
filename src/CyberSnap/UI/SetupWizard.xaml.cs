using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI;

public partial class SetupWizard : Window
{
    private readonly SettingsService _settingsService;
    private int _page = 1;
    private const int TotalPages = 3;
    private readonly Grid[] _pages;
    private readonly Border[] _stepDots;
    private readonly TextBlock[] _stepNums;
    private readonly TextBlock[] _stepLabels;
    private readonly TextBlock[] _stepSubs;
    private bool _suppressLanguageChange;
    private bool _suppressAfterCaptureChange;
    private readonly Dictionary<string, string> _languageItemSources = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string id, string label, char icon)[] CaptureHotkeys =
    {
        ("captureRect",    "Screenshot",        '\uE257'),
        ("center",         "Center capture",    '\uE257'),
        ("ocr",            "Text capture",      '\uE53C'),
        ("picker",         "Color picker",      '\uE13E'),
        ("_record",        "Record",             ToolGlyphs.RecordGlyph),
    };

    public SetupWizard(SettingsService settingsService)
    {
        _settingsService = settingsService;
        Theme.Refresh();
        InitializeComponent();
        UiScale.Set(settingsService.Settings.UiScale);
        UiScale.ApplyToWindow(this, WizardShell, scaleWindowBounds: true);
        ApplyTheme();

        _pages = new[] { Page1, Page2, Page3 };
        _stepDots = new[] { StepDot1, StepDot2, StepDot3 };
        _stepNums = new[] { StepNum1, StepNum2, StepNum3 };
        _stepLabels = new[] { StepLabel1, StepLabel2, StepLabel3 };
        _stepSubs = new[] { StepSub1, StepSub2, StepSub3 };

        BuildHotkeyRows();
        LoadDefaults();
        UpdateSteps(_page);
        PopulateLanguages();
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        RefreshLanguageComboDisplay();
    }

    private static string GetLanguageLabel(LocalizationLanguage language) =>
        string.Equals(language.EnglishName, language.NativeName, StringComparison.OrdinalIgnoreCase)
            ? language.EnglishName
            : $"{language.EnglishName} - {language.NativeName}";

    private void PopulateLanguages()
    {
        _suppressLanguageChange = true;
        try
        {
            WizLanguageCombo.Items.Clear();
            _languageItemSources.Clear();
            var current = LocalizationService.NormalizeLanguageSetting(_settingsService.Settings.InterfaceLanguage);

            var entries = new List<(string Code, string Label)>
            {
                (LocalizationService.AutoLanguageCode, "Auto (system language)"),
            };

            foreach (var lang in LocalizationService.Languages)
            {
                if (!LocalizationService.HasInterfaceTranslations(lang.Code))
                    continue;

                entries.Add((lang.Code, GetLanguageLabel(lang)));
            }

            var autoEntry = entries.First(e =>
                string.Equals(e.Code, LocalizationService.AutoLanguageCode, StringComparison.OrdinalIgnoreCase));
            var sortedLanguages = entries
                .Where(e => !string.Equals(e.Code, LocalizationService.AutoLanguageCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase);

            ComboBoxItem? toSelect = null;
            foreach (var (code, label) in new[] { autoEntry }.Concat(sortedLanguages))
            {
                _languageItemSources[code] = label;
                var item = new ComboBoxItem { Content = label, Tag = code };
                LocalizationService.SetSourceContent(item, label);
                WizLanguageCombo.Items.Add(item);
                if (toSelect is null && string.Equals(code, current, StringComparison.OrdinalIgnoreCase))
                    toSelect = item;
            }

            WizLanguageCombo.SelectedItem = toSelect
                ?? WizLanguageCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    string.Equals(item.Tag as string, LocalizationService.AutoLanguageCode, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressLanguageChange = false;
        }
    }

    private void RefreshLanguageComboDisplay()
    {
        foreach (var item in WizLanguageCombo.Items.OfType<ComboBoxItem>())
        {
            var code = item.Tag?.ToString();
            if (code is null || !_languageItemSources.TryGetValue(code, out var label))
                continue;

            item.Content = LocalizationService.Translate(label);
        }

        var selected = WizLanguageCombo.SelectedItem;
        if (selected is null)
            return;

        _suppressLanguageChange = true;
        try
        {
            WizLanguageCombo.SelectedItem = null;
            WizLanguageCombo.SelectedItem = selected;
        }
        finally
        {
            _suppressLanguageChange = false;
        }
    }

    private void SelectLanguage(string languageCode)
    {
        var normalized = LocalizationService.NormalizeLanguageSetting(languageCode);
        foreach (var item in WizLanguageCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                WizLanguageCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChange) return;
        if (WizLanguageCombo.SelectedItem is not ComboBoxItem item) return;

        var code = item.Tag as string ?? LocalizationService.DefaultLanguageCode;
        var normalized = LocalizationService.NormalizeLanguageSetting(code);
        var previous = _settingsService.Settings.InterfaceLanguage;
        if (string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _settingsService.Settings.InterfaceLanguage = normalized;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _settingsService.Settings.InterfaceLanguage = previous;
            AppDiagnostics.LogError("setup.language", ex);
            _suppressLanguageChange = true;
            SelectLanguage(previous);
            _suppressLanguageChange = false;
            ToastWindow.ShowError(
                "Language change failed",
                $"The previous language was restored.\n{ex.Message}");
            return;
        }

        LocalizationService.ApplyCurrentCulture(normalized);
        LocalizationService.ApplyTo(this, normalized);
        RefreshAfterCaptureSummary(GetAfterCaptureViewPreferenceFromControls());
        UpdateSaveDirectoryState();
        UpdateNavButtons();
        RefreshLanguageComboDisplay();
    }

    private void UpdateNavButtons()
    {
        NextBtn.Content = LocalizationService.Translate(_page == TotalPages ? "Get Started" : "Next");
    }

    private void UpdateSteps(int page)
    {
        Theme.Refresh();
        var accent = Theme.Brush(Theme.Accent);
        var onAccent = Theme.Brush(Theme.IsDark
            ? Theme.BgPrimary
            : System.Windows.Media.Color.FromRgb(255, 255, 255));
        var upcomingBorder = Theme.Brush(Theme.Border);
        var textPrimary = Theme.Brush(Theme.TextPrimary);
        var textSecondary = Theme.Brush(Theme.TextSecondary);
        var textMuted = Theme.Brush(Theme.TextMuted);
        var transparent = System.Windows.Media.Brushes.Transparent;

        for (int i = 0; i < _stepDots.Length; i++)
        {
            int step = i + 1;
            var dot = _stepDots[i];
            var num = _stepNums[i];
            var label = _stepLabels[i];
            var sub = _stepSubs[i];

            if (step < page)
            {
                dot.Background = accent;
                dot.BorderBrush = accent;
                dot.Opacity = 0.85;
                dot.Effect = null;
                num.Text = "\u2713";
                num.Foreground = onAccent;
                num.FontWeight = FontWeights.Bold;
                label.Foreground = textSecondary;
                label.FontWeight = FontWeights.Normal;
                sub.Foreground = textMuted;
            }
            else if (step == page)
            {
                dot.Background = accent;
                dot.BorderBrush = accent;
                dot.Opacity = 1;
                dot.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Theme.Accent,
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = Theme.IsDark ? 0.9 : 0.45,
                };
                num.Text = step.ToString();
                num.Foreground = onAccent;
                num.FontWeight = FontWeights.SemiBold;
                label.Foreground = textPrimary;
                label.FontWeight = FontWeights.SemiBold;
                sub.Foreground = textSecondary;
            }
            else
            {
                dot.Background = transparent;
                dot.BorderBrush = upcomingBorder;
                dot.Opacity = 1;
                dot.Effect = null;
                num.Text = step.ToString();
                num.Foreground = textMuted;
                num.FontWeight = FontWeights.SemiBold;
                label.Foreground = textMuted;
                label.FontWeight = FontWeights.Normal;
                sub.Foreground = textMuted;
            }
        }
    }

    private void BuildHotkeyRows()
    {
        var segoe = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName);
        var iconColor = Theme.IsDark
            ? System.Drawing.Color.FromArgb(160, 255, 255, 255)
            : System.Drawing.Color.FromArgb(170, 0, 0, 0);
        var s = _settingsService.Settings;

        foreach (var (id, label, icon) in CaptureHotkeys)
        {
            var row = new Border();
            row.SetResourceReference(StyleProperty, "WizRow");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (icon != '\0')
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = ToolIcons.RenderToolIconWpf(id, icon, iconColor, 20),
                    Width = 18, Height = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
                left.Children.Add(img);
            }
            left.Children.Add(new TextBlock
            {
                Text = label, FontSize = 13, FontFamily = segoe,
                Foreground = (System.Windows.Media.Brush)FindResource("WizFg"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var hkBox = new TextBox
            {
                MinWidth = 154,
            };
            hkBox.SetResourceReference(TextBox.StyleProperty, "HotkeyBox");
            var (mod, key) = s.GetToolHotkey(id);
            hkBox.Text = HotkeyFormatter.Format(mod, key);
            hkBox.Tag = id;
            WireHotkey(hkBox, id);
            Grid.SetColumn(hkBox, 1);
            grid.Children.Add(hkBox);

            row.Child = grid;
            HotkeyPanel.Children.Add(row);
        }
    }

    private void WireHotkey(TextBox box, string toolId)
    {
        bool recording = false;
        box.GotFocus += (_, _) => { recording = true; box.Text = LocalizationService.Translate("Press keys..."); };
        box.LostFocus += (_, _) =>
        {
            recording = false;
            var (m, k) = _settingsService.Settings.GetToolHotkey(toolId);
            box.Text = HotkeyFormatter.Format(m, k);
        };
        void HandleKey(Key rawKey)
        {
            if (!recording) return;
            var key = rawKey == Key.System ? Key.None : rawKey;
            if (key == Key.None) return;
            if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape)
                return;

            uint mod = HotkeyFormatter.GetActiveModifiers();
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            var previous = _settingsService.Settings.GetToolHotkey(toolId);
            try
            {
                _settingsService.Settings.SetToolHotkey(toolId, mod, vk);
                _settingsService.Save();
                box.Text = HotkeyFormatter.Format(mod, vk);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("setup.tool-hotkey", ex);
                _settingsService.Settings.SetToolHotkey(toolId, previous.mod, previous.key);
                try
                {
                    _settingsService.Save();
                }
                catch (Exception rollbackEx)
                {
                    AppDiagnostics.LogError("setup.tool-hotkey-rollback", rollbackEx);
                }

                box.Text = HotkeyFormatter.Format(previous.mod, previous.key);
                ShowSetupHotkeySaveFailed(ex);
            }
            finally
            {
                recording = false;
                Keyboard.ClearFocus();
            }
        }

        box.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            HandleKey(key);
        };
        // PrintScreen and some special keys only arrive on KeyUp
        box.PreviewKeyUp += (_, e) =>
        {
            if (!recording) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Snapshot or Key.Pause or Key.Cancel)
            {
                e.Handled = true;
                HandleKey(key);
            }
        };
    }

    private static void ShowSetupHotkeySaveFailed(Exception ex)
    {
        ToastWindow.ShowError(
            "Hotkey failed",
            $"The previous hotkey was restored. Try this setup step again, or change it later in Config -> Tools.\n{ex.Message}");
    }

    private void LoadDefaults()
    {
        var s = _settingsService.Settings;
        WizCrosshairCheck.IsChecked = s.ShowCrosshairGuides;
        WizCaptureMagnifierCheck.IsChecked = s.ShowCaptureMagnifier;
        WizEnableSoundsCheck.IsChecked = !s.MuteSounds;
        WizSaveToFileCheck.IsChecked = s.SaveToFile;
        WizCaptureWidgetCheck.IsChecked = s.ShowCaptureWidget;
        ApplyAfterCaptureViewPreference(AfterCapturePreferences.FromSettings(s));
        WizSaveDirText.Text = s.SaveDirectory;
        UpdateSaveDirectoryState();
    }

    private void WizSaveToFile_Changed(object sender, RoutedEventArgs e) => UpdateSaveDirectoryState();

    private void UpdateSaveDirectoryState()
    {
        // When "Save only" is selected, saving to file is mandatory — lock the toggle
        bool saveOnlyMode = WizAfterCaptureCombo.SelectedIndex == 2;
        WizSaveToFileCheck.IsEnabled = !saveOnlyMode;
        WizSaveToFileCheck.Opacity = saveOnlyMode ? 0.5 : 1.0;

        var saveEnabled = WizSaveToFileCheck.IsChecked == true;
        WizSaveDirRow.Opacity = saveEnabled ? 1 : 0.48;
        WizBrowseSaveDirBtn.IsEnabled = saveEnabled;
        WizSaveDirText.Visibility = saveEnabled ? Visibility.Visible : Visibility.Collapsed;
        WizSaveDirDisabledHint.Visibility = saveEnabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyAfterCaptureViewPreference(AfterCaptureViewPreference preference)
    {
        _suppressAfterCaptureChange = true;
        try
        {
            WizAfterCaptureCombo.SelectedIndex = Math.Clamp(preference.WindowIndex, 0, WizAfterCaptureCombo.Items.Count - 1);
            WizAfterCaptureCopyCheck.IsChecked = preference.Copy;
            RefreshAfterCaptureSummary(preference);
        }
        finally
        {
            _suppressAfterCaptureChange = false;
        }
    }

    private AfterCaptureViewPreference GetAfterCaptureViewPreferenceFromControls() =>
        new(
            Math.Clamp(WizAfterCaptureCombo.SelectedIndex, 0, 2),
            WizAfterCaptureCopyCheck.IsChecked == true);

    private void RefreshAfterCaptureSummary(AfterCaptureViewPreference preference)
    {
        WizAfterCaptureSummaryText.Text = LocalizationService.Translate(
            AfterCapturePreferences.GetSummaryLocalizationKey(preference, wizardLabels: true));
    }

    private void WizAfterCapture_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAfterCaptureChange)
            return;

        // "Save only" (index 2) requires saving to file — auto-enable if off
        if (WizAfterCaptureCombo.SelectedIndex == 2 && WizSaveToFileCheck.IsChecked != true)
        {
            WizSaveToFileCheck.IsChecked = true;
        }

        UpdateSaveDirectoryState();
        RefreshAfterCaptureSummary(GetAfterCaptureViewPreferenceFromControls());
    }

    private void BrowseSaveDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = _settingsService.Settings.SaveDirectory,
            Description = "Choose where screenshots are saved",
            ShowNewFolderButton = true,
        };
        var owner = new WindowHandleWrapper(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
        {
            var previous = _settingsService.Settings.SaveDirectory;
            try
            {
                _settingsService.Settings.SaveDirectory = dlg.SelectedPath;
                _settingsService.Save();
                WizSaveDirText.Text = dlg.SelectedPath;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("setup.save-directory", ex);
                _settingsService.Settings.SaveDirectory = previous;
                try
                {
                    _settingsService.Save();
                }
                catch (Exception rollbackEx)
                {
                    AppDiagnostics.LogError("setup.save-directory-rollback", rollbackEx);
                }

                WizSaveDirText.Text = previous;
                ToastWindow.ShowError(
                    "Save directory failed",
                    $"The previous save directory was restored. Stay on this setup step and try again.\n{ex.Message}");
            }
        }
    }

    private void GoToPage(int page)
    {
        if (!SaveCurrentPage())
            return;

        _page = page;

        for (int i = 0; i < _pages.Length; i++)
        {
            if (i == page - 1)
            {
                _pages[i].Opacity = 0;
                _pages[i].Visibility = Visibility.Visible;
                _pages[i].BeginAnimation(OpacityProperty,
                    Motion.FromTo(0, 1, 220, Motion.SmoothOut));
            }
            else
                _pages[i].Visibility = Visibility.Collapsed;
        }
        UpdateSteps(page);
        BackBtn.Visibility = page > 1 ? Visibility.Visible : Visibility.Collapsed;
        SkipBtn.Visibility = page == TotalPages ? Visibility.Collapsed : Visibility.Visible;
        UpdateNavButtons();
    }

    private bool SaveCurrentPage()
    {
        try
        {
            var s = _settingsService.Settings;
            switch (_page)
            {
                case 1:
                    var previousCapture = (
                        s.ShowCrosshairGuides,
                        s.ShowCaptureMagnifier,
                        s.MuteSounds,
                        s.SaveToFile,
                        s.ShowCaptureWidget,
                        s.AfterCapture,
                        s.OpenEditorAfterCapture);
                    try
                    {
                        s.ShowCrosshairGuides = WizCrosshairCheck.IsChecked == true;
                        s.ShowCaptureMagnifier = WizCaptureMagnifierCheck.IsChecked == true;
                        s.MuteSounds = WizEnableSoundsCheck.IsChecked != true;
                        s.SaveToFile = WizSaveToFileCheck.IsChecked == true;
                        s.ShowCaptureWidget = WizCaptureWidgetCheck.IsChecked == true;
                        AfterCapturePreferences.ApplyToSettings(GetAfterCaptureViewPreferenceFromControls(), s);
                        _settingsService.Save();
                    }
                    catch
                    {
                        s.ShowCrosshairGuides = previousCapture.ShowCrosshairGuides;
                        s.ShowCaptureMagnifier = previousCapture.ShowCaptureMagnifier;
                        s.MuteSounds = previousCapture.MuteSounds;
                        s.SaveToFile = previousCapture.SaveToFile;
                        s.ShowCaptureWidget = previousCapture.ShowCaptureWidget;
                        s.AfterCapture = previousCapture.AfterCapture;
                        s.OpenEditorAfterCapture = previousCapture.OpenEditorAfterCapture;
                        LoadDefaults();
                        throw;
                    }
                    break;
                case 2:
                    _settingsService.Save();
                    break;
                case 3:
                    var previousCompleted = s.HasCompletedSetup;
                    try
                    {
                        s.HasCompletedSetup = true;
                        _settingsService.Save();
                    }
                    catch
                    {
                        s.HasCompletedSetup = previousCompleted;
                        throw;
                    }
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            ShowSetupSaveFailed("setup.save-page", ex);
            return false;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < TotalPages)
            GoToPage(_page + 1);
        else
        {
            if (!SaveCurrentPage())
                return;

            DialogResult = true;
            Close();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1) GoToPage(_page - 1);
    }

    private void OnSourceInit(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Native.Dwm.DisableBackdrop(hwnd);
        Native.Dwm.TrySetWindowCornerPreference(hwnd, Native.Dwm.DWMWCP_DONOTROUND);
    }

    private void ApplyTheme()
    {
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        var accent = Theme.Accent;
        var onAccent = Theme.IsDark
            ? Theme.BgPrimary
            : System.Windows.Media.Color.FromRgb(255, 255, 255);

        Resources["WizBg"] = Theme.Brush(Theme.BgPrimary);
        Resources["WizCardBg"] = Theme.Brush(Theme.BgCard);
        Resources["WizFg"] = Theme.Brush(Theme.TextPrimary);
        Resources["WizFgMuted"] = Theme.Brush(Theme.TextSecondary);
        Resources["WizBorder"] = Theme.Brush(Theme.WindowBorder);
        Resources["WizInputBg"] = Theme.Brush(Theme.BgSecondary);
        Resources["WizAccent"] = Theme.Brush(accent);
        Resources["WizBtnPrimaryBg"] = Theme.Brush(accent);
        Resources["WizBtnPrimaryHoverBg"] = Theme.Brush(Lighten(accent, Theme.IsDark ? 0.4 : 0.14));
        Resources["WizBtnPrimaryFg"] = Theme.Brush(onAccent);
        Resources["WizBtnSecondaryBg"] = Theme.Brush(Theme.AccentSubtle);
        Resources["WizBtnSecondaryFg"] = Theme.Brush(Theme.TextPrimary);
        Resources["WizShadowColor"] = Theme.IsDark
            ? System.Windows.Media.Color.FromArgb(128, 0, 0, 0)
            : System.Windows.Media.Color.FromArgb(72, 0, 0, 0);
        Resources["WizPanelShadow"] = Theme.Brush(Theme.IsDark
            ? System.Windows.Media.Color.FromArgb(64, 0, 0, 0)
            : System.Windows.Media.Color.FromArgb(36, 0, 0, 0));
        Resources["WizAccentGlowColor"] = accent;
        Resources["WizGlowColor"] = Theme.IsDark
            ? System.Windows.Media.Color.FromArgb(58, accent.R, accent.G, accent.B)
            : System.Windows.Media.Color.FromArgb(30, accent.R, accent.G, accent.B);
        Resources["WizSidebarTopColor"] = Theme.IsDark
            ? System.Windows.Media.Color.FromRgb(15, 30, 44)
            : System.Windows.Media.Color.FromRgb(226, 236, 245);
        Resources["WizSidebarBottomColor"] = Theme.IsDark
            ? System.Windows.Media.Color.FromRgb(11, 14, 22)
            : System.Windows.Media.Color.FromRgb(213, 219, 230);
        Foreground = Theme.Brush(Theme.TextPrimary);
        Icon = ThemedLogo.Square(32);
    }

    private static System.Windows.Media.Color Lighten(System.Windows.Media.Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * amount);
        return System.Windows.Media.Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrentPage() || !MarkSetupCompleted())
            return;

        Tag = "OpenSettings";
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrentPage() || !MarkSetupCompleted())
            return;

        DialogResult = false;
        Close();
    }

    private bool MarkSetupCompleted()
    {
        var previous = _settingsService.Settings.HasCompletedSetup;
        try
        {
            _settingsService.Settings.HasCompletedSetup = true;
            _settingsService.Save();
            return true;
        }
        catch (Exception ex)
        {
            _settingsService.Settings.HasCompletedSetup = previous;
            ShowSetupSaveFailed("setup.complete", ex);
            return false;
        }
    }

    private static void ShowSetupSaveFailed(string diagnosticKey, Exception ex)
    {
        AppDiagnostics.LogError(diagnosticKey, ex);
        var (title, message) = diagnosticKey switch
        {
            "setup.complete" => (
                "Setup completion failed",
                "Setup was not marked complete. The previous setup status was restored. Stay on this step and try again."),
            _ => (
                "Setup save failed",
                "Your setup choices were not saved. Previous saved settings were restored. Stay on this step and try again, or finish setup later from Settings."),
        };

        ToastWindow.ShowError(
            title,
            $"{message}\n{ex.Message}");
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private sealed class WindowHandleWrapper : System.Windows.Forms.IWin32Window
    {
        public WindowHandleWrapper(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}
