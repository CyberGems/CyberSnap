using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;
using Point = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CyberSnap.UI;

public partial class UpdateNoticeWindow : Window
{
    private UpdateNoticePhase _phase;
    private UpdateCheckResult? _release;
    private UpdateDownloadProgress _progress;
    private bool _cancelling;
    private string? _status;
    private Point? _anchor;

    public UpdateNoticeWindow()
    {
        InitializeComponent();
        AppLogo.Source = ThemedLogo.Square(24);
        var hwnd = IntPtr.Zero;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            CaptureWindowExclusion.Apply(hwnd);
        };
        Closed += (_, _) =>
        {
            if (hwnd != IntPtr.Zero) CaptureWindowExclusion.Unregister(hwnd);
        };
        SizeChanged += (_, _) => PositionAtMonitorEdge();
    }

    internal void Present(Point anchor, bool activate)
    {
        _anchor = anchor;
        var work = Screen.FromPoint(anchor).WorkingArea;
        var (scaleX, scaleY) = PopupWindowHelper.GetScaleForPoint(anchor);
        Width = Math.Min(480, Math.Max(250, work.Width / scaleX - 32));
        MaxHeight = Math.Max(150, work.Height / scaleY - 32);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
        }
        PositionAtMonitorEdge();
        Opacity = 1;
        if (activate)
        {
            Activate();
            if (LaterButton.IsEnabled) LaterButton.Focus();
        }
    }

    private void PositionAtMonitorEdge()
    {
        if (!IsVisible || _anchor is not { } anchor || ActualHeight <= 0) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var screen = Screen.FromPoint(anchor);
        var work = PopupWindowHelper.TryGetNativeMonitorInfo(screen, out _, out var nativeWork)
            ? nativeWork : screen.WorkingArea;
        var (scaleX, scaleY) = PopupWindowHelper.GetScaleForPoint(anchor);
        int gapX = (int)Math.Round(16 * scaleX);
        int gapY = (int)Math.Round(16 * scaleY);
        int left = Math.Max(work.Left + gapX, work.Right - gapX - (int)Math.Round(ActualWidth * scaleX));
        int top = Math.Max(work.Top + gapY, work.Bottom - gapY - (int)Math.Round(ActualHeight * scaleY));
        User32.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    internal void Render(UpdateCheckResult release, UpdateNoticePhase phase, UpdateDownloadProgress progress,
        bool cancelling, string? status)
    {
        _release = release;
        _phase = phase;
        _progress = progress;
        _cancelling = cancelling;
        _status = status;
        RefreshLocalization();
    }

    internal void RefreshLocalization()
    {
        if (_release is not { } release) return;

        bool available = _phase == UpdateNoticePhase.Available;
        bool downloading = _phase == UpdateNoticePhase.Downloading;
        bool installing = _phase == UpdateNoticePhase.Installing;
        Title = StageTitle.Text = T(_phase switch
        {
            UpdateNoticePhase.Downloading => _cancelling ? "Cancelling download..." : "Downloading update...",
            UpdateNoticePhase.Ready => "Update ready",
            UpdateNoticePhase.Installing => "Starting the installer...",
            _ => "Update available"
        });
        VersionText.Text = release.LatestVersionLabel + (release.PublishedAt is { } date
            ? $" · {date.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)}" : "");

        NotesTitle.Text = T("What's New");
        var highlights = UpdateService.PeekReleaseNotes(release.ReleaseNotes, LocalizationService.CurrentLanguageCode)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        NotesList.ItemsSource = highlights;
        NotesUnavailable.Text = T("Release notes are unavailable.");
        NotesUnavailable.Visibility = VisibleWhen(highlights.Length == 0);

        DownloadPanel.Visibility = VisibleWhen(downloading);
        if (downloading) RenderProgress(_progress);
        var status = _status ?? (available && release.Asset is null ? "No compatible package was found for this edition." : null);
        StatusText.Text = status is null ? "" : T(status);
        StatusText.Visibility = VisibleWhen(status is not null);

        GithubButton.Content = T("View on GitHub");
        GithubButton.Visibility = VisibleWhen(available);
        SkipButton.Content = T("Skip version");
        SkipButton.Visibility = VisibleWhen(available);
        LaterButton.Content = T(downloading ? "Download in background" : "Later");
        LaterButton.IsEnabled = !installing;
        CancelButton.Content = T("Cancel download");
        CancelButton.Visibility = VisibleWhen(downloading);
        CancelButton.IsEnabled = !_cancelling;
        PrimaryButton.Content = T(available ? "Update now" : release.Asset?.IsPortable == true
            ? "Open download folder" : "Restart to update");
        PrimaryButton.Visibility = VisibleWhen(!downloading);
        PrimaryButton.IsEnabled = !installing && release.Asset is not null;
        DismissButton.IsEnabled = !installing;
        var dismiss = T(downloading ? "Download in background" : "Dismiss");
        DismissButton.ToolTip = dismiss;
        AutomationProperties.SetName(DismissButton, dismiss);
    }

    internal void RenderProgress(UpdateDownloadProgress progress)
    {
        _progress = progress;
        string size = string.Format(CultureInfo.CurrentUICulture, "{0:0.0} MB", progress.Downloaded / 1_000_000d);
        if (progress.Total is > 0)
            size += string.Format(CultureInfo.CurrentUICulture, " / {0:0.0} MB", progress.Total.Value / 1_000_000d);
        var title = T("Download progress");
        ProgressText.Text = $"{title}   {size}";
        AutomationProperties.SetName(ProgressBar, title);
        ProgressBar.IsIndeterminate = progress.Total is not > 0;
        ProgressBar.Value = progress.Total is > 0 ? Math.Min(100, 100.0 * progress.Downloaded / progress.Total.Value) : 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != System.Windows.Input.Key.Escape || _phase == UpdateNoticePhase.Installing) return;
        e.Handled = true;
        (Application.Current as App)?.HideUpdateNotice();
    }

    private void Github_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.OpenPendingRelease();
    private void Skip_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.SkipPendingUpdate();
    private void Cancel_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.CancelUpdateDownload();
    private void Dismiss_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.HideUpdateNotice();

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;
        if (_phase == UpdateNoticePhase.Available) await app.DownloadPendingUpdateAsync();
        else if (_phase == UpdateNoticePhase.Ready) app.FinishPendingUpdate();
    }

    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static string T(string key) => LocalizationService.Translate(key);
}
