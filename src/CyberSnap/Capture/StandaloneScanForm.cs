using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.Models;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone QR & Barcode scanner activated via global hotkey or tray menu.
/// Overlays a dimmed screenshot of all monitors and lets the user drag a rectangle
/// to select a region for barcode decoding. Right-click or Escape to close.
/// </summary>
public sealed class StandaloneScanForm : Form
{
    private readonly Bitmap _screenshot;
    private readonly Rectangle _bannerWorkingArea;
    private readonly StandaloneToolBanner _banner;
    private Point _cursorPos;
    private Point _dragStart;
    private bool _isDragging;
    private bool _closed;

    // Selection rectangle (normalized)
    private Rectangle _selectionRect;
    private bool _hasSelection;
    private Rectangle _autoDetectRect;
    private bool _autoDetectActive;

    public StandaloneScanForm()
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

        _bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;

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
        var scanLabel = LocalizationService.Translate("QR & Barcodes") + ": ";
        var scanAction = LocalizationService.Translate("Click & drag to scan QR or barcodes")
            + " · " + LocalizationService.Translate("Right-click or Esc to close");
        _banner = new StandaloneToolBanner(
            new BannerSegment[]
            {
                new(scanLabel, Color.White),
                new(scanAction, null), // accent
            },
            _bannerWorkingArea,
            Bounds,
            onInvalidate: () => Invalidate(),
            iconId: "scan");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WindowDetector.UnregisterIgnoredWindow(Handle);
            WindowDetector.ClearSnapshot();
            _banner.Dispose();
            _screenshot?.Dispose();
        }
        base.Dispose(disposing);
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
            Close();
            return;
        }

        if (e.Button == MouseButtons.Left)
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

        if (_banner.ContainsCursor(_cursorPos))
            _banner.Revive();

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

    protected override void OnMouseUp(MouseEventArgs e)
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

        // Decode barcode from the selected region
        Cursor = Cursors.WaitCursor;
        Invalidate();

        try
        {
            using var cropped = _screenshot.Clone(_selectionRect, _screenshot.PixelFormat);
            var decoded = BarcodeService.DecodeDetailed(cropped);

            // Close overlay first so screenshot is released
            Close();

            if (decoded is not null)
            {
                SoundService.PlayScanSound();

                // Save to history
                HistoryService.QuickSaveCode(decoded.Text, decoded.Format.ToString());

                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var copySucceeded = TryCopyToClipboard(decoded.Text);
                        var preview = decoded.Text.Length > 100 ? decoded.Text[..100] + "..." : decoded.Text;
                        var bmp = BarcodeService.RenderPreview(decoded.Text, decoded.Format);
                        var title = decoded.Format == ZXing.BarcodeFormat.QR_CODE
                            ? copySucceeded ? "QR Code copied" : "QR Code found"
                            : copySucceeded ? "Barcode copied" : "Barcode found";
                        ToastWindow.ShowInlinePreview(bmp, title, preview, suppressSound: true);
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("standalone-scan.result", ex);
                    }
                });
            }
            else
            {
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ToastWindow.Show(
                        LocalizationService.Translate("Scan"),
                        LocalizationService.Translate("No QR & Barcode found"));
                });
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("standalone-scan", ex);
            Close();
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

        // Draw banner
        _banner.Render(g);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
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

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            ClipboardService.CopyTextToClipboard(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
