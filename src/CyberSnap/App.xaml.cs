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

    /// <summary>Records a file path as most-recently-opened in the Editor (newest first, max 6),
    /// persisting it to settings so the burger menu's "Open recent" submenu stays current.</summary>
    public void PersistRecentFile(string path)
    {
        if (_settingsService is null || string.IsNullOrWhiteSpace(path)) return;
        var list = _settingsService.Settings.RecentFilePaths;
        // De-dupe case-insensitively, insert newest first, cap at 6.
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 6)
            list.RemoveRange(6, list.Count - 6);
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-recent-file", ex); }
    }

    /// <summary>Records a color as most-recently-selected in the editor,
    /// persisting it to settings so the color picker's "Recent colors" list stays current.</summary>
    public void PersistRecentColor(string hex)
    {
        if (_settingsService is null || string.IsNullOrWhiteSpace(hex)) return;
        var list = _settingsService.Settings.RecentColors;
        list.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        if (list.Count > 12)
            list.RemoveRange(12, list.Count - 12);
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-recent-color", ex); }
    }

    /// <summary>Clears the editor's recent-files list and persists the empty state.</summary>
    public void ClearRecentFiles()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.RecentFilePaths.Clear();
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.clear-recent-files", ex); }
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

    /// <summary>Persists the annotation editor's "lock objects in pan mode" preference.</summary>
    public void PersistEditorPanModeLockObjects(bool lockObjects)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorPanModeLockObjects == lockObjects) return;
        _settingsService.Settings.EditorPanModeLockObjects = lockObjects;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-pan-lock-pref", ex); }
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

    /// <summary>Persists the annotation editor's "show resize handles" preference.</summary>
    public void PersistEditorShowResizeHandles(bool show)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorShowResizeHandles == show) return;
        _settingsService.Settings.EditorShowResizeHandles = show;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-resize-handles", ex); }
    }

    /// <summary>Persists whether dragging the resize handles scales content or extends the canvas.</summary>
    public void PersistEditorResizeHandlesScaleContent(bool scaleContent)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorResizeHandlesScaleContent == scaleContent) return;
        _settingsService.Settings.EditorResizeHandlesScaleContent = scaleContent;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-resize-scale", ex); }
    }

    /// <summary>Persists whether the handle-drag resize confirmation dialog was suppressed.</summary>
    public void PersistEditorSuppressResizeConfirm(bool suppress)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorSuppressResizeConfirm == suppress) return;
        _settingsService.Settings.EditorSuppressResizeConfirm = suppress;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-suppress-resize-confirm", ex); }
    }

    /// <summary>Persists whether the paste-replace confirmation dialog was suppressed.</summary>
    public void PersistEditorSuppressPasteConfirm(bool suppress)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorSuppressPasteConfirm == suppress) return;
        _settingsService.Settings.EditorSuppressPasteConfirm = suppress;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-suppress-paste-confirm", ex); }
    }

    /// <summary>Persists the editor undo limit (clamped 1–200).</summary>
    public void PersistEditorUndoLimit(int limit)
    {
        if (_settingsService is null) return;
        var clamped = Math.Clamp(limit, 1, 200);
        if (_settingsService.Settings.EditorUndoLimit == clamped) return;
        _settingsService.Settings.EditorUndoLimit = clamped;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-undo-limit", ex); }
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

    /// <summary>Persists the annotation editor's "show frame/border" preference.</summary>
    public void PersistEditorShowFrame(bool showFrame)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorShowFrame == showFrame) return;
        _settingsService.Settings.EditorShowFrame = showFrame;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-frame-pref", ex); }
    }

    /// <summary>Persists the annotation editor's "show hints" preference.</summary>
    public void PersistEditorShowHints(bool showHints)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorShowHints == showHints) return;
        _settingsService.Settings.EditorShowHints = showHints;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-hints-pref", ex); }
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

    /// <summary>Persists the custom color configured in the editor color palette.</summary>
    public void PersistEditorCustomColor(int argb)
    {
        if (_settingsService is null) return;
        if (_settingsService.Settings.EditorCustomColorArgb == argb) return;
        _settingsService.Settings.EditorCustomColorArgb = argb;
        try { _settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("editor.persist-custom-color", ex); }
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

    // Settings → widget: push the AlwaysOnTop value onto the widget window.
    public void SyncWidgetAlwaysOnTop(bool alwaysOnTop)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SyncWidgetAlwaysOnTop(alwaysOnTop));
            return;
        }

        if (_widgetWindow != null)
        {
            _widgetWindow.Topmost = alwaysOnTop;
        }
    }

    // Widget → Settings: when the widget's always on top changes, pull the Settings window's checkbox back into sync.
    public void SyncSettingsAlwaysOnTopCheck()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(SyncSettingsAlwaysOnTopCheck);
            return;
        }

        _settingsWindow?.RefreshAlwaysOnTopCheck();
    }

    // Widget → Settings: when the widget's enable editor changes, pull the Settings window's checkbox back into sync.
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
