using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class OcrResultWindow
{
    private readonly ResultTabHost<OcrResultSession> _tabs;
    private int _nextOrdinal = 1;
    private bool _applyingSession;

    public static OcrResultWindow ShowResult(string text, SettingsService settingsService,
        ImageSource? previewSource = null, string? alreadyCopiedText = null)
    {
        var window = Application.Current.Windows.OfType<OcrResultWindow>()
            .FirstOrDefault(candidate => !candidate._tabs.IsClosing);
        if (window is null)
        {
            window = new OcrResultWindow(text, settingsService, previewSource, alreadyCopiedText);
            window.Show();
        }
        else
        {
            window._tabs.Add(window.CreateSession(text, previewSource, alreadyCopiedText));
            if (!window.IsVisible)
                window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
        }
        CyberSnapWindowChrome.EnsureForeground(window);
        return window;
    }

    private OcrResultSession CreateSession(string text, ImageSource? source, string? copied) => new()
    {
        Ordinal = _nextOrdinal++, Text = text, PreviewSource = source, CopiedText = copied,
        FromLanguage = _settingsService.Settings.OcrDefaultTranslateFrom,
        ToLanguage = _settingsService.Settings.OcrDefaultTranslateTo,
        Model = _settingsService.Settings.TranslationModel,
        TranslationExpanded = _settingsService.Settings.OcrTranslationPanelExpanded
    };

    private void CaptureSession(OcrResultSession session)
    {
        session.Text = OcrTextBox.Text;
        session.CopiedText = _copiedText;
        session.SelectionStart = OcrTextBox.SelectionStart;
        session.SelectionLength = OcrTextBox.SelectionLength;
        session.VerticalOffset = OcrTextBox.VerticalOffset;
        session.HorizontalOffset = OcrTextBox.HorizontalOffset;
        session.Search = SearchTextBox.Text;
        session.MatchIndex = _currentMatchIndex;
        session.FromLanguage = (FromLanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        session.ToLanguage = (ToLanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        session.Model = (int)GetSelectedModel();
        session.TranslationExpanded = TranslationPanel.Visibility == Visibility.Visible;
        session.TranslatedText = TranslatedTextBox.Text;
        session.TranslationOffset = TranslatedTextBox.VerticalOffset;
        session.PreferenceStatus = TranslationPreferenceStatusText.Text;
        if (!session.IsBusy)
            session.TranslationStatus = TranslateStatus.Visibility == Visibility.Visible ? TranslateStatus.Text : "";
    }

    private void ApplySession(OcrResultSession session)
    {
        _applyingSession = true;
        _suppressTranslationPreferenceChange = true;
        try
        {
            OcrPreviewImage.Source = session.PreviewSource;
            OcrPreviewImage.Visibility = session.PreviewSource is null ? Visibility.Collapsed : Visibility.Visible;
            OcrPreviewEmptyText.Visibility = session.PreviewSource is null ? Visibility.Visible : Visibility.Collapsed;
            _copiedText = session.CopiedText;
            OcrTextBox.IsUndoEnabled = false;
            OcrTextBox.Text = session.Text;
            OcrTextBox.IsUndoEnabled = true;
            SearchTextBox.IsUndoEnabled = false;
            SearchTextBox.Text = session.Search;
            SearchTextBox.IsUndoEnabled = true;
            RefreshSearchMatches(keepCurrentMatch: false);
            _currentMatchIndex = Math.Clamp(session.MatchIndex, -1, _searchMatchStarts.Count - 1);
            UpdateSearchStatus();
            SelectComboByTag(FromLanguageCombo, session.FromLanguage);
            SelectComboByTag(ToLanguageCombo, session.ToLanguage);
            SelectTranslationModelCombo(session.Model);
            SetTranslationPanelExpanded(session.TranslationExpanded, animate: false);
            SetTranslationPreferenceStatus(session.PreferenceStatus);
            ApplyTranslationSession(session);
            CopyTranslationBtn.IsHitTestVisible = true;
            ApplyCopyActionState();
            OcrTextBox.Select(session.SelectionStart, session.SelectionLength);
        }
        finally
        {
            _suppressTranslationPreferenceChange = false;
            _applyingSession = false;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_tabs.IsClosing || !ReferenceEquals(_tabs.Active, session))
                return;
            OcrTextBox.ScrollToVerticalOffset(session.VerticalOffset);
            OcrTextBox.ScrollToHorizontalOffset(session.HorizontalOffset);
            TranslatedTextBox.ScrollToVerticalOffset(session.TranslationOffset);
            RedrawSearchHighlights();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyTranslationSession(OcrResultSession session)
    {
        if (_tabs.IsClosing || !ReferenceEquals(_tabs.Active, session))
            return;
        StopTranslationLoading(keepStatusVisible: false);
        if (session.CheckingTranslation)
            StartTranslationConfigurationCheck();
        else if (session.Translating)
        {
            StartTranslationLoading((TranslationModel)session.Model);
            StartTranslateTimer();
        }
        else
        {
            TranslatedTextBox.Text = session.TranslatedText;
            SetTranslationCopyVisible(!string.IsNullOrWhiteSpace(session.TranslatedText));
            TranslateStatus.Text = session.TranslationStatus;
            TranslateStatus.Visibility = string.IsNullOrEmpty(session.TranslationStatus) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

internal sealed class OcrResultSession
{
    public int Ordinal { get; init; }
    public string Text { get; set; } = "";
    public ImageSource? PreviewSource { get; init; }
    public string? CopiedText { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }
    public string Search { get; set; } = "";
    public int MatchIndex { get; set; } = -1;
    public string FromLanguage { get; set; } = "auto";
    public string ToLanguage { get; set; } = "auto";
    public int Model { get; set; }
    public bool TranslationExpanded { get; set; }
    public string TranslatedText { get; set; } = "";
    public double TranslationOffset { get; set; }
    public string TranslationStatus { get; set; } = "";
    public string PreferenceStatus { get; set; } = "";
    public CancellationTokenSource? TranslationCts { get; set; }
    public bool CheckingTranslation { get; set; }
    public bool Translating { get; set; }
    public bool IsBusy => CheckingTranslation || Translating;
    public DateTime TranslationStartedAt { get; set; }

    public void CancelTranslation()
    {
        TranslationCts?.Cancel();
        TranslationCts = null;
        CheckingTranslation = false;
        Translating = false;
    }
}
