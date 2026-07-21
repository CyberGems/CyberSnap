using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

public sealed partial class RecordingForm
{
    /// <summary>
    /// Floating recording control bar modeled after the scrolling-capture bar:
    /// ready phase (Start + FPS + Cancel) and recording phase (Pause + Stop + Cancel).
    /// </summary>
    private sealed class RecordingControlBar : Form
    {
        public event Action? StartClicked;
        public event Action? StopClicked;
        public event Action? CancelClicked;
        public event Action? PauseClicked;
        public event Action<int>? FpsChanged;
        public event Action<bool>? SendToTrimmerChanged;

        private static readonly Color DoneAccent = Color.FromArgb(255, 0xBD, 0x70, 0x11);
        private static readonly Color DoneAccentHover = Color.FromArgb(255, 0xD4, 0x82, 0x18);
        private static readonly Color StartShineGlow = Color.FromArgb(168, 174, 184);
        private static readonly Color StartShineCore = Color.FromArgb(210, 215, 222);
        private const float StartShineThicknessScale = 0.55f;

        private static int BarWidth => UiChrome.ScaleInt(598);
        private static int BarHeight => UiChrome.ScaleInt(58);
        private static int PrimaryBtnHeight => UiChrome.ScaleInt(40);
        private static int SecondaryBtnSize => UiChrome.ScaleInt(38);
        private static int PrimaryBtnWidth => UiChrome.ScaleInt(88);
        /// <summary>Air between Start/Stop ↔ Trimmer and Trimmer ↔ Cancel.</summary>
        private static int TrimmerPrimaryGap => UiChrome.ScaleInt(18);
        private static int FpsComboWidth => UiChrome.ScaleInt(78);
        private static int FpsComboHeight => UiChrome.ScaleInt(30);
        private static int DotSize => UiChrome.ScaleInt(10);
        private static float CornerR => UiChrome.ScaledToolbarCornerRadius;

        private readonly Models.RecordingFormat _format;
        private readonly Color _accent;
        private readonly Color _accentHover;
        private readonly bool _supportsPause;

        private int _fps;
        private bool _sendToTrimmer;
        private bool _isRecording;
        private bool _isPaused;
        private bool _isEncoding;
        private TimeSpan _elapsed;
        private bool _fpsComboHovered;
        private ContextMenuStrip? _fpsMenu;

        private Rectangle _startBtnRect;
        private Rectangle _stopBtnRect;
        private Rectangle _pauseBtnRect;
        private Rectangle _cancelBtnRect;
        private Rectangle _trimmerToggleRect;
        private Rectangle _fpsComboRect;
        private Rectangle _phaseLabelRect;
        private Rectangle _statusRect;
        private Rectangle _recDotRect;
        private Rectangle? _hoveredRect;

        private readonly Font _statusFont = UiChrome.ChromeFont(10f, FontStyle.Bold);
        private readonly Font _hintFont = UiChrome.ChromeFont(8f, FontStyle.Regular);
        private readonly Font _phaseFont = UiChrome.ChromeFont(11f, FontStyle.Bold);
        private readonly Font _comboFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
        private readonly Font _startFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
        private WindowsToolTip? _chromeToolTip;
        private Rectangle? _tooltipAnchor;
        private readonly System.Windows.Forms.Timer _startShineTimer;
        private readonly System.Windows.Forms.Timer _pulseTimer;
        private float _startShinePhase;

