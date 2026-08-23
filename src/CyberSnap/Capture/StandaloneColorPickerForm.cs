using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone color picker activated via global hotkey or tray menu.
/// Shows a live magnifier that follows the cursor over the real screen.
/// Left-click picks the color and copies its HEX value to the clipboard.
/// Right-click or Escape to close.
/// </summary>
public sealed class StandaloneColorPickerForm : Form
{
    private readonly BannerLayeredForm _banner;
    private readonly System.Windows.Forms.Timer _trackTimer;
    private CaptureEscapeKeyHook? _escapeHook;
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

    private const int CaptureRegionSize = 21;
    private const int CaptureHalf = CaptureRegionSize / 2;
    private const int CaptureIntervalMs = 30;

    private int _captureW, _captureH;
    private Rectangle _captureBounds;
    private int[] _livePixelData = Array.Empty<int>();
    private readonly Stopwatch _captureTimer = new();

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
        Thread.Sleep(80);

        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Color.Black;
        Opacity = 0.01;

        var cursor = Cursor.Position;
        _cursorPos = new Point(cursor.X - bounds.X, cursor.Y - bounds.Y);

        _trackTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, UiChrome.FrameIntervalMs) };
        _trackTimer.Tick += OnTrackTick;

        _magBitmap = new Bitmap(PW, PH, PixelFormat.Format32bppArgb);
        _magPixels = new int[PW * PH];

        Cursor = CursorFactory.EyedropperCursor;

        var pickerLabel = LocalizationService.Translate("Color picker") + ": ";
        var pickerAction = LocalizationService.Translate("Click to pick color & copy HEX")
            + " · " + LocalizationService.Translate("Right-click or Esc to close");
        _banner = new BannerLayeredForm(
            new BannerSegment[]
            {
                new(pickerLabel, StandaloneToolBanner.LabelColor),
                new(pickerAction, null),
            },
            Screen.FromPoint(cursor).WorkingArea,
            iconId: "picker");

        _captureTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureWindowExclusion.Apply(this);
        CaptureWindowExclusion.SetLogicalBounds(Handle, static () => Rectangle.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trackTimer.Stop();
            _trackTimer.Tick -= OnTrackTick;
            _trackTimer.Dispose();
            _escapeHook?.Dispose();
            _escapeHook = null;
            if (IsHandleCreated)
                CaptureWindowExclusion.Unregister(Handle);
            _banner.Dispose();
            CloseMagnifier();
            _magBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _banner.ShowFor(this);
        Activate();
        Focus();
        _escapeHook?.Dispose();
        _escapeHook = CaptureEscapeKeyHook.Install(this, CloseFromEscape);
        SyncCursorFromScreen();
        UpdateMagnifierAtCursor();
        _trackTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _trackTimer.Stop();
        CloseMagnifier();
        base.OnFormClosing(e);
    }

    private void OnTrackTick(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed || Disposing || !Visible)
            return;

        SyncCursorFromScreen();
        _banner.DismissIfHovered(Cursor.Position);
        UpdateMagnifierAtCursor();
    }

    private void SyncCursorFromScreen()
    {
        _cursorPos = PointToClient(Cursor.Position);
    }

    private void CloseFromEscape()
    {
        if (!IsDisposed)
            Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursorPos = e.Location;
        _banner.DismissIfHovered(PointToScreen(e.Location));
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
            _cursorPos = e.Location;
            PickColor();
        }

        base.OnMouseDown(e);
    }

    private Point CursorScreenPoint => PointToScreen(_cursorPos);

    private void PickColor()
    {
        CaptureLiveRegion();
        if (!TrySampleCursorPixel(out int argb))
            return;

        var color = Color.FromArgb(argb);
        PickedColor = color;
        string hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";

        try
        {
            Clipboard.SetText($"#{hex}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("standalone-colorpicker.clipboard", ex.Message);
        }

        HistoryService.QuickSaveColor(hex);
        App.NotifyStandaloneCapture(isColor: true);

        Close();

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

    private void CaptureLiveRegion()
    {
        var screen = CursorScreenPoint;
        var virtualScreen = SystemInformation.VirtualScreen;
        int regionX = screen.X - CaptureHalf;
        int regionY = screen.Y - CaptureHalf;

        if (regionX < virtualScreen.Left)
            regionX = virtualScreen.Left;
        if (regionY < virtualScreen.Top)
            regionY = virtualScreen.Top;
        if (regionX + CaptureRegionSize > virtualScreen.Right)
            regionX = Math.Max(virtualScreen.Left, virtualScreen.Right - CaptureRegionSize);
        if (regionY + CaptureRegionSize > virtualScreen.Bottom)
            regionY = Math.Max(virtualScreen.Top, virtualScreen.Bottom - CaptureRegionSize);

        var region = new Rectangle(regionX, regionY, CaptureRegionSize, CaptureRegionSize);
        using var bmp = ScreenCapture.CaptureRegionForRecording(region, includeCursor: false);

        _captureW = bmp.Width;
        _captureH = bmp.Height;
        _captureBounds = new Rectangle(regionX, regionY, _captureW, _captureH);

        if (_livePixelData.Length != _captureW * _captureH)
            _livePixelData = new int[_captureW * _captureH];

        var bits = bmp.LockBits(new Rectangle(0, 0, _captureW, _captureH),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bits.Scan0, _livePixelData, 0, _livePixelData.Length);
        }
        finally
        {
            bmp.UnlockBits(bits);
        }
    }

    private bool TrySampleCursorPixel(out int argb)
    {
        argb = 0;
        if (_captureW <= 0 || _captureH <= 0 || _livePixelData.Length < _captureW * _captureH)
            return false;

        var screen = CursorScreenPoint;
        int cx = Math.Clamp(screen.X - _captureBounds.Left, 0, _captureW - 1);
        int cy = Math.Clamp(screen.Y - _captureBounds.Top, 0, _captureH - 1);
        argb = _livePixelData[cy * _captureW + cx];
        return true;
    }

    private void UpdateMagnifierAtCursor()
    {
        if (IsDisposed || Disposing || !Visible)
            return;

        var screen = CursorScreenPoint;
        bool pixelChanged = screen.X != _lastMagSampleX || screen.Y != _lastMagSampleY;
        bool timeElapsed = _captureTimer.ElapsedMilliseconds >= CaptureIntervalMs;
        bool needsCapture = _captureW <= 0 || !_captureBounds.Contains(screen);

        bool recapture = timeElapsed || needsCapture;
        if (recapture)
        {
            _captureTimer.Restart();
            CaptureLiveRegion();
        }

        if (!pixelChanged && !recapture)
        {
            EnsureMagnifierForm();
            _magnifierForm?.UpdateMagnifier(_magBitmap!, _cursorPos, _pickedColor, _hexStr, _rgbStr);
            return;
        }

        _lastMagSampleX = screen.X;
        _lastMagSampleY = screen.Y;

        if (!TrySampleCursorPixel(out int argb))
            return;

        _pickedColor = Color.FromArgb(argb);
        _hexStr = $"{_pickedColor.R:X2}{_pickedColor.G:X2}{_pickedColor.B:X2}";
        _rgbStr = $"R: {_pickedColor.R}  G: {_pickedColor.G}  B: {_pickedColor.B}";

        int cx = Math.Clamp(screen.X - _captureBounds.Left, 0, _captureW - 1);
        int cy = Math.Clamp(screen.Y - _captureBounds.Top, 0, _captureH - 1);
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
                int c = ((uint)sx < (uint)_captureW && (uint)sy < (uint)_captureH)
                    ? _livePixelData[sy * _captureW + sx] : unchecked((int)0xFF000000);

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
        {
            if (_magnifierForm.IsHandleCreated)
                WindowDetector.UnregisterIgnoredWindow(_magnifierForm.Handle);
            _magnifierForm.Close();
            _magnifierForm.Dispose();
            _magnifierForm = null;
        }
    }

    private void PositionMagnifier(Point cursorClient)
    {
        if (_magnifierForm is null) return;

        const int offset = 20;
        int formW = _magnifierForm.Width;
        int formH = _magnifierForm.Height;

        var screen = PointToScreen(cursorClient);
        int x = screen.X + offset;
        int y = screen.Y - formH - offset;

        var virtualScreen = SystemInformation.VirtualScreen;
        if (x + formW > virtualScreen.Right - 8)
            x = screen.X - formW - offset;
        if (y < virtualScreen.Top + 8)
            y = screen.Y + offset;

        x = Math.Clamp(x, virtualScreen.Left + 4, virtualScreen.Right - formW - 4);
        y = Math.Clamp(y, virtualScreen.Top + 4, virtualScreen.Bottom - formH - 4);

        _magnifierForm.Left = x;
        _magnifierForm.Top = y;
    }
}
