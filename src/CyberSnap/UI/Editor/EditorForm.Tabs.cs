using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

public sealed partial class EditorForm
{
    private const int SoftTabLimit = 8;
    private readonly List<EditorDocument> _documents = new();
    private EditorDocument _activeDocument = null!;
    private Panel _canvasInner = null!;
    private EditorTabStrip _tabStrip = null!;
    private bool _tabLimitWarned;

    private AnnotationCanvas _canvas => _activeDocument.Canvas;

    private string? _savedFilePath
    {
        get => _activeDocument.SavedFilePath;
        set => _activeDocument.SavedFilePath = value;
    }

    private bool ShouldReplaceActiveDocument =>
        _canvas is { IsDisposed: false, IsDefaultBlank: true };

    private EditorDocument CreateDocument(Bitmap bitmap, string? savedFilePath, AnnotationCanvas? cloneFrom)
    {
        var settings = SettingsService.LoadStatic();
        var canvas = new AnnotationCanvas(bitmap)
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.CanvasBg,
            FitToWindowOnLoad = settings?.EditorFitToWindowOnOpen ?? true,
            ShowBanners = settings?.EditorShowBanners ?? true,
            ShowWelcomeBanner = settings?.EditorShowWelcomeBanner ?? true,
            ShowHints = settings?.EditorShowHints ?? true,
            EditorAutoCropControls = settings?.EditorAutoCropControls ?? true,
            EditorShowResizeHandles = settings?.EditorShowResizeHandles ?? true,
            ResizeHandlesScaleContent = settings?.EditorResizeHandlesScaleContent ?? false,
            PanModeLockObjects = settings?.EditorPanModeLockObjects ?? true,
            UndoLimit = settings?.EditorUndoLimit ?? 100,
            ShowScrollbarsAlways = settings?.EditorShowScrollbars ?? false,
        };

        if (cloneFrom is not null)
        {
            canvas.CopySharedToolStateFrom(cloneFrom);
        }
        else
        {
            canvas.ToolColor = settings != null ? Color.FromArgb(settings.EditorToolColorArgb) : EditorColors.Accent;
            canvas.StrokeWidth = settings?.StrokeWidth ?? 4f;
            canvas.TextFontSize = settings?.EditorTextFontSize ?? 24f;
            if (settings != null)
            {
                canvas.ApplyTextStyle(
                    settings.EditorTextFontSize,
                    settings.EditorTextFontFamily ?? "Segoe UI",
                    settings.EditorTextBold,
                    settings.EditorTextItalic,
                    settings.EditorTextStroke,
                    settings.EditorTextShadow,
                    settings.EditorTextBackground,
                    settings.EditorTextAlignment);
            }
        }

