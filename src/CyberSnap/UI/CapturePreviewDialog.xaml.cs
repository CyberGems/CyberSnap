using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        private const double SideBySideBreakpoint = 700;
        /// <summary>Quick opacity fade for timer auto-close only (manual close stays instant).</summary>
        private const int AutoCloseFadeMs = 280;

        private readonly SettingsService _settingsService;
        private readonly Bitmap _capturedBitmap;
        private readonly System.Drawing.Point _targetMonitorPoint;
        private bool _isPinned = false;
        private bool _isHovered = false;
        private bool _didCenterOnOpen;
        private bool _isSideBySide = true;
        private bool _isClosing;
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

            ApplyLocalizedLabels();
            UpdateIcons();

            PreviewImage.Source = BitmapPerf.ToBitmapSource(bitmap);
            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            _lastOutcomeState = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            _lastTimeoutSeconds = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            InitAutoCloseTimer();
            ApplyLayoutMode(force: true);
            SoundService.PlayPreviewSound();
        }

        private void ApplyLocalizedLabels()
        {
            var lang = _settingsService.Settings.InterfaceLanguage;
            Helpers.WindowTitles.ApplyTaskbar(this, Helpers.WindowTitles.Preview, lang);
            TitleBar.Title = LocalizationService.Translate("Capture Preview");
            AfterCaptureHeaderLabel.Text = LocalizationService.Translate("Active actions:");
            OptionalActionsHeaderLabel.Text = LocalizationService.Translate("You can also:");
            OptionalActionsHeaderLabel.ToolTip =
                LocalizationService.Translate("Actions already covered by automatic actions are listed above.");
            EditAfterCaptureSettingsBtn.ToolTip = LocalizationService.Translate("Configure automatic actions");
            SaveText.Text = LocalizationService.Translate("Save");
            CopyText.Text = LocalizationService.Translate("Copy");
            EditText.Text = LocalizationService.Translate("Edit");
            MoreText.Text = LocalizationService.Translate("More");
            MoreBtn.ToolTip = LocalizationService.Translate("More");
            NoAutomaticActionsLabel.Text = LocalizationService.Translate("None");
            CancelText.Text = LocalizationService.Translate("Done");
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

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyLayoutMode(force: false);
        }

        private void ApplyLayoutMode(bool force)
        {
            if (MainLayout == null) return;

            bool sideBySide = MainLayout.ActualWidth <= 0
                ? ActualWidth >= SideBySideBreakpoint
                : MainLayout.ActualWidth >= SideBySideBreakpoint;

            if (!force && sideBySide == _isSideBySide)
                return;

            _isSideBySide = sideBySide;

            if (sideBySide)
            {
                PreviewCol.Width = new GridLength(1, GridUnitType.Star);
                ActionsCol.Width = new GridLength(268);
                PreviewRow.Height = new GridLength(1, GridUnitType.Star);
                ActionsRow.Height = new GridLength(0);

                Grid.SetRow(PreviewFrame, 0);
                Grid.SetColumn(PreviewFrame, 0);
                Grid.SetRow(ActionsPanel, 0);
                Grid.SetColumn(ActionsPanel, 1);

                PreviewFrame.Margin = new Thickness(0, 0, 12, 0);
                ActionsPanel.Margin = new Thickness(0);
            }
            else
            {
                PreviewCol.Width = new GridLength(1, GridUnitType.Star);
                ActionsCol.Width = new GridLength(0);
                PreviewRow.Height = new GridLength(1, GridUnitType.Star);
                ActionsRow.Height = GridLength.Auto;

                Grid.SetRow(PreviewFrame, 0);
                Grid.SetColumn(PreviewFrame, 0);
                Grid.SetRow(ActionsPanel, 1);
                Grid.SetColumn(ActionsPanel, 0);

                PreviewFrame.Margin = new Thickness(0, 0, 0, 10);
                ActionsPanel.Margin = new Thickness(0);
            }
        }

        private void UpdateIcons()
        {
            var cPrimary = Theme.TextPrimary;
            var primaryIconColor = System.Drawing.Color.FromArgb(cPrimary.A, cPrimary.R, cPrimary.G, cPrimary.B);

            var cSecondary = Theme.TextSecondary;
            var secondaryIconColor = System.Drawing.Color.FromArgb(cSecondary.A, cSecondary.R, cSecondary.G, cSecondary.B);

            SaveIcon.Source = FluentIcons.RenderWpf("save", primaryIconColor, 13, active: true);
            CopyIcon.Source = FluentIcons.RenderWpf("copy", primaryIconColor, 13, active: true);
            EditIcon.Source = FluentIcons.RenderWpf("draw", primaryIconColor, 13, active: true);
            ShareIcon.Source = FluentIcons.RenderWpf("share", primaryIconColor, 14, active: true);
            GalleryIcon.Source = FluentIcons.RenderWpf("history", primaryIconColor, 14, active: true);
            MoreIcon.Source = FluentIcons.RenderWpf("more", primaryIconColor, 13, active: true);
            EditSettingsBtnIcon.Source = FluentIcons.RenderWpf("gear", secondaryIconColor, 14, active: true);
            // List metaphor for the active-actions section (not a "done" check).
            AfterCaptureHeaderIcon.Source = FluentIcons.RenderWpf("menu", secondaryIconColor, 13, active: true);
        }

        private System.Drawing.Color GetPrimaryButtonIconColor()
        {
            // Cyan accent needs dark glyphs; light/gray accents need light glyphs.
            if (Theme.IsDark && !Theme.IsGray)
                return System.Drawing.Color.FromArgb(255, 11, 18, 32);
            return System.Drawing.Color.FromArgb(255, 255, 255, 255);
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
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
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
            {
                // Stay armed; refill stays full while the pointer remains inside.
                return;
            }

            StartProgressCountdown(_autoCloseDurationSeconds);
            _autoCloseTimer.Start();
        }

        private double CaptureProgressScale()
        {
            double progress = Math.Clamp(ProgressScale.ScaleX, 0, 1);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = progress;
            return progress;
        }

        private void StartProgressCountdown(double remainingSeconds)
        {
            if (remainingSeconds <= 0) return;
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation
                {
                    To = 0,
                    Duration = Motion.Sec(remainingSeconds),
                    FillBehavior = FillBehavior.HoldEnd
                });
        }

        private void StartProgressRefill()
        {
            if (_autoCloseDurationSeconds <= 0) return;

            double current = CaptureProgressScale();
            if (current >= 1)
                return;

            // Refill at 3× the countdown rate so the bar recovers quickly while hovered.
            double refillSeconds = Math.Max(0.05, (1.0 - current) * _autoCloseDurationSeconds / 3.0);
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation
                {
                    To = 1,
                    Duration = Motion.Sec(refillSeconds),
                    FillBehavior = FillBehavior.HoldEnd
                });
        }

        private void BeginProgressRefillForHover()
        {
            if (_autoCloseTimer == null || _isPinned) return;

            _autoCloseTimer.Stop();
            StartProgressRefill();
        }

        private void ResumeAutoCloseAfterHover()
        {
            if (_isPinned || _autoCloseTimer == null || _autoCloseDurationSeconds <= 0)
                return;

            double remaining = Math.Max(0.1, CaptureProgressScale() * _autoCloseDurationSeconds);
            _autoCloseTimer.Interval = TimeSpan.FromSeconds(remaining);
            StartProgressCountdown(remaining);
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
            BeginProgressRefillForHover();
        }

        private void OnPreviewMouseLeave()
        {
            _isHovered = false;
            if (_isPinned) return;
            ResumeAutoCloseAfterHover();
        }

        private void PerformAutoClose()
        {
            // Same outcome as Continue / Done, but fade out first (manual close stays instant).
            // Do not set DialogResult until the fade completes — WPF closes ShowDialog on set.
            if (_isClosing)
                return;
            _isClosing = true;

            StopAutoCloseTimer(resetProgress: false);
            ProgressHost.Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;

            bool commit = ResolvePrimaryButtonCommit();
            try
            {
                var fadeOut = Motion.To(0, AutoCloseFadeMs, Motion.SmoothInOut);
                fadeOut.FillBehavior = FillBehavior.HoldEnd;
                fadeOut.Completed += (_, _) =>
                {
                    BeginAnimation(OpacityProperty, null);
                    DialogResult = commit;
                };
                BeginAnimation(OpacityProperty, fadeOut);
            }
            catch
            {
                DialogResult = commit;
            }
        }

        /// <summary>
        /// Preview defers HandleCaptureResult until the dialog returns true.
        /// Done/Continue/auto-close must commit whenever a deferred outcome still
        /// needs that path (notification toast, save, editor, system viewer).
        /// Explicit cancel remains Title-bar X / Escape → DialogResult false.
        /// </summary>
        private static bool ShouldCommitDeferredOutcomes(AfterCaptureOutcomeState state) =>
            state.SystemViewer
            || state.Destination == AfterCaptureDestination.Editor
            || state.Destination == AfterCaptureDestination.Notification
            || state.EffectiveSave
            || state.Share
            // Clipboard may already have run before the dialog; still commit so
            // deferred save/share/editor paths and compact toasts can finish.
            || state.Clipboard;

        /// <summary>Applies SelectedAction for the primary button; returns the DialogResult to set.</summary>
        private bool ResolvePrimaryButtonCommit()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            if (ShouldCommitDeferredOutcomes(state))
            {
                SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
                return true;
            }
            return false;
        }

        private void CommitOrDismissFromPrimaryButton()
        {
            if (_isClosing)
                return;
            _isClosing = true;
            DialogResult = ResolvePrimaryButtonCommit();
        }

        private void EditAfterCaptureSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.ShowSettings("confirm-pills");
            }
        }

        private void MoreBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = BuildMoreMenu();
            menu.PlacementTarget = MoreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private ContextMenu BuildMoreMenu()
        {
            var menu = new ContextMenu
            {
                Background = Theme.Brush(Theme.BgElevated),
                BorderBrush = Theme.Brush(Theme.BorderSubtle),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            if (!state.Share)
            {
                menu.Items.Add(CreateMoreMenuItem(
                    LocalizationService.Translate("Share"),
                    ShareIcon.Source,
                    () => ShareBtn_Click(ShareBtn, new RoutedEventArgs())));
            }

            menu.Items.Add(CreateMoreMenuItem(
                LocalizationService.Translate("Gallery"),
                GalleryIcon.Source,
                () => GalleryBtn_Click(GalleryBtn, new RoutedEventArgs())));

            return menu;
        }

        private MenuItem CreateMoreMenuItem(string label, ImageSource? icon, Action onClick)
        {
            var item = new MenuItem
            {
                Header = label,
                Foreground = Theme.Brush(Theme.TextPrimary),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(10, 6, 14, 6),
                FontSize = 12
            };

            if (icon != null)
            {
                item.Icon = new System.Windows.Controls.Image
                {
                    Source = icon,
                    Width = 14,
                    Height = 14
                };
            }

            item.Click += (_, _) => onClick();
            return item;
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
            Resources["ThemeAccentHoverBrush"] = Theme.Brush(Theme.AccentHover);
            Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
            Resources["ThemePrimaryButtonForegroundBrush"] = Theme.IsDark && !Theme.IsGray
                ? Theme.Brush(System.Windows.Media.Color.FromRgb(11, 18, 32))
                : Theme.Brush(System.Windows.Media.Colors.White);

            CheckerboardHost.Background = Theme.CreateCheckerboardBrush();
            PreviewFrame.Background = Theme.Brush(Theme.BgSecondary);

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
            var settings = _settingsService.Settings;

            // Completed first, then pending — keeps the status column easy to scan.
            var doneGreen = System.Drawing.Color.FromArgb(255, 34, 197, 94);
            var pendingBlue = System.Drawing.Color.FromArgb(255, 0, 162, 255);

            var rows = new List<(AfterCapturePillKind Pill, AfterCapturePillTiming Timing, string IconId, string LabelKey, string TooltipKey)>();
            foreach (var pill in AfterCaptureOutcomeModel.AllPills)
            {
                if (!AfterCaptureOutcomeModel.IsActive(state, pill))
                    continue;

                // Already inside the preview dialog — Preview itself is noise here.
                // Notification stays visible as pending (compact status toast after confirm).
                if (pill is AfterCapturePillKind.Preview)
                    continue;

                string iconId = pill switch
                {
                    AfterCapturePillKind.Save => "save",
                    AfterCapturePillKind.Clipboard => "copy",
                    AfterCapturePillKind.Editor => "draw",
                    AfterCapturePillKind.SystemViewer => "folder",
                    AfterCapturePillKind.Share => "share",
                    AfterCapturePillKind.Notification => "info",
                    _ => "gear"
                };

                rows.Add((
                    pill,
                    AfterCaptureOutcomeModel.GetPreviewTiming(pill, settings),
                    iconId,
                    AfterCaptureOutcomeModel.LabelKey(pill),
                    AfterCaptureOutcomeModel.TooltipKey(pill)));
            }

            foreach (var row in rows.OrderBy(r => r.Timing == AfterCapturePillTiming.Done ? 0 : 1))
            {
                var color = row.Timing == AfterCapturePillTiming.Done ? doneGreen : pendingBlue;
                string label = LocalizationService.Translate(row.LabelKey);
                string statusTip = row.Timing == AfterCapturePillTiming.Done
                    ? LocalizationService.Translate("Already completed")
                    : LocalizationService.Translate("Runs when you continue");
                string tooltip = LocalizationService.Translate(row.TooltipKey);
                if (!string.IsNullOrWhiteSpace(tooltip))
                    tooltip = $"{tooltip}\n{statusTip}";
                else
                    tooltip = statusTip;

                AfterCapturePillsPanel.Children.Add(
                    CreateAfterCapturePillChip(row.IconId, color, label, tooltip, row.Timing));
            }

            NoAutomaticActionsLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement CreateAfterCapturePillChip(
            string iconId,
            System.Drawing.Color color,
            string label,
            string tooltip,
            AfterCapturePillTiming timing)
        {
            // Row: [pill]  status — status glyph stays outside the chip.
            var row = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 6),
                LastChildFill = true
            };

            // Status glyph matches pill family: green check (done) / blue arrow (pending = Listo).
            var statusColor = timing == AfterCapturePillTiming.Done
                ? System.Drawing.Color.FromArgb(255, 34, 197, 94)
                : System.Drawing.Color.FromArgb(255, 0, 162, 255);
            var statusIcon = new System.Windows.Controls.Image
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = timing == AfterCapturePillTiming.Done ? 1.0 : 0.9,
                Source = FluentIcons.RenderWpf(
                    timing == AfterCapturePillTiming.Done ? "check" : "arrow",
                    statusColor,
                    12,
                    active: true),
                ToolTip = timing == AfterCapturePillTiming.Done
                    ? LocalizationService.Translate("Already completed")
                    : LocalizationService.Translate("Runs when you continue")
            };
            DockPanel.SetDock(statusIcon, Dock.Right);
            row.Children.Add(statusIcon);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 4, 10, 4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Background = Theme.Brush(System.Windows.Media.Color.FromArgb(22, color.R, color.G, color.B)),
                BorderBrush = Theme.Brush(System.Windows.Media.Color.FromArgb(55, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip,
                SnapsToDevicePixels = true
            };

            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var img = new System.Windows.Controls.Image
            {
                Width = 11,
                Height = 11,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = FluentIcons.RenderWpf(iconId, color, 11, active: true)
            };

            var txt = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = Theme.Brush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            stack.Children.Add(img);
            stack.Children.Add(txt);
            border.Child = stack;
            row.Children.Add(border);
            return row;
        }

        private void UpdateContinueOrExitButton()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            bool viewerOn = state.SystemViewer;
            bool editorOn = state.Destination == AfterCaptureDestination.Editor;
            bool continuesToSurface = viewerOn || editorOn;
            var iconColor = GetPrimaryButtonIconColor();

            // Same arrow as pending chips: confirm runs the remaining deferred actions.
            CancelIcon.Source = FluentIcons.RenderWpf("arrow", iconColor, 14, active: true);
            CancelIcon.Visibility = Visibility.Visible;

            if (continuesToSurface)
            {
                CancelText.Text = LocalizationService.Translate("Continue");
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
                CancelText.Text = LocalizationService.Translate("Done");
                ViewerHintBadge.Visibility = Visibility.Collapsed;
                CancelBtn.ToolTip = null;
            }
        }

        private void UpdateOptionalActionsAvailability()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);

            // Hide duplicates of automatic actions instead of leaving disabled ghosts.
            bool saveAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Save);
            bool copyAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Clipboard);
            bool editAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Editor);

            SaveBtn.Visibility = saveAuto ? Visibility.Collapsed : Visibility.Visible;
            CopyBtn.Visibility = copyAuto ? Visibility.Collapsed : Visibility.Visible;
            EditBtn.Visibility = editAuto ? Visibility.Collapsed : Visibility.Visible;

            SaveBtn.IsEnabled = !saveAuto;
            CopyBtn.IsEnabled = !copyAuto;
            EditBtn.IsEnabled = !editAuto;

            bool anyOptionalVisible =
                SaveBtn.Visibility == Visibility.Visible
                || CopyBtn.Visibility == Visibility.Visible
                || EditBtn.Visibility == Visibility.Visible
                || MoreBtn.Visibility == Visibility.Visible;

            OptionalActionsSection.Visibility = anyOptionalVisible ? Visibility.Visible : Visibility.Collapsed;

            OptionalActionsHeaderLabel.Text = LocalizationService.Translate("You can also:");
            OptionalActionsHeaderLabel.ToolTip =
                LocalizationService.Translate("Actions already covered by automatic actions are listed above.");
        }

        private void TitleBar_CloseRequested(object sender, EventArgs e)
        {
            if (_isClosing)
                return;
            _isClosing = true;
            DialogResult = false;
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
            if (_isClosing)
                return;
            _isClosing = true;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
            DialogResult = true;
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            _isClosing = true;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Copy;
            DialogResult = true;
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            _isClosing = true;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Edit;
            DialogResult = true;
        }

        private void ShareBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            _isClosing = true;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Share;
            DialogResult = true;
        }

        private void GalleryBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            _isClosing = true;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.History;
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            CommitOrDismissFromPrimaryButton();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_isClosing)
                    return;
                _isClosing = true;
                DialogResult = false;
                e.Handled = true;
            }
        }
    }
}
