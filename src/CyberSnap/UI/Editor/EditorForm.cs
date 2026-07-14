using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.Services.Upload;
using CyberSnap.UI.Controls;
using CyberSnap.UI.Editor;
using CyberSnap.UI.Share;

namespace CyberSnap.UI.Editor;

/// <summary>
/// Dedicated post-capture editor window. Hosts an <see cref="AnnotationCanvas"/>
/// with a left-side toolbar and a bottom status bar. The capture flow can either
/// open this directly after a screenshot (when the setting is enabled) or surface
/// it via the PreviewWindow "Edit" button.
/// </summary>
public sealed partial class EditorForm : Form, IMessageFilter
{
    private const int WS_EX_COMPOSITED = 0x02000000;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int DefaultEditorWidth = 1400;
    private const int DefaultEditorHeight = 920;
    private const int RestoredWindowMargin = 80;
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
    private bool _userRestoredWindow;
    private Rectangle _restoreBounds;
    private DateTime _windowStateToggleGuardUntilUtc;
    private bool _windowStateToggleInProgress;
    private FormWindowState _lastWindowState = FormWindowState.Normal;
    private bool _enableComposited = true;
    private bool _messageFilterRegistered;
    private readonly System.Windows.Forms.Timer _saveStatusTimer = new() { Interval = 2200 };
    private readonly System.Windows.Forms.Timer _clipboardMonitorTimer = new() { Interval = 1000 };

    /// <summary>Opens or reuses the single editor instance.</summary>
    /// <param name="source">Origin of the image — controls size limits and soft warnings.</param>
    /// <returns>False when the image was rejected (caller still owns cleanup of other resources).</returns>
    public static bool ShowEditor(Bitmap captured, string? savedFilePath = null, ImageOpenSource source = ImageOpenSource.Capture)
    {
        if (captured is null) throw new ArgumentNullException(nameof(captured));

        var eval = ImageOpenPolicy.EvaluateBitmap(captured, source);
        if (!eval.IsAllowed)
        {
            captured.Dispose();
            ShowImageOpenError(eval);
            return false;
        }

        if (_instance is not null && !_instance.IsDisposed)
        {
            if (_instance._canvas.IsDirty && !_instance._canvas.IsDefaultBlank)
            {
                if (!_instance.PromptSaveChanges())
                {
                    return false;
                }
            }
            _instance.LoadCapture(captured, savedFilePath, performanceWarning: eval.ShouldWarn);
            _instance.RestoreAndActivate();
            App.NotifyFirstTimeTool("editor");
            return true;
        }
        _instance = new EditorForm(captured, savedFilePath);
        _instance.Show();
        if (eval.ShouldWarn)
            _instance.ShowLargeImagePerformanceBanner(eval);
        App.NotifyFirstTimeTool("editor");
        return true;
    }

    public static void ShowEditorFromFile(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            if (filePath.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase))
            {
                var sizeEval = ImageOpenPolicy.EvaluateFileSize(filePath, ImageOpenSource.FilePath);
                if (!sizeEval.IsAllowed)
                {
                    ShowImageOpenError(sizeEval);
                    return;
                }

                var (baseBitmap, projectData) = CanvasProjectService.LoadProject(filePath);
                var dimEval = ImageOpenPolicy.EvaluateBitmap(baseBitmap, ImageOpenSource.FilePath, sizeEval.FileSizeBytes);
                if (!dimEval.IsAllowed)
                {
                    baseBitmap.Dispose();
                    ShowImageOpenError(dimEval);
                    return;
                }

                if (_instance is not null && !_instance.IsDisposed)
                {
                    if (_instance._canvas.IsDirty && !_instance._canvas.IsDefaultBlank)
                    {
                        if (!_instance.PromptSaveChanges())
                        {
                            baseBitmap.Dispose();
                            return;
                        }
                    }
                    _instance.LoadCaptureProject(baseBitmap, projectData, filePath, performanceWarning: dimEval.ShouldWarn);
                    _instance.RestoreAndActivate();
                    return;
                }
                _instance = new EditorForm(baseBitmap, filePath);
                _instance.LoadCaptureProject(baseBitmap, projectData, filePath, performanceWarning: dimEval.ShouldWarn);
                _instance.Show();
                return;
            }

            var eval = ImageOpenPolicy.EvaluateAndLoad(
                filePath,
                ImageOpenSource.FilePath,
                LoadDecoupledBitmap,
                out var captured);
            if (!eval.IsAllowed || captured is null)
            {
                ShowImageOpenError(eval);
                return;
            }

