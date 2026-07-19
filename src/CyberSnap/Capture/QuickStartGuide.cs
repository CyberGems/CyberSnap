using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.Capture;

/// <summary>
/// Speech-bubble quick-start guide anchored to the toolbar logo.
/// Explains how to capture, points at the toolbar menu, and lists hotkeys.
/// </summary>
public sealed class QuickStartGuide : Form
{
    private const int MaxWidth = 460;
    private const int MinWidth = 380;
    private const int PadX = 22;
    private const int PadY = 20;
    private const int HeaderHeight = 30;
    private const int StepGap = 12;
    private const int StepCircle = 24;
    private const int StepTextGap = 14;
    private const int SectionGap = 16;
    private const int SectionLabelHeight = 22;
    private const int ShortcutRowHeight = 36;
    private const int KbdPadH = 10;
    private const int KbdPadV = 4;
    private const int KbdLabelGap = 10;
    private const int ShortcutColGap = 16;
    private const int TipRowMinHeight = 28;
    private const int TipRowGap = 8;
    private const int TipIconSize = 18;
    private const int IconColWidth = 24;
    private const int IconTextGap = 12;
    private const int FooterHeight = 24;
    // Classic comic-style talk bubble (see artifacts/2026-07-18_04-09-35.png).
    private const float Corner = 18f;
    private const float TailWidth = 28f;
    private const float TailHeight = 16f;
    private const int EnterDurationMs = 280;

    private readonly Font _headerFont = UiChrome.ChromeFont(13f, FontStyle.Bold);
    private readonly Font _sectionFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
    private readonly Font _bodyFont = UiChrome.ChromeFont(11f);
    private readonly Font _stepNumFont = UiChrome.ChromeFont(10f, FontStyle.Bold);
    private readonly Font _keyFont = UiChrome.ChromeFont(9.5f, FontStyle.Bold);
    private readonly Font _footerFont = UiChrome.ChromeFont(9f);

    private record ShortcutDef(string Key, string Label);
    private record StepDef(string Text);
    private record TipDef(string? IconId, string Text);

    private ShortcutDef[] _shortcuts = Array.Empty<ShortcutDef>();
    private StepDef[] _steps = Array.Empty<StepDef>();
    private TipDef[] _tips = Array.Empty<TipDef>();
    private string _title = "";
    private string _stepsTitle = "";
    private string _menuTitle = "";
    private string _shortcutsTitle = "";
    private string _footerText = "";
    private int _contentWidth;
    private int _bodyHeight;
    private int _shortcutColWidth;
    private int[] _stepHeights = Array.Empty<int>();
    private int[] _tipHeights = Array.Empty<int>();
    private Rectangle _closeRect;
    private bool _closeHovered;
    private bool _tailPointsDown = true;
    private float _tailCenterX;
    private RectangleF _bodyRect;

    // Soft fade-in only (Form.Opacity). Scale/slide caused a one-frame glitch with Region.
    private DateTime _enterStart;
    private System.Windows.Forms.Timer? _enterTimer;
    private bool _entering;

