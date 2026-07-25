using System;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.Capture;

namespace CyberSnap.UI
{
    public partial class CapturePreviewDialog : Window
    {
        private readonly SettingsService _settingsService;
        private readonly Bitmap _capturedBitmap;
        private readonly System.Drawing.Point _targetMonitorPoint;
        private bool _isPinned = false;
        private bool _isHovered = false;
        private bool _didCenterOnOpen;
        private AfterCaptureOutcomeState _lastOutcomeState;
        private int _lastTimeoutSeconds;
        private DispatcherTimer? _autoCloseTimer;
        private double _autoCloseDurationSeconds;

        public RegionOverlayForm.ConfirmCommitAction SelectedAction { get; private set; } = RegionOverlayForm.ConfirmCommitAction.Default;

        public CapturePreviewDialog(
            Bitmap bitmap,
            SettingsService settingsService,
            System.Drawing.Point? targetMonitorPoint = null)
        {
            _capturedBitmap = bitmap;
            _settingsService = settingsService;
            // Own the capture-monitor anchor immediately. The static hint is easy to consume
            // (toast / GetCurrentWorkArea) before our deferred center runs — that sent the
            // dialog to the primary monitor when capturing on a secondary.
            _targetMonitorPoint = targetMonitorPoint
                ?? PopupWindowHelper.TakeMonitorHintPoint()
                ?? System.Windows.Forms.Cursor.Position;

            InitializeComponent();
            // Hide until post-layout physical centering runs. Centering at SourceInitialized /
            // with Width alone is wrong at 150% DPI + UiScale: the HWND grows afterward and
            // the window appears to jump right.
            Opacity = 0;
            TitleBar.IsPinActive = _isPinned;
            Topmost = true; // Temporary topmost to force it to the foreground on launch
            ContentRendered += CapturePreviewDialog_ContentRendered;
            Activated += CapturePreviewDialog_Activated;
            SettingsService.SettingsChanged += SettingsService_SettingsChanged;
            Closed += (_, _) =>
            {
                SettingsService.SettingsChanged -= SettingsService_SettingsChanged;
                StopAutoCloseTimer(resetProgress: true);
            };
            MouseEnter += (_, _) => OnPreviewMouseEnter();
            MouseLeave += (_, _) => OnPreviewMouseLeave();

            CyberSnapWindowChrome.Apply(this);
            UiScale.Set(settingsService.Settings.UiScale);
            UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);

            Theme.Refresh();
            ApplyTheme();
            LocalizationService.ApplyCurrentCulture(settingsService.Settings.InterfaceLanguage);
            LocalizationService.ApplyTo(this, settingsService.Settings.InterfaceLanguage);

            var lang = settingsService.Settings.InterfaceLanguage;
            Helpers.WindowTitles.ApplyTaskbar(this, Helpers.WindowTitles.Preview, lang);
            TitleBar.Title = LocalizationService.Translate("Capture Preview");
            SaveText.Text = LocalizationService.Translate("Save");
            CopyText.Text = LocalizationService.Translate("Copy");
            EditText.Text = LocalizationService.Translate("Edit");
            ShareText.Text = LocalizationService.Translate("Share");
            GalleryText.Text = LocalizationService.Translate("Gallery");
            CancelText.Text = LocalizationService.Translate("Close");
            EditSettingsBtnText.Text = LocalizationService.Translate("Edit");

            UpdateIcons();

            PreviewImage.Source = BitmapPerf.ToBitmapSource(bitmap);
            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            _lastOutcomeState = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            _lastTimeoutSeconds = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            InitAutoCloseTimer();
        }

        private void CapturePreviewDialog_ContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= CapturePreviewDialog_ContentRendered;
            Topmost = _isPinned;

