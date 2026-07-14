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
    private const int TotalPages = 5;
    private readonly Grid[] _pages;
    private readonly Border[] _stepDots;
    private readonly TextBlock[] _stepNums;
    private readonly TextBlock[] _stepLabels;
    private readonly TextBlock[] _stepSubs;
    private bool _suppressLanguageChange;
    private bool _suppressAfterCaptureChange;
    private bool _suppressThemeChange;
    private readonly Dictionary<string, string> _languageItemSources = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Threading.DispatcherTimer? _blockedDetectTimer;
    private System.Windows.Threading.DispatcherTimer? _resetWarningTimer;
    private System.Windows.Controls.ToolTip _tooltip = null!;

    public SetupWizard(SettingsService settingsService)
    {
        _settingsService = settingsService;
        Theme.Refresh();
        InitializeComponent();
        UiScale.Set(settingsService.Settings.UiScale);
        UiScale.ApplyToWindow(this, WizardShell, scaleWindowBounds: true);
        ApplyTheme();

        _pages = new[] { Page0, Page1, Page2, Page3, Page4 };
        _stepDots = new[] { StepDot1, StepDot2, StepDot3, StepDot4, StepDot5 };
        _stepNums = new[] { StepNum1, StepNum2, StepNum3, StepNum4, StepNum5 };
        _stepLabels = new[] { StepLabel1, StepLabel2, StepLabel3, StepLabel4, StepLabel5 };
        _stepSubs = new[] { StepSub1, StepSub2, StepSub3, StepSub4, StepSub5 };

        InitializeMainHotkey();
        LoadDefaults();
        UpdateSteps(_page);
        PopulateLanguages();
        LocalizationService.ApplyTo(this, _settingsService.Settings.InterfaceLanguage);
        RefreshLanguageComboDisplay();

        // Show the first page explicitly (Page0 starts collapsed in XAML)
        Page0.Visibility = Visibility.Visible;
    }

    private static string GetLanguageLabel(LocalizationLanguage language) =>
        $"{language.NativeName} - {language.EnglishName}";

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

    private void InitializeMainHotkey()
    {
        _tooltip = new System.Windows.Controls.ToolTip();
        WizHotkeyTextBox.ToolTip = _tooltip;

        var (mod, key) = _settingsService.Settings.GetToolHotkey("rect");
        WizHotkeyTextBox.Text = HotkeyFormatter.Format(mod, key);

        // Wire up TextBox events
        WizHotkeyTextBox.GotFocus += WizHotkeyTextBox_GotFocus;
        WizHotkeyTextBox.LostFocus += WizHotkeyTextBox_LostFocus;
        WizHotkeyTextBox.PreviewKeyDown += WizHotkeyTextBox_PreviewKeyDown;
        WizHotkeyTextBox.PreviewKeyUp += WizHotkeyTextBox_PreviewKeyUp;
        WizHotkeyTextBox.PreviewTextInput += (s, e) => { e.Handled = true; };

        // Add footer note under card
        AddHotkeysFooterNote();
    }

    private void WizHotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        WizHotkeyTextBox.Text = LocalizationService.Translate("Press keys...");
        WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
        WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
        _tooltip.Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.");
        _tooltip.IsOpen = false;
        if (_resetWarningTimer != null)
        {
            _resetWarningTimer.Stop();
            _resetWarningTimer = null;
        }
        if (Application.Current is App app)
        {
            app.UnregisterAllHotkeys();
        }
    }

    private void WizHotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var (m, k) = _settingsService.Settings.GetToolHotkey("rect");
        WizHotkeyTextBox.Text = HotkeyFormatter.Format(m, k);
        WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
        WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
        _tooltip.Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.");
        _tooltip.IsOpen = false;
        if (_resetWarningTimer != null)
        {
            _resetWarningTimer.Stop();
            _resetWarningTimer = null;
        }
        if (_blockedDetectTimer != null)
        {
            _blockedDetectTimer.Stop();
            _blockedDetectTimer = null;
        }
        if (Application.Current is App app)
        {
            app.RegisterHotkeys(showReadyNotification: false);
        }
    }

    private void WizHotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = NormalizeHotkeyKey(e);
        
        if (IsModifierOnly(key))
        {
            if (_resetWarningTimer != null)
            {
                _resetWarningTimer.Stop();
                _resetWarningTimer = null;
                WizHotkeyTextBox.Text = LocalizationService.Translate("Press keys...");
                WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
                WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
                _tooltip.IsOpen = false;
            }

            if (_blockedDetectTimer == null)
            {
                _blockedDetectTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _blockedDetectTimer.Tick += (s, args) =>
                {
                    if (IsAnyNonModifierKeyPhysicallyDown())
                    {
                        _blockedDetectTimer.Stop();
                        WizHotkeyTextBox.Text = LocalizationService.Translate("Taken");
                        WizHotkeyTextBox.Foreground = System.Windows.Media.Brushes.Red;
                        WizHotkeyTextBox.FontWeight = FontWeights.Bold;
                        _tooltip.Content = LocalizationService.Translate("This hotkey is registered by another application.");
                        _tooltip.IsOpen = true;
                    }
                };
            }
            _blockedDetectTimer.Stop();
            _blockedDetectTimer.Start();
            return;
        }

        if (_blockedDetectTimer != null)
        {
            _blockedDetectTimer.Stop();
            _blockedDetectTimer = null;
        }
        if (_resetWarningTimer != null)
        {
            _resetWarningTimer.Stop();
            _resetWarningTimer = null;
        }
        WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
        WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
        _tooltip.IsOpen = false;

        uint mod = (uint)Keyboard.Modifiers;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (mod == 0 && vk == 0) return;
        if (mod == 0 && vk != Native.User32.VK_SNAPSHOT)
        {
            ShowModifierRequiredWarning();
            return;
        }

        var conflict = ToolListBuilder.FindHotkeyConflict(_settingsService.Settings, "rect", mod, vk);
        if (conflict is not null)
        {
            ShowInternalHotkeyConflictWarning(mod, vk, conflict.Label);
            return;
        }

        var (prevMod, prevKey) = _settingsService.Settings.GetToolHotkey("rect");

        try
        {
            _settingsService.Settings.SetToolHotkey("rect", mod, vk);
            _settingsService.Save();
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(mod, vk);

            var interceptors = mod == 0 && vk == Native.User32.VK_SNAPSHOT
                ? HotkeyConflictProbe.DetectPrintScreenInterceptors()
                : System.Array.Empty<string>();
            if (interceptors.Count > 0)
                ApplyPrintScreenFeedback(canRegister: true, interceptors);
            else
                Keyboard.ClearFocus();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("setup.rect-hotkey-save", ex);
            _settingsService.Settings.SetToolHotkey("rect", prevMod, prevKey);
            try { _settingsService.Save(); } catch { }
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(prevMod, prevKey);
            ToastWindow.ShowError(
                "Hotkey failed",
                $"The previous hotkey was restored after the failed save. Check your input and try again.\n{ex.Message}");
        }
    }

    private void WizHotkeyTextBox_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = NormalizeHotkeyKey(e);
        if (IsModifierOnly(key))
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_blockedDetectTimer != null)
                {
                    _blockedDetectTimer.Stop();
                    _blockedDetectTimer = null;
                }

                if (WizHotkeyTextBox.Text == LocalizationService.Translate("Taken"))
                {
                    if (_resetWarningTimer != null) _resetWarningTimer.Stop();
                    _resetWarningTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
                    _resetWarningTimer.Tick += (s, args) =>
                    {
                        _resetWarningTimer.Stop();
                        _resetWarningTimer = null;
                        if (WizHotkeyTextBox.IsFocused)
                        {
                            WizHotkeyTextBox.Text = LocalizationService.Translate("Press keys...");
                            WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
                            WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
                            _tooltip.Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.");
                            _tooltip.IsOpen = false;
                        }
                    };
                    _resetWarningTimer.Start();
                }
                else
                {
                    WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
                    WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
                    _tooltip.IsOpen = false;
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsAnyNonModifierKeyPhysicallyDown()
    {
        for (int vk = 0x41; vk <= 0x5A; vk++) // A-Z
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        for (int vk = 0x30; vk <= 0x39; vk++) // 0-9
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        for (int vk = 0x70; vk <= 0x7B; vk++) // F1-F12
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        return false;
    }

    private static Key NormalizeHotkeyKey(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed)
            key = e.ImeProcessedKey;
        if (key == Key.DeadCharProcessed)
            key = e.DeadCharProcessedKey;
        return key;
    }

    private static bool IsModifierOnly(Key key) =>
        key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static readonly System.Windows.Media.SolidColorBrush HotkeyWarningBrush =
        new(System.Windows.Media.Color.FromRgb(245, 158, 11));

    private void ShowModifierRequiredWarning()
    {
        WizHotkeyTextBox.Text = LocalizationService.Translate("Modifier required");
        WizHotkeyTextBox.Foreground = HotkeyWarningBrush;
        WizHotkeyTextBox.FontWeight = FontWeights.SemiBold;
        _tooltip.Content = LocalizationService.Translate("Global hotkeys require a modifier (Ctrl, Alt, or Shift).");
        _tooltip.IsOpen = true;
    }

    private void ShowInternalHotkeyConflictWarning(uint mod, uint vk, string conflictLabelKey)
    {
        WizHotkeyTextBox.Text = LocalizationService.Translate("Taken");
        WizHotkeyTextBox.Foreground = HotkeyWarningBrush;
        WizHotkeyTextBox.FontWeight = FontWeights.SemiBold;
        var conflictLabel = LocalizationService.Translate(conflictLabelKey);
        _tooltip.Content = string.Format(
            LocalizationService.Translate("\"{0}\" is already assigned to {1}. Enable \"Allow hotkey override\" to reassign."),
            HotkeyFormatter.Format(mod, vk),
            conflictLabel);
        _tooltip.IsOpen = true;
    }

    private void ApplyPrintScreenFeedback(bool canRegister, IReadOnlyList<string> interceptors)
    {
        if (!canRegister)
        {
            WizHotkeyTextBox.Text = LocalizationService.Translate("Taken");
            WizHotkeyTextBox.Foreground = System.Windows.Media.Brushes.Red;
            WizHotkeyTextBox.FontWeight = FontWeights.Bold;
            _tooltip.Content = LocalizationService.Translate("This hotkey is registered by another application.");
            _tooltip.IsOpen = true;
            return;
        }

        if (interceptors.Count > 0)
        {
            WizHotkeyTextBox.Foreground = HotkeyWarningBrush;
            WizHotkeyTextBox.FontWeight = FontWeights.SemiBold;
            _tooltip.Content = string.Format(
                LocalizationService.Translate("Print Screen assigned, but {0} may intercept it. Close it or change its shortcut."),
                string.Join(", ", interceptors));
            _tooltip.IsOpen = true;
            return;
        }

        WizHotkeyTextBox.ClearValue(TextBox.ForegroundProperty);
        WizHotkeyTextBox.ClearValue(TextBox.FontWeightProperty);
        _tooltip.IsOpen = false;
    }

    private void WizPrtScBtn_Click(object sender, RoutedEventArgs e)
    {
        var (prevMod, prevKey) = _settingsService.Settings.GetToolHotkey("rect");
        var app = Application.Current as App;

        bool canReg = true;
        if (app is not null)
        {
            app.UnregisterAllHotkeys();
            canReg = HotkeyConflictProbe.CanRegister(0, Native.User32.VK_SNAPSHOT);
        }
        var interceptors = HotkeyConflictProbe.DetectPrintScreenInterceptors();

        try
        {
            _settingsService.Settings.SetToolHotkey("rect", 0, Native.User32.VK_SNAPSHOT);
            _settingsService.Save();
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(0, Native.User32.VK_SNAPSHOT);
            app?.RegisterHotkeys(showReadyNotification: false);
            ApplyPrintScreenFeedback(canReg, interceptors);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("setup.rect-hotkey-prtsc", ex);
            _settingsService.Settings.SetToolHotkey("rect", prevMod, prevKey);
            try { _settingsService.Save(); } catch { }
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(prevMod, prevKey);
            app?.RegisterHotkeys(showReadyNotification: false);
            ToastWindow.ShowError(
                "Hotkey failed",
                $"The previous hotkey was restored after the failed assignment.\n{ex.Message}");
        }
    }

    private void WizClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var (prevMod, prevKey) = _settingsService.Settings.GetToolHotkey("rect");
        try
        {
            _settingsService.Settings.SetToolHotkey("rect", 0, 0);
            _settingsService.Save();
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(0, 0);
            if (Application.Current is App app)
            {
                app.RegisterHotkeys(showReadyNotification: false);
            }
            Keyboard.ClearFocus();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("setup.rect-hotkey-clear", ex);
            _settingsService.Settings.SetToolHotkey("rect", prevMod, prevKey);
            try { _settingsService.Save(); } catch { }
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(prevMod, prevKey);
            ToastWindow.ShowError(
                "Hotkey failed",
                $"The previous hotkey was restored after the failed clear.\n{ex.Message}");
        }
    }

    private void WizRestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        var (prevMod, prevKey) = _settingsService.Settings.GetToolHotkey("rect");
        uint defaultMod = Native.User32.MOD_ALT | Native.User32.MOD_SHIFT;
        uint defaultKey = 0x41; // A

        try
        {
            _settingsService.Settings.SetToolHotkey("rect", defaultMod, defaultKey);
            _settingsService.Save();
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(defaultMod, defaultKey);
            if (Application.Current is App app)
            {
                app.RegisterHotkeys(showReadyNotification: false);
            }
            Keyboard.ClearFocus();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("setup.rect-hotkey-restore", ex);
            _settingsService.Settings.SetToolHotkey("rect", prevMod, prevKey);
            try { _settingsService.Save(); } catch { }
            WizHotkeyTextBox.Text = HotkeyFormatter.Format(prevMod, prevKey);
            ToastWindow.ShowError(
                "Hotkey failed",
                $"The previous hotkey was restored after the failed restore.\n{ex.Message}");
        }
    }

    private void AddHotkeysFooterNote()
    {
        var card = new Border
        {
            Margin = new Thickness(0, 10, 0, 4),
            Background = (System.Windows.Media.Brush)FindResource("ThemeInputBackgroundBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("ThemeInputBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = "\uE946", // Info glyph
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("WizAccent"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var prefix = new System.Windows.Documents.Run(
            LocalizationService.Translate("The rest of the shortcuts can be configured in"));
        LocalizationService.SetSourceText(prefix, "The rest of the shortcuts can be configured in");

        var path = new System.Windows.Documents.Run(
            LocalizationService.Translate("Configuration -> Hotkeys"))
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("WizAccent"),
        };
        LocalizationService.SetSourceText(path, "Configuration -> Hotkeys");

        var text = new TextBlock
        {
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
            Foreground = (System.Windows.Media.Brush)FindResource("WizFgMuted"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 18,
        };
        text.Inlines.Add(prefix);
        text.Inlines.Add(new System.Windows.Documents.Run(" "));
        text.Inlines.Add(path);
        text.Inlines.Add(new System.Windows.Documents.Run("."));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        card.Child = grid;
        WizAnnotationPanelHotkeys.Children.Add(card);
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
        LoadAppearanceDefaults();
    }

    private void LoadAppearanceDefaults()
    {
        _suppressThemeChange = true;
        try
        {
            var mode = _settingsService.Settings.ThemeMode;
            WizThemeDarkRadio.IsChecked  = mode == AppThemeMode.Dark;
            WizThemeLightRadio.IsChecked = mode == AppThemeMode.Light;
            WizThemeGrayscaleRadio.IsChecked = mode == AppThemeMode.Grayscale;
            WizThemeAutoRadio.IsChecked  = mode == AppThemeMode.System;
        }
        finally
        {
            _suppressThemeChange = false;
        }
    }

    private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeChange) return;

        var mode = WizThemeDarkRadio.IsChecked  == true ? AppThemeMode.Dark
                 : WizThemeLightRadio.IsChecked == true ? AppThemeMode.Light
                 : WizThemeGrayscaleRadio.IsChecked == true ? AppThemeMode.Grayscale
                 : AppThemeMode.System;

        _settingsService.Settings.ThemeMode = mode;
        Theme.SetMode(mode);
        ApplyTheme();
        UpdateSteps(_page);
    }

    private void WizSaveToFile_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSaveDirectoryState();
        RefreshAfterCaptureSummary(GetAfterCaptureViewPreferenceFromControls());
    }

    private void UpdateSaveDirectoryState()
    {
        // When "Editor", "Save only" or "System viewer" is selected, saving to file is mandatory
        bool requiresSaveToFile = WizAfterCaptureCombo.SelectedIndex >= 1;
        WizSaveToFileCheck.IsEnabled = !requiresSaveToFile;
        WizSaveToFileCheck.Opacity = requiresSaveToFile ? 0.5 : 1.0;

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
        bool saveToFile = WizSaveToFileCheck.IsChecked == true;
        WizAfterCaptureSummaryText.Text = AfterCapturePreferences.BuildSummary(
            preference, saveToFile, LocalizationService.Translate);
    }

    private void WizAfterCapture_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAfterCaptureChange)
            return;

        // "Editor", "Save only" and "System viewer" require saving to file
        if (WizAfterCaptureCombo.SelectedIndex >= 1 && WizSaveToFileCheck.IsChecked != true)
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
                    var previousTheme = s.ThemeMode;
                    try
                    {
                        _settingsService.Save();
                    }
                    catch
                    {
                        s.ThemeMode = previousTheme;
                        Theme.SetMode(previousTheme);
                        ApplyTheme();
                        UpdateSteps(_page);
                        LoadAppearanceDefaults();
                        throw;
                    }
                    break;
                case 2:
                    var previousCapture = (
                        s.ShowCrosshairGuides,
                        s.ShowCaptureMagnifier,
                        s.MuteSounds,
                        s.ShowCaptureWidget);
                    try
                    {
                        s.ShowCrosshairGuides = WizCrosshairCheck.IsChecked == true;
                        s.ShowCaptureMagnifier = WizCaptureMagnifierCheck.IsChecked == true;
                        s.MuteSounds = WizEnableSoundsCheck.IsChecked != true;
                        s.ShowCaptureWidget = WizCaptureWidgetCheck.IsChecked == true;
                        foreach (SoundEvent evt in Enum.GetValues<SoundEvent>())
                        {
                            s.MutedSounds[evt] = s.MuteSounds;
                            SoundService.SetSoundMuted(evt, s.MuteSounds);
                        }
                        SoundService.Muted = s.MuteSounds;
                        _settingsService.Save();
                    }
                    catch
                    {
                        s.ShowCrosshairGuides = previousCapture.ShowCrosshairGuides;
                        s.ShowCaptureMagnifier = previousCapture.ShowCaptureMagnifier;
                        s.MuteSounds = previousCapture.MuteSounds;
                        s.ShowCaptureWidget = previousCapture.ShowCaptureWidget;
                        foreach (SoundEvent evt in Enum.GetValues<SoundEvent>())
                        {
                            s.MutedSounds[evt] = s.MuteSounds;
                            SoundService.SetSoundMuted(evt, s.MuteSounds);
                        }
                        SoundService.Muted = s.MuteSounds;
                        LoadDefaults();
                        throw;
                    }
                    break;
                case 3:
                    var previousSaving = (
                        s.SaveToFile,
                        s.AfterCapture,
                        s.OpenEditorAfterCapture);
                    try
                    {
                        s.SaveToFile = WizSaveToFileCheck.IsChecked == true;
                        AfterCapturePreferences.ApplyToSettings(GetAfterCaptureViewPreferenceFromControls(), s);
                        _settingsService.Save();
                    }
                    catch
                    {
                        s.SaveToFile = previousSaving.SaveToFile;
                        s.AfterCapture = previousSaving.AfterCapture;
                        s.OpenEditorAfterCapture = previousSaving.OpenEditorAfterCapture;
                        LoadDefaults();
                        throw;
                    }
                    break;
                case 4:
                    _settingsService.Save();
                    break;
                case 5:
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
        Resources["WizSidebarTopColor"] = Theme.IsGray
            ? System.Windows.Media.Color.FromRgb(34, 37, 41)
            : Theme.IsDark
            ? System.Windows.Media.Color.FromRgb(15, 30, 44)
            : System.Windows.Media.Color.FromRgb(226, 236, 245);
        Resources["WizSidebarBottomColor"] = Theme.IsGray
            ? System.Windows.Media.Color.FromRgb(22, 24, 27)
            : Theme.IsDark
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

    private void StepDot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border dot && dot.Tag is string tag && int.TryParse(tag, out int target))
        {
            e.Handled = true;
            GoToPage(target);
        }
    }

    private void StepDot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Prevent the window drag handler from capturing the mouse and eating the Up event.
        if (e.ChangedButton == MouseButton.Left)
            e.Handled = true;
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
