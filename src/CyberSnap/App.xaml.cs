using System.Windows;
using System.Windows.Threading;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap;

public partial class App : Application
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static Mutex? _mutex;
    private HotkeyService? _hotkeyService;
    private SettingsService? _settingsService;
    private HistoryService? _historyService;
    private ImageSearchIndexService? _imageSearchIndexService;
    private readonly object _historyGate = new();
    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private CaptureWidgetWindow? _widgetWindow;
    private DispatcherTimer? _idleTrimTimer;
    private int _isCapturing;
    private bool _historyRecovered;
    private bool _historyChangedHooked;
    private bool _historyMaintenanceScheduled;
    private int _historyIndexRefreshScheduled;
    private int _settingsWindowOpening;
    private int _settingsHiddenForCapture;
    private int _idleTrimInProgress;
    private DateTime _lastIdleTrimUtc = DateTime.MinValue;
    private HistoryWindow? _historyWindow;
    private int _historyWindowOpening;

    public void RefreshHistoryWindowIfOpen()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshHistoryWindowIfOpen);
            return;
        }
        _historyWindow?.RequestRefresh();
    }

    /// <summary>Persists the annotation editor's "auto-fit vs real size on open" preference.</summary>
    public void PersistEditorFitPreference(bool fitToWindow)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorFitToWindowOnOpen == fitToWindow) return;
        _settingsService.Settings.EditorFitToWindowOnOpen = fitToWindow;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-fit-pref", ex); }
    }

    /// <summary>Persists the annotation editor's "show banners" preference.</summary>
    public void PersistEditorShowBanners(bool showBanners)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorShowBanners == showBanners) return;
        _settingsService.Settings.EditorShowBanners = showBanners;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-banners-pref", ex); }
    }

    /// <summary>Persists the annotation editor's "auto crop controls" preference.</summary>
    public void PersistEditorAutoCropControls(bool autoCropControls)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorAutoCropControls == autoCropControls) return;
        _settingsService.Settings.EditorAutoCropControls = autoCropControls;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-autocrop-pref", ex); }
    }

    /// <summary>Persists the annotation editor's "show rulers" preference.</summary>
    public void PersistEditorShowRulers(bool showRulers)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorShowRulers == showRulers) return;
        _settingsService.Settings.EditorShowRulers = showRulers;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-rulers-pref", ex); }
    }

    /// <summary>Persists the color last chosen for shapes/annotations in the editor.</summary>
    public void PersistEditorToolColor(int argb)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorToolColorArgb == argb) return;
        _settingsService.Settings.EditorToolColorArgb = argb;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-tool-color", ex); }
    }

    /// <summary>Persists the stroke width last chosen in the editor (shared with the capture overlay).</summary>
    public void PersistEditorStrokeWidth(float width)
    {
        if (_settingsService is null) return;
        if (Math.Abs(_settingsService.Settings.StrokeWidth - width) < 0.01f) return;
        _settingsService.Settings.StrokeWidth = width;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-stroke-width", ex); }
    }

    /// <summary>Persists the Text tool font size last chosen in the editor.</summary>
    public void PersistEditorTextFontSize(float size)
    {
        if (_settingsService is null) return;
        if (Math.Abs(_settingsService.Settings.EditorTextFontSize - size) < 0.01f) return;
        _settingsService.Settings.EditorTextFontSize = size;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-text-font-size", ex); }
    }

    public void EnsureWidgetWindowCreated()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(EnsureWidgetWindowCreated);
            return;
        }

        if (_settingsService?.Settings.ShowCaptureWidget == true && _widgetWindow == null)
        {
            _widgetWindow = new CaptureWidgetWindow(_settingsService);
            _widgetWindow.Show();
        }
    }

    public void CloseWidgetWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(CloseWidgetWindow);
            return;
        }

        if (_widgetWindow != null)
        {
            try
            {
                _widgetWindow.Close();
            }
            catch { }
            _widgetWindow = null;
        }
    }

    public void RefreshWidgetWindowLayout()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshWidgetWindowLayout);
            return;
        }

        _widgetWindow?.RefreshLayout();
    }

    // Settings → widget: push the current OpenEditorAfterCapture value onto the widget's own
    // "Enable editor" toggle so both controls always show the same state. No-op if no widget.
    public void SyncWidgetEnableEditorToggle()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(SyncWidgetEnableEditorToggle);
            return;
        }

        _widgetWindow?.RefreshEnableEditorToggle();
    }

    // Widget → Settings: when the widget's toggle flips, pull the open Settings window's checkbox
    // back into sync. No-op if Settings isn't open.
    public void SyncSettingsEnableEditorCheck()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(SyncSettingsEnableEditorCheck);
            return;
        }

        _settingsWindow?.RefreshEnableEditorCheck();
    }
}