            ShowEditor(captured, filePath, ImageOpenSource.FilePath);
            if (_instance is not null)
                _instance.AddRecentFile(filePath);
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(
                _instance is { IsDisposed: false } ? _instance.Handle : IntPtr.Zero,
                LocalizationService.Translate("Error importing image"),
                ex.Message,
                error: true);
        }
    }

    private static void ShowImageOpenError(ImageOpenEvaluation eval)
    {
        IntPtr owner = _instance is { IsDisposed: false } ? _instance.Handle : IntPtr.Zero;
        ThemedConfirmDialog.Alert(owner, eval.ErrorTitle, eval.FormatErrorMessage(), error: true);
    }

    private void LoadCaptureProject(Bitmap baseBitmap, ProjectData data, string filePath, bool autoMaximize = true, bool performanceWarning = false)
    {
        _savedFilePath = filePath;
        _canvas.LoadProjectState(baseBitmap, data.Annotations, data.HorizontalGuides, data.VerticalGuides);
        _suppressCloseConfirm = false;
        if (autoMaximize)
            MaybeAutoMaximizeForCapture();
        UpdateCaptureCaption();
        RefreshUi();
        AddRecentFile(filePath);
        if (performanceWarning)
            ShowLargeImagePerformanceBanner(ImageOpenPolicy.EvaluateBitmap(baseBitmap, ImageOpenSource.FilePath));
        else
            _canvas.ShowToolBanner(LocalizationService.Translate("Project opened"));
    }

    private void ShowLargeImagePerformanceBanner(ImageOpenEvaluation eval)
    {
        // Sticky so the user has time to read; the next tool/status banner replaces it.
        _canvas.ShowToolBanner(eval.FormatPerformanceBanner(), sticky: true);
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
            Icon = WindowIcons.WinForms(WindowIconKind.Editor);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("editor.window-icon", $"Failed to load editor icon: {ex.Message}", ex);
            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch
            {
            }
        }
        Text = WindowTitles.Taskbar(WindowTitles.Editor);
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        ClientSize = new Size(1400, 920);
        MinimumSize = new Size(1400, 920);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = EditorColors.BgPrimary;
        Font = UiChrome.ChromeFont(9f, FontStyle.Regular);
        KeyPreview = true;

        var settings = Services.SettingsService.LoadStatic();
        _showTooltips = settings?.EditorShowTooltips ?? true;
        _canvas = new AnnotationCanvas(new Bitmap(captured))
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.CanvasBg,
            ToolColor = settings != null ? Color.FromArgb(settings.EditorToolColorArgb) : EditorColors.Accent,
            StrokeWidth = settings?.StrokeWidth ?? 4f,
            TextFontSize = settings?.EditorTextFontSize ?? 24f,
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
        if (settings != null)
        {
            _canvas.ApplyTextStyle(
                settings.EditorTextFontSize,
                settings.EditorTextFontFamily ?? "Segoe UI",
                settings.EditorTextBold,
                settings.EditorTextItalic,
                settings.EditorTextStroke,
                settings.EditorTextShadow,
                settings.EditorTextBackground,
                settings.EditorTextAlignment);
            _canvas.LoadRecentFonts(settings.EditorTextRecentFonts, settings.EditorTextFavoriteFonts);
        }
        _canvas.TextFontSizeChanged += size =>
        {
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.PersistEditorTextFontSize(size);
        };
        _canvas.TextStyleChanged += (size, family, bold, italic, stroke, shadow, bg, align) =>
        {
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.PersistEditorTextStyle(size, family, bold, italic, stroke, shadow, bg, align);
        };
        _canvas.FavoriteFontsChanged += serialized =>
        {
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.PersistEditorTextFavoriteFonts(serialized);
        };
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp += OnCanvasMouseUp;
        _canvas.DoubleClick += OnCanvasDoubleClick;
        _canvas.EmojiPlacementRequested += (_, _) => OpenEmojiPicker(GetEmojiToolButton());
        RegisterCanvasMessageFilter();
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

        AllowDrop = true;
        DragEnter += OnEditorDragEnter;
        DragDrop += OnEditorDragDrop;

        windowFrame.AllowDrop = true;
        windowFrame.DragEnter += OnEditorDragEnter;
        windowFrame.DragDrop += OnEditorDragDrop;

        root.AllowDrop = true;
        root.DragEnter += OnEditorDragEnter;
        root.DragDrop += OnEditorDragDrop;

        _canvas.AllowDrop = true;
        _canvas.DragEnter += OnEditorDragEnter;
        _canvas.DragDrop += OnEditorDragDrop;

        BuildContextMenus();

        ResumeLayout(false);
        PerformLayout();

        FormClosing += OnFormClosing;
        Activated += (_, _) =>
        {
            if (IsDisposed || Disposing || !Visible || _canvas.IsDisposed)
                return;
            RefreshUi();
        };
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
            _shareMenu?.Dispose();
            try { _shareCts?.Cancel(); } catch { }
            _shareCts?.Dispose();
            if (ReferenceEquals(_instance, this)) _instance = null;
            UnregisterCanvasMessageFilter();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized
                || _lastWindowState == FormWindowState.Minimized)
            {
                DismissVisibleHoverTooltips();
            }

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
            RefreshUi();
            UpdateStatusBarResponsiveLayout();

            // Disable compositing after initial render to avoid layout corruption bugs when losing focus
            _enableComposited = false;
            UpdateStyles();
            RefreshLayoutAndRedraw();

            _canvas.Focus();
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

    public void SetShowWelcomeBanner(bool show)
    {
        if (_canvas is null) return;
        _canvas.ShowWelcomeBanner = show;
        _canvas.Invalidate();
    }

    public void SetShowCoordinates(bool show)
    {
        if (_coordsPanel is null) return;
        if (_coordsPanel.Visible == show) return;
        _coordsPanel.Visible = show;
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

    private void LoadCapture(
        Bitmap captured,
        string? savedFilePath,
        bool autoMaximize = true,
        bool showOpenedBanner = true,
        bool performanceWarning = false)
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
        if (performanceWarning)
        {
            ShowLargeImagePerformanceBanner(ImageOpenPolicy.EvaluateBitmap(captured, ImageOpenSource.Capture));
        }
        else if (!string.IsNullOrEmpty(savedFilePath) && showOpenedBanner)
        {
            _canvas.ShowToolBanner(LocalizationService.Translate("Image opened"));
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_coordsPanel is not { Visible: true }) return;
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
        if (IsDisposed || Disposing || _canvas.IsDisposed)
            return;

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
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

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
            _canvas.ShowToolBanner(toastTitle);
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
            Title = LocalizationService.Translate("Save project as..."),
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
        DoSaveAsWithExtension(ext);
    }

    private void ExportAsPdfFlow(Bitmap output)
    {
        var options = ThemedPdfExportDialog.Show(Handle, output);
        if (options == null) return; // Cancelled by user

        string filter = "Portable Document Format (*.pdf)|*.pdf|PNG|*.png|JPEG|*.jpg";
        string defaultName = string.IsNullOrWhiteSpace(_savedFilePath)
            ? $"CyberSnap_PDF_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
            : Path.GetFileNameWithoutExtension(_savedFilePath) + ".pdf";

        using var dlg = new SaveFileDialog
        {
            Title = LocalizationService.Translate("Export canvas as..."),
            Filter = filter,
            FileName = defaultName,
            DefaultExt = ".pdf",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            SaveAsPdf(output, dlg.FileName, options);
        }
        else
        {
            try
            {
                SaveRenderedBitmap(output, dlg.FileName);
                _savedFilePath = dlg.FileName;
                FinishSuccessfulSave(output, dlg.FileName);
            }
            catch (Exception ex)
            {
                ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
            }
        }
    }

    private void DoSaveAsWithExtension(string ext)
    {
        bool isPdf = ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        if (isPdf)
        {
            try
            {
                using var output = _canvas.RenderFinal();
                ExportAsPdfFlow(output);
            }
            catch (Exception ex)
            {
                ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
            }
            return;
        }

        bool isJpg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

        string filter = isJpg
            ? "JPEG|*.jpg|PNG|*.png|Portable Document Format (*.pdf)|*.pdf"
            : "PNG|*.png|JPEG|*.jpg|Portable Document Format (*.pdf)|*.pdf";

        string prefix = isJpg ? "JPG" : "PNG";
        string defaultName;
        if (string.IsNullOrWhiteSpace(_savedFilePath))
        {
            defaultName = $"CyberSnap_{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        }
        else
        {
            defaultName = Path.GetFileNameWithoutExtension(_savedFilePath) + $"_edited{ext}";
        }

        using var dlg = new SaveFileDialog
        {
            Title = LocalizationService.Translate("Export canvas as..."),
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
            Title = LocalizationService.Translate("Export canvas as..."),
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
        var options = ThemedPdfExportDialog.Show(Handle, bitmap);
        if (options == null) return; // Cancelled by user
        SaveAsPdf(bitmap, filePath, options);
    }

    private void SaveAsPdf(Bitmap bitmap, string filePath, PdfExportOptions options)
    {
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

                        bool shouldSpan = options.PageSize != "Fit" && options.ImageLayout == "Span" && 
                            (options.FitToPage ? (imgHPoints * (printableW / imgWPoints) > printableH) : (imgHPoints > printableH));

                        if (shouldSpan)
                        {
                            // Slice vertically across pages
                            double scale = options.FitToPage ? (printableW / imgWPoints) : 1.0;
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

                                double destW = options.FitToPage ? printableW : imgWPoints;
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
                            // Fit/Draw on page
                            var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                            double drawW = imgWPoints;
                            double drawH = imgHPoints;

                            if (options.PageSize != "Fit" && options.FitToPage)
                            {
                                double scale = Math.Min(printableW / imgWPoints, printableH / imgHPoints);
                                drawW *= scale;
                                drawH *= scale;
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
        bool isPngOrJpg = filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                          filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                          filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        if (isPdf || isPngOrJpg)
        {
            toastBody += "\n" + LocalizationService.Translate("Click to open");
        }

        if (isPngOrJpg)
        {
            ToastWindow.Show(new ToastSpec
            {
                Title = toastTitle,
                Body = toastBody,
                FilePath = filePath,
                ClickActionUrl = filePath,
                ClickActionLabel = "Click to open in default viewer",
                IsSystemMessage = true
            });
        }
        else
        {
            ToastWindow.Show(toastTitle, toastBody, filePath);
        }

        // Show native action banner
        string bannerMsg = isPdf 
            ? LocalizationService.Translate("PDF exported") 
            : (filePath.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase) 
                ? LocalizationService.Translate("Project saved") 
                : LocalizationService.Translate("Image exported"));
        _canvas.ShowToolBanner(bannerMsg);
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
        _canvas.DismissWelcomeOverlay();
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
        _canvas.DismissWelcomeOverlay();
        _canvas.Invalidate();
        _canvas.ShowToolBanner(LocalizationService.Translate("New canvas created"));
    }

    private void DoOpen()
    {
        if (_canvas.IsDefaultBlank)
            _canvas.DismissWelcomeOverlay();

        if (_canvas.IsDirty)
        {
            if (!PromptSaveChanges())
                return;
        }

        using (var dlg = new OpenFileDialog
        {
            Filter = $"{LocalizationService.Translate("All Supported Files")} (*.csnp, *.png, *.jpg...)|*.csnp;*.png;*.jpg;*.jpeg;*.bmp;*.webp|" +
                     $"{LocalizationService.Translate("CyberSnap Project")} (*.csnp)|*.csnp|" +
                     $"{LocalizationService.Translate("Image Files")}|*.png;*.jpg;*.jpeg;*.bmp;*.webp|" +
                     $"{LocalizationService.Translate("All Files")} (*.*)|*.*",
            Title = LocalizationService.Translate("Open a CyberSnap project or image")
        })
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    if (dlg.FileName.EndsWith(".csnp", StringComparison.OrdinalIgnoreCase))
                    {
                        var sizeEval = ImageOpenPolicy.EvaluateFileSize(dlg.FileName, ImageOpenSource.UserImport);
                        if (!sizeEval.IsAllowed)
                        {
                            ThemedConfirmDialog.Alert(Handle, sizeEval.ErrorTitle, sizeEval.FormatErrorMessage(), error: true);
                            return;
                        }

                        var (baseBitmap, projectData) = CanvasProjectService.LoadProject(dlg.FileName);
                        var dimEval = ImageOpenPolicy.EvaluateBitmap(baseBitmap, ImageOpenSource.UserImport, sizeEval.FileSizeBytes);
                        if (!dimEval.IsAllowed)
                        {
                            baseBitmap.Dispose();
                            ThemedConfirmDialog.Alert(Handle, dimEval.ErrorTitle, dimEval.FormatErrorMessage(), error: true);
                            return;
                        }

                        LoadCaptureProject(baseBitmap, projectData, dlg.FileName, performanceWarning: dimEval.ShouldWarn);
                    }
                    else
                    {
                        var eval = ImageOpenPolicy.EvaluateAndLoad(
                            dlg.FileName,
                            ImageOpenSource.UserImport,
                            LoadDecoupledBitmap,
                            out var captured);
                        if (!eval.IsAllowed || captured is null)
                        {
                            ThemedConfirmDialog.Alert(Handle, eval.ErrorTitle, eval.FormatErrorMessage(), error: true);
                            return;
                        }

                        LoadCapture(captured, dlg.FileName, performanceWarning: eval.ShouldWarn);
                        captured.Dispose();
                        AddRecentFile(dlg.FileName);
                    }
                }
                catch (Exception ex)
                {
                    ThemedConfirmDialog.Alert(Handle, "Error loading image", ex.Message, error: true);
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
        var pngStream = new MemoryStream();
        pngEncoder.Save(pngStream);
        pngStream.Position = 0;
        return new Bitmap(pngStream);
    }

    private static Bitmap LoadDecoupledBitmap(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".webp")
        {
            return DecodeWebP(filePath);
        }
        else
        {
            var bytes = File.ReadAllBytes(filePath);
            var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
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

    private void DoShare(UploadProviderKind? providerOverride = null)
    {
        if (_shareInProgress) return;
        _ = RunShareAsync(providerOverride);
    }

    private async Task RunShareAsync(UploadProviderKind? providerOverride)
    {
        var settings = SettingsService.LoadStatic() ?? new AppSettings();
        var provider = providerOverride ?? ImageUploadService.GetDefaultProvider(settings);

        if (!ImageShareFlow.ConfirmThirdPartyUploadIfNeeded(null, Handle, provider, settings))
            return;

        _shareInProgress = true;
        SetShareButtonsEnabled(false);
        _shareCts?.Cancel();
        _shareCts = new CancellationTokenSource();
        var cts = _shareCts;

        try
        {
            using var output = _canvas.RenderFinal();
            var result = await ImageShareFlow.ShareBitmapAsync(output, provider, cts.Token)
                .ConfigureAwait(true);
            ImageShareFlow.PresentResult(result, settings);
        }
        catch (OperationCanceledException)
        {
            ToastWindow.Show(
                LocalizationService.Translate("Upload cancelled"),
                "");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("editor.share", ex);
            ToastWindow.ShowError(
                LocalizationService.Translate("Upload failed"),
                ex.Message);
        }
        finally
        {
            _shareInProgress = false;
            SetShareButtonsEnabled(true);
        }
    }

    private void SetShareButtonsEnabled(bool enabled)
    {
        if (_shareButton is not null) _shareButton.Enabled = enabled;
        if (_shareMenuButton is not null) _shareMenuButton.Enabled = enabled;
    }

    private void ShowShareMenu()
    {
        // Chevron tooltip sits under the dropdown and steals hover / z-order — dismiss first.
        DismissVisibleHoverTooltips();
        _shareMenu?.Dispose();
        _shareMenu = BuildShareMenu();

        _shareMenuButton.PressedOverride = true;
        _shareButton.HoverOverride = true;

        _shareMenu.Closed += (s, e) =>
        {
            _shareMenuButton.PressedOverride = false;
            _shareButton.HoverOverride = false;
            _shareButton.PressedOverride = false;
            StopShareMenuHoverTimer();
        };

        var pt = _shareMenuButton.PointToScreen(new Point(0, _shareMenuButton.Height));
        _shareMenu.Show(pt);
    }

    private ContextMenuStrip BuildShareMenu()
    {
        var menu = WindowsMenuRenderer.Create();
        PopulateShareMenuItems(menu.Items);
        WindowsMenuRenderer.NormalizeItemWidths(menu);
        return menu;
    }

    private void ShowExportMenu()
    {
        DismissVisibleHoverTooltips();
        _exportMenu?.Dispose();
        _exportMenu = BuildExportMenu();

        _exportMenuButton.PressedOverride = true;
        _exportButton.HoverOverride = true;

        _exportMenu.Closed += (s, e) =>
        {
            _exportMenuButton.PressedOverride = false;
            _exportButton.HoverOverride = false;
            _exportButton.PressedOverride = false;
            StopExportMenuHoverTimer();
        };

        var pt = _exportMenuButton.PointToScreen(new Point(0, _exportMenuButton.Height));
        _exportMenu.Show(pt);
    }

    private ContextMenuStrip BuildExportMenu()
    {
        var menu = WindowsMenuRenderer.Create();

        var saveItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Save"), shortcut: "Ctrl+S");
        saveItem.Click += (_, _) => DoSave();
        menu.Items.Add(saveItem);

        var saveAsItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Save as..."), shortcut: "Ctrl+Shift+S");
        saveAsItem.Click += (_, _) => DoSaveAs();
        menu.Items.Add(saveAsItem);

        menu.Items.Add(new ToolStripSeparator());

        var s = Services.SettingsService.LoadStatic();
        int activeFormat = s?.EditorExportFormat ?? 0;

        var pngItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Export as PNG"));
        pngItem.Click += (_, _) => {
            Services.SettingsService.SetEditorExportFormat(0);
            DoSaveAsWithExtension(".png");
        };
        if (activeFormat == 0) pngItem.Checked = true;
        menu.Items.Add(pngItem);

        var jpgItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Export as JPEG"));
        jpgItem.Click += (_, _) => {
            Services.SettingsService.SetEditorExportFormat(1);
            DoSaveAsWithExtension(".jpg");
        };
        if (activeFormat == 1) jpgItem.Checked = true;
        menu.Items.Add(jpgItem);

        var pdfItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Export as PDF"));
        pdfItem.Click += (_, _) => {
            Services.SettingsService.SetEditorExportFormat(2);
            DoSaveAsWithExtension(".pdf");
        };
        if (activeFormat == 2) pdfItem.Checked = true;
        menu.Items.Add(pdfItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu);
        return menu;
    }

    private void RebuildShareToSubmenu(ToolStripMenuItem shareToItem)
    {
        shareToItem.DropDownItems.Clear();
        PopulateShareMenuItems(shareToItem.DropDownItems);
        WindowsMenuRenderer.NormalizeDropDownWidths(shareToItem);
    }

    private void PopulateShareMenuItems(ToolStripItemCollection items)
    {
        var settings = SettingsService.LoadStatic() ?? new AppSettings();
        var defaultKind = ImageUploadService.GetDefaultProvider(settings);
        var providers = ImageUploadService.GetMenuProviders(settings);

        foreach (var (kind, available, label) in providers)
        {
            if (kind == UploadProviderKind.Custom)
            {
                items.Add(new ToolStripSeparator());
            }

            var display = label;
            var item = WindowsMenuRenderer.Item(display);
            item.Enabled = available || kind == UploadProviderKind.Custom;
            if (kind == defaultKind)
            {
                item.Checked = true;
            }

            if (!available)
            {
                item.ToolTipText = LocalizationService.Translate("Configure in Settings…");
                item.Click += (_, _) =>
                {
                    if (System.Windows.Application.Current is CyberSnap.App app)
                        app.ShowSettings("uploads");
                };
            }
            else
            {
                var captured = kind;
                item.Click += (_, _) =>
                {
                    Services.SettingsService.SetUploadDefaultProvider(captured);
                    DoShare(captured);
                };
            }
            items.Add(item);
        }

        items.Add(new ToolStripSeparator());
        var settingsItem = WindowsMenuRenderer.Item(
            LocalizationService.Translate("Upload settings…"),
            iconId: "gear");
        settingsItem.Click += (_, _) =>
        {
            if (System.Windows.Application.Current is CyberSnap.App app)
                app.ShowSettings("uploads");
        };
        items.Add(settingsItem);
    }

    private void DoPaste()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                if (_canvas.IsDefaultBlank)
                    _canvas.DismissWelcomeOverlay();

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
                        var eval = ImageOpenPolicy.EvaluateBitmap(bmp, ImageOpenSource.Clipboard);
                        if (!eval.IsAllowed)
                        {
                            bmp.Dispose();
                            ThemedConfirmDialog.Alert(Handle, eval.ErrorTitle, eval.FormatErrorMessage(), error: true);
                            return;
                        }

                        var command = new CyberSnap.Models.Commands.PasteImageCommand(bmp);
                        bmp.Dispose(); // command owns its own clone
                        _canvas.Push(command);
                        _canvas.ZoomFit();
                        _canvas.IsDefaultBlank = false;
                        RefreshUi();
                        if (eval.ShouldWarn)
                            ShowLargeImagePerformanceBanner(eval);
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
        // Never open a document while editing text (double-click selects a word instead),
        // or when the click landed on an annotation / non-pristine canvas.
        if (_canvas.IsEditingText) return;
        if (!_canvas.IsDefaultBlank) return;

        // Only the empty welcome canvas uses double-click-to-browse.
        _canvas.DismissWelcomeOverlay();
        DoOpen();
    }

    private void RegisterCanvasMessageFilter()
    {
        if (_messageFilterRegistered) return;
        System.Windows.Forms.Application.AddMessageFilter(this);
        _messageFilterRegistered = true;
    }

    private void UnregisterCanvasMessageFilter()
    {
        if (!_messageFilterRegistered) return;
        System.Windows.Forms.Application.RemoveMessageFilter(this);
        _messageFilterRegistered = false;
    }

    /// <summary>
    /// Intercepts canvas double-clicks before WinForms routes them through nested panels.
    /// Pick uses the same SelectAll contract as capture Move mode.
    /// </summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        if (!IsDisposed && m.Msg is 0x100 or 0x104
            && _hoverToolTip is { Visible: true, IsDisposed: false })
        {
            return HandleKeyPressedWithTooltipVisible(ref m);
        }

        if (m.Msg != WM_LBUTTONDBLCLK || IsDisposed || _canvas.IsDisposed) return false;
        if (m.HWnd != _canvas.Handle) return false;
        if (_canvas.ActiveTool != AnnotationCanvas.CanvasTool.Move) return false;

        _canvas.SelectAllFromDoubleClick();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.N)) { DoNewCanvas(); return true; }
        if (keyData == (Keys.Control | Keys.O)) { DoOpen(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { DoSave(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { DoSaveAs(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.U)) { DoShare(); return true; }
        if (keyData == (Keys.Control | Keys.C)) { DoCopy(); return true; }
        if (keyData == (Keys.Control | Keys.V)) { DoPaste(); return true; }
        if (keyData == (Keys.Control | Keys.A)) { _canvas.SelectAll(); return true; }

        // Canvas shortcuts: mirrored from AnnotationCanvas.OnKeyDown so they work
        // even when the canvas does not have keyboard focus (WPF-hosted WinForms issue).
        if (keyData == (Keys.Control | Keys.Z)) { _canvas.Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Z) || keyData == (Keys.Control | Keys.Y))
        { _canvas.Redo(); return true; }
        if (keyData == (Keys.Control | Keys.D)) { _canvas.DuplicateSelectionInternal(); return true; }
        if (keyData == Keys.Delete && !_canvas.IsEditingText)
        {
            _canvas.DeleteSelected();
            return true;
        }

        // Inline text Esc must commit/cancel directly — do not rely on SendMessage, which
        // re-enters ProcessKeyPreview and never reaches AnnotationCanvas.OnKeyDown.
        if (keyData == Keys.Escape)
        {
            _canvas.ProcessEscapeKey();
            return true;
        }

        // Ensure canvas gets focus for single-key shortcuts.
        if (!_canvas.Focused && (keyData == Keys.Space || keyData == Keys.Delete
            || EditorViewHotkeyHelper.IsAnyViewHotkey(keyData)))
        {
            _canvas.Focus();
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessKeyPreview(ref Message m)
    {
        if (TryProcessEditorKeyPreview(ref m))
            return true;
        if (TryForwardKeyDownToCanvas(ref m))
            return true;
        return base.ProcessKeyPreview(ref m);
    }

    /// <summary>
    /// When a TopMost tooltip steals keyboard input, dismiss it and route the key on this press.
    /// </summary>
    private bool HandleKeyPressedWithTooltipVisible(ref Message m)
    {
        DismissVisibleHoverTooltips();
        Activate();
        _canvas.Focus();
        if (TryProcessEditorKeyPreview(ref m))
            return true;
        if (TryForwardKeyDownToCanvas(ref m))
            return true;
        // Tooltip owned the key target — consume so it is not lost to a hidden hwnd.
        return true;
    }

    /// <summary>
    /// Delivers keys handled in <see cref="AnnotationCanvas.OnKeyDown"/> when only focus
    /// was restored in <see cref="TryProcessEditorKeyPreview"/> (Space pan, Delete).
    /// Escape is routed via <see cref="AnnotationCanvas.ProcessEscapeKey"/> directly.
    /// </summary>
    private bool TryForwardKeyDownToCanvas(ref Message m)
    {
        if (m.Msg is not (0x100 or 0x104))
            return false;

        var key = (Keys)(int)m.WParam;
        var mod = Control.ModifierKeys;
        if (mod != Keys.None)
            return false;
        if (_canvas.IsDisposed || !_canvas.IsHandleCreated)
            return false;

        if (key == Keys.Escape)
        {
            _canvas.ProcessEscapeKey();
            return true;
        }

        if (key is not (Keys.Space or Keys.Delete))
            return false;

        CyberSnap.Native.User32.SendMessage(_canvas.Handle, m.Msg, m.WParam, m.LParam);
        return true;
    }

    /// <summary>
    /// Shared editor shortcut routing for <see cref="ProcessKeyPreview"/> and
    /// <see cref="IMessageFilter.PreFilterMessage"/> (tooltip was blocking the first keypress).
    /// </summary>
    private bool TryProcessEditorKeyPreview(ref Message m)
    {
        if (m.Msg is not (0x100 or 0x104))
            return false;

        var key = (Keys)(int)m.WParam;
        var mod = Control.ModifierKeys;

        if (mod == Keys.Control && key is Keys.N) { DoNewCanvas(); return true; }
        if (mod == Keys.Control && key is Keys.O) { DoOpen(); return true; }
        if (mod == Keys.Control && key is Keys.S) { DoSave(); return true; }
        if (mod == (Keys.Control | Keys.Shift) && key is Keys.S) { DoSaveAs(); return true; }
        if (mod == (Keys.Control | Keys.Shift) && key is Keys.U) { DoShare(); return true; }
        if (mod == Keys.Control && key is Keys.C) { DoCopy(); return true; }
        if (mod == Keys.Control && key is Keys.V) { DoPaste(); return true; }

        if (mod == Keys.Control && key is Keys.Z)
        {
            if (mod.HasFlag(Keys.Shift)) _canvas.Redo(); else _canvas.Undo();
            return true;
        }
        if (mod == Keys.Control && key is Keys.Y) { _canvas.Redo(); return true; }
        if (mod == Keys.Control && key is Keys.D) { _canvas.DuplicateSelectionInternal(); return true; }
        if (mod == Keys.Control && key is Keys.A) { _canvas.SelectAll(); return true; }

        if (mod == Keys.None && key == Keys.Delete && !_canvas.IsEditingText)
        {
            _canvas.DeleteSelected();
            return true;
        }

        if (!EditorToolHotkeyHelper.IsReservedEditorChord(key | mod)
            && EditorToolHotkeyHelper.TryActivateTool(_canvas, key | mod))
            return true;

        if (!EditorToolHotkeyHelper.IsReservedEditorChord(key | mod))
        {
            var e = new KeyEventArgs(key | mod);
            if (EditorViewHotkeyHelper.TryHandleViewHotkeys(_canvas, e))
                return true;
        }

        if (key is Keys.Space && !_canvas.Focused) { _canvas.Focus(); return false; }
        if ((key is Keys.Delete || EditorViewHotkeyHelper.IsAnyViewHotkey(key | mod))
            && !_canvas.Focused)
        {
            _canvas.Focus();
        }

        return false;
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
        if (_isManualMaximized || _userRestoredWindow || _canvas is null)
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

        ApplyManualMaximize(userInitiated: false);
    }

    private void ApplyManualMaximize(bool userInitiated = true)
    {
        if (_isManualMaximized)
            return;

        _restoreBounds = Bounds;
        var area = Screen.FromControl(this).WorkingArea;
        _isManualMaximized = true;
        if (userInitiated)
            _userRestoredWindow = false;

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

        var restoreArea = Screen.FromRectangle(_restoreBounds).WorkingArea;
        var restoreBounds = NormalizeRestoredBounds(_restoreBounds, restoreArea, MinimumSize);
        if (restoreBounds.Width < MinimumSize.Width || restoreBounds.Height < MinimumSize.Height)
        {
            restoreBounds = new Rectangle(
                Left + 80,
                Top + 60,
                Math.Max(MinimumSize.Width, DefaultEditorWidth),
                Math.Max(MinimumSize.Height, DefaultEditorHeight));
        }

        _isManualMaximized = false;
        _userRestoredWindow = true;

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

    private static Size GetEditorRestoredSize(Rectangle workingArea, Size minimumSize)
    {
        int width = Math.Min(DefaultEditorWidth, Math.Max(minimumSize.Width, workingArea.Width - RestoredWindowMargin));
        int height = Math.Min(DefaultEditorHeight, Math.Max(minimumSize.Height, workingArea.Height - RestoredWindowMargin));
        return new Size(width, height);
    }

    private static Rectangle NormalizeRestoredBounds(Rectangle bounds, Rectangle workingArea, Size minimumSize)
    {
        bool tooSmall = bounds.Width < minimumSize.Width || bounds.Height < minimumSize.Height;
        bool tooLarge = bounds.Width >= workingArea.Width - 8 || bounds.Height >= workingArea.Height - 8;
        bool offscreen = !workingArea.IntersectsWith(bounds);
        if (tooSmall || tooLarge || offscreen)
        {
            var size = GetEditorRestoredSize(workingArea, minimumSize);
            return new Rectangle(
                workingArea.Left + Math.Max(0, (workingArea.Width - size.Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - size.Height) / 2),
                size.Width,
                size.Height);
        }

        int width = Math.Min(bounds.Width, workingArea.Width);
        int height = Math.Min(bounds.Height, workingArea.Height);
        int x = Math.Clamp(bounds.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        int y = Math.Clamp(bounds.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        return new Rectangle(x, y, width, height);
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

    private void OnEditorDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data is not null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effect = DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnEditorDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (_canvas.IsDefaultBlank)
            _canvas.DismissWelcomeOverlay();

        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is null || files.Length == 0)
            return;

        var filePath = files[0];
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var isProject = ext == ".csnp";
            var supportedExtensions = new[] { ".csnp", ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
            
            if (Array.IndexOf(supportedExtensions, ext) < 0)
            {
                ThemedConfirmDialog.Alert(Handle,
                    LocalizationService.Translate("Error importing image"),
                    LocalizationService.Translate("Unsupported file format. Please drop a .csnp project or a supported image (.png, .jpg, .jpeg, .bmp, .webp)."),
                    error: true);
                return;
            }

            if (isProject)
            {
                var sizeEval = ImageOpenPolicy.EvaluateFileSize(filePath, ImageOpenSource.UserImport);
                if (!sizeEval.IsAllowed)
                {
                    ThemedConfirmDialog.Alert(Handle, sizeEval.ErrorTitle, sizeEval.FormatErrorMessage(), error: true);
                    return;
                }

                if (_canvas.IsDirty)
                {
                    if (!PromptSaveChanges())
                        return;
                }

                var (baseBitmap, projectData) = CanvasProjectService.LoadProject(filePath);
                var dimEval = ImageOpenPolicy.EvaluateBitmap(baseBitmap, ImageOpenSource.UserImport, sizeEval.FileSizeBytes);
                if (!dimEval.IsAllowed)
                {
                    baseBitmap.Dispose();
                    ThemedConfirmDialog.Alert(Handle, dimEval.ErrorTitle, dimEval.FormatErrorMessage(), error: true);
                    return;
                }

                LoadCaptureProject(baseBitmap, projectData, filePath, autoMaximize: false, performanceWarning: dimEval.ShouldWarn);
            }
            else
            {
                if (_canvas.IsDirty)
                {
                    if (!PromptSaveChanges())
                        return;
                }

                var eval = ImageOpenPolicy.EvaluateAndLoad(
                    filePath,
                    ImageOpenSource.UserImport,
                    LoadDecoupledBitmap,
                    out var captured);
                if (!eval.IsAllowed || captured is null)
                {
                    ThemedConfirmDialog.Alert(Handle, eval.ErrorTitle, eval.FormatErrorMessage(), error: true);
                    return;
                }

                LoadCapture(captured, filePath, autoMaximize: false, performanceWarning: eval.ShouldWarn);
                captured.Dispose();
                AddRecentFile(filePath);
            }
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, LocalizationService.Translate("Error importing image"), ex.Message, error: true);
        }
    }
}
