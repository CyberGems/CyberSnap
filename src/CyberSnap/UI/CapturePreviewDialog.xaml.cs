using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        private const int CountdownFadeMs = 200;
        private const int PillSimInitialDelayMs = 200;
        private const int PillSimWorkMs = 1000;

        private readonly SettingsService _settingsService;
        private readonly Bitmap _capturedBitmap;
        private readonly System.Drawing.Point _targetMonitorPoint;
        private readonly string? _savedFilePath;
        private bool _isPinned = false;
        private bool _isHovered = false;
        private bool _didCenterOnOpen;
        private bool _isSideBySide = true;
        private bool _isClosing;
        private int _pillSimToken;
        private List<AfterCapturePillChip>? _activePillChips;
        private List<AfterCapturePillChip>? _pendingPillSimulation;
        private readonly List<DispatcherTimer> _pillSimTimers = new();
        private AfterCaptureOutcomeState _lastOutcomeState;
        private int _lastTimeoutSeconds;
        private double _autoCloseDurationSeconds;
        private bool _autoCloseArmed;
        private int _lastCountdownSecondText = -1;
        private int _countdownEpoch;
        private bool _pillSimRunning;
        private int _pillSimsRemaining;

        // ── Zoom & pan state ────────────────────────────────────────────
        private const double ZoomMin = 0.1;
        private const double ZoomMax = 8.0;
        private const double ZoomStep = 1.2;
        private double _currentZoom = 1.0;
        private bool _zoomToFit = true;
        private bool _isPanning;
        private System.Windows.Point _panStart;
        private double _panStartHorizontalOffset;
        private double _panStartVerticalOffset;
        private bool _zoomPointerInside;
        private DispatcherTimer? _zoomHideTimer;

        private static readonly System.Drawing.Color PillDoneGreen = System.Drawing.Color.FromArgb(255, 34, 197, 94);
        private static readonly System.Drawing.Color PillPendingBlue = System.Drawing.Color.FromArgb(255, 0, 162, 255);
        private static readonly System.Drawing.Color DeleteAccentRed = System.Drawing.Color.FromArgb(255, 239, 83, 80);

        private enum PillVisualState
        {
            Pending,
            Working,
            Done
        }

        private sealed class AfterCapturePillChip
        {
            public required FrameworkElement Root { get; init; }
            public required Border ChipBorder { get; init; }
            public required System.Windows.Controls.Image LeadingIcon { get; init; }
            public required TextBlock Label { get; init; }
            public required System.Windows.Controls.Image StatusIcon { get; init; }
            public required RotateTransform StatusRotation { get; init; }
            public required string IconId { get; init; }
            public required string ActionLabel { get; init; }
            public required string DoneLabel { get; init; }
            public required string PendingTooltip { get; init; }
            public required string DoneTooltip { get; init; }
            public required AfterCapturePillTiming FinalTiming { get; init; }
        }

        public RegionOverlayForm.ConfirmCommitAction SelectedAction { get; private set; } = RegionOverlayForm.ConfirmCommitAction.Default;

        public CapturePreviewDialog(
            Bitmap bitmap,
            SettingsService settingsService,
            System.Drawing.Point? targetMonitorPoint = null,
            string? savedFilePath = null)
        {
            _capturedBitmap = bitmap;
            _settingsService = settingsService;
            _savedFilePath = string.IsNullOrWhiteSpace(savedFilePath) ? null : savedFilePath;
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
                CancelPillCompletionSimulation();
                StopAutoCloseCountdown(resetProgress: true);
                CancelZoomHideTimer();
                StopPrimaryButtonSpin();
            };
            // Pause the auto-close countdown only while the pointer is over the actions
            // column — hovering the image preview no longer holds the dialog open.
            ActionsPanel.MouseEnter += (_, _) => OnActionsPanelMouseEnter();
            ActionsPanel.MouseLeave += (_, _) => OnActionsPanelMouseLeave();
            // Hovering "Processing" fast-forwards the pill simulation to its final state,
            // so the user about to click never has to wait out the choreography.
            CancelBtn.MouseEnter += (_, _) => FinishPillSimulationImmediately();

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
            ApplyZoom();
            ZoomViewport.SizeChanged += (_, _) =>
            {
                // Stretch="Uniform" letterboxes; force re-measure so the canvas stays centered
                // and the zoom-controls stay anchored correctly after a window resize.
                ApplyZoom();
            };
            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            _lastOutcomeState = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            _lastTimeoutSeconds = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            // Auto-close countdown starts when the dialog becomes visible (see CenterOnOpenMonitor).
            // Starting here animates on a zero-width bar while Opacity=0 — first capture looked frozen.
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
            PrintText.Text = LocalizationService.Translate("Print");
            PrintBtn.ToolTip = LocalizationService.Translate("Print this capture.");
            DeleteText.Text = LocalizationService.Translate("Delete capture");
            DeleteBtn.ToolTip = LocalizationService.Translate(
                "The saved file will be permanently deleted from disk and removed from the Gallery.");
            MoreText.Text = LocalizationService.Translate("More");
            MoreBtn.ToolTip = LocalizationService.Translate("More");
            NoAutomaticActionsLabel.Text = LocalizationService.Translate("None");
            ZoomOutBtn.ToolTip = LocalizationService.Translate("Zoom out");
            ZoomInBtn.ToolTip = LocalizationService.Translate("Zoom in");
            ZoomFitBtn.ToolTip = LocalizationService.Translate("Fit to window");
            ZoomLevelText.ToolTip = LocalizationService.Translate("Click for actual size (100%)");
            UpdateContinueOrExitButton();
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
                // Pills first: if a completion simulation starts, the countdown waits for it
                // (InitAutoCloseCountdown no-ops while _pillSimRunning and is re-run on finish).
                BeginDeferredPillCompletionSimulation();
                InitAutoCloseCountdown();
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
            PrintIcon.Source = FluentIcons.RenderWpf("print", primaryIconColor, 13, active: true);
            DeleteIcon.Source = FluentIcons.RenderWpf("trash", DeleteAccentRed, 13, active: true);
            ShareIcon.Source = FluentIcons.RenderWpf("share", primaryIconColor, 14, active: true);
            GalleryIcon.Source = FluentIcons.RenderWpf("history", primaryIconColor, 14, active: true);
            MoreIcon.Source = FluentIcons.RenderWpf("more", primaryIconColor, 13, active: true);
            EditSettingsBtnIcon.Source = FluentIcons.RenderWpf("gear", secondaryIconColor, 14, active: true);
            ZoomOutIcon.Source = FluentIcons.RenderWpf("zoomOut", secondaryIconColor, 12, active: true);
            ZoomInIcon.Source = FluentIcons.RenderWpf("zoomIn", secondaryIconColor, 12, active: true);
            ZoomFitIcon.Source = FluentIcons.RenderWpf("zoomFit", secondaryIconColor, 12, active: true);
        }

        private System.Drawing.Color GetPrimaryButtonIconColor()
        {
            // Quiet Done CTA uses primary text on a dark fill (gradient line carries the accent).
            var c = Theme.TextPrimary;
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        /// <summary>Secondary/accent color used by the pills for their pending spinner.
        /// Matches the tone of the chips so the "Processing" button loader matches them.</summary>
        private System.Drawing.Color GetPrimaryButtonSpinnerColor()
            => System.Drawing.Color.FromArgb(255, 0, 162, 255);

        private void CapturePreviewDialog_Activated(object? sender, EventArgs e)
        {
            RefreshLiveSettings();
            // Re-arm countdown only when the pointer is NOT over the actions column.
            if (_autoCloseArmed && !_isPinned && !_isHovered && !_pillSimRunning)
                ResumeAutoCloseAfterHover();
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
                InitAutoCloseCountdown();
        }

        private void InitAutoCloseCountdown()
        {
            StopAutoCloseCountdown(resetProgress: true);

            int timeoutSec = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            // While the pill simulation runs, the button reads "Processing" — a visible
            // countdown would contradict it. OnPillSimulationStepCompleted re-arms us.
            if (timeoutSec <= 0 || _isPinned || _pillSimRunning)
            {
                CountdownRingHost.Visibility = Visibility.Collapsed;
                ResetCountdownRingVisual();
                return;
            }

            _autoCloseDurationSeconds = timeoutSec;
            _autoCloseArmed = true;
            CountdownRingHost.Visibility = Visibility.Visible;
            CountdownRingHost.BeginAnimation(OpacityProperty, null);
            CountdownRingHost.Opacity = _isHovered ? 0.0 : 1.0;
            UpdateDoneCountdownText(timeoutSec);
            UpdateCountdownRingArc(1.0);

            if (_isHovered)
            {
                // Pointer already inside the actions column: stay full, start on mouse leave.
                return;
            }

            StartCountdownAnimation();
        }

        private const double CountdownRingSize = 20.0;
        private const double CountdownRingStrokeThickness = 2.0;

        /// <summary>
        /// Fraction of auto-close time remaining (1→0). A single DoubleAnimation on this
        /// property is the countdown clock: the change callback redraws the ring arc and
        /// the seconds numeral, and Completed fires the auto-close. No DispatcherTimer,
        /// so the numeral can never drift from the visual.
        /// </summary>
        public static readonly DependencyProperty CountdownFractionProperty =
            DependencyProperty.Register(nameof(CountdownFraction), typeof(double), typeof(CapturePreviewDialog),
                new PropertyMetadata(1.0, (d, e) => ((CapturePreviewDialog)d).OnCountdownFractionChanged((double)e.NewValue)));

        public double CountdownFraction
        {
            get => (double)GetValue(CountdownFractionProperty);
            set => SetValue(CountdownFractionProperty, value);
        }

        private void OnCountdownFractionChanged(double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            UpdateCountdownRingArc(fraction);
            if (_autoCloseArmed && _autoCloseDurationSeconds > 0)
                ShowDoneCountdownSeconds(fraction * _autoCloseDurationSeconds);
        }

        private void StartCountdownAnimation()
        {
            if (_autoCloseDurationSeconds <= 0)
                return;

            int epoch = ++_countdownEpoch;

            // Deliberately not Motion.Sec: the countdown is functional timing, not a
            // decorative transition. With reduced motion (Motion.Disabled → zero-duration
            // animations) the dialog must still stay open the configured seconds.
            var animation = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(_autoCloseDurationSeconds))
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            animation.Completed += (_, _) =>
            {
                // BeginAnimation(prop, null) detaches a running animation from the property,
                // but its clock still completes on the ORIGINAL schedule and raises Completed.
                // The epoch guard makes those stale clocks harmless (hover/stop bumps it).
                if (epoch != _countdownEpoch)
                    return;
                if (!_autoCloseArmed || _isClosing || _isPinned || _isHovered)
                    return;
                PerformAutoClose();
            };
            BeginAnimation(CountdownFractionProperty, animation);
        }

        /// <summary>Redraws the ring arc. The remaining arc always ends at 12 o'clock and the
        /// gap grows clockwise from the top as time runs out (skip-ad style ring). Geometry is
        /// computed from fixed constants, so it never depends on layout having run.</summary>
        private void UpdateCountdownRingArc(double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            double radius = (CountdownRingSize - CountdownRingStrokeThickness) / 2.0;
            var center = new System.Windows.Point(CountdownRingSize / 2.0, CountdownRingSize / 2.0);

            if (fraction <= 0.001)
            {
                CountdownRingArc.Data = null;
                return;
            }

            if (fraction >= 0.999)
            {
                // ArcSegment cannot express a full 360° sweep; use the ellipse directly.
                var full = new EllipseGeometry(center, radius, radius);
                full.Freeze();
                CountdownRingArc.Data = full;
                return;
            }

            // Angle measured clockwise from 12 o'clock.
            double startAngle = (1.0 - fraction) * 2.0 * Math.PI;
            var start = new System.Windows.Point(
                center.X + radius * Math.Sin(startAngle),
                center.Y - radius * Math.Cos(startAngle));
            var end = new System.Windows.Point(center.X, center.Y - radius);

            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment(
                end,
                new System.Windows.Size(radius, radius),
                0,
                isLargeArc: fraction > 0.5,
                SweepDirection.Clockwise,
                isStroked: true));
            var geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            CountdownRingArc.Data = geometry;
        }

        private void BeginProgressRefillForHover()
        {
            if (!_autoCloseArmed || _isPinned)
                return;

            // Invalidate the running clock first — its Completed would otherwise still
            // fire on the old schedule and close the dialog despite the visual reset.
            _countdownEpoch++;
            // Pause: removing the animation snaps the fraction back to its base value (1.0),
            // so the ring pops to full — hovering re-grants the full timeout.
            BeginAnimation(CountdownFractionProperty, null);
        }

        private void ResumeAutoCloseAfterHover()
        {
            if (_isPinned || !_autoCloseArmed || _autoCloseDurationSeconds <= 0)
                return;

            ShowDoneCountdownSeconds(_autoCloseDurationSeconds);
            FadeCountdownRing(1.0);
            StartCountdownAnimation();
        }

        private void StopAutoCloseCountdown(bool resetProgress)
        {
            _countdownEpoch++;
            _autoCloseArmed = false;
            BeginAnimation(CountdownFractionProperty, null);
            if (resetProgress)
                UpdateCountdownRingArc(1.0);
        }

        private void UpdateDoneCountdownText(int timeoutSeconds)
        {
            _lastCountdownSecondText = timeoutSeconds;
            AutoCloseCountdownText.Text = timeoutSeconds.ToString();
        }

        private void ShowDoneCountdownSeconds(double remainingSeconds)
        {
            int second = Math.Max(1, (int)Math.Ceiling(remainingSeconds));
            if (second == _lastCountdownSecondText) return;
            _lastCountdownSecondText = second;
            AutoCloseCountdownText.Text = second.ToString();
        }

        /// <summary>Fades the whole countdown ring (arc + numeral) in or out. Only Opacity
        /// is animated — the host keeps its layout slot, so the button text never shifts.
        /// Animating from the current value makes rapid hover enter/leave reverse smoothly.</summary>
        private void FadeCountdownRing(double targetOpacity)
        {
            var fade = new DoubleAnimation(targetOpacity, Motion.Ms(CountdownFadeMs))
            {
                EasingFunction = Motion.Ease(targetOpacity > 0 ? Motion.SmoothOut : Motion.SmoothIn)
            };
            CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void ResetCountdownRingVisual()
        {
            CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, null);
            CountdownRingHost.Opacity = 1.0;
            _lastCountdownSecondText = -1;
        }

        private void OnActionsPanelMouseEnter()
        {
            _isHovered = true;
            BeginProgressRefillForHover();
            // Hide the whole ring while paused — it only reads while actively counting down.
            if (!_isPinned && _autoCloseArmed)
                FadeCountdownRing(0.0);
        }

        private void OnActionsPanelMouseLeave()
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

            StopAutoCloseCountdown(resetProgress: false);
            CountdownRingHost.Visibility = Visibility.Collapsed;
            ResetCountdownRingVisual();
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
            CancelAutoCloseOnInteraction();
            if (Application.Current is App app)
            {
                app.ShowSettings("confirm-pills");
            }
        }

        private void MoreBtn_Click(object sender, RoutedEventArgs e)
        {
            CancelAutoCloseOnInteraction();
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
            if (CanOpenSavedFileInFolder())
            {
                var cPrimary = Theme.TextPrimary;
                var folderIconColor = System.Drawing.Color.FromArgb(cPrimary.A, cPrimary.R, cPrimary.G, cPrimary.B);
                menu.Items.Add(CreateMoreMenuItem(
                    LocalizationService.Translate("Open in folder"),
                    FluentIcons.RenderWpf("folder", folderIconColor, 14, active: true),
                    OpenSavedFileInFolder,
                    LocalizationService.Translate("Show this file in File Explorer.")));
            }

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

        private bool CanOpenSavedFileInFolder()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            return state.EffectiveSave
                && !string.IsNullOrWhiteSpace(_savedFilePath)
                && File.Exists(_savedFilePath);
        }

        private bool CanDeleteSavedCapture() =>
            !string.IsNullOrWhiteSpace(_savedFilePath) && File.Exists(_savedFilePath);

        private void OpenSavedFileInFolder()
        {
            if (!CanOpenSavedFileInFolder() || _savedFilePath is null)
                return;

            CancelAutoCloseOnInteraction();

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{Path.GetFullPath(_savedFilePath)}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ToastWindow.ShowError(
                    "Open failed",
                    "CyberSnap could not open the saved file location. The file is still saved; open it from History or try again.\n"
                    + ex.Message,
                    _savedFilePath);
            }
        }

        private MenuItem CreateMoreMenuItem(string label, ImageSource? icon, Action onClick, string? toolTip = null)
        {
            var item = new MenuItem
            {
                Header = label,
                Foreground = Theme.Brush(Theme.TextPrimary),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(10, 6, 14, 6),
                FontSize = 12,
                ToolTip = toolTip
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
            Resources["ThemeAccentSubtleBrush"] = Theme.Brush(Theme.AccentSubtle);
            Resources["ThemeAccentHoverBrush"] = Theme.Brush(Theme.AccentHover);
            Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
            Resources["ThemePrimaryButtonForegroundBrush"] = Theme.IsDark && !Theme.IsGray
                ? Theme.Brush(System.Windows.Media.Color.FromRgb(11, 18, 32))
                : Theme.Brush(System.Windows.Media.Colors.White);

            CheckerboardHost.Background = Theme.CreateCheckerboardBrush();
            PreviewFrame.Background = Theme.Brush(Theme.BgSecondary);
            // Countdown ring brushes bind to DynamicResource theme brushes set above.

            UpdateIcons();
            // Pills are owned by PopulateAfterCapturePills (constructor / live settings).
            // Rebuilding here would cancel an in-flight completion simulation.
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
        }

        private void PopulateAfterCapturePills()
        {
            if (AfterCapturePillsPanel == null || _settingsService?.Settings == null) return;
            CancelPillCompletionSimulation();
            AfterCapturePillsPanel.Children.Clear();
            int simToken = _pillSimToken;

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            var settings = _settingsService.Settings;

            var rows = new List<(AfterCapturePillKind Pill, AfterCapturePillTiming Timing, string IconId)>();
            foreach (var pill in AfterCaptureOutcomeModel.AllPills)
            {
                if (!AfterCaptureOutcomeModel.IsActive(state, pill))
                    continue;

                string iconId = pill switch
                {
                    AfterCapturePillKind.Save => "save",
                    AfterCapturePillKind.Clipboard => "copy",
                    AfterCapturePillKind.Preview => "eye",
                    AfterCapturePillKind.Editor => "draw",
                    AfterCapturePillKind.SystemViewer => "folder",
                    AfterCapturePillKind.Share => "share",
                    AfterCapturePillKind.Notification => "info",
                    _ => "gear"
                };

                rows.Add((pill, AfterCaptureOutcomeModel.GetPreviewTiming(pill, settings), iconId));
            }

            // Preview first, then other actives. Preview is already fulfilled by this dialog —
            // show it Done immediately (no pending/preloader). Other Done pills animate in.
            var allChips = new List<AfterCapturePillChip>();
            var chipsToSimulate = new List<AfterCapturePillChip>();
            foreach (var row in rows.OrderBy(r => AfterCaptureOutcomeModel.FlowDisplayOrder(r.Pill)))
            {
                string actionLabel = LocalizationService.Translate(AfterCaptureOutcomeModel.LabelKey(row.Pill));
                string doneLabel = LocalizationService.Translate(AfterCaptureOutcomeModel.DoneLabelKey(row.Pill));
                string baseTip = LocalizationService.Translate(AfterCaptureOutcomeModel.TooltipKey(row.Pill));
                string pendingTip = string.IsNullOrWhiteSpace(baseTip)
                    ? LocalizationService.Translate("Runs when you continue")
                    : $"{baseTip}\n{LocalizationService.Translate("Runs when you continue")}";
                string doneTip = string.IsNullOrWhiteSpace(baseTip)
                    ? LocalizationService.Translate("Already completed")
                    : $"{baseTip}\n{LocalizationService.Translate("Already completed")}";

                var chip = CreateAfterCapturePillChip(
                    row.IconId,
                    actionLabel,
                    doneLabel,
                    pendingTip,
                    doneTip,
                    row.Timing);
                AfterCapturePillsPanel.Children.Add(chip.Root);
                allChips.Add(chip);

                if (row.Pill == AfterCapturePillKind.Preview)
                {
                    // Already inside the preview — mark complete with no simulation.
                    ApplyPillVisualState(chip, PillVisualState.Done);
                }
                else if (row.Timing == AfterCapturePillTiming.Done)
                {
                    chipsToSimulate.Add(chip);
                }
            }

            _activePillChips = allChips;
            NoAutomaticActionsLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (chipsToSimulate.Count == 0)
            {
                _pendingPillSimulation = null;
                return;
            }

            if (Motion.Disabled)
            {
                foreach (var chip in chipsToSimulate)
                    ApplyPillVisualState(chip, PillVisualState.Done);
                _pendingPillSimulation = null;
                return;
            }

            // Defer until the window is visible (Opacity=1 after centering). If already shown,
            // start immediately so live-settings rebuilds still animate.
            if (Opacity >= 1)
                BeginPillCompletionSimulation(chipsToSimulate, simToken);
            else
                _pendingPillSimulation = chipsToSimulate;
        }

        private void BeginDeferredPillCompletionSimulation()
        {
            if (_pendingPillSimulation is not { Count: > 0 } chips)
                return;

            _pendingPillSimulation = null;
            BeginPillCompletionSimulation(chips, _pillSimToken);
        }

        private void CancelPillCompletionSimulation()
        {
            _pillSimToken++;
            _pendingPillSimulation = null;
            _pillSimRunning = false;
            _pillSimsRemaining = 0;
            foreach (var timer in _pillSimTimers)
                timer.Stop();
            _pillSimTimers.Clear();

            if (_activePillChips == null)
                return;

            foreach (var chip in _activePillChips)
                StopPillStatusSpin(chip);
            _activePillChips = null;
        }

        private void BeginPillCompletionSimulation(IReadOnlyList<AfterCapturePillChip> chips, int simToken)
        {
            // One at a time: preloader → check, then the next chip — no overlapping spinners.
            _pillSimRunning = true;
            _pillSimsRemaining = chips.Count;
            // No auto-close while "Processing": the countdown starts when the simulation ends.
            StopAutoCloseCountdown(resetProgress: true);
            CountdownRingHost.Visibility = Visibility.Collapsed;
            ApplyPrimaryButtonProcessingState();

            int delayMs = PillSimInitialDelayMs;
            foreach (var chip in chips)
            {
                SchedulePillVisual(chip, PillVisualState.Working, delayMs, simToken);
                SchedulePillVisual(chip, PillVisualState.Done, delayMs + PillSimWorkMs, simToken, isSimCompletion: true);
                delayMs += PillSimWorkMs;
            }
        }

        private void SchedulePillVisual(
            AfterCapturePillChip chip,
            PillVisualState visual,
            int delayMs,
            int simToken,
            bool isSimCompletion = false)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
            _pillSimTimers.Add(timer);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _pillSimTimers.Remove(timer);
                if (simToken != _pillSimToken || _isClosing)
                    return;
                ApplyPillVisualState(chip, visual);
                if (isSimCompletion && visual == PillVisualState.Done)
                    OnPillSimulationStepCompleted();
            };
            timer.Start();
        }

        private void OnPillSimulationStepCompleted()
        {
            if (!_pillSimRunning)
                return;

            _pillSimsRemaining = Math.Max(0, _pillSimsRemaining - 1);
            if (_pillSimsRemaining > 0)
                return;

            _pillSimRunning = false;
            UpdateContinueOrExitButton();
            // Everything settled and the button reads "Done"/"Continue" — now the
            // auto-close countdown can start without contradicting "Processing".
            InitAutoCloseCountdown();
        }

        /// <summary>Fast-forwards the pill simulation to its final state (all Done-timing
        /// chips checked) and starts the auto-close countdown. Hovering the primary button
        /// while it reads "Processing" triggers this.</summary>
        private void FinishPillSimulationImmediately()
        {
            if (!_pillSimRunning || _isClosing)
                return;

            _pillSimToken++; // Invalidate every scheduled visual step.
            foreach (var timer in _pillSimTimers)
                timer.Stop();
            _pillSimTimers.Clear();
            _pillSimRunning = false;
            _pillSimsRemaining = 0;

            if (_activePillChips != null)
            {
                foreach (var chip in _activePillChips)
                {
                    if (chip.FinalTiming == AfterCapturePillTiming.Done)
                        ApplyPillVisualState(chip, PillVisualState.Done);
                }
            }

            UpdateContinueOrExitButton();
            InitAutoCloseCountdown();
        }

        private void ApplyPrimaryButtonProcessingState()
        {
            CancelText.Text = LocalizationService.Translate("Processing");
            CancelBtn.ToolTip = null;

            // Reuse the pills' spinner ring (blue, 0/162/255) and make it spin,
            // so the Processing state reads identically to a running pill.
            var accent = GetPrimaryButtonSpinnerColor();
            CancelIcon.Source = RenderSpinnerRing(accent, 14);
            CancelIcon.Visibility = Visibility.Visible;
            StartPrimaryButtonSpin();
        }

        private void StartPrimaryButtonSpin()
        {
            var rotation = new System.Windows.Media.RotateTransform();
            CancelIcon.RenderTransform = rotation;
            CancelIcon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

            var spin = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = Motion.Sec(0.85),
                RepeatBehavior = RepeatBehavior.Forever
            };
            rotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
        }

        private void StopPrimaryButtonSpin()
        {
            if (CancelIcon.RenderTransform is System.Windows.Media.RotateTransform rotation)
            {
                rotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                rotation.Angle = 0;
                CancelIcon.RenderTransform = null;
            }
        }

        private AfterCapturePillChip CreateAfterCapturePillChip(
            string iconId,
            string actionLabel,
            string doneLabel,
            string pendingTooltip,
            string doneTooltip,
            AfterCapturePillTiming finalTiming)
        {
            // Row: [pill]  status — status glyph stays outside the chip.
            var row = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 8),
                LastChildFill = true
            };

            var statusRotation = new RotateTransform();
            var statusIcon = new System.Windows.Controls.Image
            {
                Width = 15,
                Height = 15,
                Margin = new Thickness(9, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = statusRotation,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
            };
            DockPanel.SetDock(statusIcon, Dock.Right);
            row.Children.Add(statusIcon);

            var border = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 6, 12, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };

            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 18
            };

            var leadingIcon = new System.Windows.Controls.Image
            {
                Width = 13,
                Height = 13,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = Theme.Brush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            stack.Children.Add(leadingIcon);
            stack.Children.Add(label);
            border.Child = stack;
            row.Children.Add(border);

            var chip = new AfterCapturePillChip
            {
                Root = row,
                ChipBorder = border,
                LeadingIcon = leadingIcon,
                Label = label,
                StatusIcon = statusIcon,
                StatusRotation = statusRotation,
                IconId = iconId,
                ActionLabel = actionLabel,
                DoneLabel = doneLabel,
                PendingTooltip = pendingTooltip,
                DoneTooltip = doneTooltip,
                FinalTiming = finalTiming
            };

            // Always start pending (blue); Done-timing chips animate to green afterward.
            ApplyPillVisualState(chip, PillVisualState.Pending);
            return chip;
        }

        private void ApplyPillVisualState(AfterCapturePillChip chip, PillVisualState visual)
        {
            StopPillStatusSpin(chip);

            var accent = visual == PillVisualState.Done ? PillDoneGreen : PillPendingBlue;
            chip.ChipBorder.Background = Theme.Brush(System.Windows.Media.Color.FromArgb(22, accent.R, accent.G, accent.B));
            chip.ChipBorder.BorderBrush = Theme.Brush(System.Windows.Media.Color.FromArgb(55, accent.R, accent.G, accent.B));
            chip.LeadingIcon.Source = FluentIcons.RenderWpf(chip.IconId, accent, 13, active: true);

            switch (visual)
            {
                case PillVisualState.Working:
                    chip.Label.Text = chip.ActionLabel;
                    chip.ChipBorder.ToolTip = chip.PendingTooltip;
                    chip.StatusIcon.Opacity = 1.0;
                    chip.StatusIcon.Source = RenderSpinnerRing(accent, 15);
                    chip.StatusIcon.ToolTip = chip.PendingTooltip;
                    StartPillStatusSpin(chip);
                    break;

                case PillVisualState.Done:
                    chip.Label.Text = chip.DoneLabel;
                    chip.ChipBorder.ToolTip = chip.DoneTooltip;
                    chip.StatusIcon.Opacity = 1.0;
                    chip.StatusIcon.Source = RenderCheckBadge(accent, 15);
                    chip.StatusIcon.ToolTip = LocalizationService.Translate("Already completed");
                    break;

                default:
                    chip.Label.Text = chip.ActionLabel;
                    chip.ChipBorder.ToolTip = chip.PendingTooltip;
                    chip.StatusIcon.Opacity = 0.95;
                    chip.StatusIcon.Source = RenderDoubleChevron(accent, 15);
                    chip.StatusIcon.ToolTip = LocalizationService.Translate("Runs when you continue");
                    break;
            }
        }

        /// <summary>Indeterminate progress ring: 3/4 circle arc with a gap and round caps that spins via StatusRotation.</summary>
        private static BitmapSource RenderSpinnerRing(System.Drawing.Color color, int pixelSize)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();

            return RenderVector(pixelSize, ctx =>
            {
                // Sweep 270° so the gap reads as the classic indeterminate spinner.
                var figure = new PathFigure { StartPoint = new System.Windows.Point(10, 2.4), IsClosed = false };
                figure.Segments.Add(new ArcSegment(
                    new System.Windows.Point(10, 17.6),
                    new System.Windows.Size(7.6, 7.6),
                    0,
                    isLargeArc: true,
                    SweepDirection.Clockwise,
                    isStroked: true));
                var geometry = new PathGeometry(new[] { figure });
                geometry.Freeze();

                ctx.DrawGeometry(null, new System.Windows.Media.Pen(brush, 2.4)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                }, geometry);
            });
        }

        /// <summary>Completed badge: circular disc with a centered check inside (Fluent-style).</summary>
        private static BitmapSource RenderCheckBadge(System.Drawing.Color color, int pixelSize)
        {
            var accentBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            accentBrush.Freeze();

            return RenderVector(pixelSize, ctx =>
            {
                ctx.DrawEllipse(accentBrush, null, new System.Windows.Point(10, 10), 9, 9);

                // Check mark: two-segment stroke (6.8,10.4 -> 9.4,13 -> 13.4,7.4).
                var check = new PathGeometry();
                var f = new PathFigure { StartPoint = new System.Windows.Point(6.8, 10.4), IsClosed = false };
                f.Segments.Add(new LineSegment(new System.Windows.Point(9.4, 13), true));
                f.Segments.Add(new LineSegment(new System.Windows.Point(13.4, 7.4), true));
                check.Figures.Add(f);
                check.Freeze();

                ctx.DrawGeometry(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.9)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                }, check);
            });
        }

        /// <summary>Pending action marker: right-facing double chevron, clearly distinct from the single arrow.</summary>
        private static BitmapSource RenderDoubleChevron(System.Drawing.Color color, int pixelSize)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();

            return RenderVector(pixelSize, ctx =>
            {
                // Two right-facing strokes: (8,5)->(12,10)->(8,15) and (4,5)->(8,10)->(4,15).
                var doubleChevron = new PathGeometry();
                foreach (double x in new[] { 7.5, 3.5 })
                {
                    var f = new PathFigure { StartPoint = new System.Windows.Point(x, 5), IsClosed = false };
                    f.Segments.Add(new LineSegment(new System.Windows.Point(x + 4, 10), true));
                    f.Segments.Add(new LineSegment(new System.Windows.Point(x, 15), true));
                    doubleChevron.Figures.Add(f);
                }
                doubleChevron.Freeze();

                ctx.DrawGeometry(null, new System.Windows.Media.Pen(brush, 1.9)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                }, doubleChevron);
            });
        }

        /// <summary>Draws 20x20-viewbox vector content and rasterizes it at the requested pixel size.</summary>
        private static BitmapSource RenderVector(int pixelSize, Action<DrawingContext> draw)
        {
            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
            {
                ctx.PushTransform(new ScaleTransform(pixelSize / 20.0, pixelSize / 20.0));
                draw(ctx);
            }

            var rtb = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static void StartPillStatusSpin(AfterCapturePillChip chip)
        {
            var spin = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = Motion.Sec(0.85),
                RepeatBehavior = RepeatBehavior.Forever
            };
            chip.StatusRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private static void StopPillStatusSpin(AfterCapturePillChip chip)
        {
            chip.StatusRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            chip.StatusRotation.Angle = 0;
        }

        // ── Zoom & pan implementation ───────────────────────────────────
        private void ApplyZoom()
        {
            if (PreviewImage.Source is not BitmapSource bmp) return;

            double availW = ZoomViewport.ViewportWidth;
            double availH = ZoomViewport.ViewportHeight;
            // Before first layout, ViewportWidth is 0 — bail and let the SizeChanged
            // handler (hooked in the constructor) call ApplyZoom() again.
            if (availW <= 0 || availH <= 0) return;

            if (_zoomToFit)
            {
                // Letterboxed "fit": canvas fills the viewport, Uniform scale shrinks the image
                // to the largest size that fits entirely. _currentZoom tracks the real scale
                // factor relative to bitmap pixels (may exceed 1.0 for small captures).
                ZoomCanvas.Width = availW;
                ZoomCanvas.Height = availH;
                PreviewImage.Width = availW;
                PreviewImage.Height = availH;
                _currentZoom = Math.Min(availW / bmp.Width, availH / bmp.Height);
            }
            else
            {
                // Absolute-size mode: the image is exactly _currentZoom × bitmap pixels.
                // When it exceeds the viewport the ScrollViewer enables pan (scrollbars hidden).
                double scaledW = _currentZoom * bmp.Width;
                double scaledH = _currentZoom * bmp.Height;
                ZoomCanvas.Width = Math.Max(availW, scaledW);
                ZoomCanvas.Height = Math.Max(availH, scaledH);
                PreviewImage.Width = scaledW;
                PreviewImage.Height = scaledH;
            }

            UpdateZoomLevelText();
            UpdateZoomCursor();
            UpdateZoomControlsVisibility();
        }

        private void UpdateZoomLevelText()
        {
            ZoomLevelText.Text = $"{(_currentZoom * 100):0}%";
        }

        private void UpdateZoomCursor()
        {
            // Hand cursor only when panning is actually possible.
            bool canPan = !_zoomToFit
                && (ZoomViewport.ScrollableWidth > 1 || ZoomViewport.ScrollableHeight > 1);
            if (!_isPanning)
                ZoomViewport.Cursor = canPan ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        }

        private void UpdateZoomControlsVisibility()
        {
            // Fade-in only when the pointer is inside the preview frame AND a bitmap is loaded;
            // otherwise fade out. When the pointer leaves, wait a short delay so moving across
            // the overlay itself doesn't cause flicker.
            bool shouldShow = _zoomPointerInside && PreviewImage.Source is BitmapSource;
            SetZoomOverlayVisibility(shouldShow);
        }

        private void SetZoomOverlayVisibility(bool visible)
        {
            double target = visible ? 1.0 : 0.0;
            ZoomControlsOverlay.IsHitTestVisible = visible;
            var fade = new DoubleAnimation(target, Motion.Ms(180))
            {
                EasingFunction = Motion.Ease(visible ? Motion.SmoothOut : Motion.SmoothIn)
            };
            ZoomControlsOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void CancelZoomHideTimer()
        {
            if (_zoomHideTimer != null)
            {
                _zoomHideTimer.Stop();
                _zoomHideTimer = null;
            }
        }

        private void ScheduleZoomControlsHide()
        {
            CancelZoomHideTimer();
            _zoomHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _zoomHideTimer.Tick += (_, _) =>
            {
                _zoomHideTimer!.Stop();
                _zoomHideTimer = null;
                SetZoomOverlayVisibility(false);
            };
            _zoomHideTimer.Start();
        }

        private void SetZoom(double newZoom)
        {
            newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);
            if (Math.Abs(_currentZoom - newZoom) < 0.001) return;

            _currentZoom = newZoom;
            _zoomToFit = false;
            ApplyZoom();
        }

        /// <summary>Applies a new (non-fit) zoom level, keeping the image point under
        /// <paramref name="viewportPos"/> visually stationary when possible.</summary>
        private void ZoomToFixedPos(System.Windows.Point viewportPos, double newZoom)
        {
            if (PreviewImage.Source is not BitmapSource bmp) return;

            newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);

            // Calculate rect of the displayed BitmapSource inside PreviewImage
            // right now (Stretch="Uniform", so it always letterboxes).
            double vpW = ZoomViewport.ViewportWidth;
            double vpH = ZoomViewport.ViewportHeight;
            if (vpW <= 0 || vpH <= 0) return;

            double oldScale = Math.Min(PreviewImage.ActualWidth / bmp.Width, PreviewImage.ActualHeight / bmp.Height);
            double contentW = bmp.Width * oldScale;
            double contentH = bmp.Height * oldScale;
            double padX = PreviewImage.ActualWidth > 0 ? Math.Max(0, (PreviewImage.ActualWidth - contentW) / 2) : 0;
            double padY = PreviewImage.ActualHeight > 0 ? Math.Max(0, (PreviewImage.ActualHeight - contentH) / 2) : 0;

            // Convert viewport position to ScrollViewer content coordinates.
            var ptInSv = ZoomViewport.TranslatePoint(viewportPos, this);
            double contentX = ZoomViewport.HorizontalOffset + ptInSv.X - ZoomCanvas.Margin.Left;
            double contentY = ZoomViewport.VerticalOffset + ptInSv.Y - ZoomCanvas.Margin.Top;

            // Clamp inside the displayed bitmap rect (ignoring letterbox padding).
            double relX = Math.Clamp((contentX - padX) / contentW, 0, 1);
            double relY = Math.Clamp((contentY - padY) / contentH, 0, 1);

            double oldZoom = _currentZoom;
            _currentZoom = newZoom;
            _zoomToFit = false;
            ApplyZoom();
            ZoomViewport.UpdateLayout();

            // When _zoomToFit is false, Stretch="Uniform" inside an exact-size
            // (bitmap*zoom) PreviewImage fills the whole element: no padding.
            if (Math.Abs(_currentZoom - oldZoom) < double.Epsilon)
                return;

            double newContentX = relX * (_currentZoom * bmp.Width);
            double newContentY = relY * (_currentZoom * bmp.Height);
            ZoomViewport.ScrollToHorizontalOffset(newContentX - ptInSv.X);
            ZoomViewport.ScrollToVerticalOffset(newContentY - ptInSv.Y);

            CancelAutoCloseOnInteraction();
        }

        private void ZoomToFitWindow()
        {
            _currentZoom = 1.0;
            _zoomToFit = true;
            ApplyZoom();
            CancelAutoCloseOnInteraction();
        }

        private void ZoomActualSize()
        {
            _currentZoom = 1.0;
            _zoomToFit = false;
            ApplyZoom();
            ZoomViewport.UpdateLayout();

            // Center the 1:1 image in the viewport.
            double offX = Math.Max(0, (ZoomViewport.ExtentWidth - ZoomViewport.ViewportWidth) / 2);
            double offY = Math.Max(0, (ZoomViewport.ExtentHeight - ZoomViewport.ViewportHeight) / 2);
            ZoomViewport.ScrollToHorizontalOffset(offX);
            ZoomViewport.ScrollToVerticalOffset(offY);
            CancelAutoCloseOnInteraction();
        }

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomToFixedPos(
                new System.Windows.Point(ZoomViewport.ViewportWidth / 2, ZoomViewport.ViewportHeight / 2),
                _currentZoom * ZoomStep);
        }

        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomToFixedPos(
                new System.Windows.Point(ZoomViewport.ViewportWidth / 2, ZoomViewport.ViewportHeight / 2),
                _currentZoom / ZoomStep);
        }

        private void ZoomFitBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomToFitWindow();
        }

        private void ZoomLevelText_Click(object sender, MouseButtonEventArgs e)
        {
            ZoomActualSize();
        }

        /// <summary>Preview/tunneling handler: intercepts the wheel before the ScrollViewer can
        /// consume it for vertical scrolling, and performs zoom instead.</summary>
        private void ZoomViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var posInViewport = e.GetPosition(ZoomViewport);
            double newZoom = e.Delta > 0
                ? _currentZoom * ZoomStep
                : _currentZoom / ZoomStep;
            ZoomToFixedPos(posInViewport, newZoom);
            e.Handled = true;
        }

        private void ZoomViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Pan only when zoomed in past "fit" (i.e., image larger than viewport).
            if (!_zoomToFit && _currentZoom > 0
                && (ZoomViewport.ScrollableWidth > 1 || ZoomViewport.ScrollableHeight > 1))
            {
                _isPanning = true;
                _panStart = e.GetPosition(ZoomViewport);
                _panStartHorizontalOffset = ZoomViewport.HorizontalOffset;
                _panStartVerticalOffset = ZoomViewport.VerticalOffset;
                ZoomViewport.CaptureMouse();
                ZoomViewport.Cursor = System.Windows.Input.Cursors.Hand;
                e.Handled = true;
            }
        }

        private void ZoomViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                ZoomViewport.ReleaseMouseCapture();
                UpdateZoomCursor();
                e.Handled = true;
            }
        }

        private void ZoomViewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(ZoomViewport);
                double dx = _panStart.X - pos.X;
                double dy = _panStart.Y - pos.Y;
                ZoomViewport.ScrollToHorizontalOffset(_panStartHorizontalOffset + dx);
                ZoomViewport.ScrollToVerticalOffset(_panStartVerticalOffset + dy);
                CancelAutoCloseOnInteraction();
                e.Handled = true;
            }
        }

        private void ZoomViewport_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _zoomPointerInside = true;
            CancelZoomHideTimer();
            UpdateZoomControlsVisibility();
        }

        private void ZoomViewport_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _zoomPointerInside = false;
            // Delay the fade-out so moving briefly toward the overlay doesn't flicker.
            ScheduleZoomControlsHide();
        }

        /// <summary>Fully cancels the auto-close countdown after a user interaction
        /// (zoom, pan, or any action button). Unlike the hover-over-actions-panel pause,
        /// this stops the countdown entirely.</summary>
        private void CancelAutoCloseOnInteraction()
        {
            StopAutoCloseCountdown(resetProgress: true);
            CountdownRingHost.Visibility = Visibility.Collapsed;
            ResetCountdownRingVisual();
        }

        private void UpdateContinueOrExitButton()
        {
            if (_pillSimRunning)
            {
                ApplyPrimaryButtonProcessingState();
                return;
            }

            StopPrimaryButtonSpin();

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            bool viewerOn = state.SystemViewer;
            bool editorOn = state.Destination == AfterCaptureDestination.Editor;
            bool continuesToSurface = viewerOn || editorOn;
            var iconColor = GetPrimaryButtonIconColor();

            // Same pending marker as action pills: confirm runs the remaining deferred actions.
            CancelIcon.Source = RenderDoubleChevron(iconColor, 15);
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

            // Delete only makes sense once the capture actually exists on disk.
            DeleteBtn.Visibility = CanDeleteSavedCapture() ? Visibility.Visible : Visibility.Collapsed;

            bool anyOptionalVisible =
                SaveBtn.Visibility == Visibility.Visible
                || CopyBtn.Visibility == Visibility.Visible
                || EditBtn.Visibility == Visibility.Visible
                || PrintBtn.Visibility == Visibility.Visible
                || DeleteBtn.Visibility == Visibility.Visible
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
            CancelAutoCloseOnInteraction();

            _isPinned = !_isPinned;
            TitleBar.IsPinActive = _isPinned;
            Topmost = _isPinned;

            if (_isPinned)
            {
                StopAutoCloseCountdown(resetProgress: true);
                CountdownRingHost.Visibility = Visibility.Collapsed;
                ResetCountdownRingVisual();
            }
            else
            {
                InitAutoCloseCountdown();
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

        private void PrintBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;

            // Interaction cancels the auto-close: printing means the user wants to act.
            CancelAutoCloseOnInteraction();

            // Hold the auto-close while the system print dialog is up; a fresh
            // countdown re-arms afterwards (InitAutoCloseCountdown handles pin/hover).
            StopAutoCloseCountdown(resetProgress: true);
            CountdownRingHost.Visibility = Visibility.Collapsed;

            try
            {
                var printDialog = new System.Windows.Controls.PrintDialog();
                if (printDialog.ShowDialog() == true && PreviewImage.Source is BitmapSource source)
                {
                    var visual = new DrawingVisual();
                    using (var ctx = visual.RenderOpen())
                    {
                        double areaW = printDialog.PrintableAreaWidth;
                        double areaH = printDialog.PrintableAreaHeight;
                        // Shrink to fit the printable area, never upscale small captures.
                        double scale = Math.Min(1.0, Math.Min(areaW / source.Width, areaH / source.Height));
                        double w = source.Width * scale;
                        double h = source.Height * scale;
                        ctx.DrawImage(source, new Rect((areaW - w) / 2.0, (areaH - h) / 2.0, w, h));
                    }
                    printDialog.PrintVisual(visual, "CyberSnap");
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowError(LocalizationService.Translate("Print failed"), ex.Message);
            }
            finally
            {
                if (!_isClosing)
                    InitAutoCloseCountdown();
            }
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing || !CanDeleteSavedCapture() || _savedFilePath is null)
                return;

            // Interaction cancels the auto-close while the confirmation is up.
            CancelAutoCloseOnInteraction();

            // Hold the auto-close while the confirmation is up.
            StopAutoCloseCountdown(resetProgress: true);
            CountdownRingHost.Visibility = Visibility.Collapsed;

            bool confirmed = ThemedConfirmDialog.Confirm(
                this,
                LocalizationService.Translate("Delete capture?"),
                LocalizationService.Translate(
                    "The saved file will be permanently deleted from disk and removed from the Gallery.")
                    + "\n\n" + _savedFilePath,
                LocalizationService.Translate("Delete"),
                LocalizationService.Translate("Cancel"));

            if (!confirmed)
            {
                InitAutoCloseCountdown();
                return;
            }

            if (!HistoryService.TryDeleteSavedCapture(_savedFilePath))
            {
                ToastWindow.ShowError(LocalizationService.Translate("Delete failed"), _savedFilePath);
                UpdateOptionalActionsAvailability();
                InitAutoCloseCountdown();
                return;
            }

            ToastWindow.Show(LocalizationService.Translate("Capture deleted"));
            // Discard: close without committing so deferred outcomes (save/share/viewer)
            // don't run against the file we just removed.
            _isClosing = true;
            DialogResult = false;
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
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.Add || e.Key == Key.OemPlus)
                {
                    ZoomInBtn_Click(ZoomInBtn, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
                {
                    ZoomOutBtn_Click(ZoomOutBtn, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    ZoomToFitWindow();
                    e.Handled = true;
                }
                else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    ZoomActualSize();
                    e.Handled = true;
                }
            }
        }
    }
}
