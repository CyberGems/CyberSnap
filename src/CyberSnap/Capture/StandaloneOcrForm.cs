using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.Models;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone OCR activated via global hotkey or tray menu.
/// Overlays a dimmed screenshot of all monitors and lets the user drag a rectangle
/// to select a region for text extraction. Right-click or Escape to close.
/// </summary>
public sealed class StandaloneOcrForm : Form
{
    private readonly Bitmap _screenshot;
    private readonly BannerLayeredForm _banner;
    private Point _cursorPos;
    private Point _dragStart;
    private bool _isDragging;
    private bool _closed;
    private bool _isProcessing;
    private readonly System.Windows.Forms.Timer _processingAnimationTimer =
        new() { Interval = ProcessingScanRenderer.AnimationIntervalMs };
    private float _processingScanProgress;
    private long _processingStartedAtMs;
    private int _processingAnimationDurationMs = ProcessingScanRenderer.AnimationMaxDurationMs;

    // Selection rectangle (normalized)
    private Rectangle _selectionRect;
    private bool _hasSelection;
    private Rectangle _autoDetectRect;
    private bool _autoDetectActive;

    // ── Context menu ──
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _autoCopyToggle;

    public StandaloneOcrForm()
    {
        // Give the tray context menu time to fully dismiss before screenshot
        Thread.Sleep(80);

        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        KeyPreview = true;

        _processingAnimationTimer.Tick += (_, _) =>
        {
            if (!_isProcessing || _closed)
                return;

            long elapsedMs = Environment.TickCount64 - _processingStartedAtMs;
            _processingScanProgress = Math.Clamp(
                elapsedMs / (float)_processingAnimationDurationMs,
                0f,
                1f);

            Invalidate(_selectionRect);
        };

        Theme.Refresh();
        var (bmp, _) = ScreenCapture.CaptureAllScreens(includeCursor: false);
        _screenshot = bmp;

        Cursor = CursorFactory.PrecisionCursor;

        bool isDetectEnabled = false;
        try
        {
            var settings = SettingsService.LoadStatic();
            isDetectEnabled = settings != null && settings.WindowDetection != WindowDetectionMode.Off;
        }
        catch { }

        if (isDetectEnabled)
        {
            WindowDetector.RegisterIgnoredWindow(Handle);
            WindowDetector.ClearSnapshot();
            Task.Run(() => WindowDetector.SnapshotWindows(Bounds));
        }

        // ── Banner ──
        var ocrLabel = LocalizationService.Translate("OCR") + ": ";
        var ocrAction = LocalizationService.Translate("Click & drag to select text region")
            + " · " + LocalizationService.Translate("Right-click or Esc to close");
        _banner = new BannerLayeredForm(
            new BannerSegment[]
            {
                new(ocrLabel, StandaloneToolBanner.LabelColor),
                new(ocrAction, null), // theme accent
            },
            Screen.FromPoint(Cursor.Position).WorkingArea,
            iconId: "ocr");

        // ── Context menu (shown on right-click) ──
        _contextMenu = WindowsMenuRenderer.Create(showImages: true, minWidth: 260);
        _contextMenu.ShowItemToolTips = true;

        bool autoCopy = GetOcrAutoCopySetting();
        _autoCopyToggle = WindowsMenuRenderer.Item("Auto-copy OCR text");
        _autoCopyToggle.ToolTipText = LocalizationService.Translate("Copy OCR text to the clipboard (uses global Auto-copy; skips the result window)");
        _autoCopyToggle.Image = autoCopy ? FluentIcons.RenderBitmap("check",
            UiChrome.IsDark ? Color.FromArgb(75, 130, 246) : Color.FromArgb(0, 120, 215), 20, true) : null;
        _autoCopyToggle.Click += (_, _) =>
        {
            bool current = GetOcrAutoCopySetting();
            SetOcrAutoCopySetting(!current);
        };
        _contextMenu.Items.Add(_autoCopyToggle);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var closeMenuAndContinue = WindowsMenuRenderer.Item("Close menu & continue", iconId: "undo");
        closeMenuAndContinue.Click += (_, _) => { /* just close the menu */ };
        _contextMenu.Items.Add(closeMenuAndContinue);

        var exitItem = WindowsMenuRenderer.Item("Exit OCR capture", iconId: "signOutLeave", danger: true, dangerIconOnly: true);
        exitItem.Click += (_, _) => Close();
        _contextMenu.Items.Add(exitItem);

        WindowsMenuRenderer.NormalizeItemWidths(_contextMenu, minWidth: 260);

        SettingsService.OcrAutoCopyToClipboardChanged += OnOcrAutoCopyToClipboardChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SettingsService.OcrAutoCopyToClipboardChanged -= OnOcrAutoCopyToClipboardChanged;
            _processingAnimationTimer.Stop();
            _processingAnimationTimer.Dispose();
            if (IsHandleCreated)
                WindowDetector.UnregisterIgnoredWindow(Handle);
            WindowDetector.ClearSnapshot();
            _contextMenu?.Dispose();
            _banner.Dispose();
            _screenshot?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _banner.ShowFor(this);
    }

