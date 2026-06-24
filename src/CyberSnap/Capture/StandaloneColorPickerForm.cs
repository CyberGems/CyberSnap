using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone color picker activated via global hotkey or tray menu.
/// Overlays a screenshot of all monitors with a magnifier that follows the cursor.
/// Left-click picks the color and copies its HEX value to the clipboard.
/// Right-click or Escape to close.
/// </summary>
public sealed class StandaloneColorPickerForm : Form
{
    private readonly Bitmap _screenshot;
    private readonly Rectangle _bannerWorkingArea;
    private readonly StandaloneToolBanner _banner;
    private PickerMagnifierForm? _magnifierForm;
    private Color _pickedColor = Color.Black;

    /// <summary>
    /// The color the user committed by left-clicking, or null if they cancelled
    /// (right-click / Escape). Lets an embedding caller adopt the result directly,
    /// without racing the clipboard.
    /// </summary>
    public Color? PickedColor { get; private set; }

    private string _hexStr = "000000";
    private string _rgbStr = "0, 0, 0";
    private Point _cursorPos;
    private int _bmpW, _bmpH;
    private int[] _pixelData;
    private bool _closed;

    // Magnifier rendering
    private Bitmap? _magBitmap;
    private int[] _magPixels = Array.Empty<int>();
    private int _lastMagSampleX = -1;
    private int _lastMagSampleY = -1;
    private const int Grid = 11;
    private const int Cell = 10;
    private const int PPad = 2;
    private static readonly int PW = Grid * Cell + PPad * 2;
    private static readonly int PH = Grid * Cell + PPad * 2;

    public StandaloneColorPickerForm()
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
        _bmpW = _screenshot.Width;
        _bmpH = _screenshot.Height;

