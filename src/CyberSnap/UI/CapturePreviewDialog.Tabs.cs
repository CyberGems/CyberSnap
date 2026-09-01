using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using MenuItem = System.Windows.Controls.MenuItem;

namespace CyberSnap.UI
{
    public partial class CapturePreviewDialog
    {
        private const int SoftTabLimit = 15;

        private readonly List<PreviewSession> _sessions = new();
        private PreviewSession _active = null!;
        private PreviewSession? _lastClosed;
        private int _nextCaptureOrdinal = 1;
        private bool _tabLimitWarned;
        private bool _sessionsFinalized;

        internal event Action<PreviewSession, bool>? SessionCompleted;

        internal bool CanAcceptSessions =>
            !_isClosing && !_sessionsFinalized && _active is not null;

        private Bitmap _capturedBitmap => _active.Bitmap;

        private string? _savedFilePath
        {
            get => _active.SavedFilePath;
            set => _active.SavedFilePath = value;
        }

        private Bitmap? _scaledBitmap
        {
            get => _active.ScaledBitmap;
            set => _active.ScaledBitmap = value;
        }

        private int _scaleFactor
        {
            get => _active.ScaleFactor;
            set => _active.ScaleFactor = value;
        }

        private double _currentZoom
        {
            get => _active.Zoom;
            set => _active.Zoom = value;
        }

        private bool _zoomToFit
        {
            get => _active.ZoomToFit;
            set => _active.ZoomToFit = value;
        }

        private bool _didInitialContain
        {
            get => _active.DidInitialContain;
            set => _active.DidInitialContain = value;
        }

        private bool HasMultipleSessions => _sessions.Count > 1;

        private PreviewSession CreateSession(
            Bitmap bitmap,
            string? savedFilePath,
            bool clipboardAlreadyCopied,
            CaptureKind captureKind)
        {
            return new PreviewSession(
                bitmap,
                savedFilePath,
                clipboardAlreadyCopied,
                captureKind,
                _nextCaptureOrdinal++);
        }

        private void InitTabStrip()
        {
            PreviewTabStripHost.TabSelected += (_, index) => ActivateSessionAt(index);
            PreviewTabStripHost.TabCloseRequested += (_, index) => DiscardSessionAt(index);
            PreviewTabStripHost.TabContextMenuRequested += (_, index) =>
            {
                if (index >= 0 && index < _sessions.Count)
                    ShowCaptureContextMenu(_sessions[index]);
            };
            PreviewTabStripHost.BarContextMenuRequested += (_, _) =>
                ShowCaptureContextMenu(_active);
            UpdateTabStrip();
        }

        internal void AddSession(
            Bitmap bitmap,
            string? savedFilePath,
            bool clipboardAlreadyCopied,
            CaptureKind captureKind)
        {
            if (_isClosing || _sessionsFinalized)
                return;

            CaptureViewState();
            WarnIfManyTabs();
            var session = CreateSession(bitmap, savedFilePath, clipboardAlreadyCopied, captureKind);
            session.PreviewSource = BitmapPerf.ToBitmapSource(bitmap);
            _sessions.Add(session);
            _active = session;
            ApplySessionVisuals();
            StopAutoCloseCountdown(resetProgress: true);
            SetCountdownRingShown(false, keepLayoutSlot: false);
            ResetCountdownRingVisual();
            UpdateTabStrip();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            SoundService.PlayPreviewSound();
        }

        internal void HideForCapture()
        {
            Opacity = 0;
            Hide();
        }

        internal void RestoreAfterCapture()
        {
            if (!IsVisible)
                Show();
            if (Opacity < 1)
                Opacity = 1;
            Activate();
        }

        private void WarnIfManyTabs()
        {
            if (_sessions.Count < SoftTabLimit || _tabLimitWarned)
                return;
            _tabLimitWarned = true;
            ThemedConfirmDialog.Alert(
                this,
                LocalizationService.Translate("Capture Preview"),
                LocalizationService.Translate("Several captures are open. Close some tabs if the preview feels slow."),
                error: false);
        }

        private void ActivateSessionAt(int index)
        {
            if (index < 0 || index >= _sessions.Count)
                return;
            if (ReferenceEquals(_sessions[index], _active))
                return;
            CaptureViewState();
            _active = _sessions[index];
            ApplySessionVisuals();
            UpdateTabStrip();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
        }

        private void DiscardSessionAt(int index)
        {
            if (index < 0 || index >= _sessions.Count)
                return;
            RemoveSession(_sessions[index], commit: false);
        }

        private void CycleTab(int delta)
        {
            if (_sessions.Count <= 1)
                return;
            int current = _sessions.IndexOf(_active);
            if (current < 0)
                current = 0;
            int next = (current + delta) % _sessions.Count;
            if (next < 0)
                next += _sessions.Count;
            ActivateSessionAt(next);
        }

