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
    private bool _showShadow;
    private bool _showBorder;
    private bool _includeInCaptures;
    private bool _updatingShadow;
    private bool _shown;
    private PinShadowForm? _shadow;
    private readonly Font _font = UiChrome.ChromeFont(10);
    private Point? _dragStart;
    private Point _dragOrigin;
    private bool _hover;
    private bool _disposed;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };
    private readonly HashSet<ContextMenuStrip> _openMenus = new();
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
        _showShadow = settings.PinShowShadow;
        _showBorder = settings.PinShowBorder;
        _includeInCaptures = settings.PinIncludeInCaptures;
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
        _hoverTimer.Tick += (_, _) =>
        {
            if (_disposed) return;
            bool hover = ClientRectangle.Contains(PointToClient(System.Windows.Forms.Cursor.Position));
            if (hover != _hover) { _hover = hover; Invalidate(); }
            UpdateShadow();
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
    public void ApplyAppearancePreferences(bool shadow, bool border)
    {
        if (_disposed || IsDisposed || Disposing) return;
        _showShadow = shadow;
        _showBorder = border;
        if (!shadow) { _shadow?.Dispose(); _shadow = null; }
        UpdateShadow();
        Invalidate();
    }

    private void UpdateShadow(bool positionChanged = false)
    {
        if (_disposed || _updatingShadow) return;
        _updatingShadow = true;
        try
        {
            // Windows 10 may reject capture affinity for per-pixel layered windows.
            // Keep excluded shadows hidden throughout live capture/tool sessions,
            // including legacy recording and live color sampling.
            bool excludedDuringCapture = !_includeInCaptures
                && (System.Windows.Application.Current as App)?.IsSessionBusy() == true;
            if (!_showShadow || excludedDuringCapture || !_shown || !IsHandleCreated || !Native.User32.IsWindowVisible(Handle))
            {
                _shadow?.Hide();
                return;
            }
            _shadow ??= new PinShadowForm(_includeInCaptures);
            _shadow.UpdateFor(this, positionChanged);
        }
        finally { _updatingShadow = false; }
    }

    internal void RefreshShadowVisibility() => UpdateShadow();

    public void ApplyCapturePreferences(bool include, bool topmost)
    {
        _includeInCaptures = include;
        TopMost = topmost;
        _shadow?.ApplyCapturePreference(include);
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
        if (_disposed || IsDisposed || Disposing) return;
        var work = Screen.FromRectangle(Bounds).WorkingArea;
        // Oversized references can extend off-screen, but leave a draggable portion reachable.
        Left = Math.Clamp(Left, work.Left - Math.Max(0, Width - 48), Math.Max(work.Left, work.Right - Math.Min(Width, 48)));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Math.Min(Height, 48)));
        UpdateShadow(positionChanged: true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        UpdateShadow(positionChanged: true);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is 0x0047 or 0x0018) // WM_WINDOWPOSCHANGED / WM_SHOWWINDOW
            UpdateShadow(positionChanged: true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.Dwm.TrySetTransitionsForcedDisabled(Handle, true);
        Native.Dwm.TrySetWindowCornerPreference(Handle, Native.Dwm.DWMWCP_DONOTROUND);
        ApplyCapturePreferences(_includeInCaptures, TopMost);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _shadow?.Hide();
        CaptureWindowExclusion.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Scale is expressed in physical image pixels, not monitor-dependent logical pixels.
        e.Cancel = true;
        base.OnDpiChanged(e);
        UpdateShadow(positionChanged: true);
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
        var imageState = g.Save();
        try
        {
            g.InterpolationMode = _smooth ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            int imageWidth = Math.Max(1, (int)Math.Round(_original.Width * _scale));
            int imageHeight = Math.Max(1, (int)Math.Round(_original.Height * _scale));
            var imageBounds = new Rectangle((Width - imageWidth) / 2, (Height - imageHeight) / 2, imageWidth, imageHeight);
            // Extend edge pixels into the frame gutter without rescaling the image.
            // Hiding the border must not leave a background-colored outline behind.
            float insetX = 0, insetY = 0;
            if (!_showBorder)
            {
                imageBounds.Inflate(1, 1);
                insetX = (float)_original.Width / imageWidth;
                insetY = (float)_original.Height / imageHeight;
            }
            g.DrawImage(_original, imageBounds, -insetX, -insetY, _original.Width + 2 * insetX,
                _original.Height + 2 * insetY, GraphicsUnit.Pixel, attributes);
        }
        finally { g.Restore(imageState); }
        // The image's half-pixel sampling must not shift the one-pixel frame.
        // Align all four strokes to client pixels, including the top/left edges.
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.SmoothingMode = SmoothingMode.None;
        using var border = new Pen(ToColor(_hover ? Theme.Accent : Theme.Border));
        if (_showBorder || _hover) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
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
            var icons = new[] { "copy", "more", "close" };
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
        if ((ModifierKeys & Keys.Control) != 0) SetImageOpacity(Opacity + steps * .1);
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
            if (e.Control) SetImageOpacity(Opacity + direction * .1);
            else SetImageScale(_scale + direction * .1);
        }
        else if (e.KeyCode is Keys.D0 or Keys.NumPad0) ResetView();
        else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) OpenMenu(new Point(8, 8));
        else { e.Handled = e.SuppressKeyPress = false; }
        base.OnKeyDown(e);
    }

    private void SetImageOpacity(double value) { Opacity = Math.Clamp(value, .1, 1); UpdateShadow(); }
    private void ResetView() { Opacity = 1; SetImageScale(1); UpdateShadow(); }
    private void SendImage(Action<Bitmap>? handler)
    {
        if (handler is null) return;
        var image = CopyOriginal();
        try { handler(image); }
        catch { image.Dispose(); throw; }
    }

    private void OpenMenu(Point location)
    {
        if (_disposed || IsDisposed || Disposing) return;
        var menu = BuildContextMenu();
        _openMenus.Add(menu);
        menu.Closed += (_, _) => ReleaseContextMenu(menu);
        try { ShowMenuOnCurrentMonitor(menu, location); }
        catch
        {
            ReleaseContextMenu(menu);
            throw;
        }
    }

    private void ShowMenuOnCurrentMonitor(ContextMenuStrip menu, Point location)
    {
        var anchor = PointToScreen(location);
        var screen = Screen.FromPoint(anchor);
        var work = PopupWindowHelper.TryGetNativeMonitorInfo(screen, out _, out var nativeWork)
            ? nativeWork : screen.WorkingArea;
        bool positioning = false;
        Point Clamp(Point point, Size size) => new(
            Math.Clamp(point.X, work.Left, Math.Max(work.Left, work.Right - size.Width)),
            Math.Clamp(point.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
        bool OpensRight(ToolStripDropDown parent, ToolStripDropDown child)
            => parent.Right + child.Width <= work.Right || parent.Left - child.Width < work.Left;
        void Position(ToolStripDropDown popup, ToolStripMenuItem? owner)
        {
            if (positioning || popup.IsDisposed || !popup.Visible) return;
            positioning = true;
            try
            {
                var point = anchor;
                if (owner?.Owner is ToolStripDropDown parent)
                    point = new Point(OpensRight(parent, popup) ? parent.Right - 2 : parent.Left - popup.Width + 2,
                        parent.PointToScreen(owner.Bounds.Location).Y - popup.Padding.Top);
                popup.Location = Clamp(point, popup.Size);
            }
            finally { positioning = false; }
        }
        void Configure(ToolStripDropDown popup, ToolStripMenuItem? owner)
        {
            popup.MaximumSize = work.Size;
            // WinForms may adopt another DPI or choose the adjacent monitor when
            // opening. Clamp the final native size as well as the initial anchor.
            popup.Opened += (_, _) => Position(popup, owner);
            popup.Layout += (_, _) => Position(popup, owner);
            foreach (var item in popup.Items.OfType<ToolStripMenuItem>())
            {
                if (!item.HasDropDownItems) continue;
                Configure(item.DropDown, item);
                item.DropDownOpening += (_, _) => item.DropDownDirection = OpensRight(popup, item.DropDown)
                    ? ToolStripDropDownDirection.Right : ToolStripDropDownDirection.Left;
            }
        }
        Configure(menu, null);
        menu.Show(this, PointToClient(Clamp(anchor, menu.PreferredSize)));
        Position(menu, null);
    }

    private void ReleaseContextMenu(ContextMenuStrip menu)
    {
        if (!_openMenus.Remove(menu)) return;
        // Closed fires inside ToolStripDropDown.SetVisibleCore, which still uses
        // the HWND afterwards. Never dispose on that stack or invoke through the
        // Pin HWND, since a Close command can destroy the owner before cleanup runs.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, new Action(menu.Dispose));
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 230);
        var images = new List<Image>();
        void Add(string text, string icon, Action action)
        {
            var item = WindowsMenuRenderer.Item(LocalizationService.Translate(text), iconId: icon);
            if (item.Image is { } image) images.Add(image);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Copy original image", "copy", () => SendImage(CopyRequested));
        Add("Save as...", "save", () => SendImage(SaveRequested));
        Add("Send to Editor...", "compose", () => SendImage(EditRequested));
        var opacity = WindowsMenuRenderer.Submenu(LocalizationService.Translate("Opacity"), showImages: true, iconId: "eye");
        if (opacity.Image is { } opacityImage) images.Add(opacityImage);
        opacity.ToolTipText = LocalizationService.Translate("Ctrl + wheel changes opacity.");
        menu.ShowItemToolTips = true;
        for (int percent = 10; percent <= 100; percent += 10)
        {
            double value = percent / 100d;
            var item = WindowsMenuRenderer.Item($"{value:P0}", iconId: "eye", active: Math.Abs(Opacity - value) < .001);
            if (item.Image is { } image) images.Add(image);
            item.Click += (_, _) => SetImageOpacity(value);
            opacity.DropDownItems.Add(item);
        }
        WindowsMenuRenderer.NormalizeDropDownWidths(opacity, minWidth: 150);
        menu.Items.Add(opacity);
        Add("Reset zoom and opacity", "zoomReset", ResetView);
        Add("Pin settings", "gear", () => OptionsRequested?.Invoke());
        Add("Close", "close", Close);
        WindowsMenuRenderer.NormalizeItemWidths(menu, minWidth: 230);
        menu.Disposed += (_, _) => { foreach (var image in images) image.Dispose(); };
        return menu;
    }

    private static Color ToColor(System.Windows.Media.Color value) => Color.FromArgb(value.A, value.R, value.G, value.B);
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hoverTimer.Stop();
            _shadow?.Dispose(); _shadow = null;
            foreach (var menu in _openMenus.ToArray())
            {
                if (!menu.IsDisposed) menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                ReleaseContextMenu(menu);
            }
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (IsHandleCreated) CaptureWindowExclusion.Unregister(Handle);
            _hoverTimer.Dispose(); _font.Dispose(); _original.Dispose();
        }
        base.Dispose(disposing);
    }
}