        private static readonly StringFormat SingleLineFmt = new()
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        private static readonly StringFormat CenterFmt = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        public RecordingControlBar(Rectangle captureRegion, Models.RecordingFormat format, int fps, bool sendToTrimmer)
        {
            _format = format;
            _fps = NormalizeFps(format, fps);
            _sendToTrimmer = sendToTrimmer;
            _supportsPause = format != Models.RecordingFormat.GIF;
            _accent = format == Models.RecordingFormat.GIF
                ? Color.FromArgb(255, 140, 0)
                : UiChrome.AccentColor;
            _accentHover = Color.FromArgb(
                255,
                Math.Min(255, _accent.R + 28),
                Math.Min(255, _accent.G + 28),
                Math.Min(255, _accent.B + 28));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(BarWidth, BarHeight);
            BackColor = Color.FromArgb(1, 2, 3);
            TransparencyKey = BackColor;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Default;

            PositionAboveRegion(captureRegion);
            CalcLayout();
            ApplyRoundedChromeRegion();

            _startShineTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
            _startShineTimer.Tick += (_, _) => StartShineTick();

            _pulseTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
            _pulseTimer.Tick += (_, _) =>
            {
                if (_isRecording && !_isPaused && !_isEncoding)
                    Invalidate(_recDotRect);
            };
        }

        public int Fps => _fps;

        private void ApplyRoundedChromeRegion()
        {
            if (Width <= 0 || Height <= 0)
                return;

            Region?.Dispose();
            using var path = WindowsDockRenderer.RoundedRect(new RectangleF(0, 0, Width, Height), CornerR);
            Region = new Region(path);
        }

        private void PositionAboveRegion(Rectangle captureRegion)
        {
            var screen = Screen.FromRectangle(captureRegion);
            int tx = captureRegion.X + captureRegion.Width / 2 - BarWidth / 2;
            int ty = captureRegion.Y - BarHeight - UiChrome.ScaleInt(14);
            var edge = UiChrome.ScaleInt(4);

            if (ty < screen.Bounds.Top + edge)
                ty = captureRegion.Bottom + UiChrome.ScaleInt(14);

            // If still overlapping the capture region, park near the bottom of the screen.
            var barRect = new Rectangle(tx, ty, BarWidth, BarHeight);
            if (barRect.IntersectsWith(captureRegion))
            {
                ty = screen.WorkingArea.Bottom - BarHeight - UiChrome.ScaleInt(16);
                if (ty < screen.Bounds.Top + edge)
                    ty = screen.Bounds.Top + edge;
            }

            if (tx < screen.Bounds.Left + edge) tx = screen.Bounds.Left + edge;
            if (tx + BarWidth > screen.Bounds.Right - edge) tx = screen.Bounds.Right - edge - BarWidth;
            Location = new Point(tx, ty);
        }

