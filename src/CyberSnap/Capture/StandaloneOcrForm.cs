using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone OCR activated via global hotkey or tray menu.
/// Overlays a dimmed screenshot of all monitors and lets the user drag a rectangle
/// to select a region for text extraction. Right-click or Escape to close.
/// </summary>
public sealed class StandaloneOcrForm : Form
{
    private readonly Bitmap _screenshot;
    private readonly Rectangle _bannerWorkingArea;
    private readonly StandaloneToolBanner _banner;
    private Point _cursorPos;
    private Point _dragStart;
    private bool _isDragging;
    private bool _closed;
    private bool _isProcessing;

    // Selection rectangle (normalized)
    private Rectangle _selectionRect;
    private bool _hasSelection;

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

        _bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;

        Theme.Refresh();
        var (bmp, _) = ScreenCapture.CaptureAllScreens(includeCursor: false);
        _screenshot = bmp;

        Cursor = Cursors.Cross;

        // ── Banner ──
        _banner = new StandaloneToolBanner(
            LocalizationService.Translate("Click & drag to select text region  ·  Right-click or Esc to close"),
            _bannerWorkingArea,
            Bounds,
            onInvalidate: () => Invalidate());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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

        if (e.Button == MouseButtons.Left && !_isProcessing)
        {
            _isDragging = true;
            _dragStart = e.Location;
            _hasSelection = false;
            _selectionRect = Rectangle.Empty;
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
            Invalidate();
            base.OnMouseUp(e);
            return;
        }

        // Freeze the selection visually, start OCR processing
        _isProcessing = true;
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

            // Close the overlay first so screenshot is released
            Close();

            if (!string.IsNullOrWhiteSpace(text))
            {
                SoundService.PlayTextSound();

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
                                var window = new OcrResultWindow(text, GetSettingsService());
                                window.Show();
                            }
                        }
                        else
                        {
                            var window = new OcrResultWindow(text, GetSettingsService());
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

        // If processing, show processing indicator
        if (_isProcessing && _hasSelection)
        {
            DrawProcessingIndicator(g, _selectionRect);
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

    private static void DrawProcessingIndicator(Graphics g, Rectangle rect)
    {
        // Draw a subtle scanning/processing overlay on the selection
        using var brush = new SolidBrush(Color.FromArgb(40, 0, 120, 255));
        g.FillRectangle(brush, rect);

        // "Processing..." text centered in the selection
        var text = "Processing...";
        using var font = new Font("Segoe UI", 10f, FontStyle.Regular);
        var textSize = g.MeasureString(text, font);
        float tx = rect.X + (rect.Width - textSize.Width) / 2f;
        float ty = rect.Y + (rect.Height - textSize.Height) / 2f;

        // Text background
        var bgRect = new RectangleF(tx - 6, ty - 2, textSize.Width + 12, textSize.Height + 4);
        using (var bgBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
        {
            g.FillRectangle(bgBrush, bgRect);
        }

        using (var textBrush = new SolidBrush(Color.White))
        {
            g.DrawString(text, font, textBrush, tx, ty);
        }
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
