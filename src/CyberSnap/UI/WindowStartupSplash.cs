using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using MediaColor = System.Windows.Media.Color;

namespace CyberSnap.UI;

/// <summary>
/// Lightweight startup preloader that runs on its <b>own STA thread + message loop</b>.
/// Unlike a toast on the main WPF dispatcher, this keeps painting (and a simple
/// indeterminate bar) while Editor / Gallery / Configuration construct on the UI thread.
/// </summary>
public sealed class WindowStartupSplash : IDisposable
{
    private readonly string _title;
    private readonly string _body;
    private readonly string _brandLabel;
    private readonly Color _bg;
    private readonly Color _fg;
    private readonly Color _muted;
    private readonly Color _border;
    private readonly Color _accent;
    private readonly Bitmap? _icon;
    private readonly Bitmap? _brandLogo;

    private Thread? _thread;
    private SplashForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private int _disposed;

    private WindowStartupSplash(
        string title,
        string body,
        string brandLabel,
        Color bg,
        Color fg,
        Color muted,
        Color border,
        Color accent,
        Bitmap? icon,
        Bitmap? brandLogo)
    {
        _title = title;
        _body = body;
        _brandLabel = brandLabel;
        _bg = bg;
        _fg = fg;
        _muted = muted;
        _border = border;
        _accent = accent;
        _icon = icon;
        _brandLogo = brandLogo;
    }

    /// <summary>
    /// Shows the splash immediately. Snapshots theme + window icon on the calling thread, then
    /// pumps a private message loop so animation never depends on the main UI thread.
    /// </summary>
    /// <param name="windowName">
    /// Localized window title as used in the rest of the UI (e.g. Editor, Galería, Configuración).
    /// </param>
    /// <param name="iconKind">
    /// Per-window icon (matches taskbar chrome). Falls back to the app logo if load fails.
    /// </param>
    public static WindowStartupSplash Show(string windowName, WindowIconKind iconKind, string? body = null)
    {
        var title = string.Format(
            LocalizationService.Translate("Starting {0}…"),
            windowName);
        var resolvedBody = string.IsNullOrWhiteSpace(body)
            ? LocalizationService.Translate("Preparing the workspace…")
            : body!;

        // Snapshot theme + icons on the caller (usually the UI thread) so the splash
        // thread never touches WPF Application / Theme while the main thread is busy.
        var bg = ToDrawing(Theme.ToastBg);
        var fg = ToDrawing(Theme.TextPrimary);
        var muted = ToDrawing(Theme.TextSecondary);
        var border = ToDrawing(Theme.ToastBorder);
        var accent = ToDrawing(Theme.Accent);
        var brandLogo = TryLoadLogoBitmap();
        // Window icon for the primary identity; brand logo stays as the footer mark (tray style).
        var icon = TryLoadWindowIconBitmap(iconKind) ?? (brandLogo is null ? null : new Bitmap(brandLogo));
        // Same wording as the tray menu header: "CyberSnap  vX.Y.Z"
        var brandLabel = $"CyberSnap  {UpdateService.GetCurrentVersionLabel()}";

        var splash = new WindowStartupSplash(
            title, resolvedBody, brandLabel, bg, fg, muted, border, accent, icon, brandLogo);
        splash.Start();
        // Wait until the form is actually visible (or give up quickly).
        splash._ready.Wait(millisecondsTimeout: 1500);
        return splash;
    }

    private void Start()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "CyberSnap.WindowStartupSplash",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void ThreadMain()
    {
        // Qualify Forms.Application — the project global-using aliases Application to WPF.
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            // Already set by the process — fine.
        }

