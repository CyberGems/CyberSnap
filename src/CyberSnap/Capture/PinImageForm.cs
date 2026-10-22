using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>A temporary reference image. The form owns its bitmap; display changes never modify it.</summary>
public sealed class PinImageForm : Form
{
    private readonly Bitmap _original;
    private bool _smooth;
    private readonly Font _font = UiChrome.ChromeFont(10);
    private Point? _dragStart;
    private Point _dragOrigin;
    private bool _hover;
    private bool _disposed;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };
    private double _scale;
    private readonly double _minimumScale;
    private Rectangle[] _buttons = Array.Empty<Rectangle>();
    public double ImageScale => _scale;
    public event Action? OptionsRequested;
    public event Action<Bitmap>? CopyRequested;
    public event Action<Bitmap>? SaveRequested;
    public event Action<Bitmap>? EditRequested;

    protected override AccessibleObject CreateAccessibilityInstance() => new PinAccessibleObject(this);

    private sealed class PinAccessibleObject(PinImageForm owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner._buttons.Length;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < GetChildCount() ? new PinButtonAccessibleObject(owner, index) : null;
    }

    private sealed class PinButtonAccessibleObject(PinImageForm owner, int index) : AccessibleObject
    {
        public override string? Name { get => LocalizationService.Translate(index switch { 0 => "Copy original image", 1 => "More", _ => "Close" }); set { } }
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override AccessibleObject? Parent => owner.AccessibilityObject;
        public override Rectangle Bounds => index < owner._buttons.Length ? owner.RectangleToScreen(owner._buttons[index]) : Rectangle.Empty;
        public override string? DefaultAction => LocalizationService.Translate("Activate");
        public override void DoDefaultAction()
        {
            if (index == 0) owner.SendImage(owner.CopyRequested);
            else if (index == 1) owner.OpenMenu(new Point(owner.Width / 2, 36));
            else owner.Close();
        }
    }

    public PinImageForm(Bitmap ownedImage, Point anchor, AppSettings settings)
    {
        _original = ownedImage;
        _smooth = settings.PinSmoothImage;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = settings.PinAlwaysOnTop;
        KeyPreview = true;
        DoubleBuffered = true;
        Text = LocalizationService.Translate("Pin to screen");
        AccessibleName = Text;
        SetStyle(ControlStyles.ResizeRedraw, true);
        var work = Screen.FromPoint(anchor).WorkingArea;
        _scale = NormalizeScale(settings.PinInitialScale);
        _scale = Math.Min(_scale, Math.Min((work.Width - 24d) / Math.Max(1, _original.Width),
            (work.Height - 24d) / Math.Max(1, _original.Height)));
        _minimumScale = Math.Min(.2, _scale);
        Size = ScaledSize(_scale);
        Location = new Point(anchor.X - Width / 2, anchor.Y - Height / 2);
        KeepReachable();
        Shown += (_, _) => ApplyCapturePreferences(settings.PinIncludeInCaptures, settings.PinAlwaysOnTop);
        _hoverTimer.Tick += (_, _) =>
        {
            bool hover = ClientRectangle.Contains(PointToClient(System.Windows.Forms.Cursor.Position));
            if (hover != _hover) { _hover = hover; Invalidate(); }
        };
        _hoverTimer.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    public static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, .2, 5) : 1;
    public Bitmap CopyOriginal() => CopyPixels(_original);
    public static unsafe Bitmap CopyPixels(Bitmap source)
    {
        var rectangle = new Rectangle(Point.Empty, source.Size);
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        try
        {
            var read = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var write = copy.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int row = 0; row < source.Height; row++)
                        Buffer.MemoryCopy((byte*)read.Scan0 + row * read.Stride,
                            (byte*)write.Scan0 + row * write.Stride, Math.Abs(write.Stride), source.Width * 4L);
                }
                finally { copy.UnlockBits(write); }
            }
            finally { source.UnlockBits(read); }
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }
    public void ApplyRenderingPreference(bool smooth) { _smooth = smooth; Invalidate(); }

    public void ApplyCapturePreferences(bool include, bool topmost)
    {
        TopMost = topmost;
        if (!IsHandleCreated) return;
        if (include)
        {
            CaptureWindowExclusion.Unregister(Handle);
            if (!Native.User32.SetWindowDisplayAffinity(Handle, 0))
                AppDiagnostics.LogWarning("pin.capture-affinity", "Could not enable capture of a pinned image.");
        }
        else CaptureWindowExclusion.Apply(this);
    }

    private Size ScaledSize(double scale) => new(Math.Max(24, (int)Math.Round(_original.Width * scale)) + 2,
        Math.Max(24, (int)Math.Round(_original.Height * scale)) + 2);

    public void SetImageScale(double scale)
    {
        var center = new Point(Left + Width / 2, Top + Height / 2);
        _scale = Math.Clamp(double.IsFinite(scale) ? scale : 1, _minimumScale, 5);
        var size = ScaledSize(_scale);
        Bounds = new Rectangle(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        KeepReachable();
        Invalidate();
    }

    private void KeepReachable()
    {
        var work = Screen.FromRectangle(Bounds).WorkingArea;
        // Oversized references can extend off-screen, but leave a draggable portion reachable.
        Left = Math.Clamp(Left, work.Left - Math.Max(0, Width - 48), Math.Max(work.Left, work.Right - Math.Min(Width, 48)));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Math.Min(Height, 48)));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.Dwm.TrySetTransitionsForcedDisabled(Handle, true);
        Native.Dwm.TrySetWindowCornerPreference(Handle, Native.Dwm.DWMWCP_DONOTROUND);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Scale is expressed in physical image pixels, not monitor-dependent logical pixels.
        e.Cancel = true;
        base.OnDpiChanged(e);
        Invalidate();
    }

    private void DisplayChanged(object? sender, EventArgs e)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)KeepReachable); }
        catch (InvalidOperationException) when (IsDisposed || Disposing) { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ToColor(Theme.BgSecondary));
        g.InterpolationMode = _smooth ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        int imageWidth = Math.Max(1, (int)Math.Round(_original.Width * _scale));
        int imageHeight = Math.Max(1, (int)Math.Round(_original.Height * _scale));
        g.DrawImage(_original, new Rectangle((Width - imageWidth) / 2, (Height - imageHeight) / 2, imageWidth, imageHeight),
            0, 0, _original.Width, _original.Height, GraphicsUnit.Pixel, attributes);
        using var border = new Pen(ToColor(_hover ? Theme.Accent : Theme.Border));
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        _buttons = Array.Empty<Rectangle>();
        int S(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d * UiChrome.UiScale));
        if (_hover && Width >= S(190) && Height >= S(42))
        {
            int x = (Width - S(184)) / 2;
            var bar = new Rectangle(x, S(4), S(184), S(34));
            using var fill = new SolidBrush(ToColor(Theme.BgElevated));
            g.FillRectangle(fill, bar); g.DrawRectangle(border, bar);
            _buttons = new[] { new Rectangle(x + S(3), S(6), S(32), S(30)), new Rectangle(x + S(108), S(6), S(32), S(30)), new Rectangle(x + S(147), S(6), S(32), S(30)) };
            var pointer = PointToClient(System.Windows.Forms.Cursor.Position);
            var icons = new[] { "copy", "gear", "close" };
            for (int i = 0; i < _buttons.Length; i++)
            {
                var color = ToColor(_buttons[i].Contains(pointer) ? Theme.Accent : Theme.TextPrimary);
                FluentIcons.DrawIcon(g, icons[i], _buttons[i], color, S(7));
            }
            TextRenderer.DrawText(g, $"{_scale:P0}", _font, new Rectangle(x + S(38), S(7), S(66), S(28)),
                ToColor(Theme.TextPrimary), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        base.OnPaint(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { OpenMenu(e.Location); return; }
        if (e.Button == MouseButtons.Middle) { ResetView(); return; }
        if (e.Button == MouseButtons.Left)
        {
            int button = Array.FindIndex(_buttons, rect => rect.Contains(e.Location));
            if (button == 0) { SendImage(CopyRequested); return; }
            if (button == 1) { OpenMenu(e.Location); return; }
            if (button == 2) { Close(); return; }
            _dragStart = System.Windows.Forms.Cursor.Position;
            _dragOrigin = Location;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is Point start)
        {
            var pointer = System.Windows.Forms.Cursor.Position;
            Location = new Point(_dragOrigin.X + pointer.X - start.X, _dragOrigin.Y + pointer.Y - start.Y);
        }
        _hover = true; Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragStart = null; Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) _dragStart = null;
        base.OnMouseCaptureChanged(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        double steps = e.Delta / 120d;
        if ((ModifierKeys & Keys.Control) != 0) Opacity = Math.Clamp(Opacity + steps * .1, .1, 1);
        else SetImageScale(_scale + steps * .1);
        base.OnMouseWheel(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = e.SuppressKeyPress = true;
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.Control && e.KeyCode == Keys.C) SendImage(CopyRequested);
        else if (e.Control && e.KeyCode == Keys.S) SendImage(SaveRequested);
        else if (e.KeyCode is Keys.Add or Keys.Oemplus or Keys.Subtract or Keys.OemMinus)
        {
            int direction = e.KeyCode is Keys.Add or Keys.Oemplus ? 1 : -1;
            if (e.Control) Opacity = Math.Clamp(Opacity + direction * .1, .1, 1);
            else SetImageScale(_scale + direction * .1);
        }
        else if (e.KeyCode is Keys.D0 or Keys.NumPad0) ResetView();
        else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) OpenMenu(new Point(8, 8));
        else { e.Handled = e.SuppressKeyPress = false; }
        base.OnKeyDown(e);
    }

    private void ResetView() { Opacity = 1; SetImageScale(1); }
    private void SendImage(Action<Bitmap>? handler)
    {
        if (handler is null) return;
        var image = CopyOriginal();
        try { handler(image); }
        catch { image.Dispose(); throw; }
    }

    private void OpenMenu(Point location)
    {
        var menu = WindowsMenuRenderer.Create(false, 230);
        void Add(string text, Action action)
        {
            var item = WindowsMenuRenderer.Item(LocalizationService.Translate(text));
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Copy original image", () => SendImage(CopyRequested));
        Add("Save as...", () => SendImage(SaveRequested));
        Add("Send to Editor...", () => SendImage(EditRequested));
        Add("Reset zoom and opacity", ResetView);
        Add("Pin settings", () => OptionsRequested?.Invoke());
        Add("Close", Close);
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, location);
    }

    private static Color ToColor(System.Windows.Media.Color value) => Color.FromArgb(value.A, value.R, value.G, value.B);
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (IsHandleCreated) CaptureWindowExclusion.Unregister(Handle);
            _hoverTimer.Dispose(); _font.Dispose(); _original.Dispose();
        }
        base.Dispose(disposing);
    }
}
