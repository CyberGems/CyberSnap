using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

/// <summary>
/// Dedicated post-capture editor window. Hosts an <see cref="AnnotationCanvas"/>
/// with a left-side toolbar and a bottom status bar. The capture flow can either
/// open this directly after a screenshot (when the setting is enabled) or surface
/// it via the PreviewWindow "Edit" button.
/// </summary>
public sealed partial class EditorForm : Form
{
    private const int WS_EX_COMPOSITED = 0x02000000;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private static EditorForm? _instance;

    public static EditorForm? ActiveInstance => _instance is { IsDisposed: false, Visible: true } ? _instance : null;

    private readonly AnnotationCanvas _canvas;
    private Panel? _topRulerContainer;
    private HorizontalRuler? _topRuler;
    private VerticalRuler? _leftRuler;
    private RulerCornerBlock? _cornerBlock;

    private string? _savedFilePath;
    private bool _suppressCloseConfirm;
    private bool _isManualMaximized;
    private Rectangle _restoreBounds;
    private FormWindowState _lastWindowState = FormWindowState.Normal;
    private bool _enableComposited = true;
    private readonly System.Windows.Forms.Timer _saveStatusTimer = new() { Interval = 2200 };
    private readonly System.Windows.Forms.Timer _clipboardMonitorTimer = new() { Interval = 1000 };

    /// <summary>Opens or reuses the single editor instance.</summary>
    public static void ShowEditor(Bitmap captured, string? savedFilePath = null)
    {
        if (_instance is not null && !_instance.IsDisposed)
        {
            _instance.LoadCapture(captured, savedFilePath);
            _instance.RestoreAndActivate();
            return;
        }
        _instance = new EditorForm(captured, savedFilePath);
        _instance.Show();
    }