        canvas.LoadRecentFonts(settings?.EditorTextRecentFonts, settings?.EditorTextFavoriteFonts);
        return new EditorDocument(canvas, savedFilePath);
    }

    private void AttachCanvas(AnnotationCanvas canvas)
    {
        canvas.StateChanged += OnCanvasStateChanged;
        canvas.BlankBitmapFactory = (w, h) => CreateBlankCheckerboard(EditorColors.IsDark, w, h);
        canvas.ConfirmResizeByHandle = ConfirmCanvasResizeByHandle;
        canvas.TextFontSizeChanged += OnCanvasTextFontSizeChanged;
        canvas.TextStyleChanged += OnCanvasTextStyleChanged;
        canvas.FavoriteFontsChanged += OnCanvasFavoriteFontsChanged;
        canvas.MouseMove += OnCanvasMouseMove;
        canvas.MouseUp += OnCanvasMouseUp;
        canvas.DoubleClick += OnCanvasDoubleClick;
        canvas.EmojiPlacementRequested += OnCanvasEmojiPlacementRequested;
        canvas.AllowDrop = true;
        canvas.DragEnter += OnEditorDragEnter;
        canvas.DragLeave += OnEditorDragLeave;
        canvas.DragDrop += OnEditorDragDrop;
        canvas.WelcomeNewCanvasRequested = () => DoNewCanvas();
        canvas.WelcomeOpenRequested = () => DoOpen();
        canvas.WelcomePasteRequested = () => DoPaste();
        canvas.WelcomeCaptureRequested = () =>
        {
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.OnHotkeyPressedProxy();
        };
    }

    private void DetachCanvas(AnnotationCanvas canvas)
    {
        if (canvas.IsDisposed) return;
        canvas.StateChanged -= OnCanvasStateChanged;
        canvas.TextFontSizeChanged -= OnCanvasTextFontSizeChanged;
        canvas.TextStyleChanged -= OnCanvasTextStyleChanged;
        canvas.FavoriteFontsChanged -= OnCanvasFavoriteFontsChanged;
        canvas.MouseMove -= OnCanvasMouseMove;
        canvas.MouseUp -= OnCanvasMouseUp;
        canvas.DoubleClick -= OnCanvasDoubleClick;
        canvas.EmojiPlacementRequested -= OnCanvasEmojiPlacementRequested;
        canvas.DragEnter -= OnEditorDragEnter;
        canvas.DragLeave -= OnEditorDragLeave;
        canvas.DragDrop -= OnEditorDragDrop;
        canvas.WelcomeNewCanvasRequested = null;
        canvas.WelcomeOpenRequested = null;
        canvas.WelcomePasteRequested = null;
        canvas.WelcomeCaptureRequested = null;
        canvas.ConfirmResizeByHandle = null;
        canvas.BlankBitmapFactory = null;
    }

    private bool ConfirmCanvasResizeByHandle(int w, int h)
    {
        var s = SettingsService.LoadStatic();
        if (s?.EditorSuppressResizeConfirm == true)
            return true;

        var title = LocalizationService.Translate("Resize canvas");
        var message = string.Format(
            LocalizationService.Translate("The canvas will be resized to {0} × {1} px. Continue?"),
            w, h);
        bool confirmed = ThemedConfirmDialog.Confirm(Handle, title, message, out bool dontShowAgain,
            danger: false, iconId: "maximize");
        if (dontShowAgain && System.Windows.Application.Current is CyberSnap.App app)
            app.PersistEditorSuppressResizeConfirm(true);
        return confirmed;
    }

    private void OnCanvasTextFontSizeChanged(float size)
    {
        if (System.Windows.Application.Current is CyberSnap.App app)
            app.PersistEditorTextFontSize(size);
    }

    private void OnCanvasTextStyleChanged(
        float size, string family, bool bold, bool italic, bool stroke, bool shadow, bool bg, int align)
    {
        if (System.Windows.Application.Current is CyberSnap.App app)
            app.PersistEditorTextStyle(size, family, bold, italic, stroke, shadow, bg, align);
    }

    private void OnCanvasFavoriteFontsChanged(string serialized)
    {
        if (System.Windows.Application.Current is CyberSnap.App app)
            app.PersistEditorTextFavoriteFonts(serialized);
    }

    private void OnCanvasEmojiPlacementRequested(object? sender, EventArgs e)
        => OpenEmojiPicker(GetEmojiToolButton());

    private void WireTabStrip()
    {
        _tabStrip = new EditorTabStrip { Visible = false };
        _tabStrip.TabSelected += (_, index) =>
        {
            if (index >= 0 && index < _documents.Count)
                ActivateDocument(_documents[index]);
        };
        _tabStrip.TabCloseRequested += (_, index) => CloseDocumentAt(index);
        _tabStrip.EmptyAreaDoubleClicked += (_, _) => DoNewCanvas();
    }

    private void ActivateDocument(EditorDocument document)
    {
        if (ReferenceEquals(_activeDocument, document))
        {
            RefreshTabStrip();
            return;
        }

        if (_emojiPicker is { IsDisposed: false })
            _emojiPicker.Close();

        var previous = _activeDocument;
        if (previous is not null && !previous.Canvas.IsDisposed)
        {
            DetachCanvas(previous.Canvas);
            _canvasInner?.Controls.Remove(previous.Canvas);
            previous.Canvas.Visible = false;
        }

        _activeDocument = document;
        var canvas = document.Canvas;
        canvas.Visible = true;
        canvas.Dock = DockStyle.Fill;
        AttachCanvas(canvas);
        if (_canvasInner is not null)
        {
            _canvasInner.Controls.Add(canvas);
            _canvasInner.Controls.SetChildIndex(canvas, 0);
        }
        _leftRuler?.Retarget(canvas);
        _topRuler?.Retarget(canvas);
        _cornerBlock?.Retarget(canvas);

        if (_toggleFrameSwitch is not null)
            _toggleFrameSwitch.Checked = canvas.ShowCaptureFrame;
        if (_toggleFitSwitch is not null)
            _toggleFitSwitch.Checked = canvas.FitToWindowOnLoad;
        if (_togglePanLockSwitch is not null)
            _togglePanLockSwitch.Checked = canvas.PanModeLockObjects;

        UpdateColorSwatch();
        UpdateStrokeWidthButtons();
        RefreshUi();
        RefreshTabStrip();
        canvas.Focus();
        canvas.Invalidate();
        _leftRuler?.Invalidate();
        _topRuler?.Invalidate();
        _cornerBlock?.Invalidate();
    }

    private void WarnIfManyTabs()
    {
        if (_documents.Count < SoftTabLimit || _tabLimitWarned)
            return;
        _tabLimitWarned = true;
        ThemedConfirmDialog.Alert(
            Handle,
            LocalizationService.Translate("Editor"),
            LocalizationService.Translate("Several documents are open. Close some tabs if the editor feels slow."),
            error: false);
    }

    private void OpenDocumentInTab(Bitmap bitmap, string? savedFilePath, bool autoMaximize, bool performanceWarning)
    {
        if (ShouldReplaceActiveDocument)
        {
            LoadCapture(bitmap, savedFilePath, autoMaximize, showOpenedBanner: true, performanceWarning);
            return;
        }

        WarnIfManyTabs();
        var clone = new Bitmap(bitmap);
        var doc = CreateDocument(clone, savedFilePath, _canvas);
        _documents.Add(doc);
        ActivateDocument(doc);
        if (autoMaximize)
            MaybeAutoMaximizeForCapture();
        if (performanceWarning)
            ShowLargeImagePerformanceBanner(ImageOpenPolicy.EvaluateBitmap(bitmap, ImageOpenSource.Capture));
        UpdateTabStripVisibility();
    }

    private void OpenProjectInTab(Bitmap baseBitmap, ProjectData data, string filePath, bool autoMaximize, bool performanceWarning)
    {
        if (ShouldReplaceActiveDocument)
        {
            LoadCaptureProject(baseBitmap, data, filePath, autoMaximize, performanceWarning);
            return;
        }

        WarnIfManyTabs();
        var clone = new Bitmap(baseBitmap);
        baseBitmap.Dispose();
        var doc = CreateDocument(clone, filePath, _canvas);
        doc.Canvas.LoadProjectState(clone, data.Annotations, data.HorizontalGuides, data.VerticalGuides);
        _documents.Add(doc);
        ActivateDocument(doc);
        AddRecentFile(filePath);
        if (autoMaximize)
            MaybeAutoMaximizeForCapture();
        if (performanceWarning)
            ShowLargeImagePerformanceBanner(ImageOpenPolicy.EvaluateBitmap(clone, ImageOpenSource.FilePath));
        else
            _canvas.ShowToolBanner(LocalizationService.Translate("Project opened"));
        UpdateTabStripVisibility();
    }

    private void OpenBlankTab(Bitmap? blank = null)
    {
        WarnIfManyTabs();
        blank ??= CreateBlankCheckerboard(EditorColors.IsDark);
        var doc = CreateDocument(blank, null, _canvas);
        doc.Canvas.IsDefaultBlank = true;
        doc.Canvas.IsBlankCanvas = true;
        _documents.Add(doc);
        ActivateDocument(doc);
        _canvas.ZoomFit();
        _canvas.DismissWelcomeOverlay();
        UpdateTabStripVisibility();
    }

    private bool CloseDocumentAt(int index)
    {
        if (index < 0 || index >= _documents.Count)
            return false;
        if (!ReferenceEquals(_documents[index], _activeDocument))
            ActivateDocument(_documents[index]);
        return CloseActiveTab();
    }

    private bool CloseActiveTab()
    {
        if (_documents.Count <= 1)
        {
            if (_activeDocument.IsDirty && !PromptSaveChanges())
                return false;
            ResetActiveToBlank();
            return true;
        }

        if (_activeDocument.IsDirty && !PromptSaveChanges())
            return false;

        int idx = _documents.IndexOf(_activeDocument);
        var closing = _activeDocument;
        var nextDoc = idx + 1 < _documents.Count
            ? _documents[idx + 1]
            : _documents[idx - 1];

        ActivateDocument(nextDoc);
        _documents.Remove(closing);
        closing.Dispose();
        UpdateTabStripVisibility();
        return true;
    }

    private void ResetActiveToBlank()
    {
        var blank = CreateBlankCheckerboard(EditorColors.IsDark);
        LoadCapture(blank, null, autoMaximize: false);
        blank.Dispose();
        _canvas.IsDefaultBlank = true;
        _canvas.IsBlankCanvas = true;
        _canvas.ZoomFit();
        _canvas.DismissWelcomeOverlay();
        _canvas.Invalidate();
        UpdateTabStripVisibility();
    }

    private void CycleTab(int delta)
    {
        if (_documents.Count < 2) return;
        int idx = _documents.IndexOf(_activeDocument);
        if (idx < 0) idx = 0;
        int next = (idx + delta) % _documents.Count;
        if (next < 0) next += _documents.Count;
        ActivateDocument(_documents[next]);
    }

    private void UpdateTabStripVisibility()
    {
        ApplyRulerAndTabLayout();
        _canvasInner?.PerformLayout();
        _canvas?.Invalidate();
        _leftRuler?.Invalidate();
        _topRuler?.Invalidate();
        _cornerBlock?.Invalidate();
    }

    private void ApplyRulerAndTabLayout()
    {
        if (_tabStrip is null) return;
        bool tabs = _documents.Count > 1;
        bool rulers = _rulersEnabled;

        _tabStrip.Visible = tabs;
        if (_tabRow is not null)
            _tabRow.Visible = tabs;
        if (_topRuler is not null)
            _topRuler.Visible = rulers;
        if (_rulerRow is not null)
            _rulerRow.Visible = rulers;
        if (_leftRuler is not null)
            _leftRuler.Visible = rulers;
        if (_cornerBlock is not null)
            _cornerBlock.Visible = rulers;

        if (_topRulerContainer is not null)
        {
            int height = (tabs ? EditorTabStrip.PreferredHeight : 0) + (rulers ? 28 : 0);
            _topRulerContainer.Height = height;
            _topRulerContainer.Visible = height > 0;
        }

        RefreshTabStrip();
    }

    private void RefreshTabStrip()
    {
        if (_tabStrip is null) return;
        var tabs = new EditorTabInfo[_documents.Count];
        for (int i = 0; i < _documents.Count; i++)
        {
            var doc = _documents[i];
            tabs[i] = new EditorTabInfo(doc.TabTitle, doc.IsDirty, ReferenceEquals(doc, _activeDocument));
        }
        _tabStrip.SetTabs(tabs);
    }

    private bool PromptSaveAllDirtyDocuments()
    {
        foreach (var doc in _documents.ToList())
        {
            if (!doc.IsDirty) continue;
            ActivateDocument(doc);
            if (!PromptSaveChanges())
                return false;
        }
        return true;
    }

    private void DisposeAllDocuments()
    {
        foreach (var doc in _documents)
        {
            try
            {
                if (!doc.Canvas.IsDisposed)
                    DetachCanvas(doc.Canvas);
            }
            catch { /* closing */ }
            try { doc.Dispose(); } catch { /* closing */ }
        }
        _documents.Clear();
    }
}
