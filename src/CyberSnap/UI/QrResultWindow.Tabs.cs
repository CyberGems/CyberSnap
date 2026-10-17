using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using ZXing;

namespace CyberSnap.UI;

public partial class QrResultWindow
{
    private readonly ResultTabHost<QrResultSession> _tabs;
    private int _nextOrdinal = 1;

    public static QrResultWindow ShowResult(string text, BarcodeFormat format, SettingsService settingsService,
        ImageSource? previewSource = null, string? alreadyCopiedText = null)
    {
        var window = Application.Current.Windows.OfType<QrResultWindow>()
            .FirstOrDefault(candidate => !candidate._tabs.IsClosing);
        if (window is null)
        {
            window = new QrResultWindow(text, format, settingsService, previewSource, alreadyCopiedText);
            window.Show();
        }
        else
        {
            window._tabs.Add(new QrResultSession(text, format, previewSource, alreadyCopiedText, window._nextOrdinal++));
            if (!window.IsVisible)
                window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
        }
        CyberSnapWindowChrome.EnsureForeground(window);
        return window;
    }

    private void CaptureSession(QrResultSession session)
    {
        session.CopiedText = _copiedText;
        session.SelectionStart = ContentTextBox.SelectionStart;
        session.SelectionLength = ContentTextBox.SelectionLength;
        session.VerticalOffset = ContentTextBox.VerticalOffset;
    }

    private void ApplySession(QrResultSession session)
    {
        _format = session.Format;
        _copiedText = session.CopiedText;
        ContentTextBox.Text = session.Text;
        ContentTextBox.Select(session.SelectionStart, session.SelectionLength);
        PreviewImage.Source = session.PreviewSource;
        PreviewImage.Visibility = session.PreviewSource is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewUnavailableText.Visibility = session.PreviewSource is null ? Visibility.Visible : Visibility.Collapsed;
        FormatText.Text = GetFormatLabel(session.Format);
        ApplyCopyActionState();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_tabs.IsClosing && ReferenceEquals(_tabs.Active, session))
                ContentTextBox.ScrollToVerticalOffset(session.VerticalOffset);
        }, DispatcherPriority.Loaded);
    }
}

internal sealed class QrResultSession(string text, BarcodeFormat format, ImageSource? preview, string? copied, int ordinal)
{
    public string Text { get; } = text;
    public BarcodeFormat Format { get; } = format;
    public ImageSource? PreviewSource { get; } = preview;
    public string? CopiedText { get; set; } = copied;
    public int Ordinal { get; } = ordinal;
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public double VerticalOffset { get; set; }
}