        try
        {
            _form = new SplashForm(
                _title, _body, _brandLabel, _bg, _fg, _muted, _border, _accent, _icon, _brandLogo);
            _form.Shown += (_, _) =>
            {
                try { _ready.Set(); } catch { /* ignore */ }
            };
            _form.FormClosed += (_, _) =>
            {
                try { _closed.Set(); } catch { /* ignore */ }
            };
            System.Windows.Forms.Application.Run(_form);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("startup-splash.thread", ex.Message, ex);
            try { _ready.Set(); } catch { }
            try { _closed.Set(); } catch { }
        }
        finally
        {
            try { _icon?.Dispose(); } catch { }
            try { _brandLogo?.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            var form = _form;
            if (form is { IsDisposed: false })
            {
                try
                {
                    if (form.IsHandleCreated && form.InvokeRequired)
                        form.BeginInvoke(new Action(() =>
                        {
                            try { if (!form.IsDisposed) form.Close(); } catch { }
                        }));
                    else
                        form.Close();
                }
                catch
                {
                    // Form may already be tearing down.
                }
            }
            else
            {
                try { _closed.Set(); } catch { }
            }

            _closed.Wait(millisecondsTimeout: 1500);
            if (_thread is { IsAlive: true })
                _thread.Join(millisecondsTimeout: 500);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("startup-splash.dispose", ex.Message, ex);
        }
        finally
        {
            try { _ready.Dispose(); } catch { }
            try { _closed.Dispose(); } catch { }
        }
    }

    private static Color ToDrawing(MediaColor c)
        => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Window-specific icon (Editor / Gallery / Configuration), sized for the splash card.</summary>
    private static Bitmap? TryLoadWindowIconBitmap(WindowIconKind kind)
    {
        try
        {
            using var icon = WindowIcons.WinForms(kind);
            // ToBitmap() returns a new bitmap; copy so we fully own the pixels after Icon dispose.
            using var tmp = icon.ToBitmap();
            return new Bitmap(tmp);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("startup-splash.window-icon", ex.Message, ex);
            return null;
        }
    }

