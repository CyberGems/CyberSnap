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
    private const int ResizeHitSize = 8;
    private const int WindowCornerRadius = 12;
    private const int WS_EX_COMPOSITED = 0x02000000;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
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

    private readonly AnnotationCanvas _canvas;

    private string? _savedFilePath;
    private bool _suppressCloseConfirm;
    private bool _isManualMaximized;
    private Rectangle _restoreBounds;
    private readonly System.Windows.Forms.Timer _saveStatusTimer = new() { Interval = 2200 };

    /// <summary>Opens or reuses the single editor instance.</summary>
    public static void ShowEditor(Bitmap captured, string? savedFilePath = null)
    {
        if (_instance is not null && !_instance.IsDisposed)
        {
            _instance.LoadCapture(captured, savedFilePath);
            _instance.Activate();
            _instance.BringToFront();
            return;
        }
        _instance = new EditorForm(captured, savedFilePath);
        _instance.Show();
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
        MinimumSize = new Size(1040, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = EditorColors.BgPrimary;
        Font = new Font("Segoe UI Variable Text", 9f, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;

        var settings = Services.SettingsService.LoadStatic();
        _canvas = new AnnotationCanvas(new Bitmap(captured))
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.CanvasBg,
            ToolColor = EditorColors.Accent,
            StrokeWidth = settings?.StrokeWidth ?? 4f,
        };
        _canvas.StateChanged += OnCanvasStateChanged;
        _canvas.MouseMove += OnCanvasMouseMove;
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
        var canvasInner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = EditorColors.CanvasBg,
            Padding = new Padding(1),
        };
        canvasInner.Controls.Add(_canvas);
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
            Padding = new Padding(3),
        };
        windowFrame.Controls.Add(root);
        Controls.Add(windowFrame);

        ResumeLayout(false);
        PerformLayout();

        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            _saveStatusTimer.Stop();
            _saveStatusTimer.Dispose();
            _brandBitmap?.Dispose();
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
        Resize += (_, _) =>
        {
            UpdateWindowStateButton();
            UpdateWindowChromeRegion();
            if (WindowState == FormWindowState.Normal && _canvas != null)
            {
                RefreshUi();
                _canvas.Invalidate();
            }
        };
        Shown += (_, _) => { UpdateWindowChromeRegion(); Activate(); BringToFront(); _canvas.Focus(); RefreshUi(); };
    }

    private void LoadCapture(Bitmap captured, string? savedFilePath)
    {
        _savedFilePath = savedFilePath;
        _canvas.ResetState(new Bitmap(captured));
        _suppressCloseConfirm = false;
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
        RefreshUi();
    }

    private void RefreshUi()
    {
        UpdateZoomStatus();
        if (!_saveStatusTimer.Enabled)
        {
            _hintLabel.Text = "";
            _hintLabel.Visible = false;
        }
        else
        {
            _hintLabel.Visible = true;
        }
        UpdateToolButtonState();
        UpdateCaptureCaption();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_suppressCloseConfirm || !_canvas.IsDirty) return;
        var msg = LocalizationService.Translate("Discard changes?");
        var yes = LocalizationService.Translate("Discard");
        var no = LocalizationService.Translate("Keep editing");
        var result = System.Windows.Forms.MessageBox.Show(
            this,
            msg,
            Text,
            System.Windows.Forms.MessageBoxButtons.OKCancel,
            System.Windows.Forms.MessageBoxIcon.Question);
        if (result != DialogResult.OK)
            e.Cancel = true;
    }

    // ── Actions invoked by the toolbar ─────────────────────────────────────

    private void DoSave()
    {
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
            System.Windows.Forms.MessageBox.Show(this, ex.Message, LocalizationService.Translate("Save failed"),
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
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
            System.Windows.Forms.MessageBox.Show(this, ex.Message, LocalizationService.Translate("Save failed"),
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
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
        var fileName = Path.GetFileName(filePath);
        var text = string.IsNullOrWhiteSpace(fileName) ? "Saved" : $"Saved: {fileName}";
        if (_hintLabel.Text != text)
            _hintLabel.Text = text;
        _hintLabel.Visible = true;
        _saveStatusTimer.Stop();
        _saveStatusTimer.Start();
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
            System.Windows.Forms.MessageBox.Show(this, ex.Message, LocalizationService.Translate("Copy failed"),
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { DoSave(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { DoSaveAs(); return true; }
        if (keyData == (Keys.Control | Keys.C)) { DoCopy(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_COMPOSITED;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WM_NCHITTEST || _isManualMaximized || m.Result != (IntPtr)HTCLIENT)
            return;

        var point = PointToClient(GetScreenPoint(m.LParam));
        var left = point.X <= ResizeHitSize;
        var right = point.X >= ClientSize.Width - ResizeHitSize;
        var top = point.Y <= ResizeHitSize;
        var bottom = point.Y >= ClientSize.Height - ResizeHitSize;

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

    private void ApplyManualMaximize()
    {
        if (_isManualMaximized)
            return;

        _restoreBounds = Bounds;
        var area = Screen.FromControl(this).WorkingArea;
        _isManualMaximized = true;
        SuspendLayout();
        try
        {
            SetBounds(area.X, area.Y, area.Width, area.Height, BoundsSpecified.All);
        }
        finally
        {
            ResumeLayout(true);
            UpdateWindowChromeRegion();
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
        SuspendLayout();
        try
        {
            SetBounds(restoreBounds.X, restoreBounds.Y, restoreBounds.Width, restoreBounds.Height, BoundsSpecified.All);
        }
        finally
        {
            ResumeLayout(true);
            UpdateWindowChromeRegion();
        }
    }

    private void UpdateWindowChromeRegion()
    {
        if (Width <= 0 || Height <= 0 || IsDisposed)
            return;

        Region? newRegion = null;
        if (!_isManualMaximized)
        {
            using var path = EditorPaint.RoundedRect(new Rectangle(0, 0, Width, Height), WindowCornerRadius);
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
}