        private void CaptureViewState()
        {
            if (_active is null || ZoomViewport is null)
                return;
            _active.PanHorizontal = ZoomViewport.HorizontalOffset;
            _active.PanVertical = ZoomViewport.VerticalOffset;
            if (PreviewImage.Source != null)
                _active.PreviewSource = PreviewImage.Source;
        }

        private void ApplySessionVisuals()
        {
            var source = _active.PreviewSource
                ?? BitmapPerf.ToBitmapSource(_active.EffectiveBitmap);
            _active.PreviewSource = source;
            PreviewImage.Source = source;
            UpdateScaleControls();
            ApplyZoom();
            if (!_active.DidInitialContain)
                TryApplyInitialContain();
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyZoom();
                    ZoomViewport.ScrollToHorizontalOffset(_active.PanHorizontal);
                    ZoomViewport.ScrollToVerticalOffset(_active.PanVertical);
                }, DispatcherPriority.Loaded);
            }
        }

        private void UpdateTabStrip()
        {
            if (PreviewTabStripHost is null)
                return;

            bool show = HasMultipleSessions;
            PreviewTabStripHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                return;

            var tabs = new PreviewTabInfo[_sessions.Count];
            for (int i = 0; i < _sessions.Count; i++)
            {
                var session = _sessions[i];
                tabs[i] = new PreviewTabInfo(session.TabTitle, ReferenceEquals(session, _active));
            }
            PreviewTabStripHost.SetTabs(tabs);
        }

        private void CommitActiveSession()
        {
            RemoveSession(_active, commit: true);
        }

        private void DiscardActiveSession()
        {
            RemoveSession(_active, commit: false);
        }

        private void RemoveSession(PreviewSession session, bool commit)
        {
            if (_isClosing || _sessionsFinalized)
                return;

            CaptureViewState();
            int index = _sessions.IndexOf(session);
            if (index < 0)
                return;

            _sessions.RemoveAt(index);
            if (commit)
                RaiseSessionCompleted(session, true);
            else if (_sessions.Count == 0)
                RaiseSessionCompleted(session, false);
            else
                StashClosedSession(session);

            if (_sessions.Count == 0)
            {
                DiscardStashedSession();
                _sessionsFinalized = true;
                _isClosing = true;
                Close();
                return;
            }

            int next = Math.Min(index, _sessions.Count - 1);
            _active = _sessions[next];
            ApplySessionVisuals();
            UpdateTabStrip();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            if (!HasMultipleSessions)
                InitAutoCloseCountdown();
        }

        private void RequestPrimaryClose()
        {
            if (_isClosing)
                return;

            if (!HasMultipleSessions)
            {
                FinalizeSessions(ResolvePrimaryButtonCommit());
                _isClosing = true;
                Close();
                return;
            }

            if (!ConfirmCloseAll())
                return;

            FinalizeSessions(commit: true);
            _isClosing = true;
            Close();
        }

        private void RequestChromeClose()
        {
            if (_isClosing)
                return;

            if (!HasMultipleSessions)
            {
                FinalizeSessions(commit: false);
                _isClosing = true;
                Close();
                return;
            }

            if (!ConfirmCloseAll())
                return;

            FinalizeSessions(commit: true);
            _isClosing = true;
            Close();
        }

        private bool ConfirmCloseAll()
        {
            var settings = _settingsService.Settings;
            if (settings.PreviewSuppressCloseAllConfirm)
                return true;

            int count = _sessions.Count;
            bool confirmed = ThemedConfirmDialog.Confirm(
                this,
                LocalizationService.Translate("Close capture previews?"),
                string.Format(
                    LocalizationService.Translate(
                        "You have {0} captures in this window. Closing it will apply pending actions to each one."),
                    count),
                out bool dontShowAgain,
                primaryText: LocalizationService.Translate("Close"),
                secondaryText: LocalizationService.Translate("Cancel"),
                danger: false,
                iconId: "warning");

            if (dontShowAgain && Application.Current is App app)
                app.PersistPreviewSuppressCloseAllConfirm(true);

            return confirmed;
        }

        private void FinalizeSessions(bool commit)
        {
            if (_sessionsFinalized)
                return;
            _sessionsFinalized = true;
            var list = _sessions.ToArray();
            _sessions.Clear();
            foreach (var session in list)
                RaiseSessionCompleted(session, commit);
            DiscardStashedSession();
        }

        private void StashClosedSession(PreviewSession session)
        {
            if (_lastClosed != null && !ReferenceEquals(_lastClosed, session))
                RaiseSessionCompleted(_lastClosed, committed: false);
            _lastClosed = session;
        }

        private void DiscardStashedSession()
        {
            var stashed = _lastClosed;
            _lastClosed = null;
            if (stashed is null)
                return;
            RaiseSessionCompleted(stashed, committed: false);
        }

        private void ReopenLastClosedSession()
        {
            var session = _lastClosed;
            if (session is null || _isClosing || _sessionsFinalized)
                return;

            _lastClosed = null;
            CaptureViewState();
            _sessions.Add(session);
            _active = session;
            ApplySessionVisuals();
            StopAutoCloseCountdown(resetProgress: true);
            SetCountdownRingShown(false, keepLayoutSlot: false);
            ResetCountdownRingVisual();
            UpdateTabStrip();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            if (!HasMultipleSessions)
                InitAutoCloseCountdown();
        }

        private static string? SavedPathIfExists(PreviewSession session)
        {
            if (string.IsNullOrWhiteSpace(session.SavedFilePath) || !File.Exists(session.SavedFilePath))
                return null;
            return Path.GetFullPath(session.SavedFilePath);
        }

        private void CopyPathText(string text)
        {
            ClipboardService.CopyTextToClipboard(text);
            ToastWindow.Show(LocalizationService.Translate("Copied"), text);
        }

        private void OpenPathInFolder(string path)
        {
            CancelAutoCloseOnInteraction();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ToastWindow.ShowError(
                    "Open failed",
                    "CyberSnap could not open the saved file location. The file is still saved; open it from History or try again.\n"
                    + ex.Message,
                    path);
            }
        }

        private void ShowCaptureContextMenu(PreviewSession? session)
        {
            if (session is null || _isClosing || _sessionsFinalized)
                return;

            CancelAutoCloseOnInteraction();
            var menu = BuildCaptureContextMenu(session);
            if (menu.Items.Count == 0)
                return;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private ContextMenu BuildCaptureContextMenu(PreviewSession session)
        {
            var menu = new ContextMenu
            {
                Background = Theme.Brush(Theme.BgElevated),
                BorderBrush = Theme.Brush(Theme.BorderSubtle),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };

            menu.Items.Add(CreateTabMenuItem(
                "Close",
                "close",
                () =>
                {
                    int index = _sessions.IndexOf(session);
                    if (index >= 0)
                        DiscardSessionAt(index);
                    else if (!HasMultipleSessions)
                        RequestChromeClose();
                },
                danger: true));

            if (HasMultipleSessions)
            {
                menu.Items.Add(CreateTabMenuItem(
                    "Close all",
                    "close",
                    RequestPrimaryClose,
                    danger: true));
            }

            if (_lastClosed is not null)
            {
                menu.Items.Add(CreateTabMenuItem(
                    "Reopen last closed tab",
                    "undo",
                    ReopenLastClosedSession));
            }

            var savedPath = SavedPathIfExists(session);
            if (savedPath is not null)
            {
                menu.Items.Add(new Separator());
                var folder = Path.GetDirectoryName(savedPath);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    menu.Items.Add(CreateTabMenuItem(
                        "Copy location",
                        "folder",
                        () => CopyPathText(folder)));
                }
                menu.Items.Add(CreateTabMenuItem(
                    "Copy full name",
                    "copy",
                    () => CopyPathText(savedPath)));
                menu.Items.Add(CreateTabMenuItem(
                    "Open in folder",
                    "folder",
                    () => OpenPathInFolder(savedPath)));
            }

            return menu;
        }

        private MenuItem CreateTabMenuItem(string label, string iconId, Action onClick, bool danger = false)
        {
            var c = Theme.TextPrimary;
            var color = danger
                ? System.Drawing.Color.FromArgb(239, 68, 68)
                : System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            var icon = FluentIcons.RenderWpf(iconId, color, 28, active: false);
            return CreateMoreMenuItem(LocalizationService.Translate(label), icon, onClick);
        }

        private void RaiseSessionCompleted(PreviewSession session, bool committed)
        {
            session.DetachOwnedBitmaps();
            try
            {
                SessionCompleted?.Invoke(session, committed);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("preview.session-complete", ex);
            }
            session.Dispose();
        }

        private void HandleSystemClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_replaced || _sessionsFinalized)
                return;

            if (HasMultipleSessions)
            {
                if (!ConfirmCloseAll())
                {
                    e.Cancel = true;
                    return;
                }
                FinalizeSessions(commit: true);
                return;
            }

            FinalizeSessions(commit: false);
        }

        private bool TryHandleTabHotkeys(System.Windows.Input.KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Control && e.Key == Key.W)
            {
                if (HasMultipleSessions)
                    DiscardActiveSession();
                else
                    RequestChromeClose();
                e.Handled = true;
                return true;
            }

            if (mods == ModifierKeys.Control && e.Key == Key.Tab)
            {
                CycleTab(1);
                e.Handled = true;
                return true;
            }

            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Tab)
            {
                CycleTab(-1);
                e.Handled = true;
                return true;
            }

            return false;
        }
    }
}