    public static void ShowEditorFromFile(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            if (filePath.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase))
            {
                var (baseBitmap, projectData) = CanvasProjectService.LoadProject(filePath);
                if (_instance is not null && !_instance.IsDisposed)
                {
                    _instance.LoadCaptureProject(baseBitmap, projectData, filePath);
                    _instance.RestoreAndActivate();
                    return;
                }
                _instance = new EditorForm(baseBitmap, filePath);
                _instance.LoadCaptureProject(baseBitmap, projectData, filePath);
                _instance.Show();
                return;
            }

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var tempBmp = new Bitmap(stream))
            {
                var captured = new Bitmap(tempBmp);
                ShowEditor(captured, filePath);
                if (_instance is not null)
                    _instance.AddRecentFile(filePath);
            }
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"Failed to load image: {ex.Message}", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    private void LoadCaptureProject(Bitmap baseBitmap, ProjectData data, string filePath)
    {
        _savedFilePath = filePath;
        _canvas.LoadProjectState(baseBitmap, data.Annotations, data.HorizontalGuides, data.VerticalGuides);
        _suppressCloseConfirm = false;
        MaybeAutoMaximizeForCapture();
        UpdateCaptureCaption();
        RefreshUi();
        AddRecentFile(filePath);
    }

    /// <summary>Records a file path in the app's recent-files list (persists to settings.json
    /// via the running App instance). Silently no-ops when the editor is hosted outside the app.</summary>
    private void AddRecentFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        if (System.Windows.Application.Current is App app)
            app.PersistRecentFile(filePath);
    }

    public static void ShowEditorEmptyOrPrompt()
    {
        if (_instance is not null && !_instance.IsDisposed)
        {
            _instance.RestoreAndActivate();
            return;
        }

        var blank = CreateBlankCheckerboard(Theme.IsDark);
        ShowEditor(blank);
        if (_instance is not null)
        {
            _instance._canvas.IsDefaultBlank = true;
            _instance._canvas.IsBlankCanvas = true;
            _instance.RefreshUi();
            _instance._canvas.Invalidate();
        }
    }

    private static Bitmap CreateBlankCheckerboard(bool isDark, int width = 1024, int height = 768)
    {
        width = Math.Clamp(width, AnnotationCanvas.MinCanvasSize, AnnotationCanvas.MaxCanvasSize);
        height = Math.Clamp(height, AnnotationCanvas.MinCanvasSize, AnnotationCanvas.MaxCanvasSize);
        var blank = new Bitmap(width, height);
        using (var g = Graphics.FromImage(blank))
        {
            var color1 = isDark ? Color.FromArgb(20, 22, 33) : Color.FromArgb(245, 246, 250);
            var color2 = isDark ? Color.FromArgb(28, 30, 43) : Color.FromArgb(233, 235, 243);
            g.Clear(color1);
            using (var brush = new SolidBrush(color2))
            {
                int size = 16;
                for (int y = 0; y < blank.Height; y += size)
                {
                    for (int x = 0; x < blank.Width; x += size)
                    {
                        if (((x / size) + (y / size)) % 2 == 1)
                        {
                            g.FillRectangle(brush, x, y, size, size);
                        }
                    }
                }
            }
        }
        return blank;
    }

    private static Bitmap CreateSolidBackground(int width, int height, Color color)
    {
        width = Math.Clamp(width, AnnotationCanvas.MinCanvasSize, AnnotationCanvas.MaxCanvasSize);
        height = Math.Clamp(height, AnnotationCanvas.MinCanvasSize, AnnotationCanvas.MaxCanvasSize);
        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        return bmp;
    }


    /// <summary>
    /// Brings an already-open editor back to the foreground for a new capture. Windows blocks a
    /// background process from stealing focus, so a brief TopMost toggle is used to reliably pop
    /// the window to the front (without keeping it pinned on top afterwards).
    /// </summary>
    private void RestoreAndActivate()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        if (!Visible)
            Show();

        Activate();
        BringToFront();

        bool wasTopMost = TopMost;
        TopMost = true;
        TopMost = wasTopMost;

        Activate();
        _canvas?.Focus();
    }

    public EditorForm(Bitmap captured, string? savedFilePath = null)
    {
        if (captured is null) throw new ArgumentNullException(nameof(captured));

        _savedFilePath = savedFilePath;

        SuspendLayout();
        try
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            // Fallback: app may not have an associated icon in all deployment modes
        }
        Text = LocalizationService.Translate("Annotations Editor");
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        ClientSize = new Size(1400, 920);
        MinimumSize = new Size(1400, 920);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = EditorColors.BgPrimary;
        Font = new Font("Segoe UI Variable Text", 9f, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;

        var settings = Services.SettingsService.LoadStatic();
        _canvas = new AnnotationCanvas(new Bitmap(captured))
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.CanvasBg,
            ToolColor = settings != null ? Color.FromArgb(settings.EditorToolColorArgb) : EditorColors.Accent,
            StrokeWidth = settings?.StrokeWidth ?? 4f,
            TextFontSize = settings?.EditorTextFontSize ?? 24f,
            FitToWindowOnLoad = settings?.EditorFitToWindowOnOpen ?? true,
            ShowBanners = settings?.EditorShowBanners ?? true,
            ShowHints = settings?.EditorShowHints ?? true,
            EditorAutoCropControls = settings?.EditorAutoCropControls ?? true,
            EditorShowResizeHandles = settings?.EditorShowResizeHandles ?? true,
            ResizeHandlesScaleContent = settings?.EditorResizeHandlesScaleContent ?? false,
            PanModeLockObjects = settings?.EditorPanModeLockObjects ?? true,
            UndoLimit = settings?.EditorUndoLimit ?? 100,
        };
        _canvas.StateChanged += OnCanvasStateChanged;
        _canvas.BlankBitmapFactory = (w, h) => CreateBlankCheckerboard(EditorColors.IsDark, w, h);
        _canvas.ConfirmResizeByHandle = (w, h) =>
        {
            // Reload fresh each time — the delegate outlives the constructor's snapshot.
            var s = Services.SettingsService.LoadStatic();
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
        };
        _canvas.TextFontSizeChanged += size =>
        {
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.PersistEditorTextFontSize(size);
        };
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp += OnCanvasMouseUp;
        _canvas.DoubleClick += OnCanvasDoubleClick;
        _canvas.EmojiPlacementRequested += (_, _) => OpenEmojiPicker(GetEmojiToolButton());
        _saveStatusTimer.Tick += (_, _) =>
        {
            _saveStatusTimer.Stop();
            RefreshUi();
        };
        _clipboardMonitorTimer.Tick += (_, _) =>
        {
            if (_pasteButton != null)
            {
                bool hasImage = Clipboard.ContainsImage();
                if (_pasteButton.Enabled != hasImage)
                {
                    _pasteButton.Enabled = hasImage;
                }
            }
        };
        _clipboardMonitorTimer.Start();

        BuildToolbar();
        BuildStatusBar();

        var canvasContainer = new EditorCanvasFrame
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.BgPrimary,
            Padding = new Padding(18),
        };

        bool showRulers = settings?.EditorShowRulers ?? true;

        _topRuler = new HorizontalRuler(_canvas)
        {
            Dock = DockStyle.Fill,
            Visible = showRulers
        };

        _cornerBlock = new RulerCornerBlock(_canvas)
        {
            Dock = DockStyle.Left,
            Width = 28,
            Visible = showRulers
        };

        _topRulerContainer = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            Visible = showRulers
        };
        _topRulerContainer.Controls.Add(_topRuler);
        _topRulerContainer.Controls.Add(_cornerBlock);

        _leftRuler = new VerticalRuler(_canvas)
        {
            Dock = DockStyle.Left,
            Width = 28,
            Visible = showRulers
        };

        var canvasInner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.CanvasBg,
            Padding = new Padding(1),
        };
        canvasInner.Controls.Add(_canvas);
        canvasInner.Controls.Add(_leftRuler);
        canvasInner.Controls.Add(_topRulerContainer);
        canvasContainer.Controls.Add(canvasInner);

        var workArea = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.BgPrimary,
        };
        workArea.Controls.Add(canvasContainer);
        workArea.Controls.Add(_toolbarPanel);

        var root = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.BgPrimary,
        };
        root.Controls.Add(workArea);
        root.Controls.Add(_topBarPanel);
        root.Controls.Add(_statusBarPanel);

        var windowFrame = new EditorWindowFrame
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.BgPrimary,
            // Owns the outer resize ring so edge hit-tests can bubble to the form (see EditorWindowFrame.WndProc).
            Padding = new Padding(EditorPaint.ResizeHitSize),
        };
        windowFrame.Controls.Add(root);
        Controls.Add(windowFrame);

        BuildContextMenus();

        ResumeLayout(false);
        PerformLayout();

        FormClosing += OnFormClosing;
        Activated += (s, e) => RefreshUi();
        FormClosed += (_, _) =>
        {
            _saveStatusTimer.Stop();
            _saveStatusTimer.Dispose();
            _clipboardMonitorTimer.Stop();
            _clipboardMonitorTimer.Dispose();
            _tooltipTimer?.Stop();
            _tooltipTimer?.Dispose();
            _brandBitmap?.Dispose();
            _hoverToolTip?.Dispose();
            _canvasMenu?.Dispose();
            _imageMenu?.Dispose();
            _burgerMenu?.Dispose();
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
        Resize += (_, _) =>
        {
            UpdateWindowStateButton();
            UpdateWindowChromeRegion();
            if (WindowState != FormWindowState.Minimized)
            {
                if (_lastWindowState == FormWindowState.Minimized)
                {
                    RefreshLayoutAndRedraw();
                }

                if (_canvas != null)
                {
                    RefreshUi();
                    _canvas.Invalidate();
                }
            }
            _lastWindowState = WindowState;
            UpdateStatusBarResponsiveLayout();
        };
        Shown += (_, _) =>
        {
            MaybeAutoMaximizeForCapture();
            UpdateWindowChromeRegion();
            Activate();
            BringToFront();
            _canvas.Focus();
            RefreshUi();
            UpdateStatusBarResponsiveLayout();

            // Disable compositing after initial render to avoid layout corruption bugs when losing focus
            _enableComposited = false;
            UpdateStyles();
            RefreshLayoutAndRedraw();
        };
    }

    private void UpdateStatusBarResponsiveLayout()
    {
        if (_hintArea == null) return;
        _hintArea.Visible = _canvas is { ShowHints: true } && ClientSize.Width >= 950;
    }

    private void RefreshLayoutAndRedraw()
    {
        SuspendLayout();
        PerformLayout();
        ResumeLayout(true);
        Invalidate(true);
        Update();
    }

    public void ApplyTheme()
    {
        if (InvokeRequired)
        {
            Invoke(new Action(ApplyTheme));
            return;
        }

        if (_canvas != null && _canvas.IsDefaultBlank)
        {
            var newBlank = CreateBlankCheckerboard(EditorColors.IsDark);
            _canvas.BaseBitmap = newBlank;
        }

        UpdateControlTheme(this);
        // Context menus are cached — rebuild them next time so icons/text match the new theme.
        _burgerMenu?.Dispose();
        _burgerMenu = null;
        _canvasMenu?.Dispose();
        _canvasMenu = null;
        _imageMenu?.Dispose();
        _imageMenu = null;
        RefreshLayoutAndRedraw();
    }

    private void UpdateControlTheme(Control ctrl)
    {
        foreach (Control child in ctrl.Controls)
        {
            UpdateControlTheme(child);
        }

        if (ctrl is AnnotationCanvas canvas)
        {
            canvas.BackColor = EditorColors.CanvasBg;
            return;
        }

        if (ctrl is EditorZoomHostPanel)
        {
            ctrl.BackColor = EditorColors.TitleBar;
            return;
        }

        if (ctrl.Parent is EditorZoomHostPanel)
        {
            ctrl.BackColor = Color.Transparent;
            if (ctrl is Label lbl)
            {
                if (lbl == _zoomLabel)
                    lbl.ForeColor = EditorColors.Accent;
                else
                    lbl.ForeColor = EditorColors.TextSecondary;
            }
            return;
        }

        if (ctrl == _toolbarPanel)
        {
            ctrl.BackColor = EditorColors.BgSecondary;
            return;
        }

        if (ctrl == _statusBarPanel || ctrl == _topBarPanel)
        {
            ctrl.BackColor = EditorColors.TitleBar;
            return;
        }

        if (ctrl == _titleBarPanel)
        {
            ctrl.BackColor = Color.Transparent;
            return;
        }

        if (ctrl is HorizontalRuler or VerticalRuler or RulerCornerBlock or EditorCanvasFrame or EditorWindowFrame)
        {
            ctrl.BackColor = EditorColors.BgPrimary;
            return;
        }

        if (ctrl is Panel && ctrl.Parent is EditorCanvasFrame)
        {
            ctrl.BackColor = EditorColors.CanvasBg;
            return;
        }

        if (ctrl is FlowLayoutPanel)
        {
            ctrl.BackColor = Color.Transparent;
            return;
        }

        if (ctrl is TableLayoutPanel)
        {
            ctrl.BackColor = Color.Transparent;
            return;
        }

        if (ctrl is DoubleBufferedPanel && ctrl != _statusBarPanel)
        {
            ctrl.BackColor = Color.Transparent;
            return;
        }

        if (ctrl is Panel panel)
        {
            bool inHeaderOrFooter = false;
            var p = panel.Parent;
            while (p != null)
            {
                if (p == _topBarPanel || p == _statusBarPanel)
                {
                    inHeaderOrFooter = true;
                    break;
                }
                p = p.Parent;
            }

            if (inHeaderOrFooter)
                panel.BackColor = Color.Transparent;
            else
                panel.BackColor = EditorColors.BgPrimary;
        }
        else if (ctrl is Label label)
        {
            if (label == _zoomLabel)
                label.ForeColor = EditorColors.Accent;
            else if (label == _coordsLabel || label == _fileNameLabel || label == _liveStatusLabel)
                label.ForeColor = EditorColors.TextSecondary;
            else
                label.ForeColor = EditorColors.TextPrimary;
        }
    }

    private void LoadCapture(Bitmap captured, string? savedFilePath, bool autoMaximize = true)
    {
        _savedFilePath = savedFilePath;
        _canvas.ResetState(new Bitmap(captured));
        _suppressCloseConfirm = false;
        // Auto-maximize is desirable for real captures/opened files (show the whole image),
        // but not for the blank "New" document — the user didn't ask to resize the window.
        if (autoMaximize)
            MaybeAutoMaximizeForCapture();
        UpdateCaptureCaption();
        RefreshUi();
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        var img = _canvas.PointFromScreenToImage(e.Location);
        var text = $"{img.X}, {img.Y}";
        if (_coordsLabel.Text != text)
            _coordsLabel.Text = text;
    }

    private void OnCanvasStateChanged(object? sender, EventArgs e)
    {
        // If the user switched away from Emoji, close the picker immediately so the
        // next click on a tool button is never consumed by picker deactivation.
        if (_canvas.ActiveTool != AnnotationCanvas.CanvasTool.Emoji
            && _emojiPicker is { IsDisposed: false })
        {
            _emojiPicker.Close();
        }
        RefreshUi();
    }

    private void RefreshUi()
    {
        UpdateZoomStatus();
        UpdateToolButtonState();
        UpdateCaptureCaption();
        UpdateLiveStatusText();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_emojiPicker is { IsDisposed: false })
            _emojiPicker.Close();
        if (_suppressCloseConfirm || !_canvas.IsDirty) return;
        if (!PromptSaveChanges())
            e.Cancel = true;
    }

    /// <summary>
    /// Shows a 3-button "Save changes to {document}?" dialog.
    /// Returns true when the caller should proceed (user saved or chose Don't Save),
    /// false when the user cancelled.
    /// </summary>
    private bool PromptSaveChanges()
    {
        var docName = string.IsNullOrWhiteSpace(_savedFilePath)
            ? LocalizationService.Translate("Untitled")
            : TruncateDocName(Path.GetFileName(_savedFilePath), 36);
        var message = string.Format(
            LocalizationService.Translate("Save changes to {0}?"), docName);

        var result = ThemedConfirmDialog.SavePrompt(
            Handle,
            LocalizationService.Translate("Unsaved changes"),
            message);

        switch (result)
        {
            case SavePromptResult.Save:
                return DoSave();
            case SavePromptResult.DontSave:
                return true;
            default: // Cancel
                return false;
        }
    }

    private static string TruncateDocName(string name, int maxLength)
    {
        if (name.Length <= maxLength) return name;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        int keepLen = maxLength - ext.Length - 1; // 1 for the ellipsis char
        if (keepLen < 4) keepLen = 4;
        return stem[..keepLen] + "\u2026" + ext;
    }

    // ── Actions invoked by the toolbar ─────────────────────────────────────

    private bool DoSave()
    {
        if (!_canvas.IsDirty || _canvas.IsDefaultBlank) return true;
        try
        {
            if (!string.IsNullOrWhiteSpace(_savedFilePath) && _savedFilePath.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase))
            {
                return DoSaveProject(_savedFilePath);
            }

            return DoSaveProjectAs();
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
            return false;
        }
    }

    private bool DoSaveProject(string filePath)
    {
        try
        {
            CanvasProjectService.SaveProject(
                filePath,
                _canvas.BaseBitmap,
                _canvas.Annotations,
                _canvas.HorizontalGuides,
                _canvas.VerticalGuides);

            _savedFilePath = filePath;
            _canvas.AcceptSavedProjectState();
            NotifyHistoryEditedCaptureSaved(filePath, _canvas.BaseBitmap.Width, _canvas.BaseBitmap.Height);
            UpdateCaptureCaption();
            RefreshUi();
            ShowSaveStatus(filePath);
            AddRecentFile(filePath);

            var fileName = Path.GetFileName(filePath);
            var toastTitle = filePath.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase)
                ? LocalizationService.Translate("Project saved")
                : LocalizationService.Translate("Image saved");
            var toastBody = string.Format(LocalizationService.Translate("Saved: {0}"), fileName);
            ToastWindow.Show(toastTitle, toastBody, filePath);
            return true;
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
            return false;
        }
    }

    private bool DoSaveProjectAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = $"{LocalizationService.Translate("CyberSnap Project")} (*.csnp)|*.csnp",
            FileName = string.IsNullOrWhiteSpace(_savedFilePath)
                ? $"CyberSnap_Editor_{DateTime.Now:yyyyMMdd_HHmmss}.csnp"
                : Path.ChangeExtension(Path.GetFileName(_savedFilePath), ".csnp"),
            DefaultExt = ".csnp",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        return DoSaveProject(dlg.FileName);
    }

    private void DoSaveAs()
    {
        var s = Services.SettingsService.LoadStatic();
        bool prefJpg = s?.EditorExportFormat == 1;
        bool prefPdf = s?.EditorExportFormat == 2;
        string ext = prefPdf ? ".pdf" : (prefJpg ? ".jpg" : ".png");
        string filter = prefPdf
            ? "Portable Document Format (*.pdf)|*.pdf|PNG|*.png|JPEG|*.jpg"
            : (prefJpg
                ? "JPEG|*.jpg|PNG|*.png|Portable Document Format (*.pdf)|*.pdf"
                : "PNG|*.png|JPEG|*.jpg|Portable Document Format (*.pdf)|*.pdf");

        string prefix = prefPdf ? "PDF" : (prefJpg ? "JPG" : "PNG");
        string defaultName;
        if (string.IsNullOrWhiteSpace(_savedFilePath))
        {
            defaultName = $"CyberSnap_{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        }
        else
        {
            defaultName = prefPdf && !_savedFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(_savedFilePath) + ".pdf"
                : Path.GetFileNameWithoutExtension(_savedFilePath) + $"_edited{ext}";
        }

        using var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            DefaultExt = ext,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using var output = _canvas.RenderFinal();
            if (dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                SaveAsPdf(output, dlg.FileName);
            }
            else
            {
                SaveRenderedBitmap(output, dlg.FileName);
                _savedFilePath = dlg.FileName;
                FinishSuccessfulSave(output, dlg.FileName);
            }
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
        }
    }

    private void DoSaveAs(Bitmap output)
    {
        var s = Services.SettingsService.LoadStatic();
        bool prefJpg = s?.EditorExportFormat == 1;
        bool prefPdf = s?.EditorExportFormat == 2;
        string ext = prefPdf ? ".pdf" : (prefJpg ? ".jpg" : ".png");
        string filter = prefPdf
            ? "Portable Document Format (*.pdf)|*.pdf|PNG|*.png|JPEG|*.jpg"
            : (prefJpg
                ? "JPEG|*.jpg|PNG|*.png|Portable Document Format (*.pdf)|*.pdf"
                : "PNG|*.png|JPEG|*.jpg|Portable Document Format (*.pdf)|*.pdf");

        string prefix = prefPdf ? "PDF" : (prefJpg ? "JPG" : "PNG");
        string defaultName;
        if (string.IsNullOrWhiteSpace(_savedFilePath))
        {
            defaultName = $"CyberSnap_{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        }
        else
        {
            defaultName = prefPdf && !_savedFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(_savedFilePath) + ".pdf"
                : Path.GetFileNameWithoutExtension(_savedFilePath) + $"_edited{ext}";
        }

        using var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            DefaultExt = ext,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                SaveAsPdf(output, dlg.FileName);
            }
            else
            {
                SaveRenderedBitmap(output, dlg.FileName);
                _savedFilePath = dlg.FileName;
                FinishSuccessfulSave(output, dlg.FileName);
            }
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
        }
    }

    private void SaveAsPdf(Bitmap bitmap, string filePath)
    {
        var options = ThemedPdfExportDialog.Show(Handle);
        if (options == null) return; // Cancelled by user

        try
        {
            using (var document = new PdfSharp.Pdf.PdfDocument())
            {
                document.Info.Title = options.Title;
                document.Info.Author = options.Author;
                document.Info.Keywords = options.Tags;

                var page = document.AddPage();

                // Determine size & orientation
                if (options.PageSize == "Fit")
                {
                    double imgWPoints = bitmap.Width * 0.75;
                    double imgHPoints = bitmap.Height * 0.75;
                    page.Width = PdfSharp.Drawing.XUnit.FromPoint(imgWPoints);
                    page.Height = PdfSharp.Drawing.XUnit.FromPoint(imgHPoints);
                }
                else
                {
                    if (options.PageSize == "A4")
                        page.Size = PdfSharp.PageSize.A4;
                    else if (options.PageSize == "Legal")
                        page.Size = PdfSharp.PageSize.Legal;
                    else if (options.PageSize == "A3")
                        page.Size = PdfSharp.PageSize.A3;
                    else
                        page.Size = PdfSharp.PageSize.Letter;

                    page.Orientation = options.Orientation == "Landscape"
                        ? PdfSharp.PageOrientation.Landscape
                        : PdfSharp.PageOrientation.Portrait;
                }

                // Margins in points (1 cm = 28.346 pt)
                double leftMargin = options.PageSize == "Fit" ? 0 : options.MarginLeft * 28.346;
                double rightMargin = options.PageSize == "Fit" ? 0 : options.MarginRight * 28.346;
                double topMargin = options.PageSize == "Fit" ? 0 : options.MarginTop * 28.346;
                double bottomMargin = options.PageSize == "Fit" ? 0 : options.MarginBottom * 28.346;

                double printableW = page.Width.Point - leftMargin - rightMargin;
                double printableH = page.Height.Point - topMargin - bottomMargin;

                if (printableW <= 0) printableW = 10;
                if (printableH <= 0) printableH = 10;

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    using (var xImage = PdfSharp.Drawing.XImage.FromStream(ms))
                    {
                        double imgWPoints = bitmap.Width * 0.75;
                        double imgHPoints = bitmap.Height * 0.75;

                        if (options.PageSize != "Fit" && options.ImageLayout == "Span" && imgHPoints * (printableW / imgWPoints) > printableH)
                        {
                            // Slice vertically across pages
                            double scale = printableW / imgWPoints;
                            double yOffset = 0;
                            bool firstPage = true;

                            while (yOffset < imgHPoints)
                            {
                                if (!firstPage)
                                {
                                    page = document.AddPage();
                                    page.Size = document.Pages[0].Size;
                                    page.Orientation = document.Pages[0].Orientation;
                                }
                                firstPage = false;

                                var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                                double maxSliceHImage = printableH / scale;
                                double currentSliceHImage = Math.Min(imgHPoints - yOffset, maxSliceHImage);

                                double destW = printableW;
                                double destH = currentSliceHImage * scale;

                                gfx.DrawImage(xImage,
                                    new PdfSharp.Drawing.XRect(leftMargin, topMargin, destW, destH),
                                    new PdfSharp.Drawing.XRect(0, yOffset / 0.75, bitmap.Width, currentSliceHImage / 0.75),
                                    PdfSharp.Drawing.XGraphicsUnit.Point);

                                yOffset += currentSliceHImage;
                            }
                        }
                        else
                        {
                            // Fit to page
                            var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                            double drawW = imgWPoints;
                            double drawH = imgHPoints;

                            if (options.PageSize != "Fit")
                            {
                                double scale = Math.Min(printableW / imgWPoints, printableH / imgHPoints);
                                if (scale < 1.0)
                                {
                                    drawW *= scale;
                                    drawH *= scale;
                                }
                            }

                            double destX = leftMargin + (printableW - drawW) / 2;
                            double destY = topMargin + (printableH - drawH) / 2;

                            gfx.DrawImage(xImage, destX, destY, drawW, drawH);
                        }
                    }
                }

                document.Save(filePath);
            }

            FinishSuccessfulSave(bitmap, filePath);
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "PDF Export failed", ex.Message, error: true);
        }
    }

    private static void SaveRenderedBitmap(Bitmap output, string filePath)
    {
        if (filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            output.Save(filePath, ImageFormat.Jpeg);
            return;
        }

        CaptureOutputService.SavePng(output, filePath);
    }

    private void FinishSuccessfulSave(Bitmap output, string filePath)
    {
        _savedFilePath = filePath;
        _canvas.AcceptSavedBaseline(output);
        
        bool isPdf = filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf)
        {
            NotifyHistoryEditedCaptureSaved(filePath, output.Width, output.Height);
        }

        UpdateCaptureCaption();
        RefreshUi();
        ShowSaveStatus(filePath);

        var fileName = Path.GetFileName(filePath);
        var toastTitle = filePath.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.Translate("Project saved")
            : isPdf
                ? LocalizationService.Translate("PDF saved")
                : LocalizationService.Translate("Image saved");
        var toastBody = string.Format(LocalizationService.Translate("Saved: {0}"), fileName);
        ToastWindow.Show(toastTitle, toastBody, filePath);
    }

    private static void NotifyHistoryEditedCaptureSaved(string filePath, int width, int height)
    {
        if (System.Windows.Application.Current is CyberSnap.App app)
            app.NotifyEditedCaptureSaved(filePath, width, height);
    }

    private void ShowSaveStatus(string filePath)
    {
        _saveStatusTimer.Stop();
        _saveStatusTimer.Start();
    }

    private void DoNew()
    {
        if (_canvas.IsDirty)
        {
            if (!PromptSaveChanges())
                return;
        }

        var blank = CreateBlankCheckerboard(EditorColors.IsDark);

        LoadCapture(blank, null, autoMaximize: false);
        _canvas.IsDefaultBlank = true;
        _canvas.IsBlankCanvas = true;
        // The blank document has no real pixels worth inspecting at 100%, so always frame it
        // to fit the window regardless of the user's auto-fit preference. Without this, the
        // crop handles would sit off-screen when auto-fit is disabled (we no longer maximize).
        _canvas.ZoomFit();
        _canvas.Invalidate();
    }

    private void DoResizeCanvas()
    {
        int curW = _canvas.BaseBitmap.Width;
        int curH = _canvas.BaseBitmap.Height;
        var result = ThemedResizeDialog.Show(Handle, curW, curH);
        if (result is null) return;

        // ResizeCanvas handles the blank-document case itself (regenerates the checkerboard
        // via BlankBitmapFactory), so a single call covers both blank and real images.
        _canvas.ResizeCanvas(result.Width, result.Height, result.ScaleContent, result.Anchor);
    }

    private void DoNewCanvas()
    {
        if (_canvas.IsDirty)
        {
            if (!PromptSaveChanges())
                return;
        }

        var result = ThemedNewCanvasDialog.Show(Handle);
        if (result is null) return;

        var blank = result.BackgroundColor is { } bg
            ? CreateSolidBackground(result.Width, result.Height, bg)
            : CreateBlankCheckerboard(EditorColors.IsDark, result.Width, result.Height);
        LoadCapture(blank, null, autoMaximize: false);
        _canvas.IsDefaultBlank = true;
        _canvas.IsBlankCanvas = true;
        _canvas.ZoomFit();
        _canvas.Invalidate();
    }

    private void DoOpen()
    {
        if (_canvas.IsDirty)
        {
            if (!PromptSaveChanges())
                return;
        }

        using (var dlg = new OpenFileDialog
        {
            Filter = $"{LocalizationService.Translate("CyberSnap Project")} (*.csnp)|*.csnp|Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All Files|*.*",
            Title = LocalizationService.Translate("Open Image")
        })
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    if (dlg.FileName.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase))
                    {
                        var (baseBitmap, projectData) = CanvasProjectService.LoadProject(dlg.FileName);
                        LoadCaptureProject(baseBitmap, projectData, dlg.FileName);
                    }
                    else
                    {
                        using (var stream = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read))
                        using (var tempBmp = new Bitmap(stream))
                        {
                            var captured = new Bitmap(tempBmp);
                            LoadCapture(captured, dlg.FileName);
                            AddRecentFile(dlg.FileName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ThemedConfirmDialog.Alert(Handle, "Error loading image", ex.Message, error: true);
                }
            }
        }
    }

    private void DoImport()
    {
        if (_canvas.IsDirty)
        {
            if (!PromptSaveChanges())
                return;
        }

        using (var dlg = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*",
            Title = LocalizationService.Translate("Import Image")
        })
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    // ── File size check: max 10 MB ──
                    var fileInfo = new FileInfo(dlg.FileName);
                    const long maxFileSize = 10 * 1024 * 1024; // 10 MB
                    if (fileInfo.Length > maxFileSize)
                    {
                        ThemedConfirmDialog.Alert(Handle,
                            LocalizationService.Translate("Error importing image"),
                            string.Format(LocalizationService.Translate("The file is too large ({0:F1} MB). Maximum allowed is 10 MB."), fileInfo.Length / 1024.0 / 1024.0),
                            error: true);
                        return;
                    }

                    Bitmap captured;
                    var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();

                    if (ext == ".webp")
                    {
                        // Decode WebP via WIC (available on Windows 10 19041+)
                        captured = DecodeWebP(dlg.FileName);
                    }
                    else
                    {
                        using (var stream = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read))
                        using (var tempBmp = new Bitmap(stream))
                        {
                            captured = new Bitmap(tempBmp);
                        }
                    }

                    // ── Max dimension check: 4K (4096 px on longest side) ──
                    const int maxDimension = 4096;
                    if (captured.Width > maxDimension || captured.Height > maxDimension)
                    {
                        captured.Dispose();
                        ThemedConfirmDialog.Alert(Handle,
                            LocalizationService.Translate("Error importing image"),
                            string.Format(LocalizationService.Translate("The image is too large ({0}x{1}). Maximum allowed is {2} pixels on the longest side (4K)."), captured.Width, captured.Height, maxDimension),
                            error: true);
                        return;
                    }

                    LoadCapture(captured, dlg.FileName);
                }
                catch (Exception ex)
                {
                    ThemedConfirmDialog.Alert(Handle, LocalizationService.Translate("Error importing image"), ex.Message, error: true);
                }
            }
        }
    }

    /// <summary>Decodes a WebP image using Windows Imaging Component (WIC),
    /// re-encodes to PNG in memory, then loads as a GDI+ Bitmap to avoid
    /// pixel-format mismatch issues.</summary>
    private static Bitmap DecodeWebP(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];

        // Re-encode to PNG via WPF to normalize the pixel format, then load with GDI+
        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(frame));
        using var pngStream = new MemoryStream();
        pngEncoder.Save(pngStream);
        pngStream.Position = 0;
        return new Bitmap(pngStream);
    }

    private void DoCopy()
    {
        try
        {
            using var output = _canvas.RenderFinal();
            // CopyToClipboard takes (Bitmap, filePath?). We pass the saved path if known
            // so the clipboard format also includes the file reference.
            ClipboardService.CopyToClipboard(output, _savedFilePath);

            var toastTitle = LocalizationService.Translate("System Message");
            var toastBody = LocalizationService.Translate("Copied to clipboard");
            ToastWindow.Show(toastTitle, toastBody, _savedFilePath);
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Copy failed", ex.Message, error: true);
        }
    }

    private void DoPaste()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                if (!_canvas.IsDefaultBlank)
                {
                    var s = Services.SettingsService.LoadStatic();
                    if (s?.EditorSuppressPasteConfirm != true)
                    {
                        var confirmTitle = LocalizationService.Translate("Confirm Paste");
                        var confirmMsg = LocalizationService.Translate("Pasting this image will replace your current document. Do you want to proceed?");
                        bool pasteConfirmed = ThemedConfirmDialog.Confirm(Handle, confirmTitle, confirmMsg,
                            out bool dontShowAgain, danger: false, iconId: "paste");
                        if (dontShowAgain && System.Windows.Application.Current is CyberSnap.App app)
                            app.PersistEditorSuppressPasteConfirm(true);
                        if (!pasteConfirmed)
                            return;
                    }
                }

                if (_canvas.IsDirty)
                {
                    if (!PromptSaveChanges())
                        return;
                }

                using (var img = Clipboard.GetImage())
                {
                    if (img != null)
                    {
                        var bmp = new Bitmap(img);
                        var command = new CyberSnap.Models.Commands.PasteImageCommand(bmp);
                        _canvas.Push(command);
                        _canvas.ZoomFit();
                        _canvas.IsDefaultBlank = false;
                        RefreshUi();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Paste failed", ex.Message, error: true);
        }
    }

    private void OnCanvasDoubleClick(object? sender, EventArgs e)
    {
        if (_canvas.IsDefaultBlank)
        {
            DoOpen();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N)) { DoNewCanvas(); return true; }
        if (keyData == (Keys.Control | Keys.O)) { DoOpen(); return true; }
        if (keyData == (Keys.Control | Keys.I)) { DoImport(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { DoSave(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { DoSaveAs(); return true; }
        if (keyData == (Keys.Control | Keys.C)) { DoCopy(); return true; }
        if (keyData == (Keys.Control | Keys.V)) { DoPaste(); return true; }
        if (keyData == (Keys.Control | Keys.A)) { _canvas.SelectAll(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }


    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Composite bottom-up so the nested panels present in one pass — without this the editor
            // shows an unpainted flash on open and resizes less smoothly.
            if (_enableComposited)
            {
                cp.ExStyle |= WS_EX_COMPOSITED;
            }
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Matches the WPF windows (CyberSnapWindowChrome): rounded, anti-aliased corners on
        // Windows 11. No-op on Windows 10, where UpdateWindowChromeRegion clips the corners instead.
        CyberSnap.Native.Dwm.TrySetWindowCornerPreference(Handle, CyberSnap.Native.Dwm.DWMWCP_ROUND);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ENTERSIZEMOVE)
        {
            _enableComposited = true;
            UpdateStyles();
        }
        else if (m.Msg == WM_EXITSIZEMOVE)
        {
            _enableComposited = false;
            UpdateStyles();
            RefreshLayoutAndRedraw();
        }

        base.WndProc(ref m);

        if (m.Msg != WM_NCHITTEST || _isManualMaximized || m.Result != (IntPtr)HTCLIENT)
            return;

        var point = PointToClient(GetScreenPoint(m.LParam));
        var left = point.X <= EditorPaint.ResizeHitSize;
        var right = point.X >= ClientSize.Width - EditorPaint.ResizeHitSize;
        var top = point.Y <= EditorPaint.ResizeHitSize;
        var bottom = point.Y >= ClientSize.Height - EditorPaint.ResizeHitSize;

        if (top && left) m.Result = (IntPtr)HTTOPLEFT;
        else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
        else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
        else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
        else if (left) m.Result = (IntPtr)HTLEFT;
        else if (right) m.Result = (IntPtr)HTRIGHT;
        else if (top) m.Result = (IntPtr)HTTOP;
        else if (bottom) m.Result = (IntPtr)HTBOTTOM;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        if (e.Clicks > 1)
        {
            ToggleWindowState();
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>
    /// If the freshly loaded capture is larger than the canvas can show at the current window
    /// size, maximize the editor so the most possible is visible at first glance. Only ever
    /// grows the window (never restores), so a smaller capture — or a window the user already
    /// sized — is left untouched. No-op when already maximized or when the working area offers
    /// no extra room.
    /// </summary>
    private void MaybeAutoMaximizeForCapture()
    {
        if (_isManualMaximized || _canvas is null)
            return;

        var bmp = _canvas.BaseBitmap;
        var viewport = _canvas.ClientSize;
        if (bmp is null || viewport.Width <= 0 || viewport.Height <= 0)
            return;

        if (bmp.Width <= viewport.Width && bmp.Height <= viewport.Height)
            return; // the capture already fits at 100% — nothing to gain

        var area = Screen.FromControl(this).WorkingArea;
        if (area.Width <= Width && area.Height <= Height)
            return; // already as big as the screen allows

        ApplyManualMaximize();
    }

    private void ApplyManualMaximize()
    {
        if (_isManualMaximized)
            return;

        _restoreBounds = Bounds;
        var area = Screen.FromControl(this).WorkingArea;
        _isManualMaximized = true;

        _enableComposited = true;
        UpdateStyles();

        SuspendLayout();
        try
        {
            SetBounds(area.X, area.Y, area.Width, area.Height, BoundsSpecified.All);
        }
        finally
        {
            ResumeLayout(true);
            UpdateWindowChromeRegion();

            _enableComposited = false;
            UpdateStyles();
            RefreshLayoutAndRedraw();
        }
    }

    private void RestoreManualMaximize()
    {
        if (!_isManualMaximized)
            return;

        var restoreBounds = _restoreBounds;
        if (restoreBounds.Width < MinimumSize.Width || restoreBounds.Height < MinimumSize.Height)
        {
            restoreBounds = new Rectangle(
                Left + 80,
                Top + 60,
                Math.Max(MinimumSize.Width, 1400),
                Math.Max(MinimumSize.Height, 920));
        }

        _isManualMaximized = false;

        _enableComposited = true;
        UpdateStyles();

        SuspendLayout();
        try
        {
            SetBounds(restoreBounds.X, restoreBounds.Y, restoreBounds.Width, restoreBounds.Height, BoundsSpecified.All);
        }
        finally
        {
            ResumeLayout(true);
            UpdateWindowChromeRegion();

            _enableComposited = false;
            UpdateStyles();
            RefreshLayoutAndRedraw();
        }
    }

    private void UpdateWindowChromeRegion()
    {
        if (Width <= 0 || Height <= 0 || IsDisposed)
            return;

        Region? newRegion = null;
        if (!_isManualMaximized && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // Match the WPF Settings/Gallery windows: DPI-scaled radius so the rounding looks the
            // same at any display scale. On Windows 11 leave the region null and let DWM round the
            // corners (anti-aliased) via the corner preference set in OnHandleCreated.
            int radius = (int)Math.Round(EditorPaint.WindowCornerRadius * (DeviceDpi / 96.0));
            using var path = EditorPaint.RoundedRect(new Rectangle(0, 0, Width, Height), radius);
            newRegion = new Region(path);
        }

        var oldRegion = Region;
        Region = newRegion;
        oldRegion?.Dispose();
    }

    private static Point GetScreenPoint(IntPtr lParam)
    {
        var value = unchecked((int)lParam.ToInt64());
        return new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // The "properties" verb is only honored by ShellExecuteEx when SEE_MASK_INVOKEIDLIST
    // is set; the simpler ShellExecute API cannot pass that flag and fails (SE_ERR_NOASSOC).
    private const int SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    // ── Context menus ──────────────────────────────────────────────────────

    private ContextMenuStrip? _canvasMenu;
    private ContextMenuStrip? _imageMenu;
    private ContextMenuStrip? _burgerMenu;
    private DateTime _burgerMenuLastClosed = DateTime.MinValue;
    private bool _burgerMenuNearRight;

    private void BuildContextMenus()
    {
        _canvasMenu = BuildCanvasContextMenu();
    }

    private ContextMenuStrip BuildCanvasContextMenu()
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 260);

        var headerLabel = new ToolStripLabel($"CyberSnap  {Services.UpdateService.GetCurrentVersionLabel()}")
        {
            ForeColor = UiChrome.SurfaceTextMuted,
            Font = UiChrome.ChromeFont(8.5f),
            Padding = new Padding(10, 4, 0, 2),
            AutoSize = true,
        };
        menu.Items.Add(headerLabel);
        menu.Items.Add(new ToolStripSeparator());

        var openItem = WindowsMenuRenderer.Item("Open image...", iconId: null);
        var pasteItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Paste"), iconId: "paste");
        var saveProjectAsItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Save project..."), iconId: "save");
        var resizeItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Resize canvas..."), iconId: "maximize");
        var fitItem = WindowsMenuRenderer.Item("Fit to window", iconId: null);
        var resetItem = WindowsMenuRenderer.Item("Reset zoom", iconId: null);
        var undoItem = WindowsMenuRenderer.Item("Undo", iconId: null);
        var redoItem = WindowsMenuRenderer.Item("Redo", iconId: null);
        var exitItem = WindowsMenuRenderer.Item("Exit", iconId: "close", danger: true);

        openItem.Click += (_, _) => DoOpen();
        pasteItem.Click += (_, _) => DoPaste();
        pasteItem.Enabled = Clipboard.ContainsImage();
        saveProjectAsItem.Click += (_, _) => DoSaveProjectAs();
        resizeItem.Click += (_, _) => DoResizeCanvas();
        fitItem.Click += (_, _) => _canvas.ZoomFit();
        resetItem.Click += (_, _) => _canvas.ZoomReset();
        undoItem.Click += (_, _) => _canvas.Undo();
        redoItem.Click += (_, _) => _canvas.Redo();
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(openItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(saveProjectAsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(resizeItem);
        menu.Items.Add(fitItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(undoItem);
        menu.Items.Add(redoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu);
        return menu;

    }

    private ContextMenuStrip BuildImageContextMenu()
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 260);
        var copyItem = WindowsMenuRenderer.Item("Copy", iconId: "copy");
        var pasteItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Paste"), iconId: "paste");
        var saveItem = WindowsMenuRenderer.Item("Save", iconId: "download");
        var saveProjectAsItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Save project..."), iconId: "save");
        var saveAsItem = WindowsMenuRenderer.Item("Export...", iconId: "export");
        var resizeItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Resize canvas..."), iconId: "maximize");
        var openLocItem = WindowsMenuRenderer.Item("Open location", iconId: "folder");
        var propsItem = WindowsMenuRenderer.Item("Properties", iconId: null);
        var exitItem = WindowsMenuRenderer.Item("Exit", iconId: "close", danger: true);

        copyItem.Click += (_, _) => DoCopy();
        pasteItem.Click += (_, _) => DoPaste();
        pasteItem.Enabled = Clipboard.ContainsImage();
        saveItem.Click += (_, _) => DoSave();
        saveItem.Enabled = _canvas.IsDirty && !_canvas.IsDefaultBlank;
        saveProjectAsItem.Click += (_, _) => DoSaveProjectAs();
        saveAsItem.Click += (_, _) => DoSaveAs();
        resizeItem.Click += (_, _) => DoResizeCanvas();
        openLocItem.Click += (_, _) => DoOpenLocation();
        propsItem.Click += (_, _) => DoShowProperties();
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(saveItem);
        menu.Items.Add(saveProjectAsItem);
        menu.Items.Add(saveAsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(resizeItem);
        menu.Items.Add(new ToolStripSeparator());

        bool hasPath = !string.IsNullOrWhiteSpace(_savedFilePath) && File.Exists(_savedFilePath);
        openLocItem.Visible = hasPath;
        propsItem.Visible = hasPath;
        menu.Items.Add(openLocItem);
        menu.Items.Add(propsItem);

        if (hasPath)
            menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(exitItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu);
        return menu;
    }

    private void OnCanvasMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;

        var imgPt = _canvas.PointFromScreenToImage(e.Location);
        int hit = _canvas.HitTestAnnotationInternal(imgPt);
        if (hit >= 0)
        {
            if (_canvas.ActiveTool != AnnotationCanvas.CanvasTool.Move)
            {
                _canvas.ActiveTool = AnnotationCanvas.CanvasTool.Move;
            }

            if (!_canvas.MultiSelectedIndicesInternal.Contains(hit))
            {
                _canvas.SelectedAnnotationIndexInternal = hit;
                _canvas.MultiSelectedIndicesInternal.Clear();
                _canvas.Invalidate();
            }
            ShowEditorAnnotationContextMenu(e.Location);
            return;
        }

        // Right-click escapes the active tool: it cancels any in-progress action and
        // deselects the tool instead of opening a context menu. Only when no tool is
        // active (neutral Pan state) does the canvas/image context menu appear.
        if (_canvas.TryDeselectTool())
            return;

        int hHover = _canvas.HitTestHorizontalGuide(e.Location);
        int vHover = _canvas.HitTestVerticalGuide(e.Location);
        if (hHover >= 0 || vHover >= 0)
        {
            var guideMenu = WindowsMenuRenderer.Create(showImages: true, minWidth: 180);
            var deleteItem = WindowsMenuRenderer.Item("Delete Guide", iconId: "trash", danger: true);
            var clearAllItem = WindowsMenuRenderer.Item("Clear All Guides", iconId: "trash", danger: true);

            int hIdx = hHover;
            int vIdx = vHover;

            deleteItem.Click += (s, ev) => {
                if (hIdx >= 0) _canvas.RemoveHorizontalGuideAt(hIdx);
                else if (vIdx >= 0) _canvas.RemoveVerticalGuideAt(vIdx);
                _canvas.ShowToolBanner(LocalizationService.Translate("Guide removed"));
            };

            clearAllItem.Click += (s, ev) => {
                _canvas.ClearAllGuides();
                _canvas.ShowToolBanner(LocalizationService.Translate("All Guides Cleared"));
            };

            guideMenu.Items.Add(deleteItem);
            guideMenu.Items.Add(clearAllItem);
            WindowsMenuRenderer.NormalizeItemWidths(guideMenu, 180);
            guideMenu.Show(_canvas, e.Location);
            return;
        }

        imgPt = _canvas.PointFromScreenToImage(e.Location);
        bool onImage = imgPt.X >= 0 && imgPt.Y >= 0
            && imgPt.X < _canvas.BaseBitmap.Width
            && imgPt.Y < _canvas.BaseBitmap.Height;

        if (onImage)
        {
            _imageMenu?.Dispose();
            _imageMenu = BuildImageContextMenu();
            _imageMenu.Show(_canvas, e.Location);
        }
        else
        {
            _canvasMenu ??= BuildCanvasContextMenu();
            _canvasMenu.Show(_canvas, e.Location);
        }
    }

    private void ShowEditorAnnotationContextMenu(Point clickLocation)
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 180);

        bool multi = _canvas.MultiSelectedIndicesInternal.Count > 1;
        var duplicateItem = WindowsMenuRenderer.Item(
            multi ? "Duplicate selection" : "Duplicate",
            iconId: "copy");
        duplicateItem.Click += (s, e) => _canvas.DuplicateSelectionInternal();

        var deleteItem = WindowsMenuRenderer.Item(
            multi ? "Delete selection" : "Delete",
            iconId: "trash",
            danger: true);
        deleteItem.Click += (s, e) => {
            if (_canvas.MultiSelectedIndicesInternal.Count > 1)
            {
                _canvas.DeleteMultiSelectedAnnotationsInternal();
            }
            else if (_canvas.SelectedAnnotationIndexInternal >= 0)
            {
                _canvas.DeleteAnnotationAtInternal(_canvas.SelectedAnnotationIndexInternal);
            }
        };

        menu.Items.Add(duplicateItem);
        menu.Items.Add(deleteItem);
        WindowsMenuRenderer.NormalizeItemWidths(menu, 180);

        menu.Show(_canvas, clickLocation);
    }

    private void DoOpenLocation()
    {
        if (string.IsNullOrWhiteSpace(_savedFilePath) || !File.Exists(_savedFilePath)) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{_savedFilePath}\"")
            {
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Error", ex.Message, error: true);
        }
    }

    private void DoShowProperties()
    {
        if (string.IsNullOrWhiteSpace(_savedFilePath) || !File.Exists(_savedFilePath)) return;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = Handle,
                lpVerb = "properties",
                lpFile = _savedFilePath,
                nShow = SW_SHOW
            };
            if (!ShellExecuteEx(ref info))
            {
                ThemedConfirmDialog.Alert(Handle, "Error", "Could not open the file properties dialog.", error: true);
            }
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Error", ex.Message, error: true);
        }
    }

    public void ToggleRulers(bool show)
    {
        if (_topRulerContainer != null) _topRulerContainer.Visible = show;
        if (_leftRuler != null) _leftRuler.Visible = show;
        if (_topRuler != null) _topRuler.Visible = show;
        if (_cornerBlock != null) _cornerBlock.Visible = show;
        RefreshLayoutAndRedraw();
    }
}