        private void CalcLayout()
        {
            int btnPad = WindowsDockRenderer.SurfacePadding;
            int btnGap = WindowsDockRenderer.ButtonSpacing;
            int gap = UiChrome.ScaleInt(8);
            int leftPad = UiChrome.ScaleInt(14);

            float centerY = BarHeight / 2f;
            int dotY = (int)(centerY - DotSize / 2f);
            _recDotRect = new Rectangle(leftPad, dotY, DotSize, DotSize);

            string phaseReady = PhaseLabel(false, false);
            string phaseRec = PhaseLabel(true, false);
            string phasePaused = PhaseLabel(true, true);
            int phaseWidth = Math.Max(UiChrome.ScaleInt(96),
                Math.Max(
                    MeasurePhaseLabelWidth(phaseReady),
                    Math.Max(MeasurePhaseLabelWidth(phaseRec), MeasurePhaseLabelWidth(phasePaused))));
            int phaseX = _recDotRect.Right + UiChrome.ScaleInt(8);
            _phaseLabelRect = new Rectangle(phaseX, 0, phaseWidth, BarHeight);

            int comboY = (BarHeight - FpsComboHeight) / 2;
            _fpsComboRect = !_isRecording && !_isEncoding
                ? new Rectangle(_phaseLabelRect.Right + gap, comboY, FpsComboWidth, FpsComboHeight)
                : Rectangle.Empty;

            int secY = (BarHeight - SecondaryBtnSize) / 2;
            int priY = (BarHeight - PrimaryBtnHeight) / 2;
            _cancelBtnRect = new Rectangle(BarWidth - btnPad - SecondaryBtnSize, secY, SecondaryBtnSize, SecondaryBtnSize);

            if (_isEncoding)
            {
                _startBtnRect = Rectangle.Empty;
                _stopBtnRect = Rectangle.Empty;
                _pauseBtnRect = Rectangle.Empty;
                _trimmerToggleRect = Rectangle.Empty;
            }
            else if (!_isRecording)
            {
                // Ready: [Start] —gap— [Trimmer] —gap— [Cancel]
                _stopBtnRect = Rectangle.Empty;
                _pauseBtnRect = Rectangle.Empty;
                _trimmerToggleRect = new Rectangle(
                    _cancelBtnRect.X - TrimmerPrimaryGap - SecondaryBtnSize, secY, SecondaryBtnSize, SecondaryBtnSize);
                _startBtnRect = new Rectangle(
                    _trimmerToggleRect.X - TrimmerPrimaryGap - PrimaryBtnWidth, priY, PrimaryBtnWidth, PrimaryBtnHeight);
            }
            else
            {
                // Recording: [Pause?] [Stop] —gap— [Trimmer] —gap— [Cancel]
                _startBtnRect = Rectangle.Empty;
                _trimmerToggleRect = new Rectangle(
                    _cancelBtnRect.X - TrimmerPrimaryGap - SecondaryBtnSize, secY, SecondaryBtnSize, SecondaryBtnSize);
                _stopBtnRect = new Rectangle(
                    _trimmerToggleRect.X - TrimmerPrimaryGap - PrimaryBtnWidth, priY, PrimaryBtnWidth, PrimaryBtnHeight);
                _pauseBtnRect = _supportsPause
                    ? new Rectangle(_stopBtnRect.X - TrimmerPrimaryGap - SecondaryBtnSize, secY, SecondaryBtnSize, SecondaryBtnSize)
                    : Rectangle.Empty;
            }

            int firstBtnX = !_pauseBtnRect.IsEmpty ? _pauseBtnRect.X
                : !_startBtnRect.IsEmpty ? _startBtnRect.X
                : !_stopBtnRect.IsEmpty ? _stopBtnRect.X
                : !_trimmerToggleRect.IsEmpty ? _trimmerToggleRect.X
                : _cancelBtnRect.X;

            int statusX = !_fpsComboRect.IsEmpty
                ? _fpsComboRect.Right + gap
                : _phaseLabelRect.Right + gap;
            _statusRect = new Rectangle(statusX, 0, Math.Max(0, firstBtnX - gap - statusX), BarHeight);
        }

