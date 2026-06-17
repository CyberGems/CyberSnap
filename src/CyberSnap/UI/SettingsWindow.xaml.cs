using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CaptureMode = CyberSnap.Models.CaptureMode;
using CyberSnap.Models;
using CyberSnap.Services;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace CyberSnap.UI;

public partial class SettingsWindow : Window
{
    private const int UpdateActionCooldownMs = 900;
    private const int LocalEngineProjectOpenCooldownMs = 900;
    private static readonly (string Token, string Label)[] FileNameTokens =
    [
        ("{year}", "Year"),
        ("{month}", "Month"),
        ("{day}", "Day"),
        ("{hour}", "Hour"),
        ("{min}", "Minute"),
        ("{sec}", "Second"),
        ("{date}", "Date"),
        ("{time}", "Time"),
        ("{datetime}", "Date time"),
        ("{w}", "Width"),
        ("{h}", "Height"),
        ("{aspect}", "Aspect"),
        ("{rand}", "Random"),
    ];
    private static readonly SemaphoreSlim ThumbDecodeGate = new(4);
    private readonly SettingsService _settingsService;
    private readonly HistoryService _historyService;
    private readonly ImageSearchIndexService _imageSearchIndexService;

    private bool _suppressCaptureSavePreferenceChange;
    private bool _suppressToastPreferenceChange;
    private bool _suppressGeneralPreferenceChange;
    private bool _suppressRecordingPreferenceChange;
    private bool _suppressOcrPreferenceChange;
    private bool _suppressHistoryPreferenceChange;
    private bool ImageIndexResetInProgress { get; set; }
    private bool _suppressStartWithWindowsChange;

    public event Action? HotkeyChanged;
    public event Action? LocalizationChanged;