    // ── Keyboard ──

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Mouse ──

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _contextMenu.Show(this, e.Location);
            return;
        }

        if (e.Button == MouseButtons.Left && !_isProcessing)
        {
            _isDragging = true;
            _dragStart = e.Location;
            if (_autoDetectActive && !_autoDetectRect.IsEmpty)
            {
                _selectionRect = _autoDetectRect;
                _hasSelection = true;
            }
            else
            {
                _hasSelection = false;
                _selectionRect = Rectangle.Empty;
            }
            _banner.Dismiss();
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursorPos = e.Location;

        // Don't revive while dragging — the banner was dismissed on mouse-down on purpose.
        if (!_isDragging)
            _banner.DismissIfHovered(PointToScreen(e.Location));

        if (_isDragging)
        {
            _selectionRect = NormRect(_dragStart, e.Location);
            _hasSelection = _selectionRect.Width > 2 && _selectionRect.Height > 2;

            // Full invalidate to avoid ghost trails when shrinking the selection
            Invalidate();
        }
        else
        {
            bool isDetectEnabled = false;
            try
            {
                var settings = SettingsService.LoadStatic();
                isDetectEnabled = settings != null && settings.WindowDetection != WindowDetectionMode.Off;
            }
            catch { }

            if (isDetectEnabled)
            {
                var rect = WindowDetector.GetDetectionRectAtPoint(_cursorPos, Bounds, WindowDetectionMode.WindowOnly);
                if (rect.Width > 0 && rect.Height > 0)
                {
                    if (rect != _autoDetectRect)
                    {
                        _autoDetectRect = rect;
                        _autoDetectActive = true;
                        Invalidate();
                    }
                }
                else
                {
                    if (_autoDetectActive)
                    {
                        _autoDetectRect = Rectangle.Empty;
                        _autoDetectActive = false;
                        Invalidate();
                    }
                }
            }
        }

        base.OnMouseMove(e);
    }

    protected override async void OnMouseUp(MouseEventArgs e)
    {
        if (!_isDragging || e.Button != MouseButtons.Left)
        {
            base.OnMouseUp(e);
            return;
        }

        _isDragging = false;

        if (!_hasSelection || _selectionRect.Width < 5 || _selectionRect.Height < 5)
        {
            if (_autoDetectActive && !_autoDetectRect.IsEmpty)
            {
                _selectionRect = _autoDetectRect;
                _hasSelection = true;
            }
            else
            {
                // Just a click without meaningful selection — revive the instruction banner
                _banner.Revive();
                Invalidate();
                base.OnMouseUp(e);
                return;
            }
        }

        // Freeze the selection visually, start OCR processing
        _isProcessing = true;
        _processingScanProgress = 0f;
        _processingStartedAtMs = Environment.TickCount64;
        _processingAnimationDurationMs =
            ProcessingScanRenderer.GetAnimationDurationMs(_selectionRect.Height);
        _processingAnimationTimer.Start();
        Cursor = Cursors.WaitCursor;
        Invalidate();

        // Capture the selected region
        Bitmap? cropped = null;
        try
        {
            cropped = _screenshot.Clone(_selectionRect, _screenshot.PixelFormat);

            // Run OCR on background thread
            var langTag = GetOcrLanguageTag();
            string text = await Task.Run(() => OcrService.RecognizeAsync(cropped, langTag));
            var previewSource = BitmapPerf.ToBitmapSource(cropped);

            // Always let one complete scan pass before closing, while slower OCR runs add no delay.
            long elapsedMs = Environment.TickCount64 - _processingStartedAtMs;
            int remainingMs = _processingAnimationDurationMs
                - (int)Math.Min(int.MaxValue, elapsedMs);
            if (remainingMs > 0)
                await Task.Delay(remainingMs);

            // Close the overlay first so screenshot is released
            Close();

            if (!string.IsNullOrWhiteSpace(text))
            {
                SoundService.PlayTextSound();

                // Save to history
                HistoryService.QuickSaveOcr(text);

                // Count toward milestones, streak and first-OCR achievement.
                App.NotifyStandaloneCapture(isOcr: true);

                // Show result on the WPF dispatcher thread
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // Check auto-copy setting
                        bool autoCopy = GetOcrAutoCopySetting();
                        if (autoCopy)
                        {
                            try
                            {
                                System.Windows.Clipboard.SetText(text);
                                ToastWindow.Show(
                                    ToastSpec.Standard(
                                        LocalizationService.Translate("OCR copied"),
                                        FormatOcrPreview(text))
                                    with { SuppressSound = true });
                            }
                            catch (Exception clipEx)
                            {
                                AppDiagnostics.LogWarning("standalone-ocr.clipboard", clipEx.Message);
                                var window = new OcrResultWindow(text, GetSettingsService(), previewSource);
                                window.Show();
                            }
                        }
                        else
                        {
                            var window = new OcrResultWindow(text, GetSettingsService(), previewSource);
                            window.Show();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("standalone-ocr.result", ex);
                    }
                });
            }
            else
            {
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ToastWindow.Show(
                        LocalizationService.Translate("OCR"),
                        LocalizationService.Translate("No text found"));
                });
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("standalone-ocr", ex);
            Close();
        }
        finally
        {
            cropped?.Dispose();
        }

        base.OnMouseUp(e);
    }

    // ── Paint ──

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_closed) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;

        // Draw screenshot as background
        g.DrawImage(_screenshot, ClientRectangle);

        // Draw selection frame
        if (_hasSelection && !_selectionRect.IsEmpty)
        {
            SelectionFrameRenderer.DrawRectangle(g, _selectionRect);
        }
        else if (_autoDetectActive && !_autoDetectRect.IsEmpty)
        {
            SelectionFrameRenderer.DrawAutoDetectRectangle(g, _autoDetectRect);
        }

        // If processing, show a lightweight scan animation without obscuring the capture.
        if (_isProcessing && _hasSelection)
        {
            ProcessingScanRenderer.Draw(g, _selectionRect, _processingScanProgress);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        _processingAnimationTimer.Stop();
        base.OnFormClosed(e);
    }

    // ── Helpers ──

    private static Rectangle NormRect(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(a.X - b.X);
        int h = Math.Abs(a.Y - b.Y);
        return new Rectangle(x, y, w, h);
    }

    private static string GetOcrLanguageTag()
    {
        try
        {
            return SettingsService.LoadStatic()?.OcrLanguageTag ?? "auto";
        }
        catch
        {
            return "auto";
        }
    }

    private static bool GetOcrAutoCopySetting()
    {
        try
        {
            return SettingsService.LoadStatic()?.OcrAutoCopyToClipboard ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static void SetOcrAutoCopySetting(bool value)
    {
        SettingsService.SetOcrAutoCopyToClipboard(value);
    }

    private void OnOcrAutoCopyToClipboardChanged(bool value)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => OnOcrAutoCopyToClipboardChanged(value)));
            }
            catch (ObjectDisposedException) { }
            return;
        }

        if (_autoCopyToggle != null)
        {
            _autoCopyToggle.Image = value ? FluentIcons.RenderBitmap("check",
                UiChrome.IsDark ? Color.FromArgb(75, 130, 246) : Color.FromArgb(0, 120, 215), 20, true) : null;
        }
    }

    private static SettingsService GetSettingsService()
    {
        var svc = new SettingsService();
        svc.Load();
        return svc;
    }

    private static string FormatOcrPreview(string text)
    {
        var preview = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return preview.Length > 80 ? preview[..80] + "..." : preview;
    }
}
