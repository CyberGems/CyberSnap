using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
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
    private readonly Color _bg;
    private readonly Color _fg;
    private readonly Color _muted;
    private readonly Color _border;
    private readonly Color _accent;
    private readonly Bitmap? _logo;

    private Thread? _thread;
    private SplashForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _closed = new(false);
    private int _disposed;

    private WindowStartupSplash(
        string title,
        string body,
        Color bg,
        Color fg,
        Color muted,
        Color border,
        Color accent,
        Bitmap? logo)
    {
        _title = title;
        _body = body;
        _bg = bg;
        _fg = fg;
        _muted = muted;
        _border = border;
        _accent = accent;
        _logo = logo;
    }

    /// <summary>
    /// Shows the splash immediately. Snapshots theme/logo on the calling thread, then
    /// pumps a private message loop so animation never depends on the main UI thread.
    /// </summary>
    public static WindowStartupSplash Show(string title, string? body = null)
    {
        var resolvedBody = string.IsNullOrWhiteSpace(body)
            ? LocalizationService.Translate("Preparing the workspace…")
            : body!;

        // Snapshot theme + logo on the caller (usually the UI thread) so the splash
        // thread never touches WPF Application / Theme while the main thread is busy.
        var bg = ToDrawing(Theme.ToastBg);
        var fg = ToDrawing(Theme.TextPrimary);
        var muted = ToDrawing(Theme.TextSecondary);
        var border = ToDrawing(Theme.ToastBorder);
        var accent = ToDrawing(Theme.Accent);
        var logo = TryLoadLogoBitmap();

        var splash = new WindowStartupSplash(title, resolvedBody, bg, fg, muted, border, accent, logo);
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
            _form = new SplashForm(_title, _body, _bg, _fg, _muted, _border, _accent, _logo);
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
            try { _logo?.Dispose(); } catch { }
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
        private readonly Color _bg;
        private readonly Color _fg;
        private readonly Color _muted;
        private readonly Color _border;
        private readonly Color _accent;
        private readonly Bitmap? _logo;
        private readonly System.Windows.Forms.Timer _pulseTimer;
        private float _phase;
        private readonly Font _titleFont;
        private readonly Font _bodyFont;

        public SplashForm(
            string title,
            string body,
            Color bg,
            Color fg,
            Color muted,
            Color border,
            Color accent,
            Bitmap? logo)
        {
            _title = title;
            _body = body;
            _bg = bg;
            _fg = fg;
            _muted = muted;
            _border = border;
            _accent = accent;
            _logo = logo;

            _titleFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold, GraphicsUnit.Point);
            _bodyFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

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
            ClientSize = new Size(340, 118);
            Opacity = 0.98;

            // Soft indeterminate bar animation — runs on THIS form's message loop only.
            _pulseTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _pulseTimer.Tick += (_, _) =>
            {
                _phase = (_phase + 0.045f) % 1f;
                Invalidate(new Rectangle(20, ClientSize.Height - 22, ClientSize.Width - 40, 6));
            };
            _pulseTimer.Start();

            // Rounded region after handle exists.
            HandleCreated += (_, _) => ApplyRoundedRegion();
            Resize += (_, _) => ApplyRoundedRegion();
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

            int pad = 20;
            int logoSize = 40;
            int textLeft = pad;

            if (_logo is not null)
            {
                try
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_logo, pad, pad + 2, logoSize, logoSize);
                    textLeft = pad + logoSize + 14;
                }
                catch
                {
                    // Logo optional.
                }
            }

            using (var titleBrush = new SolidBrush(_fg))
            using (var bodyBrush = new SolidBrush(_muted))
            {
                float textTop = pad + 2;
                g.DrawString(_title, _titleFont, titleBrush, textLeft, textTop);
                g.DrawString(_body, _bodyFont, bodyBrush, textLeft, textTop + 22);
            }

            // Indeterminate accent bar at the bottom.
            var track = new RectangleF(pad, Height - 22, Width - pad * 2, 4);
            using (var trackBrush = new SolidBrush(Color.FromArgb(40, _accent)))
            using (var trackPath = RoundedRect(track, 2f))
                g.FillPath(trackBrush, trackPath);

            float barW = track.Width * 0.32f;
            float travel = track.Width - barW;
            float x = track.X + (_phase * travel);
            var bar = new RectangleF(x, track.Y, barW, track.Height);
            using (var barBrush = new SolidBrush(Color.FromArgb(220, _accent)))
            using (var barPath = RoundedRect(bar, 2f))
                g.FillPath(barBrush, barPath);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _pulseTimer.Stop(); _pulseTimer.Dispose(); } catch { }
                try { _titleFont.Dispose(); } catch { }
                try { _bodyFont.Dispose(); } catch { }
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