        private int MeasurePhaseLabelWidth(string text) =>
            TextRenderer.MeasureText(text, _phaseFont, new Size(int.MaxValue, BarHeight),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + UiChrome.ScaleInt(8);

        public void TransitionToRecording()
        {
            if (InvokeRequired) { BeginInvoke(TransitionToRecording); return; }
            _isRecording = true;
            _isPaused = false;
            _isEncoding = false;
            _elapsed = TimeSpan.Zero;
            _startShineTimer.Stop();
            if (!UI.Motion.Disabled)
                _pulseTimer.Start();
            CalcLayout();
            Invalidate();
        }

        public void TransitionToEncoding()
        {
            if (InvokeRequired) { BeginInvoke(TransitionToEncoding); return; }
            _isEncoding = true;
            _isPaused = false;
            _startShineTimer.Stop();
            _pulseTimer.Stop();
            CalcLayout();
            Invalidate();
        }

        public void SetElapsed(TimeSpan elapsed)
        {
            if (InvokeRequired) { BeginInvoke(() => SetElapsed(elapsed)); return; }
            _elapsed = elapsed;
            if (_isRecording && !_isEncoding)
                Invalidate(_statusRect);
        }

        public void SetPaused(bool paused)
        {
            if (InvokeRequired) { BeginInvoke(() => SetPaused(paused)); return; }
            if (_isPaused == paused) return;
            _isPaused = paused;
            if (_isPaused)
                _pulseTimer.Stop();
            else if (_isRecording && !_isEncoding && !UI.Motion.Disabled)
                _pulseTimer.Start();
            Invalidate();
        }

        public void Reposition(Rectangle captureRegion)
        {
            if (InvokeRequired) { BeginInvoke(() => Reposition(captureRegion)); return; }
            PositionAboveRegion(captureRegion);
            CalcLayout();
            Invalidate();
        }

        private void StartShineTick()
        {
            if (UI.Motion.Disabled || _isRecording || _isEncoding || _startBtnRect.IsEmpty)
            {
                _startShineTimer.Stop();
                return;
            }

            bool hovered = _hoveredRect == _startBtnRect;
            float delta = (float)(UiChrome.FrameIntervalMs / 2600.0) * (hovered ? 2f : 1f);
            _startShinePhase += delta;
            if (_startShinePhase >= 1f) _startShinePhase -= 1f;
            InvalidateStartShine();
        }

        private void InvalidateStartShine()
        {
            if (_startBtnRect.IsEmpty)
                return;

            int pad = UiChrome.ScaleInt(10);
            Invalidate(Rectangle.Inflate(_startBtnRect, pad, pad));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var barRect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var bgPath = WindowsDockRenderer.RoundedRect(barRect, CornerR);

            var glowRect = barRect;
            glowRect.Inflate(3f, 3f);
            using (var glowPath = WindowsDockRenderer.RoundedRect(glowRect, CornerR))
            using (var glowBrush = new SolidBrush(Color.FromArgb(25, _accent)))
                g.FillPath(glowBrush, glowPath);

            using (var micaBrush = new SolidBrush(Color.FromArgb(225, 12, 12, 16)))
                g.FillPath(micaBrush, bgPath);

            using (var bp = new Pen(Color.FromArgb(150, _accent), 1f))
                g.DrawPath(bp, bgPath);

            WindowsDockRenderer.PaintShadow(g, barRect, CornerR);

            float centerY = BarHeight / 2f;
            float dotX = _recDotRect.X;
            float dotY = centerY - DotSize / 2f;
            var dotRect = new RectangleF(dotX, dotY, DotSize, DotSize);

            if (_isEncoding)
            {
                using var glowDot = new SolidBrush(Color.FromArgb(40, _accent));
                g.FillEllipse(glowDot, dotRect.X - 4, dotRect.Y - 4, DotSize + 8, DotSize + 8);
                using var spinBrush = new SolidBrush(Color.FromArgb(200, _accent));
                g.FillEllipse(spinBrush, dotRect);
            }
            else if (!_isRecording)
            {
                using var glowDot = new SolidBrush(Color.FromArgb(30, _accent));
                g.FillEllipse(glowDot, dotRect.X - 4, dotRect.Y - 4, DotSize + 8, DotSize + 8);
                using var dotBrush = new SolidBrush(Color.FromArgb(180, _accent));
                g.FillEllipse(dotBrush, dotRect);
            }
            else
            {
                Color recColor = _isPaused ? UiChrome.SurfaceTextMuted : _accent;
                double pulse = _isPaused ? 0 : Math.Sin(Environment.TickCount / 250.0);
                float pa = (float)((pulse + 1.0) / 2.0);
                int glowAlpha = _isPaused ? 20 : (int)(30 + 40 * pa);
                int dotAlpha = _isPaused ? 120 : (int)(200 + 55 * pa);
                using var glowDot = new SolidBrush(Color.FromArgb(glowAlpha, recColor));
                g.FillEllipse(glowDot, dotRect.X - 4, dotRect.Y - 4, DotSize + 8, DotSize + 8);
                using var dotBrush = new SolidBrush(Color.FromArgb(dotAlpha, recColor));
                g.FillEllipse(dotBrush, dotRect);
            }

            using (var labelBrush = new SolidBrush(Color.FromArgb(220, _isPaused ? UiChrome.SurfaceTextMuted : _accent)))
            {
                var phaseRect = new RectangleF(_phaseLabelRect.X, centerY - UiChrome.ScaleFloat(8f),
                    _phaseLabelRect.Width, UiChrome.ScaleFloat(16f));
                g.DrawString(PhaseLabel(), _phaseFont, labelBrush, phaseRect, SingleLineFmt);
            }

            if (!_fpsComboRect.IsEmpty)
                DrawFpsCombo(g);

            var statusText = StatusDisplayText();
            if (!string.IsNullOrEmpty(statusText))
            {
                bool isHint = IsStatusHint();
                using var statusBrush = new SolidBrush(isHint ? UiChrome.SurfaceTextMuted : UiChrome.SurfaceTextPrimary);
                g.DrawString(statusText, isHint ? _hintFont : _statusFont, statusBrush, _statusRect, SingleLineFmt);
            }

            if (!_startBtnRect.IsEmpty)
            {
                DrawPrimaryTextBtn(g, _startBtnRect, LocalizationService.Translate("Start recording button"),
                    _hoveredRect == _startBtnRect, _accent, _accentHover,
                    withShine: true, shinePhase: _startShinePhase);
            }

            if (!_stopBtnRect.IsEmpty)
            {
                DrawPrimaryTextBtn(g, _stopBtnRect, LocalizationService.Translate("Stop recording button"),
                    _hoveredRect == _stopBtnRect, DoneAccent, DoneAccentHover);
            }

            if (!_pauseBtnRect.IsEmpty)
            {
                Color pauseColor = _hoveredRect == _pauseBtnRect ? _accent : UiChrome.SurfaceTextPrimary;
                DrawIconBtn(g, _pauseBtnRect, _isPaused ? "play" : "pause",
                    _hoveredRect == _pauseBtnRect, pauseColor);
            }

            if (!_trimmerToggleRect.IsEmpty)
            {
                bool hovered = _hoveredRect == _trimmerToggleRect;
                Color iconColor = _sendToTrimmer
                    ? (hovered ? _accentHover : _accent)
                    : (hovered ? _accent : UiChrome.SurfaceTextPrimary);
                DrawIconBtn(g, _trimmerToggleRect, "filmstrip", hovered || _sendToTrimmer, iconColor, filled: _sendToTrimmer);
            }

            if (!_isEncoding)
            {
                bool cancelHovered = _hoveredRect == _cancelBtnRect;
                var cancelColor = cancelHovered
                    ? Color.FromArgb(255, 255, 80, 80)
                    : UiChrome.SurfaceTextPrimary;
                DrawIconBtn(g, _cancelBtnRect, "close", cancelHovered, cancelColor);
            }
        }

        private void DrawFpsCombo(Graphics g)
        {
            var rect = new RectangleF(_fpsComboRect.X, _fpsComboRect.Y, _fpsComboRect.Width, _fpsComboRect.Height);
            bool hovered = _fpsComboHovered;
            WindowsDockRenderer.PaintButton(g, rect, false, hovered, CornerR - 1f, _accent);

            using (var border = new Pen(Color.FromArgb(hovered ? 90 : 70, _accent), 1f))
            using (var path = WindowsDockRenderer.RoundedRect(rect, CornerR - 1f))
                g.DrawPath(border, path);

            int alpha = hovered ? 255 : 220;
            using (var textBrush = new SolidBrush(Color.FromArgb(alpha, UiChrome.SurfaceTextPrimary)))
            {
                var textRect = new RectangleF(rect.X + UiChrome.ScaleFloat(8f), rect.Y,
                    rect.Width - UiChrome.ScaleFloat(22f), rect.Height);
                g.DrawString(FpsComboLabel(_fps), _comboFont, textBrush, textRect, SingleLineFmt);
            }

            float chevronX = rect.Right - UiChrome.ScaleFloat(14f);
            float chevronY = rect.Y + rect.Height / 2f;
            using var chevronPen = new Pen(Color.FromArgb(180, _accent), UiChrome.ScaleFloat(1.4f));
            g.DrawLine(chevronPen, chevronX - 3, chevronY - 2, chevronX, chevronY + 1);
            g.DrawLine(chevronPen, chevronX, chevronY + 1, chevronX + 3, chevronY - 2);
        }

        private void DrawPrimaryTextBtn(
            Graphics g, Rectangle rect, string text, bool hovered,
            Color normal, Color hoverFill, bool withShine = false, float shinePhase = 0f)
        {
            var rectF = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            using var path = WindowsDockRenderer.RoundedRect(rectF, CornerR);
            using (var brush = new SolidBrush(hovered ? hoverFill : normal))
                g.FillPath(brush, path);
            using (var border = new Pen(Color.FromArgb(hovered ? 220 : 160, Color.White), 1f))
                g.DrawPath(border, path);

            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(text, _startFont, textBrush, rectF, CenterFmt);

            if (withShine && !UI.Motion.Disabled)
            {
                var clipState = g.Save();
                g.ResetClip();
                WindowsDockRenderer.PaintBorderShine(
                    g, rectF, CornerR, shinePhase, StartShineGlow, StartShineCore, 1f, StartShineThicknessScale);
                g.Restore(clipState);
            }
        }

        private void DrawIconBtn(Graphics g, Rectangle r, string iconId, bool hovered, Color iconColor, bool filled = false)
        {
            bool paintFilled = filled || iconId == "stop";
            // Use the bar accent (cyan video / orange GIF) so active rings match the chrome theme.
            WindowsDockRenderer.PaintButton(g, r, paintFilled, hovered, accent: _accent);
            int alpha = paintFilled ? 255 : hovered ? 240 : 200;
            WindowsDockRenderer.PaintIcon(g, iconId, r, Color.FromArgb(alpha, iconColor.R, iconColor.G, iconColor.B), paintFilled);
        }

        private void ShowFpsMenu()
        {
            if (_isRecording || _isEncoding) return;

            _fpsMenu?.Close();

            var menu = WindowsMenuRenderer.Create(showImages: false, minWidth: FpsComboWidth + UiChrome.ScaleInt(24));
            _fpsMenu = menu;

            foreach (var option in GetFpsOptions(_format))
            {
                var item = WindowsMenuRenderer.Item($"{option} FPS", active: _fps == option);
                int captured = option;
                item.Click += (_, _) => QueueApplyFps(captured);
                menu.Items.Add(item);
            }

            menu.Show(this, new Point(_fpsComboRect.Left, _fpsComboRect.Bottom + UiChrome.ScaleInt(2)));
        }

        private void QueueApplyFps(int fps)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                _fpsMenu?.Close();
                ApplyFps(fps);
            }));
        }

        private void ApplyFps(int fps)
        {
            fps = NormalizeFps(_format, fps);
            if (_fps == fps) return;
            _fps = fps;
            FpsChanged?.Invoke(fps);
            Invalidate(_fpsComboRect);
            Invalidate(_statusRect);
        }

        /// <summary>
        /// GIF: 15 (default) / 30. Video: 15 / 24 / 30 / 60 (default 30).
        /// </summary>
        private static int[] GetFpsOptions(Models.RecordingFormat format) =>
            format == Models.RecordingFormat.GIF
                ? [15, 30]
                : [15, 24, 30, 60];

        private static int NormalizeFps(Models.RecordingFormat format, int fps)
        {
            var options = GetFpsOptions(format);
            if (Array.IndexOf(options, fps) >= 0)
                return fps;
            return format == Models.RecordingFormat.GIF ? 15 : 30;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var prev = _hoveredRect;
            bool prevCombo = _fpsComboHovered;

            _hoveredRect = HitTestInteractive(e.Location);
            _fpsComboHovered = !_fpsComboRect.IsEmpty && _fpsComboRect.Contains(e.Location);

            Cursor = _hoveredRect != null || _fpsComboHovered ? Cursors.Hand : Cursors.Default;

            if (_hoveredRect != prev)
            {
                if (prev.HasValue) Invalidate(prev.Value);
                if (_hoveredRect.HasValue) Invalidate(_hoveredRect.Value);
            }
            if (_fpsComboHovered != prevCombo && !_fpsComboRect.IsEmpty)
                Invalidate(_fpsComboRect);

            UpdateToolTip(e.Location);
        }

        private Rectangle? HitTestInteractive(Point p)
        {
            if (!_startBtnRect.IsEmpty && _startBtnRect.Contains(p)) return _startBtnRect;
            if (!_stopBtnRect.IsEmpty && _stopBtnRect.Contains(p)) return _stopBtnRect;
            if (!_pauseBtnRect.IsEmpty && _pauseBtnRect.Contains(p)) return _pauseBtnRect;
            if (!_trimmerToggleRect.IsEmpty && _trimmerToggleRect.Contains(p)) return _trimmerToggleRect;
            if (!_isEncoding && _cancelBtnRect.Contains(p)) return _cancelBtnRect;
            return null;
        }

        private void UpdateToolTip(Point location)
        {
            string? tip = null;
            Rectangle? anchor = null;

            if (!_fpsComboRect.IsEmpty && _fpsComboRect.Contains(location))
            {
                tip = string.Format(
                    LocalizationService.Translate("Recording fps tooltip"),
                    _fps);
                anchor = _fpsComboRect;
            }
            else if (!_startBtnRect.IsEmpty && _startBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate("Recording start tooltip");
                anchor = _startBtnRect;
            }
            else if (!_stopBtnRect.IsEmpty && _stopBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate("Recording stop tooltip");
                anchor = _stopBtnRect;
            }
            else if (!_pauseBtnRect.IsEmpty && _pauseBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate(
                    _isPaused ? "Recording resume tooltip" : "Recording pause tooltip");
                anchor = _pauseBtnRect;
            }
            else if (!_trimmerToggleRect.IsEmpty && _trimmerToggleRect.Contains(location))
            {
                tip = LocalizationService.Translate(_sendToTrimmer
                    ? "Send to Trimmer is on"
                    : "Send to Trimmer is off");
                tip += "\n" + LocalizationService.Translate(
                    _format == Models.RecordingFormat.GIF
                        ? "Open this GIF in the Trimmer when recording finishes"
                        : "Open this video in the Trimmer when recording finishes");
                anchor = _trimmerToggleRect;
            }
            else if (!_isEncoding && _cancelBtnRect.Contains(location))
            {
                tip = LocalizationService.Translate(
                    _isRecording ? "Recording discard tooltip" : "Recording cancel tooltip");
                anchor = _cancelBtnRect;
            }

            if (tip == null || anchor == null)
            {
                HideChromeToolTip();
                return;
            }

            if (_tooltipAnchor == anchor && _chromeToolTip?.Visible == true)
                return;

            _tooltipAnchor = anchor;
            _chromeToolTip ??= new WindowsToolTip();
            var screenAnchor = GetScreenBounds(anchor.Value);
            _chromeToolTip.ShowNear(this, tip, screenAnchor, above: true);
        }

        private Rectangle GetScreenBounds(Rectangle clientRect)
        {
            var origin = PointToScreen(Point.Empty);
            return new Rectangle(origin.X + clientRect.X, origin.Y + clientRect.Y, clientRect.Width, clientRect.Height);
        }

        private void HideChromeToolTip()
        {
            _tooltipAnchor = null;
            _chromeToolTip?.Hide();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoveredRect != null)
            {
                var p = _hoveredRect.Value;
                _hoveredRect = null;
                Invalidate(p);
            }
            if (_fpsComboHovered)
            {
                _fpsComboHovered = false;
                if (!_fpsComboRect.IsEmpty)
                    Invalidate(_fpsComboRect);
            }
            HideChromeToolTip();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!_fpsComboRect.IsEmpty && _fpsComboRect.Contains(e.Location))
            {
                ShowFpsMenu();
                return;
            }
            if (!_startBtnRect.IsEmpty && _startBtnRect.Contains(e.Location))
                StartClicked?.Invoke();
            else if (!_stopBtnRect.IsEmpty && _stopBtnRect.Contains(e.Location))
                StopClicked?.Invoke();
            else if (!_pauseBtnRect.IsEmpty && _pauseBtnRect.Contains(e.Location))
                PauseClicked?.Invoke();
            else if (!_trimmerToggleRect.IsEmpty && _trimmerToggleRect.Contains(e.Location))
            {
                _sendToTrimmer = !_sendToTrimmer;
                SendToTrimmerChanged?.Invoke(_sendToTrimmer);
                // Inflate so the active ring's AA fringe is fully redrawn.
                Invalidate(Rectangle.Inflate(_trimmerToggleRect, 3, 3));
                UpdateToolTip(e.Location);
            }
            else if (!_isEncoding && _cancelBtnRect.Contains(e.Location))
                CancelClicked?.Invoke();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape)
            {
                if (!_isEncoding)
                    CancelClicked?.Invoke();
                return true;
            }
            if (!_isRecording && !_isEncoding && key == Keys.Enter)
            {
                StartClicked?.Invoke();
                return true;
            }
            if (_isRecording && !_isEncoding && key == Keys.Enter)
            {
                StopClicked?.Invoke();
                return true;
            }
            if (_isRecording && !_isEncoding && _supportsPause && key == Keys.Space)
            {
                PauseClicked?.Invoke();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CaptureWindowExclusion.Apply(this);
            CaptureWindowExclusion.SetLogicalBounds(Handle, static () => Rectangle.Empty);
            ApplyRoundedChromeRegion();
            try
            {
                Dwm.TrySetWindowCornerPreference(Handle, Dwm.DWMWCP_ROUND);
                Dwm.TrySetImmersiveDarkMode(Handle, UiChrome.IsDark);
            }
            catch { /* optional DWM polish */ }

            if (!UI.Motion.Disabled && !_isRecording && !_isEncoding)
                _startShineTimer.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!IsHandleCreated)
                return;

            if (Visible && !UI.Motion.Disabled && !_isRecording && !_isEncoding)
                _startShineTimer.Start();
            else
                _startShineTimer.Stop();

            if (Visible && _isRecording && !_isPaused && !_isEncoding && !UI.Motion.Disabled)
                _pulseTimer.Start();
            else if (!Visible)
                _pulseTimer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _startShineTimer.Stop();
                _startShineTimer.Dispose();
                _pulseTimer.Stop();
                _pulseTimer.Dispose();
                _fpsMenu?.Dispose();
                _fpsMenu = null;
                _chromeToolTip?.Dispose();
                _chromeToolTip = null;
                _statusFont.Dispose();
                _hintFont.Dispose();
                _phaseFont.Dispose();
                _comboFont.Dispose();
                _startFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private string StatusDisplayText()
        {
            if (_isEncoding)
            {
                return _format == Models.RecordingFormat.GIF
                    ? LocalizationService.Translate("Encoding GIF...")
                    : LocalizationService.Translate("Saving...");
            }

            if (_isRecording)
                return $"{(int)_elapsed.TotalMinutes:D2}:{_elapsed.Seconds:D2}";

            return string.Format(
                LocalizationService.Translate("Recording ready hint"),
                FormatLabel(),
                _fps);
        }

        private bool IsStatusHint() => !_isRecording && !_isEncoding;

        private string PhaseLabel() => PhaseLabel(_isRecording, _isPaused);

        private static string PhaseLabel(bool recording, bool paused)
        {
            if (!recording)
                return LocalizationService.Translate("Recording ready");
            if (paused)
                return LocalizationService.Translate("Recording paused");
            return LocalizationService.Translate("Recording active");
        }

        private string FormatLabel() => _format switch
        {
            Models.RecordingFormat.MP4 => "MP4",
            _ => "GIF"
        };

        private static string FpsComboLabel(int fps) => $"{fps} FPS";
    }
}