    public SettingsWindow(SettingsService settingsService, HistoryService historyService, ImageSearchIndexService imageSearchIndexService)
    {
        _settingsService = settingsService;
        _historyService = historyService;
        _imageSearchIndexService = imageSearchIndexService;
        InitializeComponent();
        CyberSnapWindowChrome.Apply(this);
        Theme.Refresh();
        Theme.ApplyTo(Application.Current.Resources);
        ApplyThemeColors();
        LoadFileNameTokenButtons();
        LoadSettings();
        InitializeSearch();
        UpdateWindowTitle();
        Loaded += (_, _) => ApplyMicaBackdrop();
        Loaded += (_, _) => EnsureSettingsWindowFitsWorkArea();
        StateChanged += (_, _) => SettingsTitleBar.RefreshIcons();
        LocalizationChanged += () => RefreshAfterCaptureSummary(GetAfterCaptureViewPreference());
        BackgroundRuntimeJobService.Changed += BackgroundRuntimeJobService_Changed;
        Activated += (_, _) =>
        {
            ApplyThemeColors();
        };
        Closed += (_, _) =>
        {
            BackgroundRuntimeJobService.Changed -= BackgroundRuntimeJobService_Changed;
            SaveWindowBounds();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        LoadWindowBounds();
        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
    }

    private void LoadWindowBounds()
    {
        var settings = _settingsService.Settings;
        if (settings.SettingsWindowLeft != -1)
        {
            // Restore the last size only. The position is intentionally NOT restored:
            // the window always opens centered on the screen where the user triggered it,
            // even if they moved it before closing last time.
            this.Width = settings.SettingsWindowWidth;
            this.Height = settings.SettingsWindowHeight;
        }

        // Always center on the monitor under the cursor before clamping into the work area.
        PopupWindowHelper.CenterOnCurrentScreen(this);
        EnsureSettingsWindowFitsWorkArea();

        if (settings.SettingsWindowState == (int)WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowBounds()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var settings = _settingsService.Settings;
        if (WindowState == WindowState.Maximized)
        {
            var bounds = RestoreBounds;
            if (double.IsNaN(bounds.Left) || double.IsInfinity(bounds.Left) ||
                double.IsNaN(bounds.Top) || double.IsInfinity(bounds.Top) ||
                double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width) ||
                double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height))
            {
                return;
            }
            settings.SettingsWindowState = (int)WindowState.Maximized;
            settings.SettingsWindowLeft = bounds.Left;
            settings.SettingsWindowTop = bounds.Top;
            settings.SettingsWindowWidth = bounds.Width;
            settings.SettingsWindowHeight = bounds.Height;
        }
        else
        {
            if (double.IsNaN(Left) || double.IsInfinity(Left) ||
                double.IsNaN(Top) || double.IsInfinity(Top) ||
                double.IsNaN(Width) || double.IsInfinity(Width) ||
                double.IsNaN(Height) || double.IsInfinity(Height))
            {
                return;
            }
            settings.SettingsWindowState = (int)WindowState.Normal;
            settings.SettingsWindowLeft = Left;
            settings.SettingsWindowTop = Top;
            settings.SettingsWindowWidth = Width;
            settings.SettingsWindowHeight = Height;
        }

        try
        {
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.save-window-bounds", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<Native.User32.MINMAXINFO>(lParam);
        var monitor = Native.User32.MonitorFromWindow(hwnd, Native.User32.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new Native.User32.MONITORINFO { cbSize = Marshal.SizeOf<Native.User32.MONITORINFO>() };
            Native.User32.GetMonitorInfo(monitor, ref monitorInfo);
            var rcWork = monitorInfo.rcWork;
            var rcMonitor = monitorInfo.rcMonitor;
            mmi.ptMaxPosition.X = Math.Abs(rcWork.Left - rcMonitor.Left);
            mmi.ptMaxPosition.Y = Math.Abs(rcWork.Top - rcMonitor.Top);
            mmi.ptMaxSize.X = Math.Abs(rcWork.Right - rcWork.Left);
            mmi.ptMaxSize.Y = Math.Abs(rcWork.Bottom - rcWork.Top);
        }

        // Enforce the window's minimum size while the user drags a resize border. This window uses
        // WindowStyle=None + AllowsTransparency and marks WM_GETMINMAXINFO as handled, which bypasses
        // WPF's own MinWidth/MinHeight enforcement, so we populate ptMinTrackSize (physical pixels) here.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void EnsureSettingsWindowFitsWorkArea()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var screen = hwnd != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(hwnd)
            : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);

        if (hwnd == IntPtr.Zero)
        {
             var workArea = SystemParameters.WorkArea;
             if (Left + 100 > workArea.Right || Left + Width - 100 < workArea.Left ||
                 Top + 50 > workArea.Bottom || Top < workArea.Top)
             {
                 Left = workArea.Left + (workArea.Width - Width) / 2;
                 Top = workArea.Top + (workArea.Height - Height) / 2;
             }
             return;
        }

        // Use same DIP conversion as CenterOnCurrentScreen for consistency.
        var wa = PopupWindowHelper.ScreenWorkingAreaToDips(screen);

        const double screenMargin = 12d;
        var maxWidth = wa.Width - screenMargin * 2;
        MinWidth = Math.Min(MinWidth, maxWidth);
        if (Width > maxWidth)
            Width = maxWidth;

        var minLeft = wa.Left + screenMargin;
        var minTop = wa.Top + screenMargin;
        var maxLeft = wa.Right - Width - screenMargin;
        var maxTop = wa.Bottom - Height - screenMargin;

        Left = Math.Min(Math.Max(Left, minLeft), Math.Max(minLeft, maxLeft));
        Top = Math.Min(Math.Max(Top, minTop), Math.Max(minTop, maxTop));
    }

    private void BackgroundRuntimeJobService_Changed(string key)
    {
    }



    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();