    private static Bitmap? TryLoadLogoBitmap()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/CyberSnap_square.png", UriKind.Absolute));
            if (info?.Stream is null)
                return null;
            using (info.Stream)
            using (var src = new Bitmap(info.Stream))
            {
                // Independent clone so the splash thread owns its bitmap fully.
                return new Bitmap(src);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("startup-splash.logo", ex.Message, ex);
            return null;
        }
    }

    /// <summary>Private message-loop form — never parented to the main windows.</summary>
    private sealed class SplashForm : Form
    {
        private readonly string _title;
        private readonly string _body;
        private readonly string _brandLabel;
        private readonly Color _bg;
        private readonly Color _fg;
        private readonly Color _muted;
        private readonly Color _border;
        private readonly Color _accent;
        private readonly Bitmap? _icon;
        private readonly Bitmap? _brandLogo;
        private readonly System.Windows.Forms.Timer _pulseTimer;
        private float _phase;
        private readonly Font _titleFont;
        private readonly Font _bodyFont;
        private readonly Font _brandFont;

        public SplashForm(
            string title,
            string body,
            string brandLabel,
            Color bg,
            Color fg,
            Color muted,
            Color border,
            Color accent,
            Bitmap? icon,
            Bitmap? brandLogo)
        {
            _title = title;
            _body = body;
            _brandLabel = brandLabel;
            _bg = bg;
            _fg = fg;
            _muted = muted;
            _border = border;
            _accent = accent;
            _icon = icon;
            _brandLogo = brandLogo;

            _titleFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold, GraphicsUnit.Point);
            _bodyFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            // Matches tray menu header (≈13px semi-bold, secondary tone).
            _brandFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            // Not a modal app window — just a floating card.
            ShowIcon = false;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            DoubleBuffered = true;
            BackColor = bg;
            // Room for: title block · gap · progress (mid) · gap · brand · bottom margin.
            ClientSize = new Size(340, 164);
            Opacity = 0.98;

            // Soft indeterminate bar animation — runs on THIS form's message loop only.
            _pulseTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _pulseTimer.Tick += (_, _) =>
            {
                _phase = (_phase + 0.045f) % 1f;
                var track = GetProgressTrackRect();
                // Inflate slightly so the moving segment never leaves dirty edges.
                Invalidate(Rectangle.Inflate(Rectangle.Round(track), 2, 2));
            };
            _pulseTimer.Start();

            // Rounded region after handle exists.
            HandleCreated += (_, _) => ApplyRoundedRegion();
            Resize += (_, _) => ApplyRoundedRegion();
        }

        // Layout (top → bottom): title block · gap · progress bar (mid band) · gap · brand · margin.
        private const int ContentPad = 20;
        private const int ProgressBarHeight = 4;
        private const int BrandLogoSize = 14;
        private const int GapAfterTitle = 22;
        private const int GapAfterBar = 16;
        private const int BottomMargin = 16;

        private RectangleF GetProgressTrackRect()
        {
            // Vertically centered in the free band under the title block and above the brand strip.
            float titleBlockBottom = ContentPad + 48f;
            float brandBlockHeight = BrandLogoSize + 2f;
            float freeTop = titleBlockBottom + GapAfterTitle;
            float freeBottom = Height - BottomMargin - brandBlockHeight - GapAfterBar;
            float freeMid = (freeTop + freeBottom) / 2f;
            float y = freeMid - ProgressBarHeight / 2f;
            return new RectangleF(ContentPad, y, Width - ContentPad * 2, ProgressBarHeight);
        }

        private void ApplyRoundedRegion()
        {
            try
            {
                const int radius = 14;
                using var path = RoundedRect(new Rectangle(0, 0, Width, Height), radius);
                Region = new Region(path);
            }
            catch
            {
                // Region is cosmetic only.
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(_bg);

            // Border
            using (var pen = new Pen(_border, 1.5f))
            using (var path = RoundedRect(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), 13f))
                g.DrawPath(pen, path);

            int logoSize = 40;
            int textLeft = ContentPad;

            if (_icon is not null)
            {
                try
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_icon, ContentPad, ContentPad + 2, logoSize, logoSize);
                    textLeft = ContentPad + logoSize + 14;
                }
                catch
                {
                    // Icon optional.
                }
            }

            using (var titleBrush = new SolidBrush(_fg))
            using (var bodyBrush = new SolidBrush(_muted))
            {
                float textTop = ContentPad + 2;
                g.DrawString(_title, _titleFont, titleBrush, textLeft, textTop);
                g.DrawString(_body, _bodyFont, bodyBrush, textLeft, textTop + 22);
            }

            // Progress bar in the mid band (clear of title and brand).
            var track = GetProgressTrackRect();
            using (var trackBrush = new SolidBrush(Color.FromArgb(40, _accent)))
            using (var trackPath = RoundedRect(track, 2f))
                g.FillPath(trackBrush, trackPath);

            float barW = track.Width * 0.32f;
            float travel = Math.Max(0, track.Width - barW);
            float x = track.X + (_phase * travel);
            var bar = new RectangleF(x, track.Y, barW, track.Height);
            using (var barBrush = new SolidBrush(Color.FromArgb(220, _accent)))
            using (var barPath = RoundedRect(bar, 2f))
                g.FillPath(barBrush, barPath);

            // Brand footer BELOW the bar (tray-style), with comfortable margins.
            DrawBrandFooter(g, track.Bottom + GapAfterBar);
        }

        /// <summary>
        /// Mirrors the tray menu header: 14px logo + CyberSnap version, soft secondary color.
        /// Centered under the progress bar.
        /// </summary>
        private void DrawBrandFooter(Graphics g, float y)
        {
            const int gap = 6;
            var textSize = g.MeasureString(_brandLabel, _brandFont);
            float contentW = textSize.Width + (_brandLogo is null ? 0 : BrandLogoSize + gap);
            float startX = (Width - contentW) / 2f;

            if (_brandLogo is not null)
            {
                try
                {
                    // Soft like tray Opacity 0.75
                    var colorMatrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.75f };
                    using var attrs = new System.Drawing.Imaging.ImageAttributes();
                    attrs.SetColorMatrix(colorMatrix);
                    var dest = new Rectangle(
                        (int)Math.Round(startX),
                        (int)Math.Round(y + (textSize.Height - BrandLogoSize) / 2f),
                        BrandLogoSize,
                        BrandLogoSize);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(
                        _brandLogo,
                        dest,
                        0, 0, _brandLogo.Width, _brandLogo.Height,
                        GraphicsUnit.Pixel,
                        attrs);
                    startX += BrandLogoSize + gap;
                }
                catch
                {
                    // Brand logo optional.
                }
            }

            using var brandBrush = new SolidBrush(Color.FromArgb(200, _muted));
            g.DrawString(_brandLabel, _brandFont, brandBrush, startX, y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _pulseTimer.Stop(); _pulseTimer.Dispose(); } catch { }
                try { _titleFont.Dispose(); } catch { }
                try { _bodyFont.Dispose(); } catch { }
                try { _brandFont.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            if (d <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Tool window: no taskbar flash, stays out of alt-tab clutter.
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // Don't steal focus from the window being constructed.
        protected override bool ShowWithoutActivation => true;
    }
}