            // Defer until layout/DPI settle — SourceInitialized GetWindowRect is still short
            // at 150% with AllowsTransparency + UiScale LayoutTransform.
            Dispatcher.BeginInvoke(new Action(CenterOnOpenMonitor), DispatcherPriority.ContextIdle);
        }

        private void CenterOnOpenMonitor()
        {
            if (_didCenterOnOpen) return;
            _didCenterOnOpen = true;

            // Mixed-DPI (primary 125% / secondary 150%): only SetWindowPos in physical pixels.
            // Setting WPF Left/Top in DIPs afterward pulls the window top-left on the
            // non-primary monitor. First move may change DPI and grow the HWND; second
            // pass recenters with the final physical size.
            try
            {
                UpdateLayout();
                PopupWindowHelper.CenterWindowOnPhysicalMonitor(this, _targetMonitorPoint);
            }
            catch { /* retry below */ }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    UpdateLayout();
                    PopupWindowHelper.CenterWindowOnPhysicalMonitor(this, _targetMonitorPoint);
                }
                catch { /* keep current position */ }

                Opacity = 1;
            }), DispatcherPriority.ApplicationIdle);
        }

        private void UpdateIcons()
        {
            var cPrimary = Theme.TextPrimary;
            var primaryIconColor = System.Drawing.Color.FromArgb(cPrimary.A, cPrimary.R, cPrimary.G, cPrimary.B);

            var cSecondary = Theme.TextSecondary;
            var secondaryIconColor = System.Drawing.Color.FromArgb(cSecondary.A, cSecondary.R, cSecondary.G, cSecondary.B);

            SaveIcon.Source = FluentIcons.RenderWpf("save", primaryIconColor, 14, active: true);
            CopyIcon.Source = FluentIcons.RenderWpf("copy", primaryIconColor, 14, active: true);
            EditIcon.Source = FluentIcons.RenderWpf("draw", primaryIconColor, 14, active: true);
            ShareIcon.Source = FluentIcons.RenderWpf("share", primaryIconColor, 14, active: true);
            GalleryIcon.Source = FluentIcons.RenderWpf("history", primaryIconColor, 14, active: true);
            CancelIcon.Source = FluentIcons.RenderWpf("cross", primaryIconColor, 14, active: true);
            EditSettingsBtnIcon.Source = FluentIcons.RenderWpf("settings", secondaryIconColor, 12, active: true);

            AfterCaptureHeaderIcon.Source = FluentIcons.RenderWpf("settings", secondaryIconColor, 14, active: true);
        }

        private void CapturePreviewDialog_Activated(object? sender, EventArgs e)
        {
            RefreshLiveSettings();
        }

        private void SettingsService_SettingsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(SettingsService_SettingsChanged));
                return;
            }
            RefreshLiveSettings();
        }

        private void RefreshLiveSettings()
        {
            if (!IsLoaded) return;

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            int timeoutSeconds = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            if (state == _lastOutcomeState && timeoutSeconds == _lastTimeoutSeconds) return;

            bool timeoutChanged = timeoutSeconds != _lastTimeoutSeconds;
            _lastOutcomeState = state;
            _lastTimeoutSeconds = timeoutSeconds;

            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();

            if (timeoutChanged)
                InitAutoCloseTimer();
        }

        private void InitAutoCloseTimer()
        {
            StopAutoCloseTimer(resetProgress: true);

            int timeoutSec = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            if (timeoutSec <= 0 || _isPinned)
            {
                ProgressHost.Visibility = Visibility.Collapsed;
                return;
            }

            _autoCloseDurationSeconds = timeoutSec;
            ProgressHost.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressScale.ScaleX = 1;

            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_autoCloseDurationSeconds) };
            _autoCloseTimer.Tick += (_, _) =>
            {
                StopAutoCloseTimer(resetProgress: false);
                if (_isPinned || _isHovered)
                    return;
                PerformAutoClose();
            };

            if (_isHovered)
                return;

            StartProgressAnimation(_autoCloseDurationSeconds);
            _autoCloseTimer.Start();
        }

        private void StartProgressAnimation(double remainingSeconds)
        {
            if (remainingSeconds <= 0) return;
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation { To = 0, Duration = Motion.Sec(remainingSeconds) });
        }

        private void PauseAutoCloseForHover()
        {
            if (_autoCloseTimer == null) return;

            _autoCloseTimer.Stop();
            double progress = Math.Clamp(ProgressScale.ScaleX, 0, 1);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = progress;
        }

        private void ResumeAutoCloseAfterHover()
        {
            if (_isPinned || _autoCloseTimer == null || _autoCloseDurationSeconds <= 0)
                return;

            double remaining = Math.Max(0.1, Math.Clamp(ProgressScale.ScaleX, 0, 1) * _autoCloseDurationSeconds);
            _autoCloseTimer.Interval = TimeSpan.FromSeconds(remaining);
            StartProgressAnimation(remaining);
            _autoCloseTimer.Start();
        }

        private void StopAutoCloseTimer(bool resetProgress)
        {
            if (_autoCloseTimer != null)
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer = null;
            }

            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            if (resetProgress)
                ProgressScale.ScaleX = 1;
        }

        private void OnPreviewMouseEnter()
        {
            _isHovered = true;
            PauseAutoCloseForHover();
        }

        private void OnPreviewMouseLeave()
        {
            _isHovered = false;
            if (_isPinned) return;
            ResumeAutoCloseAfterHover();
        }

        private void PerformAutoClose()
        {
            // Same outcome as Continue / Exit (CancelBtn).
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            if (state.SystemViewer || state.Destination == AfterCaptureDestination.Editor)
            {
                SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }
            Close();
        }

        private void EditAfterCaptureSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.ShowSettings("confirm-pills");
            }
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

            UpdateIcons();
            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
        }

        private void PopulateAfterCapturePills()
        {
            if (AfterCapturePillsPanel == null || _settingsService?.Settings == null) return;
            AfterCapturePillsPanel.Children.Clear();

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);

            foreach (var pill in AfterCaptureOutcomeModel.AllPills)
            {
                if (!AfterCaptureOutcomeModel.IsActive(state, pill))
                    continue;

                var (iconId, color, labelKey, tooltipKey) = pill switch
                {
                    AfterCapturePillKind.Save => ("save", System.Drawing.Color.FromArgb(255, 34, 197, 94), "Outcome step: save file", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Preview => ("eye", System.Drawing.Color.FromArgb(255, 59, 130, 246), "Outcome step: preview", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Clipboard => ("copy", System.Drawing.Color.FromArgb(255, 0, 162, 255), "Auto-copy", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Notification => ("bell", System.Drawing.Color.FromArgb(255, 245, 158, 11), "Outcome step: show notification", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Editor => ("draw", System.Drawing.Color.FromArgb(255, 139, 92, 246), "Outcome step: open editor", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.SystemViewer => ("openFolder", System.Drawing.Color.FromArgb(255, 6, 182, 212), "Outcome step: open in system viewer", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    _ => ("settings", System.Drawing.Color.FromArgb(255, 150, 150, 150), pill.ToString(), "")
                };

                string label = LocalizationService.Translate(labelKey);
                string tooltip = LocalizationService.Translate(tooltipKey);

                var chip = CreateAfterCapturePillChip(iconId, color, label, tooltip);
                AfterCapturePillsPanel.Children.Add(chip);
            }
        }

        private System.Windows.FrameworkElement CreateAfterCapturePillChip(string iconId, System.Drawing.Color color, string label, string tooltip)
        {
            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 3, 10, 3),
                Margin = new Thickness(0, 2, 6, 2),
                Background = Theme.Brush(System.Windows.Media.Color.FromArgb(30, color.R, color.G, color.B)),
                BorderBrush = Theme.Brush(System.Windows.Media.Color.FromArgb(100, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip,
                SnapsToDevicePixels = true
            };

            var stack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var img = new System.Windows.Controls.Image
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = FluentIcons.RenderWpf(iconId, color, 12, active: true)
            };

            var txt = new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = Theme.Brush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(img);
            stack.Children.Add(txt);
            border.Child = stack;

            return border;
        }

        private void UpdateContinueOrExitButton()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            bool viewerOn = state.SystemViewer;
            bool editorOn = state.Destination == AfterCaptureDestination.Editor;

            if (viewerOn || editorOn)
            {
                CancelText.Text = LocalizationService.Translate("Continue");
                CancelIcon.Visibility = Visibility.Collapsed;
                if (editorOn)
                {
                    ViewerHintBadge.Text = LocalizationService.Translate("The annotation editor opens when this window closes.");
                }
                else
                {
                    ViewerHintBadge.Text = LocalizationService.Translate("The system viewer opens when this window closes.");
                }
                ViewerHintBadge.Visibility = Visibility.Visible;
                CancelBtn.ToolTip = ViewerHintBadge.Text;
            }
            else
            {
                CancelText.Text = LocalizationService.Translate("Exit");
                CancelIcon.Visibility = Visibility.Visible;
                ViewerHintBadge.Visibility = Visibility.Collapsed;
                CancelBtn.ToolTip = null;
            }
        }

        private void UpdateOptionalActionsAvailability()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            SaveBtn.IsEnabled = !AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Save);
            CopyBtn.IsEnabled = !AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Clipboard);
            EditBtn.IsEnabled = !AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Editor);

            OptionalActionsHeaderLabel.Text = LocalizationService.Translate("Optional actions");
            OptionalActionsHeaderLabel.ToolTip =
                LocalizationService.Translate("Buttons covered by an active automatic action are disabled.");
        }

        private void TitleBar_CloseRequested(object sender, EventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_PinRequested(object sender, EventArgs e)
        {
            _isPinned = !_isPinned;
            TitleBar.IsPinActive = _isPinned;
            Topmost = _isPinned;

            if (_isPinned)
            {
                StopAutoCloseTimer(resetProgress: true);
                ProgressHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                InitAutoCloseTimer();
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
            DialogResult = true;
            Close();
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Copy;
            DialogResult = true;
            Close();
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Edit;
            DialogResult = true;
            Close();
        }

        private void ShareBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Share;
            DialogResult = true;
            Close();
        }

        private void GalleryBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.History;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            if (state.SystemViewer || state.Destination == AfterCaptureDestination.Editor)
            {
                SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }
            Close();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }
    }
}