    private void ApplyMicaBackdrop()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.Dwm.DisableBackdrop(hwnd);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.apply-backdrop", ex.Message, ex);
        }
        ApplyThemeColors();
    }

    private void AfterCaptureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = GetAfterCaptureViewPreference();
        var selected = GetAfterCaptureViewPreferenceFromControls();

        UpdateCaptureSavePreference(
            "settings.after-capture",
            "After capture",
            previous,
            selected,
            value =>
            {
                SetAfterCaptureViewPreference(value);
                RefreshAfterCaptureSummary(value);
                // Keep the notification dual preview's emphasis in step with the editor state.
                RefreshEditorPreviewState();
            },
            value =>
            {
                ApplyAfterCaptureViewPreference(value);
                ((App)Application.Current).RefreshWidgetWindowLayout();
                RefreshEditorPreviewState();
            },
            () => ((App)Application.Current).RefreshWidgetWindowLayout());
    }

    private void AfterCaptureCopyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = GetAfterCaptureViewPreference();
        var selected = GetAfterCaptureViewPreferenceFromControls();

        UpdateCaptureSavePreference(
            "settings.after-capture-copy",
            "Copy capture to clipboard",
            previous,
            selected,
            value =>
            {
                SetAfterCaptureViewPreference(value);
                RefreshAfterCaptureSummary(value);
            },
            value => ApplyAfterCaptureViewPreference(value),
            () => ((App)Application.Current).RefreshWidgetWindowLayout());
    }

    private AfterCaptureViewPreference GetAfterCaptureViewPreference()
    {
        var action = NormalizeAfterCaptureAction(_settingsService.Settings.AfterCapture);
        var openEditor = _settingsService.Settings.OpenEditorAfterCapture;

        return action switch
        {
            AfterCaptureAction.CopyToClipboard => new AfterCaptureViewPreference(2, true),
            AfterCaptureAction.None => new AfterCaptureViewPreference(2, false),
            _ => openEditor
                ? new AfterCaptureViewPreference(1, action == AfterCaptureAction.PreviewAndCopy)
                : new AfterCaptureViewPreference(0, action == AfterCaptureAction.PreviewAndCopy)
        };
    }

    private static void SetAfterCaptureViewPreference(AfterCaptureViewPreference preference, AppSettings settings)
    {
        (AfterCaptureAction action, bool openEditor) = preference.WindowIndex switch
        {
            0 => preference.Copy
                ? (AfterCaptureAction.PreviewAndCopy, false)
                : (AfterCaptureAction.PreviewOnly, false),
            1 => preference.Copy
                ? (AfterCaptureAction.PreviewAndCopy, true)
                : (AfterCaptureAction.PreviewOnly, true),
            _ => preference.Copy
                ? (AfterCaptureAction.CopyToClipboard, false)
                : (AfterCaptureAction.None, false)
        };

        settings.AfterCapture = action;
        settings.OpenEditorAfterCapture = openEditor;
    }

    private void SetAfterCaptureViewPreference(AfterCaptureViewPreference preference) =>
        SetAfterCaptureViewPreference(preference, _settingsService.Settings);

    private void ApplyAfterCaptureViewPreference(AfterCaptureViewPreference preference)
    {
        AfterCaptureCombo.SelectedIndex = preference.WindowIndex;
        AfterCaptureCopyCheck.IsChecked = preference.Copy;
        RefreshAfterCaptureSummary(preference);
    }

    private AfterCaptureViewPreference GetAfterCaptureViewPreferenceFromControls() =>
        new(AfterCaptureCombo.SelectedIndex, AfterCaptureCopyCheck.IsChecked == true);

    private void RefreshAfterCaptureSummary(AfterCaptureViewPreference preference)
    {
        string key = preference switch
        {
            (0, true) => "After capture: open preview and copy to clipboard.",
            (0, false) => "After capture: open preview only.",
            (1, true) => "After capture: open editor and copy to clipboard.",
            (1, false) => "After capture: open editor only.",
            (2, true) => "After capture: copy to clipboard.",
            _ => "After capture: save the file only."
        };
        AfterCaptureSummaryText.Text = LocalizationService.Translate(key);
    }

    private static AfterCaptureAction NormalizeAfterCaptureAction(AfterCaptureAction action) =>
        Enum.IsDefined(typeof(AfterCaptureAction), action)
            ? action
            : AfterCaptureAction.PreviewAndCopy;

    private readonly record struct AfterCaptureViewPreference(int WindowIndex, bool Copy);

    private void DefaultCaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.DefaultCaptureMode;
        var selected = DefaultCaptureModeCombo.SelectedIndex switch
        {
            1 => CaptureMode.Center,
            2 => CaptureMode.Freeform,
            _ => CaptureMode.Rectangle
        };

        UpdateCaptureSavePreference(
            "settings.default-capture-mode",
            "Default capture method",
            previous,
            selected,
            value => _settingsService.Settings.DefaultCaptureMode = value,
            value => DefaultCaptureModeCombo.SelectedIndex = value switch
            {
                CaptureMode.Center => 1,
                CaptureMode.Freeform => 2,
                _ => 0
            },
            notifyHotkeyChanged: true);
    }

    private void CenterAspectRatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.CenterSelectionAspectRatio;
        var selectedIndex = Math.Clamp(CenterAspectRatioCombo.SelectedIndex, 0, 5);
        var selected = (CenterSelectionAspectRatio)selectedIndex;

        UpdateCaptureSavePreference(
            "settings.center-aspect-ratio",
            "Center aspect ratio",
            previous,
            selected,
            value => _settingsService.Settings.CenterSelectionAspectRatio = value,
            value => CenterAspectRatioCombo.SelectedIndex = (int)value,
            () => CenterAspectRatioCombo.SelectedIndex = selectedIndex);
    }

    private void SaveToFileCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.SaveToFile;
        var selected = SaveToFileCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.save-to-file",
            "Save screenshots",
            previous,
            selected,
            value => _settingsService.Settings.SaveToFile = value,
            value =>
            {
                SaveToFileCheck.IsChecked = value;
                SaveDirPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            },
            () => SaveDirPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed);
    }

    private void AutoOpenCapturedImagesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.AutoOpenCapturedImages;
        var selected = AutoOpenCapturedImagesCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.auto-open-images",
            "Auto-open image in system viewer",
            previous,
            selected,
            value => _settingsService.Settings.AutoOpenCapturedImages = value,
            value => AutoOpenCapturedImagesCheck.IsChecked = value);
    }

    private void AskFileNameCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.AskForFileNameOnSave;
        var selected = AskFileNameCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.ask-file-name",
            "Ask for file name",
            previous,
            selected,
            value => _settingsService.Settings.AskForFileNameOnSave = value,
            value => AskFileNameCheck.IsChecked = value);
    }

    private void LoadFileNameTemplate(string currentTemplate)
    {
        FileNameTemplateBox.Text = currentTemplate;
        UpdateFileNameTemplatePreview(currentTemplate);
    }

    private void SetSaveDirectoryPath(string path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "" : path;
        SaveDirBox.Text = value;
        AutomationProperties.SetHelpText(
            SaveDirBox,
            string.IsNullOrWhiteSpace(value)
                ? "No save folder selected."
                : $"Current save folder: {value}");
    }

    private void FileNameTemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.FileNameTemplate;
        var template = FileNameTemplateBox.Text;
        UpdateCaptureSavePreference(
            "settings.file-name-template",
            "File name pattern",
            previous,
            template,
            value => _settingsService.Settings.FileNameTemplate = value,
            value =>
            {
                FileNameTemplateBox.Text = value;
                UpdateFileNameTemplatePreview(value);
            },
            () => UpdateFileNameTemplatePreview(template));
    }

    private void UpdateCaptureSavePreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action? applyCurrentUi = null,
        bool notifyHotkeyChanged = false)
    {
        try
        {
            setValue(current);
            if (applyCurrentUi != null)
            {
                _suppressCaptureSavePreferenceChange = true;
                try
                {
                    applyCurrentUi();
                }
                finally
                {
                    _suppressCaptureSavePreferenceChange = false;
                }
            }

            _settingsService.Save();
            SetCaptureSavePreferenceStatus(string.Empty);
            if (notifyHotkeyChanged)
                HotkeyChanged?.Invoke();
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

            _suppressCaptureSavePreferenceChange = true;
            try
            {
                restoreUi(previous);
            }
            finally
            {
                _suppressCaptureSavePreferenceChange = false;
            }

            ShowCaptureSavePreferenceFailed(label, ex);
        }
    }

    private void ShowCaptureSavePreferenceFailed(string label, Exception ex)
    {
        SetCaptureSavePreferenceStatus($"{label} change was not saved. Previous setting restored.");
        ToastWindow.ShowError(
            $"{label} failed",
            $"The previous capture setting was restored. Check Config -> Capture and try again.\n{ex.Message}");
    }

    private void SetCaptureSavePreferenceStatus(string message)
    {
        CaptureSavePreferenceStatusText.Text = message;
        CaptureSavePreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateFileNameTemplatePreview(string template)
    {
        if (FileNameTemplatePreviewText is null)
            return;

        FileNameTemplatePreviewText.Text = $"Preview: {Helpers.FileNameTemplate.FormatExample(template)}.png";
    }

    private void LoadFileNameTokenButtons()
    {
        FileNameTokenPanel.Children.Clear();
        foreach (var (token, label) in FileNameTokens)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = token,
                ToolTip = $"Insert {label} token",
                FontSize = 11,
                MinHeight = 28,
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = token,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            AutomationProperties.SetName(button, $"Insert {label} token");
            AutomationProperties.SetHelpText(button, token);
            button.Click += FileNameTokenButton_Click;
            FileNameTokenPanel.Children.Add(button);
        }
    }

    private void FileNameTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string token })
            return;

        var box = FileNameTemplateBox;
        var text = box.Text ?? "";
        var start = Math.Clamp(box.SelectionStart, 0, text.Length);
        var length = Math.Clamp(box.SelectionLength, 0, text.Length - start);
        var insert = NeedsLeadingSeparator(text, start) ? "-" + token : token;

        box.Text = text.Remove(start, length).Insert(start, insert);
        box.Focus();
        box.SelectionStart = start + insert.Length;
        box.SelectionLength = 0;
    }

    private void FileNameTemplateBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || box.SelectionLength > 0)
            return;

        var text = box.Text ?? "";
        var caret = box.SelectionStart;
        var range = e.Key switch
        {
            Key.Back => FindTokenRangeBeforeCaret(text, caret),
            Key.Delete => FindTokenRangeAfterCaret(text, caret),
            _ => null
        };

        if (range is not { } tokenRange)
            return;

        box.Text = text.Remove(tokenRange.Start, tokenRange.Length);
        box.SelectionStart = tokenRange.Start;
        box.SelectionLength = 0;
        e.Handled = true;
    }

    private static bool NeedsLeadingSeparator(string text, int insertionIndex)
        => insertionIndex > 0
            && !char.IsWhiteSpace(text[insertionIndex - 1])
            && text[insertionIndex - 1] is not '_' and not '-' and not '.' and not '(';

    private static RangeSpec? FindTokenRangeBeforeCaret(string text, int caret)
    {
        foreach (var (token, _) in FileNameTokens)
        {
            var start = caret - token.Length;
            if (start >= 0 && string.Equals(text.Substring(start, token.Length), token, StringComparison.OrdinalIgnoreCase))
                return new RangeSpec(start, token.Length);
        }

        return null;
    }

    private static RangeSpec? FindTokenRangeAfterCaret(string text, int caret)
    {
        foreach (var (token, _) in FileNameTokens)
        {
            if (caret + token.Length <= text.Length && string.Equals(text.Substring(caret, token.Length), token, StringComparison.OrdinalIgnoreCase))
                return new RangeSpec(caret, token.Length);
        }

        return null;
    }

    private sealed record RangeSpec(int Start, int Length);

    private void MonthlyFoldersCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.SaveInMonthlyFolders;
        var selected = MonthlyFoldersCheck.IsChecked == true;
        UpdateCaptureSavePreference(
            "settings.monthly-folders",
            "Monthly folders",
            previous,
            selected,
            value => _settingsService.Settings.SaveInMonthlyFolders = value,
            value => MonthlyFoldersCheck.IsChecked = value);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose save folder",
            SelectedPath = _settingsService.Settings.SaveDirectory,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var previous = _settingsService.Settings.SaveDirectory;
            var selectedPath = dlg.SelectedPath;
            UpdateCaptureSavePreference(
                "settings.save-directory",
                "Save folder",
                previous,
                selectedPath,
                value => _settingsService.Settings.SaveDirectory = value,
                SetSaveDirectoryPath,
                () => SetSaveDirectoryPath(selectedPath));
        }
    }

    private void StartWithWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressStartWithWindowsChange) return;
        bool on = StartWithWindowsCheck.IsChecked == true;
        bool previous = _settingsService.Settings.StartWithWindows;

        try
        {
            UninstallService.SetStartupEntry(on);
            _settingsService.Settings.StartWithWindows = on;
            _settingsService.Save();
            SetStartupPreferenceStatus(string.Empty);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.start-with-windows", ex);
            try
            {
                UninstallService.SetStartupEntry(previous);
            }
            catch (Exception rollbackEx)
            {
                AppDiagnostics.LogError("settings.start-with-windows-rollback", rollbackEx);
            }

            _settingsService.Settings.StartWithWindows = previous;
            try
            {
                _settingsService.Save();
            }
            catch (Exception rollbackEx)
            {
                AppDiagnostics.LogError("settings.start-with-windows-save-rollback", rollbackEx);
            }

            _suppressStartWithWindowsChange = true;
            try
            {
                StartWithWindowsCheck.IsChecked = previous;
                SaveHistoryCheck.IsChecked = _settingsService.Settings.SaveHistory;
            }
            finally
            {
                _suppressStartWithWindowsChange = false;
            }

            ShowStartupPreferenceFailed(ex);
        }
    }

    private void ShowStartupPreferenceFailed(Exception ex)
    {
        SetStartupPreferenceStatus("Startup setting change was not saved. Previous setting restored.");
        ToastWindow.ShowError(
            "Startup setting failed",
            $"The previous startup setting was restored. Check Config -> About and try again.\n{ex.Message}");
    }

    private void SetStartupPreferenceStatus(string message)
    {
        StartupPreferenceStatusText.Text = message;
        StartupPreferenceStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CaptureFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;

        var previous = _settingsService.Settings.CaptureImageFormat;
        var selected = (CaptureImageFormat)CaptureFormatCombo.SelectedIndex;
        UpdateCaptureSavePreference(
            "settings.capture-format",
            "Capture format",
            previous,
            selected,
            value => _settingsService.Settings.CaptureImageFormat = value,
            value =>
            {
                CaptureFormatCombo.SelectedIndex = (int)value;
                _historyService.CaptureImageFormat = value;
                UpdateCaptureFormatControls();
            },
            () =>
            {
                _historyService.CaptureImageFormat = selected;
                UpdateCaptureFormatControls();
            });
    }

    private void JpegQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;
        var selected = JpegQualityCombo.SelectedItem as ComboBoxItem;
        var tag = selected?.Tag?.ToString();
        var quality = int.TryParse(tag, out var value) ? value : 85;
        var previous = _settingsService.Settings.JpegQuality;
        var selectedIndex = JpegQualityCombo.SelectedIndex;
        UpdateCaptureSavePreference(
            "settings.jpeg-quality",
            "JPG quality",
            previous,
            quality,
            value => _settingsService.Settings.JpegQuality = value,
            value =>
            {
                JpegQualityCombo.SelectedIndex = value switch
                {
                    >= 95 => 0,
                    >= 90 => 1,
                    >= 85 => 2,
                    >= 75 => 3,
                    _ => 4
                };
                _historyService.JpegQuality = value;
            },
            () =>
            {
                JpegQualityCombo.SelectedIndex = selectedIndex;
                _historyService.JpegQuality = quality;
            });
    }

    private void CaptureSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;
        var selected = CaptureSizeCombo.SelectedItem as ComboBoxItem;
        var tag = selected?.Tag?.ToString();
        var maxLongEdge = int.TryParse(tag, out var value) ? value : 0;
        var previous = _settingsService.Settings.CaptureMaxLongEdge;
        var selectedIndex = CaptureSizeCombo.SelectedIndex;
        UpdateCaptureSavePreference(
            "settings.capture-size",
            "Max image size",
            previous,
            maxLongEdge,
            value => _settingsService.Settings.CaptureMaxLongEdge = value,
            value => CaptureSizeCombo.SelectedIndex = value switch
            {
                2160 => 1,
                1440 => 2,
                1080 => 3,
                720 => 4,
                480 => 5,
                _ => 0
            },
            () => CaptureSizeCombo.SelectedIndex = selectedIndex);
    }
    private void GithubButton_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/CyberGems/CyberSnap") { UseShellExecute = true }); } catch { }
    }

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await UpdateService.CheckForUpdatesAsync();
        if (result.IsUpdateAvailable)
        {
            var msg = $"{result.StatusMessage}\n\nCurrent: {result.CurrentVersion}\nLatest: {result.LatestVersionLabel}\n\nDownload and install now?";
            var choice = ThemedConfirmDialog.Confirm(this, "Update available", msg, "Download", "Later", danger: false);
            if (choice)
            {
                await StartUpdateDownloadAsync(result);
            }
        }
        else
        {
            ThemedConfirmDialog.Alert(this, "Check for Updates", result.StatusMessage, error: false);
        }
    }

    public async Task StartUpdateDownloadAsync(UpdateCheckResult result)
    {
        AboutTab.IsChecked = true;
        TabChanged(AboutTab, new RoutedEventArgs());

        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateBtn.IsEnabled = false;
        GithubBtn.IsEnabled = false;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var updatesFolder = Path.Combine(appData, "CyberSnap", "Updates");
        var filename = result.AssetName ?? $"cybersnap_setup_{UpdateService.GetRuntimeChannel()}.exe";
        var installerPath = Path.Combine(updatesFolder, filename);

        var progress = new Progress<double>(val =>
        {
            UpdateProgressBar.Value = val;
            UpdateProgressText.Text = $"Downloading update ({val:F1}%)...";
        });

        try
        {
            UpdateProgressBar.Value = 0;
            UpdateProgressText.Text = "Downloading update (0.0%)...";

            if (string.IsNullOrEmpty(result.DownloadUrl))
            {
                throw new Exception("Direct download link is not available for this release.");
            }

            await UpdateService.DownloadUpdateAsync(result.DownloadUrl, installerPath, progress);

            UpdateProgressText.Text = "Download completed. Launching installer...";

            ThemedConfirmDialog.Alert(this, "Download Complete", "The update has been successfully downloaded. CyberSnap will now close to continue the installation.", error: false);

            UpdateService.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateBtn.IsEnabled = true;
            GithubBtn.IsEnabled = true;

            var errorChoice = ThemedConfirmDialog.Confirm(this, "Download Failed", $"Failed to download update automatically:\n{ex.Message}\n\nWould you like to open the GitHub release page instead?", "Open Browser", "Cancel", danger: false);
            if (errorChoice)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
                }
                catch { }
            }
        }
    }

}