    public QuickStartGuide()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = UiChrome.SurfaceTier1;
        ForeColor = UiChrome.SurfaceTextPrimary;
    }

    protected override bool ShowWithoutActivation => true;

    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTCLIENT = new(1);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTCLIENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CaptureWindowExclusion.Register(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        CaptureWindowExclusion.Unregister(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Close();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hovered = _closeRect.Contains(e.Location);
        if (hovered != _closeHovered)
        {
            _closeHovered = hovered;
            Invalidate(_closeRect);
        }
        Cursor = hovered ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_closeHovered)
        {
            _closeHovered = false;
            Invalidate(_closeRect);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>
    /// Shows the guide as a talk bubble pointing at <paramref name="anchorScreenBounds"/> (logo).
    /// When <paramref name="above"/> is true the bubble sits above the anchor with the tail down.
    /// </summary>
    public void ShowNear(IWin32Window owner, Rectangle anchorScreenBounds, bool above)
    {
        CyberSnap.UI.Theme.Refresh();
        BackColor = UiChrome.SurfaceTier1;
        ForeColor = UiChrome.SurfaceTextPrimary;

        LoadStrings();

        using var g = CreateGraphics();
        MeasureLayout(g);

        int width = Math.Min(MaxWidth, Math.Max(MinWidth, _contentWidth + PadX * 2));
        _contentWidth = width - PadX * 2;

        // Re-measure tip/step wrap with final content width
        MeasureLayout(g);

        int bodyH = _bodyHeight;
        int totalH = bodyH + (int)Math.Ceiling(TailHeight);
        int totalW = width;

        _tailPointsDown = above;
        _bodyRect = above
            ? new RectangleF(0, 0, totalW, bodyH)
            : new RectangleF(0, TailHeight, totalW, bodyH);

        // Align bubble so the tail points at the logo center; prefer leftish placement
        // so the bubble does not cover the whole toolbar.
        int anchorCx = anchorScreenBounds.Left + anchorScreenBounds.Width / 2;
        int preferredX = anchorCx - (int)(totalW * 0.22f);
        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        int x = Math.Clamp(preferredX, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - totalW - 4));

        // Keep the caret tip close to the logo without overlapping it.
        int gap = 2;
        int y = above
            ? anchorScreenBounds.Top - totalH - gap
            : anchorScreenBounds.Bottom + gap;
        y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - totalH - 4));

        // Tail tip X in local coords — clamp so it stays on the body edge
        float localAnchorX = anchorCx - x;
        float minTail = Corner + TailWidth / 2f + 4f;
        float maxTail = totalW - Corner - TailWidth / 2f - 4f;
        _tailCenterX = Math.Clamp(localAnchorX, minTail, maxTail);

        Bounds = new Rectangle(x, y, totalW, totalH);
        ApplyBubbleRegion(totalW, totalH);

        bool animate = !CyberSnap.UI.Motion.Disabled;
        try { Opacity = animate ? 0.01 : 1.0; } catch { Opacity = 1.0; }

        Show(owner);

        try
        {
            Native.Dwm.TrySetWindowCornerPreference(Handle, Native.Dwm.DWMWCP_DONOTROUND);
            Native.Dwm.TrySetImmersiveDarkMode(Handle, UiChrome.IsDark);
        }
        catch { }

        // Force a complete first paint while still nearly invisible, then fade up.
        // Avoids the half-formed content flash that looked like a glitch.
        try { Update(); } catch { }

        if (animate)
            BeginInvoke(new Action(StartEnterAnimation));
        else
            try { Opacity = 1.0; } catch { }
    }

    private void StartEnterAnimation()
    {
        if (IsDisposed || Disposing)
            return;
        _entering = true;
        _enterStart = DateTime.UtcNow;
        try { Opacity = 0.01; } catch { }
        _enterTimer?.Stop();
        _enterTimer ??= new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _enterTimer.Tick -= OnEnterTick;
        _enterTimer.Tick += OnEnterTick;
        _enterTimer.Start();
    }

    private void OnEnterTick(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || !_entering)
        {
            StopEnterAnimation();
            return;
        }

        float raw = (float)(DateTime.UtcNow - _enterStart).TotalMilliseconds / EnterDurationMs;
        if (raw >= 1f)
        {
            try { Opacity = 1.0; } catch { }
            StopEnterAnimation();
            return;
        }

        // Ease-out cubic fade only — no scale/slide (those caused a Region redraw glitch).
        float t = 1f - MathF.Pow(1f - raw, 3f);
        try { Opacity = Math.Clamp(0.01 + 0.99 * t, 0.01, 1.0); } catch { }
    }

    private void StopEnterAnimation()
    {
        _entering = false;
        if (_enterTimer == null) return;
        _enterTimer.Stop();
        _enterTimer.Tick -= OnEnterTick;
    }

    private void LoadStrings()
    {
        string T(string key) => LocalizationService.Translate(key);

        _title = T("Quick Start");
        _stepsTitle = T("HOW TO CAPTURE");
        _menuTitle = T("TOOLBAR MENU");
        _shortcutsTitle = T("KEYBOARD SHORTCUTS");
        _footerText = T("Click or Esc to close");

        _steps =
        [
            new StepDef(T("Drag to select the capture area")),
            new StepDef(T("Press Enter to capture, or Esc to cancel")),
            new StepDef(T("Use toolbar tools to annotate before capturing")),
        ];

        _tips =
        [
            new TipDef("position", T("Drag the toolbar or click ⇅ to reposition it")),
            new TipDef("more", T("▼ opens hidden tools, preferences, and this help")),
            new TipDef("rect", T("Right-click a tool on the bar to hide it")),
        ];

        _shortcuts =
        [
            new ShortcutDef("Enter", T("Capture")),
            new ShortcutDef("Esc", T("Cancel")),
            new ShortcutDef("Ctrl+Z", T("Undo")),
            new ShortcutDef("Ctrl+Y", T("Redo")),
        ];
    }

    private void MeasureLayout(Graphics g)
    {
        int maxShortcutCellW = 0;
        foreach (var sc in _shortcuts)
            maxShortcutCellW = Math.Max(maxShortcutCellW, MeasureShortcutCell(g, sc));

        int stepsTextCol = Math.Max(160, (_contentWidth > 0 ? _contentWidth : MinWidth - PadX * 2) - StepCircle - StepTextGap);
        _stepHeights = new int[_steps.Length];
        int stepsBlock = 0;
        for (int i = 0; i < _steps.Length; i++)
        {
            var size = TextRenderer.MeasureText(g, _steps[i].Text, _bodyFont,
                new Size(stepsTextCol, 0),
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            _stepHeights[i] = Math.Max(StepCircle, size.Height + 2);
            stepsBlock += _stepHeights[i];
            if (i < _steps.Length - 1)
                stepsBlock += StepGap;
        }

        int tipTextW = Math.Max(140, (_contentWidth > 0 ? _contentWidth : MinWidth - PadX * 2) - IconColWidth - IconTextGap);
        _tipHeights = new int[_tips.Length];
        int tipsBlock = 0;
        for (int i = 0; i < _tips.Length; i++)
        {
            using var format = StringFormat.GenericTypographic;
            var size = g.MeasureString(_tips[i].Text, _bodyFont, tipTextW, format);
            _tipHeights[i] = Math.Max(TipRowMinHeight, (int)Math.Ceiling(size.Height) + 4);
            tipsBlock += _tipHeights[i];
            if (i < _tips.Length - 1)
                tipsBlock += TipRowGap;
        }

        int titleW = TextRenderer.MeasureText(g, _title, _headerFont, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 28 + 28;
        int stepLineMax = 0;
        for (int i = 0; i < _steps.Length; i++)
        {
            int tw = TextRenderer.MeasureText(g, _steps[i].Text, _bodyFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            stepLineMax = Math.Max(stepLineMax, StepCircle + StepTextGap + tw);
        }
        int tipLineMax = 0;
        foreach (var tip in _tips)
        {
            int tw = (int)Math.Ceiling(g.MeasureString(tip.Text, _bodyFont, int.MaxValue, StringFormat.GenericTypographic).Width);
            tipLineMax = Math.Max(tipLineMax, IconColWidth + IconTextGap + tw);
        }

        int needed = Math.Max(titleW, Math.Max(stepLineMax, Math.Max(tipLineMax, maxShortcutCellW * 2 + ShortcutColGap)));
        if (_contentWidth <= 0)
            _contentWidth = Math.Max(MinWidth - PadX * 2, Math.Min(MaxWidth - PadX * 2, needed + 8));
        _shortcutColWidth = maxShortcutCellW;

        int y = PadY;
        y += HeaderHeight + SectionGap;
        y += SectionLabelHeight + 8;
        y += stepsBlock + SectionGap;
        y += SectionLabelHeight + 8;
        y += tipsBlock + SectionGap;
        y += SectionLabelHeight + 10;
        int shortcutRows = (_shortcuts.Length + 1) / 2;
        y += shortcutRows * ShortcutRowHeight + 10;
        y += 1 + 8 + FooterHeight + PadY;
        _bodyHeight = y;
    }

    private void ApplyBubbleRegion(int width, int height)
    {
        Region?.Dispose();
        using var path = CreateBubblePath(width, height);
        Region = new Region(path);
    }

    /// <summary>
    /// Classic talk-bubble silhouette: rounded rect + triangular caret pointing at the logo.
    /// Tail base is slightly asymmetric so the tip reads as a comic speech pointer.
    /// </summary>
    private GraphicsPath CreateBubblePath(float width, float height)
    {
        var body = _bodyRect;
        float r = Math.Min(Corner, Math.Min(body.Width, body.Height) / 2f - 1f);
        float d = r * 2f;
        float tw = TailWidth;
        float th = TailHeight;
        float tx = _tailCenterX;

        // Classic bubble: wider base on the outer side, tip slightly biased toward logo.
        float baseLeft = tx - tw * 0.55f;
        float baseRight = tx + tw * 0.40f;
        float tipX = tx - tw * 0.06f; // slight left lean like the reference

        // Keep base fully on the flat bottom/top edge (inside corner radii).
        float minBase = body.X + r + 2f;
        float maxBase = body.Right - r - 2f;
        baseLeft = Math.Clamp(baseLeft, minBase, maxBase - 8f);
        baseRight = Math.Clamp(baseRight, baseLeft + 8f, maxBase);
        tipX = Math.Clamp(tipX, baseLeft + 2f, baseRight - 2f);

        var path = new GraphicsPath();

        if (_tailPointsDown)
        {
            // Clockwise: top-left → top-right → bottom-right → tail → bottom-left
            path.AddArc(body.X, body.Y, d, d, 180, 90);
            path.AddArc(body.Right - d, body.Y, d, d, 270, 90);
            path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90);
            path.AddLine(body.Right - r, body.Bottom, baseRight, body.Bottom);
            path.AddLine(baseRight, body.Bottom, tipX, body.Bottom + th);
            path.AddLine(tipX, body.Bottom + th, baseLeft, body.Bottom);
            path.AddLine(baseLeft, body.Bottom, body.X + r, body.Bottom);
            path.AddArc(body.X, body.Bottom - d, d, d, 90, 90);
        }
        else
        {
            // Tail on top (toolbar docked at top)
            path.AddArc(body.X, body.Y, d, d, 180, 90);
            path.AddLine(body.X + r, body.Y, baseLeft, body.Y);
            path.AddLine(baseLeft, body.Y, tipX, body.Y - th);
            path.AddLine(tipX, body.Y - th, baseRight, body.Y);
            path.AddLine(baseRight, body.Y, body.Right - r, body.Y);
            path.AddArc(body.Right - d, body.Y, d, d, 270, 90);
            path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90);
            path.AddArc(body.X, body.Bottom - d, d, d, 90, 90);
        }

        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var accent = UiChrome.AccentColor;
        using (var path = CreateBubblePath(Width, Height))
        {
            // Soft ambient shadow under the bubble
            using (var shadowPath = (GraphicsPath)path.Clone())
            {
                using var m = new Matrix();
                m.Translate(0, 3f);
                shadowPath.Transform(m);
                using var shadow = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 80 : 48, 0, 0, 0));
                g.FillPath(shadow, shadowPath);
            }

            using var bg = new SolidBrush(UiChrome.SurfaceTier1);
            g.FillPath(bg, path);

            // Light rim like the reference bubble (readable on dark chrome)
            Color rim = UiChrome.IsDark
                ? Color.FromArgb(210, 235, 235, 240)
                : Color.FromArgb(220, 255, 255, 255);
            using (var rimPen = new Pen(rim, 2.2f) { LineJoin = LineJoin.Round, Alignment = PenAlignment.Inset })
                g.DrawPath(rimPen, path);

            // Subtle accent inner edge so it still feels on-brand
            using (var accentPen = new Pen(Color.FromArgb(UiChrome.IsDark ? 90 : 70, accent), 1f)
            {
                LineJoin = LineJoin.Round,
                Alignment = PenAlignment.Inset,
            })
                g.DrawPath(accentPen, path);
        }

        int originY = (int)Math.Round(_bodyRect.Y);
        int curY = originY + PadY;

        // Header
        const int closeBtnSize = 16;
        _closeRect = new Rectangle(
            Width - PadX - closeBtnSize - 8,
            originY + PadY - 4,
            closeBtnSize + 12,
            closeBtnSize + 12);

        if (_closeHovered)
        {
            using var hoverPath = WindowsDockRenderer.RoundedRect(_closeRect, 6f);
            using var hoverBrush = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 32 : 22, accent));
            g.FillPath(hoverBrush, hoverPath);
        }

        FluentIcons.DrawIcon(g, "info",
            new RectangleF(PadX, curY + 4, 18, 18), accent, iconInset: 1f);

        var headerRect = new Rectangle(PadX + 26, curY, _contentWidth - closeBtnSize - 36, HeaderHeight);
        TextRenderer.DrawText(g, _title, _headerFont, headerRect,
            UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var closeColor = _closeHovered
            ? UiChrome.SurfaceTextPrimary
            : Color.FromArgb(150, UiChrome.SurfaceTextSecondary);
        FluentIcons.DrawIcon(g, "close",
            new RectangleF(_closeRect.X + 6, _closeRect.Y + 6, closeBtnSize, closeBtnSize),
            closeColor, iconInset: 0f);
        curY += HeaderHeight + SectionGap;

        // Steps
        curY = PaintSectionLabel(g, _stepsTitle, curY) + 8;
        curY = PaintSteps(g, curY, accent) + SectionGap;

        // Toolbar menu tips
        curY = PaintSectionLabel(g, _menuTitle, curY) + 8;
        curY = PaintTips(g, curY) + SectionGap;

        // Shortcuts
        curY = PaintSectionLabel(g, _shortcutsTitle, curY) + 10;
        curY = PaintShortcutGrid(g, curY);

        // Footer
        int footerY = originY + (int)_bodyRect.Height - PadY - FooterHeight - 4;
        using (var sep = new Pen(UiChrome.SurfaceBorderSubtle, 1f))
            g.DrawLine(sep, PadX, footerY, Width - PadX, footerY);

        var footerRect = new Rectangle(PadX, footerY + 6, _contentWidth, FooterHeight);
        TextRenderer.DrawText(g, _footerText, _footerFont, footerRect,
            UiChrome.SurfaceTextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private int PaintSectionLabel(Graphics g, string text, int y)
    {
        var rect = new Rectangle(PadX, y, _contentWidth, SectionLabelHeight);
        TextRenderer.DrawText(g, text, _sectionFont, rect,
            Color.FromArgb(UiChrome.IsDark ? 170 : 140, UiChrome.SurfaceTextSecondary),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return y + SectionLabelHeight;
    }

    private int PaintSteps(Graphics g, int startY, Color accent)
    {
        int curY = startY;
        for (int i = 0; i < _steps.Length; i++)
        {
            int rowH = i < _stepHeights.Length ? _stepHeights[i] : StepCircle;
            float cy = curY + rowH / 2f;

            // Number circle
            var circle = new RectangleF(PadX, cy - StepCircle / 2f, StepCircle, StepCircle);
            using (var fill = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 40 : 30, accent)))
                g.FillEllipse(fill, circle);
            using (var ring = new Pen(Color.FromArgb(UiChrome.IsDark ? 140 : 110, accent), 1.2f))
                g.DrawEllipse(ring, circle);

            string num = (i + 1).ToString();
            TextRenderer.DrawText(g, num, _stepNumFont,
                Rectangle.Round(circle),
                accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int textX = PadX + StepCircle + StepTextGap;
            int textW = _contentWidth - StepCircle - StepTextGap;
            var textRect = new Rectangle(textX, curY, textW, rowH);
            TextRenderer.DrawText(g, _steps[i].Text, _bodyFont, textRect,
                UiChrome.SurfaceTextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            curY += rowH;
            if (i < _steps.Length - 1)
                curY += StepGap;
        }
        return curY;
    }

    private int PaintTips(Graphics g, int startY)
    {
        int curY = startY;
        var iconColor = UiChrome.AccentColor;
        int tipTextWidth = _contentWidth - IconColWidth - IconTextGap;

        for (int i = 0; i < _tips.Length; i++)
        {
            var tip = _tips[i];
            int rowH = i < _tipHeights.Length ? _tipHeights[i] : TipRowMinHeight;

            float iconY = curY + (rowH - TipIconSize) / 2f;
            var iconRect = new RectangleF(PadX + 2, iconY, TipIconSize, TipIconSize);

            if (tip.IconId != null && FluentIcons.HasIcon(tip.IconId))
                FluentIcons.DrawIcon(g, tip.IconId, iconRect, iconColor, iconInset: 0f);
            else
            {
                using var dot = new SolidBrush(Color.FromArgb(180, iconColor));
                g.FillEllipse(dot, PadX + IconColWidth / 2f - 3f, curY + rowH / 2f - 3f, 6f, 6f);
            }

            var tipTextRect = new RectangleF(PadX + IconColWidth + IconTextGap, curY, tipTextWidth, rowH);
            using var textBrush = new SolidBrush(UiChrome.SurfaceTextSecondary);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
            };
            g.DrawString(tip.Text, _bodyFont, textBrush, tipTextRect, format);

            curY += rowH;
            if (i < _tips.Length - 1)
                curY += TipRowGap;
        }
        return curY;
    }

    private int PaintShortcutGrid(Graphics g, int startY)
    {
        var accent = UiChrome.AccentColor;
        for (int i = 0; i < _shortcuts.Length; i++)
        {
            int col = i % 2;
            int row = i / 2;
            int cellX = PadX + col * (_shortcutColWidth + ShortcutColGap);
            int cellY = startY + row * ShortcutRowHeight;
            PaintShortcutCell(g, _shortcuts[i], cellX, cellY, accent);
        }
        int rows = (_shortcuts.Length + 1) / 2;
        return startY + rows * ShortcutRowHeight;
    }

    private void PaintShortcutCell(Graphics g, ShortcutDef sc, int x, int y, Color accent)
    {
        int keyW = TextRenderer.MeasureText(g, sc.Key, _keyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        int labelW = TextRenderer.MeasureText(g, sc.Label, _bodyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;

        int kbdW = Math.Max(48, keyW + KbdPadH * 2 + 6);
        int kbdH = ShortcutRowHeight - 8;
        var kbdRect = new RectangleF(x, y + 4, kbdW, kbdH);

        using (var kbdPath = WindowsDockRenderer.RoundedRect(kbdRect, 6f))
        {
            using var kbdBg = new SolidBrush(UiChrome.SurfaceTier2);
            g.FillPath(kbdBg, kbdPath);
            using var kbdBorder = new Pen(Color.FromArgb(UiChrome.IsDark ? 100 : 75, accent), 1.2f);
            g.DrawPath(kbdBorder, kbdPath);
        }

        TextRenderer.DrawText(g, sc.Key, _keyFont,
            Rectangle.Round(kbdRect),
            accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, sc.Label, _bodyFont,
            new Rectangle(x + kbdW + KbdLabelGap, y + 4, labelW + 12, kbdH),
            UiChrome.SurfaceTextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private int MeasureShortcutCell(Graphics g, ShortcutDef sc)
    {
        int keyW = TextRenderer.MeasureText(g, sc.Key, _keyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        int labelW = TextRenderer.MeasureText(g, sc.Label, _bodyFont,
            new Size(0, 0), TextFormatFlags.NoPadding).Width;
        int kbdW = Math.Max(48, keyW + KbdPadH * 2 + 6);
        return kbdW + KbdLabelGap + labelW + 8;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopEnterAnimation();
        base.OnFormClosed(e);
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopEnterAnimation();
            if (_enterTimer != null)
            {
                _enterTimer.Dispose();
                _enterTimer = null;
            }
            Region?.Dispose();
            _headerFont.Dispose();
            _sectionFont.Dispose();
            _bodyFont.Dispose();
            _stepNumFont.Dispose();
            _keyFont.Dispose();
            _footerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
