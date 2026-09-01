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
    public enum GuideMode { Capture, Annotation }

    public enum TailDirection { Up, Down, Left, Right }

    private const int MaxWidth = 540;
    private const int MinWidth = 440;
    private const int PadX = 28;
    private const int PadY = 24;
    private const int HeaderHeight = 30;
    private const int StepGap = 14;
    private const int StepCircle = 26;
    private const int StepTextGap = 12;
    private const int SectionGap = 18;
    private const int SectionLabelHeight = 24;
    private const int ShortcutRowHeight = 34;
    private const int KbdPadH = 8;
    private const int KbdLabelGap = 8;
    private const int ShortcutColGap = 20;
    private const int TipRowMinHeight = 32;
    private const int TipRowGap = 12;
    private const int TipIconSize = 28;
    private const int IconColWidth = 36;
    private const int IconTextGap = 12;
    private const int FooterHeight = 28;
    private const int TextOverhang = 6;
    // Classic comic-style talk bubble (see artifacts/2026-07-18_04-09-35.png).
    private const float Corner = 18f;
    private const float TailWidth = 28f;
    private const float TailHeight = 16f;
    private const int EnterDurationMs = 280;

    private readonly Font _headerFont = UiChrome.ChromeFont(13f, FontStyle.Bold);
    private readonly Font _sectionFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
    private readonly Font _bodyFont = UiChrome.ChromeFont(11f);
    private readonly Font _stepNumFont = UiChrome.ChromeFont(11.5f, FontStyle.Bold);
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
    private int _contentLeft;
    private int _contentWidth;
    private int _bodyHeight;
    private int _shortcutColWidth;
    private int[] _stepHeights = Array.Empty<int>();
    private int[] _tipHeights = Array.Empty<int>();
    private Rectangle _closeRect;
    private bool _closeHovered;
    private TailDirection _tailDirection = TailDirection.Down;
    private float _tailCenterX;
    private float _tailCenterY;
    private RectangleF _bodyRect;
    private GuideMode _guideMode = GuideMode.Capture;

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
    /// Shows the guide as a talk bubble pointing at <paramref name="anchorScreenBounds"/>.
    /// The tail direction determines where the bubble appears relative to the anchor.
    /// </summary>
    public void ShowNear(IWin32Window owner, Rectangle anchorScreenBounds, TailDirection tailDirection, GuideMode mode = GuideMode.Capture)
    {
        _guideMode = mode;
        _tailDirection = tailDirection;
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
        int bodyW = width;
        bool horizontalTail = tailDirection == TailDirection.Up || tailDirection == TailDirection.Down;
        int totalH = bodyH + (horizontalTail ? (int)Math.Ceiling(TailHeight) : 0);
        int totalW = bodyW + (horizontalTail ? 0 : (int)Math.Ceiling(TailHeight));

        // Position body rect based on tail direction
        _bodyRect = tailDirection switch
        {
            TailDirection.Up => new RectangleF(0, TailHeight, bodyW, bodyH),
            TailDirection.Down => new RectangleF(0, 0, bodyW, bodyH),
            TailDirection.Left => new RectangleF(TailHeight, 0, bodyW, bodyH),
            TailDirection.Right => new RectangleF(0, 0, bodyW, bodyH),
            _ => new RectangleF(0, 0, bodyW, bodyH)
        };

        var screen = Screen.FromRectangle(anchorScreenBounds).WorkingArea;
        int anchorCx = anchorScreenBounds.Left + anchorScreenBounds.Width / 2;
        int anchorCy = anchorScreenBounds.Top + anchorScreenBounds.Height / 2;
        int gap = 2;
        int x, y;

        switch (tailDirection)
        {
            case TailDirection.Up:
                // Bubble below anchor, tail points up
                x = Math.Clamp(anchorCx - (int)(totalW * 0.22f), screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - totalW - 4));
                y = anchorScreenBounds.Bottom + gap;
                y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - totalH - 4));
                break;
            case TailDirection.Down:
                // Bubble above anchor, tail points down
                x = Math.Clamp(anchorCx - (int)(totalW * 0.22f), screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - totalW - 4));
                y = anchorScreenBounds.Top - totalH - gap;
                y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - totalH - 4));
                break;
            case TailDirection.Left:
                // Bubble to the right of anchor, tail points left
                x = anchorScreenBounds.Right + gap;
                x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - totalW - 4));
                y = Math.Clamp(anchorCy - (int)(totalH * 0.5f), screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - totalH - 4));
                break;
            case TailDirection.Right:
                // Bubble to the left of anchor, tail points right
                x = anchorScreenBounds.Left - totalW - gap;
                x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - totalW - 4));
                y = Math.Clamp(anchorCy - (int)(totalH * 0.5f), screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - totalH - 4));
                break;
            default:
                x = Math.Clamp(anchorCx - (int)(totalW * 0.22f), screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - totalW - 4));
                y = anchorScreenBounds.Top - totalH - gap;
                y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - totalH - 4));
                break;
        }

        // Set tail center in local coords
        if (horizontalTail)
        {
            float localAnchorX = anchorCx - x;
            float minTail = Corner + TailWidth / 2f + 4f;
            float maxTail = totalW - Corner - TailWidth / 2f - 4f;
            _tailCenterX = Math.Clamp(localAnchorX, minTail, maxTail);
        }
        else
        {
            float localAnchorY = anchorCy - y;
            float minTail = Corner + TailWidth / 2f + 4f;
            float maxTail = totalH - Corner - TailWidth / 2f - 4f;
            _tailCenterY = Math.Clamp(localAnchorY, minTail, maxTail);
        }

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

        if (_guideMode == GuideMode.Annotation)
        {
            LoadAnnotationStrings(T);
        }
        else
        {
            LoadCaptureStrings(T);
        }
    }

    private void LoadCaptureStrings(Func<string, string> T)
    {
        _title = T("Quick Start");
        _stepsTitle = T("HOW TO CAPTURE");
        _menuTitle = T("CAPTURE BAR & MENU");
        _shortcutsTitle = T("KEYBOARD SHORTCUTS");
        _footerText = T("Click or Esc to close");

        _steps =
        [
            new StepDef(T("Press your capture hotkey or click the floating widget")),
            new StepDef(T("Drag to select the area, or click a window to capture it")),
            new StepDef(T("Use the preview to save, edit, or share your capture")),
        ];

        _tips =
        [
            new TipDef("position", T("The capture bar offers area, window, scrolling, recording, OCR, and QR modes")),
            new TipDef("moreVertical", T("Right-click the ⋮ menu for toolbar dock, hidden tools, and preferences")),
            new TipDef("select", T("Annotation tools let you draw, add text, arrows, shapes, and more")),
        ];

        _shortcuts =
        [
            new ShortcutDef("Enter", T("Confirm capture")),
            new ShortcutDef("Esc", T("Cancel")),
            new ShortcutDef("Ctrl+Z", T("Undo")),
            new ShortcutDef("Ctrl+Y", T("Redo")),
            new ShortcutDef("[ ]", T("Stroke width")),
            new ShortcutDef("Del", T("Delete annotation")),
        ];
    }

    private void LoadAnnotationStrings(Func<string, string> T)
    {
        _title = T("Editor Quick Start");
        _stepsTitle = T("HOW TO ANNOTATE");
        _menuTitle = T("TIPS");
        _shortcutsTitle = T("KEYBOARD SHORTCUTS");
        _footerText = T("Click or Esc to close");

        _steps =
        [
            new StepDef(T("Open a capture in the editor to access annotation tools")),
            new StepDef(T("Select a tool from the toolbar — draw, text, arrows, shapes, and more")),
            new StepDef(T("Use color and stroke options to customize your annotations")),
        ];

        _tips =
        [
            new TipDef("draw", T("F1-F12 keys quickly switch between annotation tools")),
            new TipDef("select", T("Right-click objects to duplicate, delete, or transform them")),
            new TipDef("menu", T("Use the burger menu for save, export, view options, and more")),
        ];

        _shortcuts =
        [
            new ShortcutDef("F1–F12", T("Select tool")),
            new ShortcutDef("Ctrl+Z", T("Undo")),
            new ShortcutDef("Ctrl+Y", T("Redo")),
            new ShortcutDef("Ctrl+S", T("Save")),
            new ShortcutDef("Ctrl+C", T("Copy")),
            new ShortcutDef("Del", T("Delete object")),
        ];
    }

    private void MeasureLayout(Graphics g)
    {
        int stepsTextCol = Math.Max(160, (_contentWidth > 0 ? _contentWidth : MinWidth - PadX * 2) - StepCircle - StepTextGap);
        _stepHeights = new int[_steps.Length];
        int stepsBlock = 0;
        for (int i = 0; i < _steps.Length; i++)
        {
            var size = TextRenderer.MeasureText(g, _steps[i].Text, _bodyFont,
                new Size(stepsTextCol, 0),
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            _stepHeights[i] = Math.Max(StepCircle, size.Height + TextOverhang);
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
            _tipHeights[i] = Math.Max(TipRowMinHeight, (int)Math.Ceiling(size.Height) + TextOverhang);
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

        int needed = Math.Max(titleW, Math.Max(stepLineMax, tipLineMax));
        if (_contentWidth <= 0)
            _contentWidth = Math.Max(MinWidth - PadX * 2, Math.Min(MaxWidth - PadX * 2, needed + 8));
        _shortcutColWidth = Math.Max(120, (_contentWidth - ShortcutColGap) / 2);

        int y = PadY;
        y += HeaderHeight + SectionGap;
        y += SectionLabelHeight + 8;
        y += stepsBlock + SectionGap;
        y += SectionLabelHeight + 8;
        y += tipsBlock + SectionGap;
        y += SectionLabelHeight + 10;
        int shortcutRows = (_shortcuts.Length + 1) / 2;
        y += shortcutRows * ShortcutRowHeight + 10;
        y += 1 + 10 + FooterHeight + PadY;
        _bodyHeight = y;
    }

    private void ApplyBubbleRegion(int width, int height)
    {
        Region?.Dispose();
        using var path = CreateBubblePath(width, height);
        Region = new Region(path);
    }

    /// <summary>
    /// Classic talk-bubble silhouette: rounded rect + triangular caret pointing at the anchor.
    /// Tail base is slightly asymmetric so the tip reads as a comic speech pointer.
    /// Supports all four directions (Up, Down, Left, Right).
    /// </summary>
    private GraphicsPath CreateBubblePath(float width, float height)
    {
        var body = _bodyRect;
        float r = Math.Min(Corner, Math.Min(body.Width, body.Height) / 2f - 1f);
        float d = r * 2f;
        float tw = TailWidth;
        float th = TailHeight;

        var path = new GraphicsPath();

        switch (_tailDirection)
        {
            case TailDirection.Down:
                // Tail on bottom edge
                {
                    float tx = _tailCenterX;
                    float baseLeft = tx - tw * 0.55f;
                    float baseRight = tx + tw * 0.40f;
                    float tipX = tx - tw * 0.06f;
                    float minBase = body.X + r + 2f;
                    float maxBase = body.Right - r - 2f;
                    baseLeft = Math.Clamp(baseLeft, minBase, maxBase - 8f);
                    baseRight = Math.Clamp(baseRight, baseLeft + 8f, maxBase);
                    tipX = Math.Clamp(tipX, baseLeft + 2f, baseRight - 2f);
                    path.AddArc(body.X, body.Y, d, d, 180, 90);
                    path.AddArc(body.Right - d, body.Y, d, d, 270, 90);
                    path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90);
                    path.AddLine(body.Right - r, body.Bottom, baseRight, body.Bottom);
                    path.AddLine(baseRight, body.Bottom, tipX, body.Bottom + th);
                    path.AddLine(tipX, body.Bottom + th, baseLeft, body.Bottom);
                    path.AddLine(baseLeft, body.Bottom, body.X + r, body.Bottom);
                    path.AddArc(body.X, body.Bottom - d, d, d, 90, 90);
                }
                break;
            case TailDirection.Up:
                // Tail on top edge
                {
                    float tx = _tailCenterX;
                    float baseLeft = tx - tw * 0.55f;
                    float baseRight = tx + tw * 0.40f;
                    float tipX = tx - tw * 0.06f;
                    float minBase = body.X + r + 2f;
                    float maxBase = body.Right - r - 2f;
                    baseLeft = Math.Clamp(baseLeft, minBase, maxBase - 8f);
                    baseRight = Math.Clamp(baseRight, baseLeft + 8f, maxBase);
                    tipX = Math.Clamp(tipX, baseLeft + 2f, baseRight - 2f);
                    path.AddArc(body.X, body.Y, d, d, 180, 90);
                    path.AddLine(body.X + r, body.Y, baseLeft, body.Y);
                    path.AddLine(baseLeft, body.Y, tipX, body.Y - th);
                    path.AddLine(tipX, body.Y - th, baseRight, body.Y);
                    path.AddLine(baseRight, body.Y, body.Right - r, body.Y);
                    path.AddArc(body.Right - d, body.Y, d, d, 270, 90);
                    path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90);
                    path.AddArc(body.X, body.Bottom - d, d, d, 90, 90);
                }
                break;
            case TailDirection.Left:
                // Tail on left edge (bubble to the right of anchor)
                {
                    float ty = _tailCenterY;
                    float baseTop = ty - tw * 0.55f;
                    float baseBottom = ty + tw * 0.40f;
                    float tipY = ty - tw * 0.06f;
                    float minBase = body.Y + r + 2f;
                    float maxBase = body.Bottom - r - 2f;
                    baseTop = Math.Clamp(baseTop, minBase, maxBase - 8f);
                    baseBottom = Math.Clamp(baseBottom, baseTop + 8f, maxBase);
                    tipY = Math.Clamp(tipY, baseTop + 2f, baseBottom - 2f);
                    path.AddArc(body.X, body.Y, d, d, 180, 90);
                    path.AddArc(body.Right - d, body.Y, d, d, 270, 90);
                    path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90);
                    path.AddArc(body.X, body.Bottom - d, d, d, 90, 90);
                    path.AddLine(body.X, body.Bottom - r, body.X, baseBottom);
                    path.AddLine(body.X, baseBottom, body.X - th, tipY);
                    path.AddLine(body.X - th, tipY, body.X, baseTop);
                    path.AddLine(body.X, baseTop, body.X, body.Y + r);
                }
                break;
            case TailDirection.Right:
                // Tail on right edge (bubble to the left of anchor)
                {
                    float ty = _tailCenterY;
                    float baseTop = ty - tw * 0.55f;
                    float baseBottom = ty + tw * 0.40f;
                    float tipY = ty - tw * 0.06f;
                    float minBase = body.Y + r + 2f;
                    float maxBase = body.Bottom - r - 2f;
                    baseTop = Math.Clamp(baseTop, minBase, maxBase - 8f);
                    baseBottom = Math.Clamp(baseBottom, baseTop + 8f, maxBase);
                    tipY = Math.Clamp(tipY, baseTop + 2f, baseBottom - 2f);
                    path.AddArc(body.X, body.Y, d, d, 180, 90);
                    path.AddArc(body.Right - d, body.Y, d, d, 270, 90);
                    path.AddLine(body.Right - r, body.Y, body.Right, baseTop);
                    path.AddLine(body.Right, baseTop, body.Right + th, tipY);
                    path.AddLine(body.Right + th, tipY, body.Right, baseBottom);
                    path.AddLine(body.Right, baseBottom, body.Right, body.Bottom - r);
                    path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90);
                    path.AddArc(body.X, body.Bottom - d, d, d, 90, 90);
                }
                break;
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
        _contentLeft = (int)Math.Round(_bodyRect.X) + PadX;
        int curY = originY + PadY;

        // Header
        const int closeBtnSize = 16;
        _closeRect = new Rectangle(
            _contentLeft + _contentWidth - closeBtnSize - 10,
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
            new RectangleF(_contentLeft, curY + 5, 18, 18), accent, iconInset: 1f);

        var headerRect = new Rectangle(_contentLeft + 24, curY, _contentWidth - closeBtnSize - 36, HeaderHeight);
        TextRenderer.DrawText(g, _title, _headerFont, headerRect,
            UiChrome.SurfaceTextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

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

        // Tips
        curY = PaintSectionLabel(g, _menuTitle, curY) + 8;
        curY = PaintTips(g, curY) + SectionGap;

        // Shortcuts
        curY = PaintSectionLabel(g, _shortcutsTitle, curY) + 10;
        curY = PaintShortcutGrid(g, curY);

        // Footer — keep it above the rounded corner so descenders are not clipped by Region.
        int footerY = originY + (int)_bodyRect.Height - PadY - FooterHeight;
        using (var sep = new Pen(UiChrome.SurfaceBorderSubtle, 1f))
            g.DrawLine(sep, _contentLeft, footerY, _contentLeft + _contentWidth, footerY);

        var footerRect = new Rectangle(_contentLeft, footerY + 4, _contentWidth, FooterHeight - 2);
        TextRenderer.DrawText(g, _footerText, _footerFont, footerRect,
            UiChrome.SurfaceTextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private int PaintSectionLabel(Graphics g, string text, int y)
    {
        var rect = new Rectangle(_contentLeft, y, _contentWidth, SectionLabelHeight);
        TextRenderer.DrawText(g, text, _sectionFont, rect,
            Color.FromArgb(UiChrome.IsDark ? 170 : 140, UiChrome.SurfaceTextSecondary),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return y + SectionLabelHeight;
    }

    private int PaintSteps(Graphics g, int startY, Color accent)
    {
        int curY = startY;
        for (int i = 0; i < _steps.Length; i++)
        {
            int rowH = i < _stepHeights.Length ? _stepHeights[i] : StepCircle;

            // Align the badge with the first line, not the middle of a wrapped block.
            var circle = new RectangleF(_contentLeft, curY, StepCircle, StepCircle);
            using (var fill = new SolidBrush(Color.FromArgb(UiChrome.IsDark ? 40 : 30, accent)))
                g.FillEllipse(fill, circle);
            using (var ring = new Pen(Color.FromArgb(UiChrome.IsDark ? 140 : 110, accent), 1.2f))
                g.DrawEllipse(ring, circle);

            string num = (i + 1).ToString();
            var numState = g.Save();
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var numBrush = new SolidBrush(accent))
            using (var numFormat = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            })
            {
                // Optical nudge: GDI+ centers on cell metrics and reads high in a circle.
                var numRect = circle;
                numRect.Offset(0.4f, 1.1f);
                g.DrawString(num, _stepNumFont, numBrush, numRect, numFormat);
            }
            g.Restore(numState);

            int textX = _contentLeft + StepCircle + StepTextGap;
            int textW = _contentWidth - StepCircle - StepTextGap;
            var textRect = new Rectangle(textX, curY, textW, rowH);
            TextRenderer.DrawText(g, _steps[i].Text, _bodyFont, textRect,
                UiChrome.SurfaceTextPrimary,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

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

            float iconY = curY + Math.Max(0, (rowH - TipIconSize) / 2f);
            var iconRect = new RectangleF(_contentLeft, iconY, TipIconSize, TipIconSize);

            if (tip.IconId != null && FluentIcons.HasIcon(tip.IconId))
                FluentIcons.DrawIcon(g, tip.IconId, iconRect, iconColor, iconInset: 0f);
            else
            {
                using var dot = new SolidBrush(Color.FromArgb(180, iconColor));
                g.FillEllipse(dot, _contentLeft + IconColWidth / 2f - 3f, curY + rowH / 2f - 3f, 6f, 6f);
            }

            var tipTextRect = new RectangleF(_contentLeft + IconColWidth + IconTextGap, curY, tipTextWidth, rowH);
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
            int cellX = _contentLeft + col * (_shortcutColWidth + ShortcutColGap);
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

        int kbdW = Math.Min(_shortcutColWidth - 48, Math.Max(44, keyW + KbdPadH * 2 + 4));
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

        int labelX = x + kbdW + KbdLabelGap;
        int labelW = Math.Max(8, _shortcutColWidth - kbdW - KbdLabelGap);
        TextRenderer.DrawText(g, sc.Label, _bodyFont,
            new Rectangle(labelX, y + 4, labelW, kbdH),
            UiChrome.SurfaceTextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
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