        // Extract pixel data for fast color sampling
        _pixelData = new int[_bmpW * _bmpH];
        var bits = _screenshot.LockBits(new Rectangle(0, 0, _bmpW, _bmpH),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bits.Scan0, _pixelData, 0, _pixelData.Length);
        }
        finally
        {
            _screenshot.UnlockBits(bits);
        }

        _magBitmap = new Bitmap(PW, PH, PixelFormat.Format32bppArgb);
        _magPixels = new int[PW * PH];

        Cursor = Cursors.Cross;

        // ── Banner ──
        _banner = new StandaloneToolBanner(
            LocalizationService.Translate("Click to pick color & copy HEX  ·  Right-click or Esc to close"),
            _bannerWorkingArea,
            Bounds,
            onInvalidate: () => Invalidate());

        // Initial magnifier at cursor position
        UpdateMagnifierAtCursor();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _banner.Dispose();
            CloseMagnifier();
            _screenshot?.Dispose();
            _magBitmap?.Dispose();
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursorPos = e.Location;

        if (_banner.ContainsCursor(_cursorPos))
            _banner.Revive();

        UpdateMagnifierAtCursor();
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Close();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            PickColor();
        }

        base.OnMouseDown(e);
    }

    private void PickColor()
    {
        int cx = Math.Clamp(_cursorPos.X, 0, _bmpW - 1);
        int cy = Math.Clamp(_cursorPos.Y, 0, _bmpH - 1);

        // Re-read pixel directly from screenshot to ensure accuracy
        var argb = _pixelData[cy * _bmpW + cx];
        var color = Color.FromArgb(argb);
        PickedColor = color;
        string hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";

        // Copy to clipboard
        try
        {
            Clipboard.SetText($"#{hex}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("standalone-colorpicker.clipboard", ex.Message);
        }

        // Save to history
        HistoryService.QuickSaveColor(hex);

        // Close the picker first so the screenshot is released
        Close();

        // Show toast on the WPF dispatcher thread (we're on a WinForms STA thread)
        var wpfColor = System.Windows.Media.Color.FromRgb(color.R, color.G, color.B);
        try
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                ToastWindow.ShowWithColor(
                    $"#{hex}",
                    $"R: {color.R}  G: {color.G}  B: {color.B}",
                    wpfColor,
                    suppressSound: false);
            });
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("standalone-colorpicker.toast", ex);
        }
    }

    // ── Paint ──

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_closed) return;
        var g = e.Graphics;

        // Draw screenshot as background
        g.DrawImage(_screenshot, ClientRectangle);

        // Draw banner
        _banner.Render(g);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        base.OnFormClosed(e);
    }

    // ── Magnifier ──

    private void UpdateMagnifierAtCursor()
    {
        int cx = Math.Clamp(_cursorPos.X, 0, _bmpW - 1);
        int cy = Math.Clamp(_cursorPos.Y, 0, _bmpH - 1);

        int argb = _pixelData[cy * _bmpW + cx];
        _pickedColor = Color.FromArgb(argb);
        _hexStr = $"{_pickedColor.R:X2}{_pickedColor.G:X2}{_pickedColor.B:X2}";
        _rgbStr = $"R: {_pickedColor.R}  G: {_pickedColor.G}  B: {_pickedColor.B}";

        // Only rebuild magnifier bitmap if we moved to a different pixel
        if (cx == _lastMagSampleX && cy == _lastMagSampleY)
        {
            // Just update the existing magnifier form with new color info
            EnsureMagnifierForm();
            _magnifierForm?.UpdateMagnifier(_magBitmap!, _cursorPos, _pickedColor, _hexStr, _rgbStr);
            return;
        }

        _lastMagSampleX = cx;
        _lastMagSampleY = cy;

        BuildMagnifierPixels(cx, cy);

        EnsureMagnifierForm();
        if (_magnifierForm is null) return;

        if (!_magnifierForm.Visible)
            _magnifierForm.Show(this);
        _magnifierForm.UpdateMagnifier(_magBitmap!, _cursorPos, _pickedColor, _hexStr, _rgbStr);
        PositionMagnifier(_cursorPos);
    }

    private void BuildMagnifierPixels(int cx, int cy)
    {
        Array.Fill(_magPixels, unchecked((int)0xFF202020));

        int half = Grid / 2;
        for (int gy = 0; gy < Grid; gy++)
        {
            int sy = cy - half + gy;
            for (int gx = 0; gx < Grid; gx++)
            {
                int sx = cx - half + gx;
                int c = ((uint)sx < (uint)_bmpW && (uint)sy < (uint)_bmpH)
                    ? _pixelData[sy * _bmpW + sx] : unchecked((int)0xFF000000);

                int ox = PPad + gx * Cell;
                int oy = PPad + gy * Cell;
                for (int py = 0; py < Cell - 1; py++)
                {
                    int row = (oy + py) * PW + ox;
                    for (int px = 0; px < Cell - 1; px++)
                        _magPixels[row + px] = c;
                    _magPixels[row + Cell - 1] = Lighten(c, 15);
                }
                int bot = (oy + Cell - 1) * PW + ox;
                int gl = Lighten(c, 15);
                for (int px = 0; px < Cell; px++)
                    _magPixels[bot + px] = gl;
            }
        }

        // Center pixel border (white crosshair)
        int bx = PPad + half * Cell, byVal = PPad + half * Cell;
        const int w = unchecked((int)0xFFFFFFFF);
        for (int i = -1; i <= Cell; i++)
        {
            SetMagPx(bx + i, byVal - 1, w); SetMagPx(bx + i, byVal + Cell, w);
            SetMagPx(bx - 1, byVal + i, w); SetMagPx(bx + Cell, byVal + i, w);
        }

        var bitsLock = _magBitmap!.LockBits(new Rectangle(0, 0, PW, PH),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(_magPixels, 0, bitsLock.Scan0, _magPixels.Length);
        }
        finally
        {
            _magBitmap.UnlockBits(bitsLock);
        }
    }

    private void SetMagPx(int x, int y, int v)
    {
        if ((uint)x < (uint)PW && (uint)y < (uint)PH)
            _magPixels[y * PW + x] = v;
    }

    private static int Lighten(int c, int amt)
    {
        int r = Math.Min(((c >> 16) & 0xFF) + amt, 255);
        int gg = Math.Min(((c >> 8) & 0xFF) + amt, 255);
        int b = Math.Min((c & 0xFF) + amt, 255);
        return unchecked((int)0xFF000000) | (r << 16) | (gg << 8) | b;
    }

    private void EnsureMagnifierForm()
    {
        if (_magnifierForm != null) return;
        _magnifierForm = new PickerMagnifierForm();
        var _ = _magnifierForm.Handle;
        WindowDetector.RegisterIgnoredWindow(_magnifierForm.Handle);
    }

    private void CloseMagnifier()
    {
        if (_magnifierForm != null)
            WindowDetector.UnregisterIgnoredWindow(_magnifierForm.Handle);
        _magnifierForm?.Close();
        _magnifierForm?.Dispose();
        _magnifierForm = null;
    }

    /// <summary>
    /// Position the magnifier to the top-right of the cursor, flipping if near screen edges.
    /// Uses actual form size after UpdateMagnifier may have resized it.
    /// </summary>
    private void PositionMagnifier(Point cursor)
    {
        if (_magnifierForm is null) return;

        const int offset = 20;
        int formW = _magnifierForm.Width;
        int formH = _magnifierForm.Height;

        int x = cursor.X + offset;
        int y = cursor.Y - formH - offset;

        // Flip horizontally if too close to right edge
        if (x + formW > _bmpW - 8)
            x = cursor.X - formW - offset;

        // Flip vertically if too close to top edge
        if (y < 8)
            y = cursor.Y + offset;

        // Clamp
        x = Math.Clamp(x, 4, _bmpW - formW - 4);
        y = Math.Clamp(y, 4, _bmpH - formH - 4);

        _magnifierForm.Left = x + Bounds.Left;
        _magnifierForm.Top = y + Bounds.Top;
    }
}
