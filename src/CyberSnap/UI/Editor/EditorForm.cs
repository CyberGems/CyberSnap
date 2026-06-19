using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
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

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var tempBmp = new Bitmap(stream))
            {
                var captured = new Bitmap(tempBmp);
                ShowEditor(captured, filePath);
            }
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show($"Failed to load image: {ex.Message}", "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
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
            _instance.RefreshUi();
            _instance._canvas.Invalidate();
        }
    }

    private static Bitmap CreateBlankCheckerboard(bool isDark)
    {
        var blank = new Bitmap(1024, 768);
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
            EditorAutoCropControls = settings?.EditorAutoCropControls ?? true,
        };
        _canvas.StateChanged += OnCanvasStateChanged;
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
        FormClosed += (_, _) =>
        {
            _saveStatusTimer.Stop();
            _saveStatusTimer.Dispose();
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
        };
        Shown += (_, _) =>
        {
            MaybeAutoMaximizeForCapture();
            UpdateWindowChromeRegion();
            Activate();
            BringToFront();
            _canvas.Focus();
            RefreshUi();

            // Disable compositing after initial render to avoid layout corruption bugs when losing focus
            _enableComposited = false;
            UpdateStyles();
            RefreshLayoutAndRedraw();
        };
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

        if (ctrl is EditorZoomHostPanel || ctrl.Parent is EditorZoomHostPanel)
        {
            ctrl.BackColor = EditorColors.TitleBar;
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

        if (ctrl is Panel panel)
        {
            panel.BackColor = EditorColors.BgPrimary;
        }
        else if (ctrl is Label label)
        {
            if (label == _dimensionsLabel)
                label.ForeColor = EditorColors.TextMuted;
            else if (label == _zoomLabel)
                label.ForeColor = EditorColors.Accent;
            else if (label == _coordsLabel || label == _fileNameLabel || label == _titleFileNameLabel)
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
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_emojiPicker is { IsDisposed: false })
            _emojiPicker.Close();
        if (_suppressCloseConfirm || !_canvas.IsDirty) return;
        var discard = ThemedConfirmDialog.Confirm(
            Handle,
            "Unsaved changes",
            "Discard changes?",
            "Discard",
            "Keep editing",
            danger: false);
        if (!discard)
            e.Cancel = true;
    }

    // ── Actions invoked by the toolbar ─────────────────────────────────────

    private void DoSave()
    {
        if (!_canvas.IsDirty || _canvas.IsDefaultBlank) return;
        try
        {
            using var output = _canvas.RenderFinal();
            if (!string.IsNullOrWhiteSpace(_savedFilePath))
            {
                SaveRenderedBitmap(output, _savedFilePath!);
            }
            else
            {
                DoSaveAs(output);
                return;
            }

            FinishSuccessfulSave(output, _savedFilePath!);
        }
        catch (Exception ex)
        {
            ThemedConfirmDialog.Alert(Handle, "Save failed", ex.Message, error: true);
        }
    }

    private void DoSaveAs()
    {
        using var output = _canvas.RenderFinal();
        DoSaveAs(output);
    }

    private void DoSaveAs(Bitmap output)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png|JPEG|*.jpg",
            FileName = string.IsNullOrWhiteSpace(_savedFilePath)
                ? $"CyberSnap_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                : Path.GetFileNameWithoutExtension(_savedFilePath) + "_edited.png",
            DefaultExt = ".png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

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
        NotifyHistoryEditedCaptureSaved(filePath, output.Width, output.Height);
        UpdateCaptureCaption();
        RefreshUi();
        ShowSaveStatus(filePath);

        var fileName = Path.GetFileName(filePath);
        var toastTitle = LocalizationService.Translate("System Message");
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
            var discard = ThemedConfirmDialog.Confirm(
                Handle,
                "Unsaved changes",
                "Discard changes?",
                "Discard",
                "Keep editing",
                danger: false);
            if (!discard)
                return;
        }

        var blank = CreateBlankCheckerboard(EditorColors.IsDark);

        LoadCapture(blank, null, autoMaximize: false);
        _canvas.IsDefaultBlank = true;
        // The blank document has no real pixels worth inspecting at 100%, so always frame it
        // to fit the window regardless of the user's auto-fit preference. Without this, the
        // crop handles would sit off-screen when auto-fit is disabled (we no longer maximize).
        _canvas.ZoomFit();
        _canvas.Invalidate();
    }

    private void DoOpen()
    {
        if (_canvas.IsDirty)
        {
            var discard = ThemedConfirmDialog.Confirm(
                Handle,
                "Unsaved changes",
                "Discard changes?",
                "Discard",
                "Keep editing",
                danger: false);
            if (!discard)
                return;
        }

        using (var dlg = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All Files|*.*",
            Title = LocalizationService.Translate("Open Image")
        })
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    using (var stream = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read))
                    using (var tempBmp = new Bitmap(stream))
                    {
                        var captured = new Bitmap(tempBmp);
                        LoadCapture(captured, dlg.FileName);
                    }
                }
                catch (Exception ex)
                {
                    ThemedConfirmDialog.Alert(Handle, "Error loading image", ex.Message, error: true);
                }
            }
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

    private void DoPaste()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
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
        if (keyData == (Keys.Control | Keys.N)) { DoNew(); return true; }
        if (keyData == (Keys.Control | Keys.O)) { DoOpen(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { DoSave(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { DoSaveAs(); return true; }
        if (keyData == (Keys.Control | Keys.C)) { DoCopy(); return true; }
        if (keyData == (Keys.Control | Keys.V)) { DoPaste(); return true; }
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
        var pasteItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Paste"), iconId: "arrow");
        var fitItem = WindowsMenuRenderer.Item("Fit to window", iconId: null);
        var resetItem = WindowsMenuRenderer.Item("Reset zoom", iconId: null);
        var undoItem = WindowsMenuRenderer.Item("Undo", iconId: null);
        var redoItem = WindowsMenuRenderer.Item("Redo", iconId: null);
        var exitItem = WindowsMenuRenderer.Item("Quit", iconId: "close", danger: true);

        openItem.Click += (_, _) => DoOpen();
        pasteItem.Click += (_, _) => DoPaste();
        pasteItem.Enabled = Clipboard.ContainsImage();
        fitItem.Click += (_, _) => _canvas.ZoomFit();
        resetItem.Click += (_, _) => _canvas.ZoomReset();
        undoItem.Click += (_, _) => _canvas.Undo();
        redoItem.Click += (_, _) => _canvas.Redo();
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(openItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new ToolStripSeparator());
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
        var copyItem = WindowsMenuRenderer.Item("Copy", iconId: null);
        var pasteItem = WindowsMenuRenderer.Item(LocalizationService.Translate("Paste"), iconId: "arrow");
        var saveItem = WindowsMenuRenderer.Item("Save", iconId: "download");
        var saveAsItem = WindowsMenuRenderer.Item("Save as...", iconId: null);
        var openLocItem = WindowsMenuRenderer.Item("Open location", iconId: "folder");
        var propsItem = WindowsMenuRenderer.Item("Properties", iconId: null);
        var exitItem = WindowsMenuRenderer.Item("Quit", iconId: "close", danger: true);

        copyItem.Click += (_, _) => DoCopy();
        pasteItem.Click += (_, _) => DoPaste();
        pasteItem.Enabled = Clipboard.ContainsImage();
        saveItem.Click += (_, _) => DoSave();
        saveAsItem.Click += (_, _) => DoSaveAs();
        openLocItem.Click += (_, _) => DoOpenLocation();
        propsItem.Click += (_, _) => DoShowProperties();
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(saveItem);
        menu.Items.Add(saveAsItem);
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

        // Right-click escapes the active tool: it cancels any in-progress action and
        // deselects the tool instead of opening a context menu. Only when no tool is
        // active (neutral Pan state) does the canvas/image context menu appear.
        if (_canvas.TryDeselectTool())
            return;

        int hHover = _canvas.HitTestHorizontalGuide(e.Location);
        int vHover = _canvas.HitTestVerticalGuide(e.Location);
        if (hHover >= 0 || vHover >= 0)
        {
            var guideMenu = WindowsMenuRenderer.Create(showImages: false, minWidth: 160);
            var deleteItem = WindowsMenuRenderer.Item("Delete Guide", iconId: null);
            var clearAllItem = WindowsMenuRenderer.Item("Clear All Guides", iconId: null);

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
            guideMenu.Show(_canvas, e.Location);
            return;
        }

        var imgPt = _canvas.PointFromScreenToImage(e.Location);
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
