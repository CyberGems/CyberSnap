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
        /// <summary>Resume auto-close after the pointer stops moving over the window.</summary>
        private const int CursorIdleResumeMs = 650;
        private const int CountdownFadeMs = 220;
        /// <summary>Resting opacity for the Done CTA accent hairline (quiet, not shouty).</summary>
        private const double CtaBorderOpacityRest = 0.22;
        /// <summary>Peak opacity for the Done-border breathe — wide enough to read, still soft.</summary>
        private const double CtaBorderOpacityPeak = 0.58;
        private const double CtaBorderBreatheSeconds = 2.6;
        /// <summary>How far the Done chevron nudges right on hover (invite to proceed).</summary>
        private const double ChevronInviteSlidePx = 7;
        private const int ChevronInviteMs = 200;
        private const int PillSimInitialDelayMs = 200;
        private const int PillSimWorkMs = 1000;

        private readonly SettingsService _settingsService;
        private readonly Bitmap _capturedBitmap;
        private readonly System.Drawing.Point _targetMonitorPoint;
        private readonly string? _savedFilePath;
        private bool _isPinned = false;
        /// <summary>True while the countdown is paused because the cursor is moving over the window.</summary>
        private bool _countdownPausedForMotion;
        private DispatcherTimer? _cursorIdleTimer;
        private SolidColorBrush? _ctaBorderBrush;
        private bool _ctaBorderPulseActive;
        private bool _primaryButtonHovered;
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

        /// <summary>
        /// Result of the preview session, replacing the WPF DialogResult (this window is
        /// shown non-modally via Show() so other app windows — the floating widget — stay
        /// responsive). Read after <see cref="System.Windows.Window.Closed"/> fires; at
        /// that point this property is final.
        /// true = commit pending outcomes; false = discard; null = disposed by replacement
        /// (the user pressed the capture hotkey again while this preview was open).
        /// </summary>
        public bool? CommittedResult { get; private set; }

        /// <summary>Set by <see cref="CloseFromReplace"/> so the Closing ??= fallback keeps
        /// CommittedResult null (replacement) instead of converting it to a user cancel.</summary>
        private bool _replaced;

        /// <summary>
        /// Closes this preview because a newer capture is replacing it. No fade-out, no
        /// commit — just a clean close so the new preview can take over the active slot.
        /// </summary>
        public void CloseFromReplace()
        {
            if (_isClosing)
                return;
            _isClosing = true;
            _replaced = true;
            CommittedResult = null;
            Close();
        }

        private void CommitAndClose(bool result)
        {
            if (_isClosing)
                return;
            _isClosing = true;
            CommittedResult = result;
            Close();
        }

        /// <summary>True when the caller already wrote this capture to the clipboard
        /// (eager copy fired before the preview opened). Lets the owner skip a
        /// duplicate copy when the preview commits.</summary>
        public bool ClipboardAlreadyCopied { get; }

        public bool IsAutoCloseEnabled =>
            _settingsService.Settings.CapturePreviewTimeoutSeconds > 0;

        public void SetAutoCloseEnabled(bool enabled)
        {
            int current = _settingsService.Settings.CapturePreviewTimeoutSeconds;
            int next = enabled ? (current > 0 ? current : 20) : 0;
            if (current == next)
                return;

            _settingsService.Settings.CapturePreviewTimeoutSeconds = next;
            _settingsService.Save();
        }

        public CapturePreviewDialog(
            Bitmap bitmap,
            SettingsService settingsService,
            System.Drawing.Point? targetMonitorPoint = null,
            string? savedFilePath = null,
            bool clipboardAlreadyCopied = false)
        {
            _capturedBitmap = bitmap;
            _settingsService = settingsService;
            _savedFilePath = string.IsNullOrWhiteSpace(savedFilePath) ? null : savedFilePath;
            ClipboardAlreadyCopied = clipboardAlreadyCopied;
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
            Closing += (_, _) =>
            {
                // Any close path that did not go through CommitAndClose (Alt+F4, system X,
                // taskbar close) counts as discard — never run deferred outcomes. A replace-
                // close keeps CommittedResult null so App doesn't run a redundant reset.
                if (!_replaced)
                    CommittedResult ??= false;
            };
            Closed += (_, _) =>
            {
                SettingsService.SettingsChanged -= SettingsService_SettingsChanged;
                CancelPillCompletionSimulation();
                StopAutoCloseCountdown(resetProgress: true);
                CancelCursorIdleTimer();
                CancelZoomHideTimer();
                StopPrimaryButtonSpin();
                StopCtaBorderPulse();
            };
            // Pause auto-close only while the cursor is moving over this window; when it
            // stops (or leaves), the countdown resumes from the preserved remaining time.
            PreviewMouseMove += (_, _) => OnWindowCursorMoved();
            MouseLeave += (_, _) => OnWindowCursorLeft();
            // Hovering Done: hide the timer, nudge the chevron right, and (if still
            // "Processing") fast-forward the pill simulation so the CTA is ready to click.
            CancelBtn.MouseEnter += (_, _) => OnPrimaryButtonMouseEnter();
            CancelBtn.MouseLeave += (_, _) => OnPrimaryButtonMouseLeave();

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
                // Recalculate after the viewport changes so fit mode tracks the available canvas.
                ApplyZoom();
            };
            Loaded += (_, _) =>
            {
                // The first SizeChanged can occur before the preview is fully arranged (and
                // before ApplyLayoutMode has moved the actions panel). Run one final fit pass
                // after WPF completes the initial layout.
                Dispatcher.BeginInvoke(new Action(ApplyZoom), DispatcherPriority.ContextIdle);
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

        // Suffix "(<shortcut>)" appended to a translated tooltip body so hotkeys are
        // discoverable on hover without taking up label space.
        private static string WithHotkeyHint(string text, string shortcut)
            => string.IsNullOrWhiteSpace(text) ? $"({shortcut})" : $"{text} ({shortcut})";

        /// <summary>
        /// Anchor a control's ToolTip above the control itself instead of WPF's default
        /// cursor-relative placement (which lands under the pointer, can overflow the
        /// window on long texts, and feels random on stacked activity buttons).
        /// PlacementTarget = the element, so the tip anchors to the control, not the mouse.
        /// </summary>
        private static void ApplyTooltipPlacement(FrameworkElement element)
        {
            ToolTipService.SetPlacement(element, System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetPlacementTarget(element, element);
            ToolTipService.SetVerticalOffset(element, -6);
        }

        private void ApplyLocalizedLabels()
        {
            var lang = _settingsService.Settings.InterfaceLanguage;
            Helpers.WindowTitles.ApplyTaskbar(this, Helpers.WindowTitles.Preview, lang);
            TitleBar.Title = LocalizationService.Translate(lang, Helpers.WindowTitles.Preview);
            TitleBar.CloseToolTip = WithHotkeyHint(
                LocalizationService.Translate("Cancel and discard pending actions."),
                "Esc");
            AfterCaptureHeaderLabel.Text = LocalizationService.Translate("Active actions:");
            OptionalActionsHeaderLabel.Text = LocalizationService.Translate("You can also:");
            OptionalActionsHeaderLabel.ToolTip =
                LocalizationService.Translate("Actions already covered by automatic actions are listed above.");
            EditAfterCaptureSettingsBtn.ToolTip = LocalizationService.Translate("Configure automatic actions");
            SaveText.Text = LocalizationService.Translate("Save");
            CopyText.Text = LocalizationService.Translate("Copy");
            EditText.Text = LocalizationService.Translate("Edit");
            OpenViewerText.Text = LocalizationService.Translate("Open in viewer");
            PrintText.Text = LocalizationService.Translate("Print");
            PrintBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Print this capture."), "Ctrl+P");
            DeleteText.Text = LocalizationService.Translate("Delete");
            DeleteBtn.ToolTip = LocalizationService.Translate(
                "The saved file will be permanently deleted from disk and removed from the Gallery.");
            MoreText.Text = LocalizationService.Translate("More");
            MoreBtn.ToolTip = LocalizationService.Translate("More");
            NoAutomaticActionsLabel.Text = LocalizationService.Translate("None");
            ZoomOutBtn.ToolTip = LocalizationService.Translate("Zoom out");
            ZoomInBtn.ToolTip = LocalizationService.Translate("Zoom in");
            ZoomFitBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Fit to window"), "Ctrl+0");
            ZoomLevelBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Click for actual size (100%)"), "Ctrl+1");

            // Optional-action tooltips: previously Edit/Copy/Save had none, so hovering
            // them gave no feedback. Each also carries its hotkey as a discreet suffix.
            SaveBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Save a copy of the image"), "Ctrl+S");
            CopyBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Copy to clipboard"), "Ctrl+C");
            EditBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Open in the annotation editor"), "Ctrl+E");
            OpenViewerBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Open in system default viewer"), "Ctrl+O");

            // Tooltip placement: anchor each tip above its control. UpdateContinueOrExitButton
            // rewrites CancelBtn.ToolTip on state changes — SetPlacement survives that because
            // it's an attached property on the button, not tied to the tooltip content.
            ApplyTooltipPlacement(OptionalActionsHeaderLabel);
            ApplyTooltipPlacement(EditAfterCaptureSettingsBtn);
            ApplyTooltipPlacement(SaveBtn);
            ApplyTooltipPlacement(CopyBtn);
            ApplyTooltipPlacement(EditBtn);
            ApplyTooltipPlacement(OpenViewerBtn);
            ApplyTooltipPlacement(PrintBtn);
            ApplyTooltipPlacement(MoreBtn);
            ApplyTooltipPlacement(DeleteBtn);
            ApplyTooltipPlacement(CancelBtn);
            ApplyTooltipPlacement(ZoomOutBtn);
            ApplyTooltipPlacement(ZoomInBtn);
            ApplyTooltipPlacement(ZoomFitBtn);
            ApplyTooltipPlacement(ZoomLevelBtn);

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
                ActionsCol.Width = new GridLength(285);
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

            // Render at 2× display DIPs so 150% DPI + UiScale LayoutTransform stay sharp
            // (same pattern as Settings achievements / crisp toolbar glyphs).
            SetPreviewIcon(SaveIcon, "save", primaryIconColor, 13);
            SetPreviewIcon(CopyIcon, "copy", primaryIconColor, 13);
            SetPreviewIcon(EditIcon, "draw", primaryIconColor, 13);
            SetPreviewIcon(OpenViewerIcon, "eye", primaryIconColor, 13);
            SetPreviewIcon(PrintIcon, "print", primaryIconColor, 13);
            SetPreviewIcon(DeleteIcon, "trash", DeleteAccentRed, 13);
            SetPreviewIcon(ShareIcon, "share", primaryIconColor, 14);
            SetPreviewIcon(GalleryIcon, "history", primaryIconColor, 14);
            SetPreviewIcon(MoreIcon, "more", primaryIconColor, 13);
            SetPreviewIcon(EditSettingsBtnIcon, "gear", secondaryIconColor, 14);
            SetPreviewIcon(ZoomOutIcon, "zoomOut", secondaryIconColor, 12);
            SetPreviewIcon(ZoomInIcon, "zoomIn", secondaryIconColor, 12);
            SetPreviewIcon(ZoomFitIcon, "zoomFit", secondaryIconColor, 12);
        }

        private static void SetPreviewIcon(
            System.Windows.Controls.Image image,
            string iconId,
            System.Drawing.Color color,
            int displayDip)
        {
            image.Source = FluentIcons.RenderWpf(iconId, color, displayDip * 2, active: false);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        }

        /// <summary>Secondary/accent color used by the pills for their pending spinner.
        /// Matches the tone of the chips so the "Processing" button loader matches them.</summary>
        private System.Drawing.Color GetPrimaryButtonSpinnerColor()
            => System.Drawing.Color.FromArgb(255, 0, 162, 255);

        private void CapturePreviewDialog_Activated(object? sender, EventArgs e)
        {
            RefreshLiveSettings();
            // Re-arm countdown only when motion-pause is not holding it.
            if (_autoCloseArmed && !_isPinned && !_countdownPausedForMotion && !_pillSimRunning)
                ResumeAutoCloseAfterCursorIdle();
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
                // No timer for this session — collapse so the Done button doesn't reserve space.
                SetCountdownRingShown(false, keepLayoutSlot: false);
                ResetCountdownRingVisual();
                StopCtaBorderPulse();
                return;
            }

            _autoCloseDurationSeconds = timeoutSec;
            _autoCloseArmed = true;
            _countdownPausedForMotion = false;
            CancelCursorIdleTimer();
            SetCountdownRingShown(true, keepLayoutSlot: true);
            // Hovering Done already: keep the ring slot but hide it instantly (invite UX).
            if (_primaryButtonHovered)
            {
                CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, null);
                CountdownRingHost.Opacity = 0.0;
            }
            else
                EnsureCountdownRingVisible();
            UpdateDoneCountdownText(timeoutSec);
            UpdateCountdownRingArc(1.0);
            StartCtaBorderPulse();

            StartCountdownAnimation();
        }

        private const double CountdownRingSize = 24.0;
        private const double CountdownRingStrokeThickness = 2.2;

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

        private void StartCountdownAnimation(double startFraction = 1.0)
        {
            if (_autoCloseDurationSeconds <= 0)
                return;

            startFraction = Math.Clamp(startFraction, 0, 1);
            if (startFraction <= 0)
                return;

            int epoch = ++_countdownEpoch;

            // Deliberately not Motion.Sec: the countdown is functional timing, not a
            // decorative transition. With reduced motion (Motion.Disabled → zero-duration
            // animations) the dialog must still stay open the configured seconds.
            var animation = new DoubleAnimation(
                startFraction,
                0.0,
                TimeSpan.FromSeconds(_autoCloseDurationSeconds * startFraction))
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
                if (!_autoCloseArmed || _isClosing || _isPinned || _countdownPausedForMotion)
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

        private void ResumeAutoCloseAfterCursorIdle()
        {
            if (_isPinned || !_autoCloseArmed || _autoCloseDurationSeconds <= 0)
                return;

            _countdownEpoch++;
            BeginAnimation(CountdownFractionProperty, null);
            SetCurrentValue(CountdownFractionProperty, 1.0);
            UpdateCountdownRingArc(1.0);
            ShowDoneCountdownSeconds(_autoCloseDurationSeconds);
            FadeCountdownRing(_primaryButtonHovered ? 0.0 : 1.0);
            StartCountdownAnimation(1.0);
        }

        private void StopAutoCloseCountdown(bool resetProgress)
        {
            _countdownEpoch++;
            _autoCloseArmed = false;
            _countdownPausedForMotion = false;
            CancelCursorIdleTimer();
            StopCtaBorderPulse();
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

        private void EnsureCountdownRingVisible()
        {
            CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, null);
            CountdownRingHost.Opacity = _primaryButtonHovered ? 0.0 : 1.0;
        }

        /// <summary>Fades the countdown ring opacity. Layout slot is preserved.</summary>
        private void FadeCountdownRing(double targetOpacity)
        {
            if (targetOpacity <= 0.0 || _primaryButtonHovered)
            {
                var fadeOut = new DoubleAnimation(0.0, Motion.Ms(CountdownFadeMs))
                {
                    EasingFunction = Motion.Ease(Motion.SmoothIn)
                };
                CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                return;
            }

            var fadeIn = new DoubleAnimation(targetOpacity, Motion.Ms(CountdownFadeMs))
            {
                EasingFunction = Motion.Ease(Motion.SmoothOut)
            };
            CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        /// <summary>
        /// Shows or hides the countdown ring. When <paramref name="keepLayoutSlot"/> is true,
        /// uses Hidden (not Collapsed) so the Done button does not jump when the ring dismisses.
        /// </summary>
        private void SetCountdownRingShown(bool shown, bool keepLayoutSlot)
        {
            if (shown)
            {
                CountdownRingHost.Visibility = Visibility.Visible;
                return;
            }

            CountdownRingHost.BeginAnimation(UIElement.OpacityProperty, null);
            CountdownRingHost.Opacity = keepLayoutSlot ? 0.0 : 1.0;
            CountdownRingHost.Visibility = keepLayoutSlot ? Visibility.Hidden : Visibility.Collapsed;
        }

        private void ResetCountdownRingVisual()
        {
            EnsureCountdownRingVisible();
            _lastCountdownSecondText = -1;
        }

        private void OnWindowCursorMoved()
        {
            if (!_autoCloseArmed || _isPinned || _isClosing || _pillSimRunning)
                return;

            if (!_countdownPausedForMotion)
            {
                _countdownPausedForMotion = true;
                _countdownEpoch++;
                BeginAnimation(CountdownFractionProperty, null);
                SetCurrentValue(CountdownFractionProperty, 1.0);
                UpdateCountdownRingArc(1.0);
                ShowDoneCountdownSeconds(_autoCloseDurationSeconds);
                // Completely hide the timer ring while moving the cursor, matching button hover
                FadeCountdownRing(0.0);
            }

            ScheduleCursorIdleResume();
        }

        private void OnWindowCursorLeft()
        {
            if (!_countdownPausedForMotion)
            {
                CancelCursorIdleTimer();
                return;
            }

            // Cursor left the window: treat as idle and resume the (refilled) countdown.
            CancelCursorIdleTimer();
            _countdownPausedForMotion = false;
            if (_isPinned) return;
            ResumeAutoCloseAfterCursorIdle();
        }

        private void ScheduleCursorIdleResume()
        {
            if (_cursorIdleTimer == null)
            {
                _cursorIdleTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(CursorIdleResumeMs)
                };
                _cursorIdleTimer.Tick += (_, _) =>
                {
                    _cursorIdleTimer!.Stop();
                    if (!_countdownPausedForMotion || _isClosing || _isPinned || !_autoCloseArmed)
                        return;
                    _countdownPausedForMotion = false;
                    ResumeAutoCloseAfterCursorIdle();
                };
            }

            _cursorIdleTimer.Stop();
            _cursorIdleTimer.Interval = TimeSpan.FromMilliseconds(CursorIdleResumeMs);
            _cursorIdleTimer.Start();
        }

        private void CancelCursorIdleTimer()
        {
            if (_cursorIdleTimer == null)
                return;
            _cursorIdleTimer.Stop();
        }

        private void PerformAutoClose()
        {
            // Same outcome as Continue / Done, but fade out first (manual close stays instant).
            if (_isClosing)
                return;
            _isClosing = true;

            StopAutoCloseCountdown(resetProgress: false);
            SetCountdownRingShown(false, keepLayoutSlot: true);
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
                    CommittedResult = commit;
                    Close();
                };
                BeginAnimation(OpacityProperty, fadeOut);
            }
            catch
            {
                CommittedResult = commit;
                Close();
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
            CommitAndClose(ResolvePrimaryButtonCommit());
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
                    FluentIcons.RenderWpf("folder", folderIconColor, 28, active: false),
                    OpenSavedFileInFolder,
                    LocalizationService.Translate("Show this file in File Explorer.")));
            }

            if (!state.Share)
            {
                menu.Items.Add(CreateMoreMenuItem(
                    LocalizationService.Translate("Share"),
                    ShareIcon.Source,
                    () => ShareBtn_Click(ShareBtn, new RoutedEventArgs()),
                    LocalizationService.Translate("Share image in the cloud.")));
            }

            menu.Items.Add(CreateMoreMenuItem(
                LocalizationService.Translate("Gallery"),
                GalleryIcon.Source,
                () => GalleryBtn_Click(GalleryBtn, new RoutedEventArgs()),
                LocalizationService.Translate("Open the captures Gallery.")));

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
                var iconImage = new System.Windows.Controls.Image
                {
                    Source = icon,
                    Width = 14,
                    Height = 14
                };
                RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
                item.Icon = iconImage;
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

            // Live (unfrozen) accent hairline on the button only — ResourceDictionary freezes
            // Freezables, and animating a frozen brush crashes before the dialog can show.
            bool wasPulsing = _ctaBorderPulseActive;
            StopCtaBorderPulse(resetOpacity: false);
            var accent = Theme.Accent;
            Resources["ThemePrimaryCtaBorderBrush"] = Theme.Brush(
                System.Windows.Media.Color.FromArgb(
                    (byte)Math.Clamp((int)(CtaBorderOpacityRest * 255), 0, 255),
                    accent.R, accent.G, accent.B));
            _ctaBorderBrush = new SolidColorBrush(accent) { Opacity = CtaBorderOpacityRest };
            CancelBtn.BorderBrush = _ctaBorderBrush;
            if (wasPulsing || _autoCloseArmed)
                StartCtaBorderPulse();

            CheckerboardHost.Background = Theme.CreateCheckerboardBrush();
            PreviewFrame.Background = Theme.Brush(Theme.BgSecondary);
            // Countdown ring brushes bind to DynamicResource theme brushes set above.

            UpdateIcons();
            // Pills are owned by PopulateAfterCapturePills (constructor / live settings).
            // Rebuilding here would cancel an in-flight completion simulation.
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
        }

        /// <summary>
        /// Cheap "life" on the Done CTA: one looping Opacity animation on the accent
        /// SolidColorBrush. No DropShadow, no extra visuals — GPU cost is negligible.
        /// </summary>
        private void StartCtaBorderPulse()
        {
            if (_ctaBorderBrush is null)
                return;

            if (Motion.Disabled)
            {
                _ctaBorderBrush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
                _ctaBorderBrush.Opacity = CtaBorderOpacityRest;
                _ctaBorderPulseActive = false;
                return;
            }

            var breathe = new DoubleAnimation(
                CtaBorderOpacityRest,
                CtaBorderOpacityPeak,
                TimeSpan.FromSeconds(CtaBorderBreatheSeconds))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Ease(Motion.SmoothInOut)
            };
            _ctaBorderBrush.BeginAnimation(SolidColorBrush.OpacityProperty, breathe);
            _ctaBorderPulseActive = true;
        }

        private void StopCtaBorderPulse(bool resetOpacity = true)
        {
            if (_ctaBorderBrush is null)
            {
                _ctaBorderPulseActive = false;
                return;
            }

            _ctaBorderBrush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
            if (resetOpacity)
                _ctaBorderBrush.Opacity = CtaBorderOpacityRest;
            _ctaBorderPulseActive = false;
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
            SetCountdownRingShown(false, keepLayoutSlot: CountdownRingHost.Visibility != Visibility.Collapsed);
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

        private void OnPrimaryButtonMouseEnter()
        {
            FinishPillSimulationImmediately();
            SetPrimaryButtonInvite(true);
        }

        private void OnPrimaryButtonMouseLeave()
        {
            SetPrimaryButtonInvite(false);
        }

        /// <summary>
        /// Done/Continue hover invite: hide the timer set and nudge the trailing chevron
        /// further right — a soft cue to proceed.
        /// </summary>
        private void SetPrimaryButtonInvite(bool invite)
        {
            _primaryButtonHovered = invite;

            if (_autoCloseArmed && CountdownRingHost.Visibility == Visibility.Visible)
            {
                if (invite)
                {
                    FadeCountdownRing(0.0);
                }
                else if (!_isClosing)
                {
                    FadeCountdownRing(_countdownPausedForMotion ? 0.0 : 1.0);
                }
            }

            if (_pillSimRunning)
                return;

            AnimateChevronInvite(invite);
        }

        private void AnimateChevronInvite(bool invite)
        {
            double target = invite ? ChevronInviteSlidePx : 0;
            var anim = new DoubleAnimation(target, Motion.Ms(ChevronInviteMs))
            {
                EasingFunction = Motion.Ease(invite ? Motion.SoftOut : Motion.SmoothInOut)
            };
            CancelIconSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void ResetChevronInviteSlide()
        {
            CancelIconSlide.BeginAnimation(TranslateTransform.XProperty, null);
            CancelIconSlide.X = 0;
        }

        private void ApplyPrimaryButtonProcessingState()
        {
            CancelText.Text = LocalizationService.Translate("Processing");
            CancelBtn.ToolTip = null;

            ResetChevronInviteSlide();

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
                Margin = new Thickness(0, 0, 0, 6),
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
            RenderOptions.SetBitmapScalingMode(leadingIcon, BitmapScalingMode.HighQuality);

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

            // Anchor the pill's ToolTip above the chip (not under the cursor). Placement is
            // attached to the two tooltip hosts (border + status icon) so the rewritten text
            // in ApplyPillVisualState keeps the anchored placement.
            ApplyTooltipPlacement(border);
            ToolTipService.SetPlacement(statusIcon, System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetPlacementTarget(statusIcon, border);
            ToolTipService.SetVerticalOffset(statusIcon, -6);

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
            chip.LeadingIcon.Source = FluentIcons.RenderWpf(chip.IconId, accent, 26, active: false);

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

        private void GetPhysicalBaseDimensions(BitmapSource bmp, out double baseW, out double baseH)
        {
            double dpiScale = 1.0;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0)
                    dpiScale = dpi.DpiScaleX;
            }
            catch { }

            double uiScale = UiScale.Current > 0 ? UiScale.Current : 1.0;
            // 1 physical screen pixel = (1.0 / (dpiScale * uiScale)) DIPs inside RootBorder LayoutTransform.
            double physicalToDip = 1.0 / (dpiScale * uiScale);
            baseW = bmp.PixelWidth * physicalToDip;
            baseH = bmp.PixelHeight * physicalToDip;
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

            GetPhysicalBaseDimensions(bmp, out double baseW, out double baseH);

            if (_zoomToFit)
            {
                // Fit large images to the viewport, but never upscale small captures.
                // The canvas still fills the viewport so the image remains centered.
                ZoomCanvas.Width = availW;
                ZoomCanvas.Height = availH;
                _currentZoom = Math.Min(1.0, Math.Min(availW / baseW, availH / baseH));
                PreviewImage.Width = baseW * _currentZoom;
                PreviewImage.Height = baseH * _currentZoom;
            }
            else
            {
                // Absolute-size mode: at zoom=1.0, 1 physical capture pixel = 1 physical screen pixel.
                // When it exceeds the viewport the ScrollViewer enables pan (scrollbars hidden).
                double scaledW = _currentZoom * baseW;
                double scaledH = _currentZoom * baseH;
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

            GetPhysicalBaseDimensions(bmp, out double baseW, out double baseH);

            double oldScale = Math.Min(PreviewImage.ActualWidth / baseW, PreviewImage.ActualHeight / baseH);
            double contentW = baseW * oldScale;
            double contentH = baseH * oldScale;
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
            // (base*zoom) PreviewImage fills the whole element: no padding.
            if (Math.Abs(_currentZoom - oldZoom) < double.Epsilon)
                return;

            double newContentX = relX * (_currentZoom * baseW);
            double newContentY = relY * (_currentZoom * baseH);
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

        private void ZoomLevelBtn_Click(object sender, RoutedEventArgs e)
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
        /// (zoom, pan, or any action button). Unlike the cursor-motion pause,
        /// this stops the countdown entirely and hides the ring.</summary>
        private void CancelAutoCloseOnInteraction()
        {
            StopAutoCloseCountdown(resetProgress: true);
            // Hidden (not Collapsed): keep the layout slot so Done/Continue doesn't jump.
            SetCountdownRingShown(false, keepLayoutSlot: true);
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
            // Same pending marker as action pills: confirm runs the remaining deferred actions.
            CancelIcon.Source = RenderDoubleChevron(PillPendingBlue, 15);
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
                CancelBtn.ToolTip = WithHotkeyHint(
                    LocalizationService.Translate("Close this preview and continue with pending actions."),
                    "Enter");
            }
            else
            {
                CancelText.Text = LocalizationService.Translate("Done");
                ViewerHintBadge.Visibility = Visibility.Collapsed;
                CancelBtn.ToolTip = WithHotkeyHint(
                    LocalizationService.Translate("Close this preview and continue with pending actions."),
                    "Enter");
            }

            // Re-apply invite nudge if the cursor is already over Done (e.g. after
            // fast-forwarding "Processing" → Done while still hovered).
            if (_primaryButtonHovered)
                AnimateChevronInvite(true);
            else
                ResetChevronInviteSlide();
        }

        private void UpdateOptionalActionsAvailability()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);

            // Hide duplicates of automatic actions instead of leaving disabled ghosts.
            bool saveAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Save);
            bool copyAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Clipboard);
            bool editAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Editor);
            bool viewerAuto = AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.SystemViewer);

            SaveBtn.Visibility = saveAuto ? Visibility.Collapsed : Visibility.Visible;
            CopyBtn.Visibility = copyAuto ? Visibility.Collapsed : Visibility.Visible;
            EditBtn.Visibility = editAuto ? Visibility.Collapsed : Visibility.Visible;
            OpenViewerBtn.Visibility = viewerAuto ? Visibility.Collapsed : Visibility.Visible;

            SaveBtn.IsEnabled = !saveAuto;
            CopyBtn.IsEnabled = !copyAuto;
            EditBtn.IsEnabled = !editAuto;
            OpenViewerBtn.IsEnabled = !viewerAuto;

            // Delete only makes sense once the capture actually exists on disk.
            DeleteBtn.Visibility = CanDeleteSavedCapture() ? Visibility.Visible : Visibility.Collapsed;

            bool anyOptionalVisible =
                SaveBtn.Visibility == Visibility.Visible
                || CopyBtn.Visibility == Visibility.Visible
                || EditBtn.Visibility == Visibility.Visible
                || OpenViewerBtn.Visibility == Visibility.Visible
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
            CommitAndClose(false);
        }

        private void TitleBar_PinRequested(object sender, EventArgs e) => TogglePinned();

        private void TogglePinned()
        {
            CancelAutoCloseOnInteraction();

            _isPinned = !_isPinned;
            TitleBar.IsPinActive = _isPinned;
            Topmost = _isPinned;

            if (_isPinned)
            {
                StopAutoCloseCountdown(resetProgress: true);
                SetCountdownRingShown(false, keepLayoutSlot: true);
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
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
            CommitAndClose(true);
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Copy;
            CommitAndClose(true);
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Edit;
            CommitAndClose(true);
        }

        private void OpenViewerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Viewer;
            CommitAndClose(true);
        }

        private void ShareBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Share;
            CommitAndClose(true);
        }

        private void GalleryBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing)
                return;
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.History;
            CommitAndClose(true);
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
            SetCountdownRingShown(false, keepLayoutSlot: true);

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
            SetCountdownRingShown(false, keepLayoutSlot: true);

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
            CommitAndClose(false);
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
                CommitAndClose(false);
                e.Handled = true;
                return;
            }

            var mods = Keyboard.Modifiers;

            // Plain keys (no modifier) ─────────────────────────────────────
            if (mods == ModifierKeys.None)
            {
                if (e.Key == Key.Enter)
                {
                    CancelBtn_Click(CancelBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.P)                     // P — pin / unpin
                {
                    TogglePinned();
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+... ──────────────────────────────────────────────────────
            if (mods == ModifierKeys.Control)
            {
                if (e.Key == Key.Add || e.Key == Key.OemPlus)
                {
                    ZoomInBtn_Click(ZoomInBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
                {
                    ZoomOutBtn_Click(ZoomOutBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    ZoomToFitWindow();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    ZoomActualSize();
                    e.Handled = true;
                    return;
                }

                // Action shortcuts — same handlers the optional-action buttons call,
                // so enabled/disabled state is respected automatically.
                if (e.Key == Key.S && SaveBtn.IsEnabled && SaveBtn.IsVisible)         // Ctrl+S — save
                {
                    SaveBtn_Click(SaveBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.E && EditBtn.IsEnabled && EditBtn.IsVisible)         // Ctrl+E — edit
                {
                    EditBtn_Click(EditBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.O && OpenViewerBtn.IsEnabled && OpenViewerBtn.IsVisible) // Ctrl+O — open in viewer
                {
                    OpenViewerBtn_Click(OpenViewerBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.C && CopyBtn.IsEnabled && CopyBtn.IsVisible)         // Ctrl+C — copy to clipboard
                {
                    CopyBtn_Click(CopyBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.P && PrintBtn.IsEnabled && PrintBtn.IsVisible)       // Ctrl+P — print
                {
                    PrintBtn_Click(PrintBtn, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}
