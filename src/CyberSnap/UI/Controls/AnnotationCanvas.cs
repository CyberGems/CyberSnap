using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Models.Commands;
using CyberSnap.Services;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Interactive WinForms canvas for the post-capture editor. Hosts a base bitmap,
/// renders vector annotations on top, exposes zoom/pan and a command-based undo
/// stack. Used by EditorForm.
/// </summary>
public sealed partial class AnnotationCanvas : UserControl, IEditorContext
{
    public enum CanvasTool
    {
        Pan,
        Move,
        Draw,
        Arrow,
        CurvedArrow,
        Line,
        Rect,
        Circle,
        Text,
        Crop,
        Eraser,
        Highlight,
        Blur,
        StepNumber,
        Magnifier,
        Emoji,
        CutOut,
    }

    private int _undoStackLimit = 100;
    private const double MinZoom = 0.2;
    private const double MaxZoom = 8.0;

    // Above this source-pixel count, zoom gestures draw a fast (slightly soft) draft and
    // refine to crisp on settle. Below it, a full-quality rescale per frame is cheap enough
    // that the draft would only add a visible blur + snap-back, so we skip it. ~4 MP keeps
    // typical screenshots (1080p/1200p/1440p) crisp while large images stay fluid.
    private const long DraftZoomPixelThreshold = 4_000_000;
    public const int MinZoomPercent = 20;
    public const int MaxZoomPercent = 800;

    private Bitmap _baseBitmap;
    private readonly List<Annotation> _annotations = new();
    private readonly List<IEditCommand> _undoStack = new();
    private readonly List<IEditCommand> _redoStack = new();
    private readonly List<CommandViewSnapshot> _undoViews = new();
    private readonly List<CommandViewSnapshot> _redoViews = new();
    private System.Windows.Forms.Timer? _historyRevealTimer;
    private Action? _pendingHistoryCommit;
    private IEditCommand? _pendingHistoryCommand;
    private const int HistoryRevealDelayMs = 120;

    private double _zoom = 1.0;
    private PointF _pan; // pixel offset of image-space origin relative to control client
    private bool _zoomInteracting;       // user is mid zoom gesture: draw fast, refine on settle
    private System.Windows.Forms.Timer? _zoomSettleTimer;
    private System.Windows.Forms.Timer? _deferredZoomStateTimer;
    private bool _viewFitsWindow = true; // image auto-fits the canvas until the user zooms
    private bool _userPanned;            // user has manually dragged the image
    private bool _welcomeDismissed;      // welcome overlay hidden after first meaningful interaction
    private bool _welcomeDragOver;       // file drag currently over the editor while welcome is shown
    private RectangleF _welcomeCardRect;
    private RectangleF _welcomeIconRect;
    private readonly RectangleF[] _welcomeChipRects = new RectangleF[4];
    private int _welcomeHoverChip = -1;  // -1 none, 0 New, 1 Open, 2 Paste, 3 Capture
    private bool _welcomeHoverCard;
    private bool _welcomeHoverIcon;
    private int _welcomePressedChip = -1;
    private bool _welcomePressedIcon;
    private bool _isPanning;
    private Point _panStart;
    private PointF _panStartOffset;
    private CanvasTool? _preSpaceTool;
    private DateTime _spaceKeyDownUtc;
    private bool _isTempMoveFromPan;

    // Selection state (Move tool)
    private int _selectedAnnotationIndex = -1;
    private int _moveHoverIndex = -1;

    // Multi-selection state
    private readonly HashSet<int> _multiSelectedIndices = new();
    private List<(int Index, Annotation Original)>? _multiDragOriginals;
    private Point _multiDragStartImg;

    // After a click-to-place annotation (step/emoji/magnifier) the cursor sits on top of the
    // fresh item; suppress its hover/control box until the cursor leaves it once, so the box
    // only appears on a deliberate re-hover. -1 = nothing suppressed.
    private int _suppressHoverIndex = -1;
    private Annotation? _selectOriginalAnnotation;
    private Point _selectDragStartImg;
    private bool _isSelectResizing;
    private int _selectResizeHandle = -1;
    private Rectangle _selectHandleBounds;
    private Annotation? _selectResizeOriginalAnnotation;
    private bool _isRotateMode;
    private bool _pendingRotateToggle;
    private bool _isRotating;
    private PointF _rotatePivot;
    private float _rotateStartDegrees;
    private Annotation? _rotateOriginal;
    private System.Windows.Forms.Timer? _rotateToggleTimer;

    // Guide Lines hover and active drag state
    private int _hoveredHorizontalGuideIndex = -1;
    private int _hoveredVerticalGuideIndex = -1;
    private int _activeDraggedHorizontalGuideIndex = -1;
    private int _activeDraggedVerticalGuideIndex = -1;

    // Eraser hover highlight
    private int _eraserHoverIndex = -1;

    // Blur / Step / Magnifier / Emoji tool state
    private readonly CyberSnap.Capture.EmojiRenderer _emojiRenderer = new();
    private Bitmap? _blurScratch;
    private string? _selectedEmoji;
    private float _emojiPlaceSize = 32f;
    // While OnPaint is inside the zoom/pan transform, PaintEmoji draws in screen space
    // at 1:1 so the glyph is not nearest-neighbor scaled. RenderFinal leaves this at 1.
    private float _annotationViewScale = 1f;

    /// <summary>Emoji glyph placed by the Emoji tool on the next click. Set by the editor's picker.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedEmoji
    {
        get => _selectedEmoji;
        set
        {
            _selectedEmoji = value;
            if (!string.IsNullOrEmpty(value))
                LastUsedEmoji.Remember(value);
        }
    }

    /// <summary>Pixel size for emoji placed by the Emoji tool (clamped 16–128).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float EmojiPlaceSize
    {
        get => _emojiPlaceSize;
        set => _emojiPlaceSize = Math.Clamp(value, CyberSnap.Capture.EmojiRenderer.PlaceSizeMin, CyberSnap.Capture.EmojiRenderer.PlaceSizeMax);
    }

    public AnnotationCanvas(Bitmap baseBitmap)
    {
        _baseBitmap = baseBitmap ?? throw new ArgumentNullException(nameof(baseBitmap));

        // Initialize crop rect to full image size by default
        _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
        _cropHasRect = true;

        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 30, 30);
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.StandardClick |
                 ControlStyles.StandardDoubleClick |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        TabStop = true;
    }

    // ── Public surface ─────────────────────────────────────────────────────

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Bitmap BaseBitmap
    {
        get => _baseBitmap;
        set
        {
            if (ReferenceEquals(_baseBitmap, value)) return;
            var old = _baseBitmap;
            _baseBitmap = value ?? throw new ArgumentNullException(nameof(value));
            old?.Dispose();
            InvalidateScaledCache();

            // Reset crop handles to the new bitmap size if auto crop controls is enabled
            if (EditorAutoCropControls)
            {
                _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
                _cropHasRect = true;
            }
            else
            {
                ClearCropPending();
            }
            ClearCutOutPending();

            Invalidate();
            OnStateChanged();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PointF Pan => _pan;

    private readonly List<float> _horizontalGuides = new();
    private readonly List<float> _verticalGuides = new();

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<float> HorizontalGuides => _horizontalGuides;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<float> VerticalGuides => _verticalGuides;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float? DraggingTempHorizontalGuide { get; set; }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float? DraggingTempVerticalGuide { get; set; }

    public void AddHorizontalGuide(float y)
    {
        if (!_horizontalGuides.Any(g => Math.Abs(g - y) < 2))
        {
            _horizontalGuides.Add(y);
            Invalidate();
        }
    }

    public void AddVerticalGuide(float x)
    {
        if (!_verticalGuides.Any(g => Math.Abs(g - x) < 2))
        {
            _verticalGuides.Add(x);
            Invalidate();
        }
    }

    public void RemoveHorizontalGuideAt(int index)
    {
        if (index >= 0 && index < _horizontalGuides.Count)
        {
            _horizontalGuides.RemoveAt(index);
            Invalidate();
        }
    }

    public void RemoveVerticalGuideAt(int index)
    {
        if (index >= 0 && index < _verticalGuides.Count)
        {
            _verticalGuides.RemoveAt(index);
            Invalidate();
        }
    }

    public void ClearAllGuides()
    {
        _horizontalGuides.Clear();
        _verticalGuides.Clear();
        Invalidate();
    }

    public int HitTestHorizontalGuide(Point clientPt)
    {
        const float tolerance = 5f; // Screen pixels tolerance
        for (int i = 0; i < _horizontalGuides.Count; i++)
        {
            float y = (float)(_horizontalGuides[i] * _zoom + _pan.Y);
            if (Math.Abs(clientPt.Y - y) <= tolerance)
            {
                return i;
            }
        }
        return -1;
    }

    public int HitTestVerticalGuide(Point clientPt)
    {
        const float tolerance = 5f;
        for (int i = 0; i < _verticalGuides.Count; i++)
        {
            float x = (float)(_verticalGuides[i] * _zoom + _pan.X);
            if (Math.Abs(clientPt.X - x) <= tolerance)
            {
                return i;
            }
        }
        return -1;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<Annotation> Annotations => _annotations;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ToolColor { get; set; } = Color.FromArgb(0, 255, 255);

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowCaptureFrame { get; set; } = false;

    private bool _showBanners = true;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowBanners
    {
        get => _showBanners;
        set
        {
            if (_showBanners == value) return;
            _showBanners = value;
            if (!_showBanners)
            {
                _bannerOpacity = 0f;
                _bannerSlide = 1f;
                _bannerText = "";
                _bannerTimer?.Stop();
                InvalidateBannerRegion();
                _bannerDirtyUnion = Rectangle.Empty;
            }
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowWelcomeBanner { get; set; } = true;

    /// <summary>True while the blank-canvas welcome overlay is currently painted.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsWelcomeVisible => IsDefaultBlank && !_welcomeDismissed && ShowWelcomeBanner;

    /// <summary>Highlight the welcome card while a file is dragged over the editor.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool WelcomeDragOver
    {
        get => _welcomeDragOver;
        set
        {
            if (_welcomeDragOver == value) return;
            _welcomeDragOver = value;
            if (IsWelcomeVisible) Invalidate();
        }
    }

    /// <summary>Welcome chip: create a new custom canvas dialog.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? WelcomeNewCanvasRequested { get; set; }

    /// <summary>Welcome chip: open a file/project dialog.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? WelcomeOpenRequested { get; set; }

    /// <summary>Welcome chip: paste image from clipboard.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? WelcomePasteRequested { get; set; }

    /// <summary>Welcome chip: start a new region capture.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? WelcomeCaptureRequested { get; set; }

    private const float BannerFadeInSeconds = 0.10f;
    private const float BannerFadeOutSeconds = 0.10f;
    private const float BannerHoverFadeOutSeconds = 0.05f;
    private const float BannerHoldSeconds = 0.80f;
    private const float BannerSlidePixels = 10f;

    private float _bannerOpacity;
    private float _bannerSlide;
    private string _bannerText = "";
    private Rectangle _bannerPaintRect;
    private Rectangle _bannerDirtyUnion;
    private System.Windows.Forms.Timer? _bannerTimer;
    private readonly Stopwatch _bannerClock = new();
    private long _bannerStateStartedMs;
    private float _bannerAnimFromOpacity;
    private float _bannerAnimFromSlide;
    private float _bannerAnimDuration = BannerFadeInSeconds;
    private enum BannerState { FadeIn, Hold, FadeOut }
    private BannerState _bannerState = BannerState.FadeIn;
    private bool _bannerIsSticky;

    private float _resizeHandlesOpacity = 0f;
    private System.Windows.Forms.Timer? _resizeHandlesTimer;

    private void ResizeHandlesTimer_Tick(object? sender, EventArgs e)
    {
        if (_baseBitmap == null) return;
        var imgRect = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        bool isHoveringExterior = _cursorOnCanvas && !imgRect.Contains(_cursorClient);
        bool targetVisible = _resizeDragging || isHoveringExterior;

        float targetOpacity = targetVisible ? 1.0f : 0.0f;
        if (Math.Abs(_resizeHandlesOpacity - targetOpacity) < 0.01f)
        {
            _resizeHandlesOpacity = targetOpacity;
            _resizeHandlesTimer?.Stop();
        }
        else
        {
            if (_resizeHandlesOpacity < targetOpacity)
                _resizeHandlesOpacity = Math.Min(targetOpacity, _resizeHandlesOpacity + 0.15f);
            else
                _resizeHandlesOpacity = Math.Max(targetOpacity, _resizeHandlesOpacity - 0.15f);
        }
        Invalidate();
    }

    private void UpdateResizeHandlesHover()
    {
        if (!EditorShowResizeHandles || _baseBitmap == null) return;
        if (HideCanvasResizeHandles) return;

        if (_resizeHandlesTimer == null)
        {
            _resizeHandlesTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _resizeHandlesTimer.Tick += ResizeHandlesTimer_Tick;
        }
        if (!_resizeHandlesTimer.Enabled)
        {
            _resizeHandlesTimer.Start();
        }
    }


    public void ShowToolBanner(string text, bool sticky = false)
    {
        if (!_showBanners) return;
        if (string.IsNullOrEmpty(text)) return;

        if (_bannerText == text && _bannerOpacity > 0.05f)
        {
            _bannerIsSticky = sticky;
            if (_bannerState == BannerState.FadeOut)
                StartBannerFadeIn(fromCurrent: true);
            else if (_bannerState == BannerState.Hold)
                _bannerStateStartedMs = BannerNowMs();
            return;
        }

        _bannerText = text;
        _bannerIsSticky = sticky;
        RecalcBannerLayout();
        StartBannerFadeIn(fromCurrent: _bannerOpacity > 0.05f);
    }

    public void HideToolBanner()
    {
        if (_bannerOpacity <= 0f && _bannerState != BannerState.FadeIn)
            return;
        _bannerIsSticky = false;
        StartBannerFadeOut(BannerFadeOutSeconds);
    }

    private void DismissToolBannerIfHovered(Point clientPoint)
    {
        if (_bannerOpacity <= 0.02f || _bannerState == BannerState.FadeOut)
            return;
        var hit = _bannerPaintRect;
        hit.Inflate(4, 4);
        if (!hit.Contains(clientPoint))
            return;
        _bannerIsSticky = false;
        StartBannerFadeOut(BannerHoverFadeOutSeconds);
    }

    private void StartBannerFadeIn(bool fromCurrent)
    {
        _bannerState = BannerState.FadeIn;
        _bannerAnimFromOpacity = fromCurrent ? _bannerOpacity : 0f;
        _bannerAnimFromSlide = fromCurrent ? _bannerSlide : 1f;
        if (!fromCurrent)
        {
            _bannerOpacity = 0f;
            _bannerSlide = 1f;
        }
        _bannerAnimDuration = BannerFadeInSeconds;
        _bannerStateStartedMs = BannerNowMs();
        EnsureBannerTimer().Start();
        InvalidateBannerRegion();
    }

    private void StartBannerFadeOut(float duration)
    {
        if (_bannerState == BannerState.FadeOut && duration >= _bannerAnimDuration && _bannerOpacity < 0.95f)
            return;

        _bannerState = BannerState.FadeOut;
        _bannerAnimFromOpacity = Math.Max(_bannerOpacity, 0.001f);
        _bannerAnimFromSlide = _bannerSlide;
        _bannerAnimDuration = Math.Max(0.02f, duration);
        _bannerStateStartedMs = BannerNowMs();
        EnsureBannerTimer().Start();
        InvalidateBannerRegion();
    }

    private long BannerNowMs()
    {
        if (!_bannerClock.IsRunning)
            _bannerClock.Start();
        return _bannerClock.ElapsedMilliseconds;
    }

    private System.Windows.Forms.Timer EnsureBannerTimer()
    {
        if (_bannerTimer == null)
        {
            _bannerTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, UiChrome.FrameIntervalMs) };
            _bannerTimer.Tick += BannerTimer_Tick;
        }
        return _bannerTimer;
    }

    private void BannerTimer_Tick(object? sender, EventArgs e)
    {
        float elapsed = (BannerNowMs() - _bannerStateStartedMs) / 1000f;
        switch (_bannerState)
        {
            case BannerState.FadeIn:
            {
                float t = Math.Clamp(elapsed / _bannerAnimDuration, 0f, 1f);
                float ease = UiChrome.EaseOutCubic(t);
                _bannerOpacity = _bannerAnimFromOpacity + (1f - _bannerAnimFromOpacity) * ease;
                _bannerSlide = _bannerAnimFromSlide * (1f - ease);
                if (t >= 1f)
                {
                    _bannerOpacity = 1f;
                    _bannerSlide = 0f;
                    _bannerState = BannerState.Hold;
                    _bannerStateStartedMs = BannerNowMs();
                }
                RecalcBannerLayout();
                InvalidateBannerRegion();
                break;
            }
            case BannerState.Hold:
                if (_bannerIsSticky)
                {
                    _bannerTimer?.Stop();
                    break;
                }
                if (elapsed >= BannerHoldSeconds)
                    StartBannerFadeOut(BannerFadeOutSeconds);
                break;
            case BannerState.FadeOut:
            {
                float t = Math.Clamp(elapsed / _bannerAnimDuration, 0f, 1f);
                float ease = UiChrome.EaseInCubic(t);
                _bannerOpacity = _bannerAnimFromOpacity * (1f - ease);
                _bannerSlide = _bannerAnimFromSlide + (1f - _bannerAnimFromSlide) * ease;
                if (t >= 1f)
                {
                    _bannerOpacity = 0f;
                    _bannerSlide = 1f;
                    _bannerTimer?.Stop();
                }
                RecalcBannerLayout();
                InvalidateBannerRegion();
                if (_bannerOpacity <= 0f)
                    _bannerDirtyUnion = Rectangle.Empty;
                break;
            }
        }
    }

    private void RecalcBannerLayout()
    {
        if (string.IsNullOrEmpty(_bannerText))
        {
            _bannerPaintRect = Rectangle.Empty;
            return;
        }

        using var font = UiChrome.ChromeFont(11f, FontStyle.Bold);
        var size = TextRenderer.MeasureText(
            _bannerText,
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int paddingH = 16;
        int paddingV = 10;
        int y = 18 + (int)Math.Round(-BannerSlidePixels * _bannerSlide);
        _bannerPaintRect = new Rectangle(18, y, size.Width + paddingH * 2, size.Height + paddingV * 2);
    }

    private void InvalidateBannerRegion()
    {
        var r = _bannerPaintRect;
        if (r.IsEmpty && _bannerDirtyUnion.IsEmpty)
            return;
        r.Inflate(10, 12);
        var dirty = _bannerDirtyUnion.IsEmpty ? r : Rectangle.Union(_bannerDirtyUnion, r);
        dirty.Intersect(ClientRectangle);
        _bannerDirtyUnion = r;
        if (dirty.Width > 0 && dirty.Height > 0)
            Invalidate(dirty);
    }

    private string GetToolName(CanvasTool tool)
    {
        var key = tool switch
        {
            CanvasTool.Pan => "Pan",
            CanvasTool.Move => "Pick",
            CanvasTool.Crop => "Crop",
            CanvasTool.CutOut => "Cut Out",
            CanvasTool.Text => "Text",
            CanvasTool.Draw => "Draw",
            CanvasTool.Arrow => "Arrow",
            CanvasTool.CurvedArrow => "Curved arrow",
            CanvasTool.Line => "Line",
            CanvasTool.Rect => "Rectangle",
            CanvasTool.Circle => "Circle",
            CanvasTool.Eraser => "Eraser",
            CanvasTool.Highlight => "Highlight",
            CanvasTool.Blur => "Blur",
            CanvasTool.StepNumber => "Step Number",
            CanvasTool.Magnifier => "Magnifier",
            CanvasTool.Emoji => "Emoji",
            _ => tool.ToString()
        };
        return LocalizationService.Translate(key);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CanvasTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value) return;
            // Leaving the Text tool by picking another one should keep what was typed,
            // not throw it away. Commit first so CancelInProgressTool's discard no-ops.
            CommitOrCancelInlineText(commit: true);
            CancelInProgressTool();

            // Leaving Crop with a rectangle the user actually resized: apply it on the way out
            // (the banner says so, and Undo reverses it) instead of silently abandoning the
            // handles on the canvas. A pending rect that still covers the whole image is a no-op,
            // so it's just discarded. The active tool is switched first so TryConfirmCrop won't
            // re-arm a fresh full-image crop whose handles would then linger under the new tool.
            bool leavingCrop = _activeTool == CanvasTool.Crop && value != CanvasTool.Crop && _preSpaceTool == null;
            bool leavingCutOut = _activeTool == CanvasTool.CutOut && value != CanvasTool.CutOut && _preSpaceTool == null;
            _activeTool = value;
            bool cropApplied = false;
            bool cutOutApplied = false;
            if (leavingCrop)
                cropApplied = FinalizeLeavingCrop();
            if (leavingCutOut)
                cutOutApplied = FinalizeLeavingCutOut();

            if (value == CanvasTool.Crop || value == CanvasTool.CutOut)
                CancelPendingResize(silent: true);

            if (value == CanvasTool.Crop)
            {
                if (!_cropHasRect || _cropRect.IsEmpty)
                {
                    _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
                    _cropHasRect = true;
                }
            }

            if (value == CanvasTool.Emoji)
                SelectedEmoji = LastUsedEmoji.Get();

            UpdateCursor();
            UpdateResizeHandlesHover();
            ShowToolBanner(cropApplied
                ? LocalizationService.Translate("Crop applied")
                : cutOutApplied
                    ? LocalizationService.Translate("Cut Out applied")
                    : GetToolName(value));
            if (HasPendingResize)
                ShowToolBanner(LocalizationService.Translate("Enter / Double-click to confirm"), sticky: true);
            if (IsDefaultBlank)
                DismissWelcomeOverlay();
            Invalidate();
            OnStateChanged();
        }
    }

    /// <summary>Hides the blank-canvas welcome overlay until the next pristine blank document.</summary>
    public void DismissWelcomeOverlay()
    {
        if (!_welcomeDismissed)
        {
            _welcomeDismissed = true;
            _welcomeHoverChip = -1;
            _welcomeHoverCard = false;
            _welcomeHoverIcon = false;
            _welcomePressedChip = -1;
            _welcomePressedIcon = false;
            _welcomeDragOver = false;
            Invalidate();
        }
    }

    private bool TryWelcomeMouseMove(Point client)
    {
        if (!IsWelcomeVisible) return false;

        bool overCard = _welcomeCardRect.Contains(client);
        bool overIcon = _welcomeIconRect.Contains(client);
        int chip = HitTestWelcomeChip(client);

        if (overCard != _welcomeHoverCard || overIcon != _welcomeHoverIcon || chip != _welcomeHoverChip)
        {
            _welcomeHoverCard = overCard;
            _welcomeHoverIcon = overIcon;
            _welcomeHoverChip = chip;
            Cursor = (overIcon || (chip >= 0 && IsWelcomeChipEnabled(chip))) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        else if (overIcon || (chip >= 0 && IsWelcomeChipEnabled(chip)))
        {
            Cursor = Cursors.Hand;
        }

        return overCard || overIcon || chip >= 0;
    }

    private bool TryWelcomeMouseDown(Point client)
    {
        if (!IsWelcomeVisible) return false;
        if (_welcomeIconRect.Contains(client))
        {
            _welcomePressedIcon = true;
            Invalidate();
            return true;
        }
        int chip = HitTestWelcomeChip(client);
        if (chip < 0 || !IsWelcomeChipEnabled(chip)) return false;
        _welcomePressedChip = chip;
        Invalidate();
        return true;
    }

    private bool TryWelcomeMouseUp(Point client)
    {
        if (!IsWelcomeVisible) return false;
        if (_welcomePressedIcon)
        {
            _welcomePressedIcon = false;
            Invalidate();
            if (_welcomeIconRect.Contains(client))
            {
                WelcomeOpenRequested?.Invoke();
                return true;
            }
        }
        int pressed = _welcomePressedChip;
        _welcomePressedChip = -1;
        if (pressed < 0) return false;

        int chip = HitTestWelcomeChip(client);
        Invalidate();
        if (chip != pressed || !IsWelcomeChipEnabled(chip)) return true;

        switch (chip)
        {
            case 0: WelcomeNewCanvasRequested?.Invoke(); break;
            case 1: WelcomeOpenRequested?.Invoke(); break;
            case 2: WelcomePasteRequested?.Invoke(); break;
            case 3: WelcomeCaptureRequested?.Invoke(); break;
        }
        return true;
    }

    private int HitTestWelcomeChip(Point client)
    {
        for (int i = 0; i < _welcomeChipRects.Length; i++)
        {
            if (_welcomeChipRects[i].Contains(client))
                return i;
        }
        return -1;
    }

    private static bool IsWelcomeChipEnabled(int chip)
    {
        if (chip == 2)
        {
            try { return Clipboard.ContainsImage(); }
            catch { return false; }
        }
        return chip is 0 or 1 or 3;
    }
    private CanvasTool _activeTool = CanvasTool.Move;
    private int _lastClickTick;
    private Point _lastClickLocation;

    /// <summary>
    /// Right-click "escape": cancels any in-progress action and returns to the neutral
    /// Pan state (the editor's resting default). Shows an internal banner naming the tool
    /// that was deselected. Returns <c>true</c> if a tool was actually deselected, so the
    /// caller can suppress the context menu; <c>false</c> when already in the neutral state.
    /// </summary>
    public bool TryDeselectTool()
    {
        if (_activeTool == CanvasTool.Move)
            return false;

        // Deselecting the Text tool keeps what was typed (use the Esc key inside the
        // text box to discard instead). Commit first so the discard below no-ops.
        bool hadPendingCrop = HasPendingCrop;
        bool hadPendingCutOut = HasPendingCutOut;
        CommitOrCancelInlineText(commit: true);
        CancelInProgressTool();
        CancelCropPending();
        CancelCutOutPending();
        _activeTool = CanvasTool.Move;
        _selectedEmoji = null;
        UpdateCursor();
        // CancelCropPending / CancelCutOutPending already announced when a pending
        // strip existed; only fall back to the generic deselect banner otherwise.
        if (HasPendingResize)
            ShowToolBanner(LocalizationService.Translate("Enter / Double-click to confirm"), sticky: true);
        else if (!hadPendingCrop && !hadPendingCutOut)
            ShowToolBanner(LocalizationService.Translate("Tool deselected"));
        Invalidate();
        OnStateChanged();
        return true;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AnnotationStrokeShadow { get; set; } = true;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float StrokeWidth { get; set; } = 6f;

    /// <summary>Calculates proportional stroke thickness based on canvas size relative to 1280px standard width.</summary>
    public float GetScaledStrokeWidth(float strokeWidth)
    {
        if (_baseBitmap == null) return strokeWidth;
        float scale = Math.Max(1f, _baseBitmap.Width / 1280f);
        return strokeWidth * scale;
    }

    /// <summary>Current Text-tool font size (pixels). Backed by the toolbar's <c>_textFontSize</c>.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float TextFontSize
    {
        get => _textFontSize;
        set => _textFontSize = Math.Clamp(value, 10f, 120f);
    }

    /// <summary>Applies a full text-tool style snapshot (from settings).</summary>
    public void ApplyTextStyle(float size, string fontFamily, bool bold, bool italic,
        bool stroke, bool shadow, bool background, int alignment)
    {
        _textFontSize = Math.Clamp(size, 10f, 120f);
        if (!string.IsNullOrWhiteSpace(fontFamily))
            _textFontFamily = fontFamily;
        _textBold = bold;
        _textItalic = italic;
        _textStroke = stroke;
        _textShadow = shadow;
        _textBackground = background;
        _textAlign = (TextHAlign)Math.Clamp(alignment, 0, 2);
    }

    /// <summary>
    /// Copies tool, stroke, text style, and editor chrome from another canvas so a new tab
    /// matches the one the user is already working in.
    /// </summary>
    public void CopySharedToolStateFrom(AnnotationCanvas source)
    {
        if (source is null || ReferenceEquals(source, this)) return;

        ToolColor = source.ToolColor;
        StrokeWidth = source.StrokeWidth;
        ShowCaptureFrame = source.ShowCaptureFrame;
        FitToWindowOnLoad = source.FitToWindowOnLoad;
        ShowBanners = source.ShowBanners;
        ShowWelcomeBanner = source.ShowWelcomeBanner;
        ShowHints = source.ShowHints;
        EditorAutoCropControls = source.EditorAutoCropControls;
        EditorShowResizeHandles = source.EditorShowResizeHandles;
        ResizeHandlesScaleContent = source.ResizeHandlesScaleContent;
        PanModeLockObjects = source.PanModeLockObjects;
        UndoLimit = source.UndoLimit;
        ShowScrollbarsAlways = source.ShowScrollbarsAlways;
        ApplyTextStyle(
            source.TextFontSize,
            source._textFontFamily,
            source._textBold,
            source._textItalic,
            source._textStroke,
            source._textShadow,
            source._textBackground,
            (int)source._textAlign);
        ActiveTool = source.ActiveTool;
    }

    /// <summary>Raised when the user changes the Text-tool font size (toolbar buttons or wheel).</summary>
    public event Action<float>? TextFontSizeChanged;

    /// <summary>Raised when any Text-tool style property changes (for settings persistence).</summary>
    public event Action<float, string, bool, bool, bool, bool, bool, int>? TextStyleChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanUndo => _undoStack.Count > 0;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanRedo => _redoStack.Count > 0;

    private bool _isDirty = false;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDirty
    {
        get => _isDirty;
        set => _isDirty = value;
    }

    public event EventHandler? StateChanged;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDefaultBlank { get; set; } = false;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsBlankCanvas { get; set; } = false;

    /// <summary>Number of annotations currently selected (multi or single).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedCount => _multiSelectedIndices.Count > 0
        ? _multiSelectedIndices.Count
        : (_selectedAnnotationIndex >= 0 ? 1 : 0);

    /// <summary>True while Space is held for temporary pan (previous tool stored).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsTemporarySpacePan => _preSpaceTool is not null;

    /// <summary>Selects all annotations on the canvas. Shows a sticky banner with the count.</summary>
    public void SelectAll()
    {
        if (_annotations.Count == 0) return;
        ActiveTool = CanvasTool.Move;
        ExitRotateMode(invalidate: false);
        _multiSelectedIndices.Clear();
        for (int i = 0; i < _annotations.Count; i++)
            _multiSelectedIndices.Add(i);
        _selectedAnnotationIndex = _annotations.Count - 1;
        var msg = string.Format(LocalizationService.Translate("{0} objects selected"), _multiSelectedIndices.Count);
        ShowToolBanner(msg, sticky: true);
        Invalidate();
        OnStateChanged();
    }

    /// <summary>
    /// Pick-tool double-click entry point. Text under the cursor takes priority over select-all
    /// (so a single double-click edits text without fighting select-all).
    /// On a pristine blank canvas, double-click opens a file (welcome browse gesture).
    /// </summary>
    internal void SelectAllFromDoubleClick()
    {
        if (_preSpaceTool != null) return;
        // Already in text edit from an earlier path of the same gesture — do nothing.
        if (_inlineTextBox is not null) return;
        if (_activeTool != CanvasTool.Move) return;

        // Empty welcome document: double-click browses for a file (not select-all).
        // PreFilterMessage and OnMouseDoubleClick both land here, so this keeps the gesture working.
        if (IsDefaultBlank)
        {
            CancelSelectAllDoubleClickSideEffects();
            WelcomeOpenRequested?.Invoke();
            return;
        }

        // Prefer re-edit when the double-click lands on text — must be centralized because
        // WndProc (WM_LBUTTONDBLCLK), OnMouseDown timing, and OnMouseDoubleClick all converge here.
        Point client;
        try { client = PointToClient(Cursor.Position); }
        catch { client = Point.Empty; }
        var imgPt = ScreenToImage(client);
        int textHit = HitTestTextAnnotation(imgPt);
        if (textHit >= 0)
        {
            CancelSelectAllDoubleClickSideEffects();
            ActiveTool = CanvasTool.Text;
            BeginReEditText(textHit);
            return;
        }

        CancelSelectAllDoubleClickSideEffects();
        SelectAll();
    }

    /// <summary>Clears all multi-selection state and hides the sticky banner.</summary>
    private void ClearMultiSelection()
    {
        if (_multiSelectedIndices.Count == 0) return;
        _multiSelectedIndices.Clear();
        _multiDragOriginals = null;
        HideToolBanner();
        Invalidate();
        OnStateChanged(); // refresh contextual selection hints
    }

    /// <summary>Raised after any modification (push/undo/redo, tool change, base bitmap change).</summary>
    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Clears annotations, undo/redo, resets zoom/pan and switches to a new base image.</summary>
    public void ResetState(Bitmap newBaseBitmap)
    {
        if (newBaseBitmap is null) throw new ArgumentNullException(nameof(newBaseBitmap));

        var oldBaseBitmap = _baseBitmap;
        _baseBitmap = newBaseBitmap;
        InvalidateScaledCache();
        _annotations.Clear();
        ClearAllGuides();
        ClearEditHistory();
        _zoom = 1.0;
        _pan = PointF.Empty;
        _viewFitsWindow = true;
        _userPanned = false;
        _welcomeDismissed = false;
        _isPanning = false;
        _selectedAnnotationIndex = -1;
        _selectOriginalAnnotation = null;
        _selectDragStartImg = Point.Empty;
        _multiSelectedIndices.Clear();
        _multiDragOriginals = null;
        _eraserHoverIndex = -1;
        IsDefaultBlank = false;
        IsBlankCanvas = false;
        CancelInProgressTool();
        CancelPendingResize(silent: true);
        ActiveTool = CanvasTool.Move;
        if (!ReferenceEquals(oldBaseBitmap, newBaseBitmap))
            oldBaseBitmap?.Dispose();

        if (EditorAutoCropControls)
        {
            _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            _cropHasRect = true;
        }
        else
        {
            ClearCropPending();
        }
        ClearCutOutPending();

        ApplyInitialView();
        Invalidate();
        _isDirty = false;
        OnStateChanged();
    }

    /// <summary>Loads the complete project state (base image, annotations, guides) and resets the edit history.</summary>
    public void LoadProjectState(Bitmap newBaseBitmap, List<Annotation> annotations, List<float> horizontalGuides, List<float> verticalGuides)
    {
        if (newBaseBitmap is null) throw new ArgumentNullException(nameof(newBaseBitmap));

        var oldBaseBitmap = _baseBitmap;
        _baseBitmap = newBaseBitmap;
        InvalidateScaledCache();
        _annotations.Clear();
        if (annotations != null)
        {
            _annotations.AddRange(annotations);
        }

        ClearAllGuides();
        if (horizontalGuides != null)
        {
            _horizontalGuides.AddRange(horizontalGuides);
        }
        if (verticalGuides != null)
        {
            _verticalGuides.AddRange(verticalGuides);
        }

        ClearEditHistory();
        _zoom = 1.0;
        _pan = PointF.Empty;
        _viewFitsWindow = true;
        _userPanned = false;
        _welcomeDismissed = false;
        _isPanning = false;
        _selectedAnnotationIndex = -1;
        _selectOriginalAnnotation = null;
        _selectDragStartImg = Point.Empty;
        _multiSelectedIndices.Clear();
        _multiDragOriginals = null;
        _eraserHoverIndex = -1;
        IsDefaultBlank = false;
        IsBlankCanvas = false;
        CancelInProgressTool();
        CancelPendingResize(silent: true);
        ActiveTool = CanvasTool.Move;
        if (!ReferenceEquals(oldBaseBitmap, newBaseBitmap))
            oldBaseBitmap?.Dispose();

        if (EditorAutoCropControls)
        {
            _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            _cropHasRect = true;
        }
        else
        {
            ClearCropPending();
        }
        ClearCutOutPending();

        ApplyInitialView();
        Invalidate();
        _isDirty = false;
        OnStateChanged();
    }

    /// <summary>Bakes the saved image into the canvas and treats it as the new clean baseline.</summary>
    public void AcceptSavedBaseline(Bitmap renderedBitmap)
    {
        if (renderedBitmap is null) throw new ArgumentNullException(nameof(renderedBitmap));

        var oldBaseBitmap = _baseBitmap;
        _baseBitmap = new Bitmap(renderedBitmap);
        oldBaseBitmap?.Dispose();
        InvalidateScaledCache();
        _annotations.Clear();
        ClearEditHistory();
        _isPanning = false;
        _selectedAnnotationIndex = -1;
        _selectOriginalAnnotation = null;
        _selectDragStartImg = Point.Empty;
        _eraserHoverIndex = -1;
        IsBlankCanvas = false;
        CancelInProgressTool();
        CancelPendingResize(silent: true);

        if (EditorAutoCropControls)
        {
            _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            _cropHasRect = true;
        }
        else
        {
            ClearCropPending();
        }
        ClearCutOutPending();

        Invalidate();
        _isDirty = false;
        OnStateChanged();
    }

    /// <summary>Marks the current project state as saved/clean without baking or clearing vector elements.</summary>
    public void AcceptSavedProjectState()
    {
        _isDirty = false;
        OnStateChanged();
    }

    void IEditorContext.Invalidate()
    {
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Maximum number of undo steps kept in memory. Clamped 1–200.
    /// Lower values reduce memory for large canvases; higher values keep more history.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int UndoLimit
    {
        get => _undoStackLimit;
        set => _undoStackLimit = Math.Clamp(value, 1, 200);
    }

    // ── Undo / Redo ────────────────────────────────────────────────────────

    private const int MaxAnnotations = 200;

    public void Push(IEditCommand command)
    {
        if (command is AddAnnotationCommand addCmd)
        {
            var bounds = GetAnnotationBounds(addCmd.Annotation);
            var canvasBounds = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            if (!bounds.IsEmpty && !bounds.IntersectsWith(canvasBounds))
            {
                ShowToolBanner(LocalizationService.Translate("Draw objects inside the canvas"));
                return;
            }
            if (Annotations.Count >= MaxAnnotations)
            {
                ShowToolBanner(
                    string.Format(LocalizationService.Translate("Maximum annotations reached ({0})"), MaxAnnotations),
                    sticky: true);
                return;
            }
        }

        CommitPendingHistory();
        if (IsDefaultBlank)
        {
            IsDefaultBlank = false;
        }
        var beforeView = CaptureView();
        var beforeSel = CaptureSelection();
        command.Apply(this);
        RecordUndo(command, beforeView, beforeSel);
        _isDirty = true;
        OnStateChanged();
    }

    /// <summary>Records an undoable command without marking the document dirty or clearing the
    /// <see cref="IsDefaultBlank"/> flag. Used to resize a still-pristine blank canvas: the
    /// change stays reversible via undo/redo, yet the empty document keeps Save disabled and
    /// won't prompt to save on close (the IsDefaultBlank guard short-circuits both).</summary>
    private void PushClean(IEditCommand command)
    {
        CommitPendingHistory();
        var beforeView = CaptureView();
        var beforeSel = CaptureSelection();
        command.Apply(this);
        RecordUndo(command, beforeView, beforeSel);
        OnStateChanged();
    }

    public void Undo()
    {
        CommitPendingHistory();
        if (HasPendingResize)
        {
            CancelPendingResize();
            return;
        }
        if (_undoStack.Count == 0) return;
        var cmd = _undoStack[^1];
        var views = _undoViews[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _undoViews.RemoveAt(_undoViews.Count - 1);
        PlayHistoryStep(cmd, views, undo: true);
    }

    public void Redo()
    {
        CommitPendingHistory();
        if (HasPendingResize)
        {
            CancelPendingResize();
            return;
        }
        if (_redoStack.Count == 0) return;
        var cmd = _redoStack[^1];
        var views = _redoViews[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _redoViews.RemoveAt(_redoViews.Count - 1);
        PlayHistoryStep(cmd, views, undo: false);
    }

    private void PlayHistoryStep(IEditCommand cmd, CommandViewSnapshot views, bool undo)
    {
        var targetView = undo ? views.Before : views.After;
        bool moved = ShouldRestoreView(views.AffectedBounds) && RestoreView(targetView);
        void ApplyEdit()
        {
            if (undo) cmd.Revert(this); else cmd.Apply(this);
            RestoreSelection(undo ? views.BeforeSelection : views.AfterSelection);
            if (undo)
            {
                _redoStack.Add(cmd);
                _redoViews.Add(views);
            }
            else
            {
                _undoStack.Add(cmd);
                _undoViews.Add(views);
            }
            _isDirty = true;
            OnStateChanged();
        }

        if (moved)
        {
            OnStateChanged();
            ScheduleHistoryCommit(cmd, ApplyEdit);
        }
        else
        {
            ApplyEdit();
        }
    }

    private void ScheduleHistoryCommit(IEditCommand cmd, Action commit)
    {
        _pendingHistoryCommand = cmd;
        _pendingHistoryCommit = commit;
        if (_historyRevealTimer is null)
        {
            _historyRevealTimer = new System.Windows.Forms.Timer { Interval = HistoryRevealDelayMs };
            _historyRevealTimer.Tick += (_, _) =>
            {
                _historyRevealTimer!.Stop();
                CommitPendingHistory();
            };
        }
        _historyRevealTimer.Stop();
        _historyRevealTimer.Start();
    }

    private void CommitPendingHistory()
    {
        _historyRevealTimer?.Stop();
        var action = _pendingHistoryCommit;
        _pendingHistoryCommit = null;
        _pendingHistoryCommand = null;
        action?.Invoke();
    }

    private void DiscardPendingHistory()
    {
        _historyRevealTimer?.Stop();
        _pendingHistoryCommit = null;
        _pendingHistoryCommand?.Dispose();
        _pendingHistoryCommand = null;
    }

    private void RecordUndo(IEditCommand command, CanvasViewSnapshot beforeView, SelectionSnapshot beforeSel)
    {
        _undoStack.Add(command);
        _undoViews.Add(new CommandViewSnapshot(
            beforeView,
            CaptureView(),
            beforeSel,
            CaptureSelection(),
            GetCommandAffectedBounds(command)));
        if (_undoStack.Count > _undoStackLimit)
        {
            _undoStack[0].Dispose();
            _undoStack.RemoveAt(0);
            _undoViews.RemoveAt(0);
        }
        ClearRedo();
    }

    /// <summary>Call after Push when the caller then changes selection (e.g. duplicate).</summary>
    private void RefreshLastUndoAfterSelection()
    {
        if (_undoViews.Count == 0) return;
        var snap = _undoViews[^1];
        _undoViews[^1] = snap with { AfterSelection = CaptureSelection() };
    }

    /// <summary>Call after Push when the caller then adjusts pan/zoom (handle resize view-lock).</summary>
    private void RefreshLastUndoAfterView()
    {
        if (_undoViews.Count == 0) return;
        var snap = _undoViews[^1];
        _undoViews[^1] = snap with { After = CaptureView() };
    }

    private readonly record struct CanvasViewSnapshot(
        double Zoom, float PanX, float PanY, bool ViewFitsWindow, bool UserPanned);

    private readonly record struct SelectionSnapshot(int Primary, int[] Multi);

    private readonly record struct CommandViewSnapshot(
        CanvasViewSnapshot Before,
        CanvasViewSnapshot After,
        SelectionSnapshot BeforeSelection,
        SelectionSnapshot AfterSelection,
        Rectangle AffectedBounds);

    private CanvasViewSnapshot CaptureView()
        => new(_zoom, _pan.X, _pan.Y, _viewFitsWindow, _userPanned);

    private SelectionSnapshot CaptureSelection()
    {
        int[] multi = _multiSelectedIndices.Count > 0
            ? _multiSelectedIndices.ToArray()
            : [];
        return new SelectionSnapshot(_selectedAnnotationIndex, multi);
    }

    private void RestoreSelection(in SelectionSnapshot sel)
    {
        _selectOriginalAnnotation = null;
        _selectResizeOriginalAnnotation = null;
        _isSelectResizing = false;
        _selectResizeHandle = -1;
        ExitRotateMode(invalidate: false);
        _multiDragOriginals = null;
        _multiSelectedIndices.Clear();

        int count = _annotations.Count;
        foreach (int i in sel.Multi)
        {
            if (i >= 0 && i < count)
                _multiSelectedIndices.Add(i);
        }

        int primary = sel.Primary;
        if (primary < 0 || primary >= count)
            primary = -1;
        _selectedAnnotationIndex = primary;

        if (_multiSelectedIndices.Count == 1 && _selectedAnnotationIndex < 0)
            _selectedAnnotationIndex = _multiSelectedIndices.First();

        Invalidate();
    }

    private static Rectangle GetCommandAffectedBounds(IEditCommand command) => command switch
    {
        AddAnnotationCommand c => AnnotationTransforms.GetBounds(c.Annotation),
        DeleteAnnotationCommand c => AnnotationTransforms.GetBounds(c.Annotation),
        ReplaceAnnotationCommand c => UnionBounds(
            AnnotationTransforms.GetBounds(c.Original),
            AnnotationTransforms.GetBounds(c.Replacement)),
        TransformAnnotationCommand c => UnionBounds(
            AnnotationTransforms.GetBounds(c.Original),
            AnnotationTransforms.GetBounds(AnnotationTransforms.Translate(c.Original, c.Dx, c.Dy))),
        AddMultipleAnnotationsCommand c => UnionAnnotationBounds(c.Items),
        DeleteMultipleAnnotationsCommand c => UnionAnnotationBounds(c.Items.Select(x => x.Annotation)),
        TransformMultipleAnnotationsCommand c => UnionBounds(
            UnionAnnotationBounds(c.Items.Select(x => x.Original)),
            UnionAnnotationBounds(c.Items.Select(x => AnnotationTransforms.Translate(x.Original, c.Dx, c.Dy)))),
        EraseCommand c => c.EraseRect,
        _ => Rectangle.Empty,
    };

    private static Rectangle UnionAnnotationBounds(IEnumerable<Annotation> items)
    {
        var union = Rectangle.Empty;
        foreach (var a in items)
            union = UnionBounds(union, AnnotationTransforms.GetBounds(a));
        return union;
    }

    private static Rectangle UnionBounds(Rectangle a, Rectangle b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return Rectangle.Union(a, b);
    }

    /// <summary>
    /// True when the edit is off-screen, so the camera should jump to the stored view.
    /// Empty bounds mean a canvas-wide change (crop, rotate, resize) — always restore the stored view.
    /// </summary>
    private bool ShouldRestoreView(Rectangle affectedBounds)
    {
        if (affectedBounds.IsEmpty) return true;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return true;

        var visible = GetVisibleImageRect();
        visible.Inflate(16, 16);
        var padded = affectedBounds;
        padded.Inflate(16, 16);
        return !visible.IntersectsWith(padded);
    }

    private Rectangle GetVisibleImageRect()
    {
        var topLeft = ScreenToImage(Point.Empty);
        var bottomRight = ScreenToImage(new Point(ClientSize.Width, ClientSize.Height));
        int x = Math.Min(topLeft.X, bottomRight.X);
        int y = Math.Min(topLeft.Y, bottomRight.Y);
        return Rectangle.FromLTRB(x, y, Math.Max(topLeft.X, bottomRight.X), Math.Max(topLeft.Y, bottomRight.Y));
    }

    /// <returns>True if zoom/pan actually changed.</returns>
    private bool RestoreView(in CanvasViewSnapshot view)
    {
        bool zoomChanged = Math.Abs(_zoom - view.Zoom) > 1e-6;
        if (!zoomChanged
            && _pan.X == view.PanX
            && _pan.Y == view.PanY
            && _viewFitsWindow == view.ViewFitsWindow
            && _userPanned == view.UserPanned)
            return false;

        _zoom = view.Zoom;
        _pan = new PointF(view.PanX, view.PanY);
        _viewFitsWindow = view.ViewFitsWindow;
        _userPanned = view.UserPanned;
        if (zoomChanged)
            InvalidateScaledCache();
        NotifyScrollbarActivity();
        Invalidate();
        return true;
    }

    private void ClearRedo()
    {
        foreach (var c in _redoStack) c.Dispose();
        _redoStack.Clear();
        _redoViews.Clear();
    }

    private void ClearEditHistory()
    {
        DiscardPendingHistory();
        foreach (var c in _undoStack) c.Dispose();
        foreach (var c in _redoStack) c.Dispose();
        _undoStack.Clear();
        _redoStack.Clear();
        _undoViews.Clear();
        _redoViews.Clear();
    }

    // ── Zoom / Pan ─────────────────────────────────────────────────────────

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Zoom => _zoom;

    public void ZoomReset()
    {
        FlushDeferredZoomState();
        _zoom = 1.0;
        _viewFitsWindow = false;
        _userPanned = false;
        CenterImage();
        NotifyScrollbarActivity();
        Invalidate();
        OnStateChanged();
    }

    public void ZoomFit()
    {
        FlushDeferredZoomState();
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        double sx = (double)ClientSize.Width / _baseBitmap.Width;
        double sy = (double)ClientSize.Height / _baseBitmap.Height;
        _zoom = Math.Clamp(Math.Min(sx, sy) * 0.95, MinZoom, MaxZoom);
        _viewFitsWindow = true;
        _userPanned = false;
        CenterImage();
        NotifyScrollbarActivity();
        Invalidate();
        OnStateChanged();
    }

    public void ZoomBy(double factor, Point screenAnchor)
    {
        if (IsDefaultBlank)
            DismissWelcomeOverlay();
        ZoomTo(_zoom * factor, screenAnchor);
    }

    public void ZoomTo(double zoom, Point screenAnchor)
    {
        FlushDeferredZoomState();
        ZoomToCore(zoom, screenAnchor, forceDraft: false, notifyState: true);
    }

    private void ZoomToCore(double zoom, Point screenAnchor, bool forceDraft, bool notifyState)
    {
        var oldZoom = _zoom;
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 1e-6) return;

        if (IsDefaultBlank)
            DismissWelcomeOverlay();

        var imageAnchor = ScreenToImage(screenAnchor);
        _zoom = newZoom;
        _viewFitsWindow = false;
        _userPanned = true;
        _pan = new PointF(
            screenAnchor.X - (float)(imageAnchor.X * _zoom),
            screenAnchor.Y - (float)(imageAnchor.Y * _zoom));
        BeginZoomInteraction(forceDraft);
        NotifyScrollbarActivity();
        Invalidate();
        if (notifyState)
            OnStateChanged();
    }

    /// <summary>
    /// Marks an active zoom gesture so the next repaints draw the base image fast
    /// (cheap interpolation straight from the source) instead of rebuilding the crisp
    /// pre-scaled cache on every wheel tick. A one-shot timer clears the flag shortly
    /// after the last zoom step and forces one final high-quality repaint.
    /// </summary>
    private void BeginZoomInteraction(bool forceDraft = false)
    {
        // Small images rebuild the crisp cache cheaply enough every frame; engaging the
        // draft path would only add a perceptible blur and a snap back to sharp. Reserve
        // it for large bitmaps where the per-frame bicubic rescale is the actual cost.
        if (!forceDraft && (long)_baseBitmap.Width * _baseBitmap.Height < DraftZoomPixelThreshold)
            return;

        _zoomInteracting = true;
        if (_zoomSettleTimer is null)
        {
            _zoomSettleTimer = new System.Windows.Forms.Timer { Interval = 140 };
            _zoomSettleTimer.Tick += (_, _) =>
            {
                _zoomSettleTimer!.Stop();
                _zoomInteracting = false;
                Invalidate(); // rebuilds the HQ cache for the settled zoom level
            };
        }
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
    }

    /// <summary>
    /// Applies wheel zoom immediately. Delta is preserved for high-resolution wheels and
    /// touchpads, while a smaller per-notch factor provides finer framing than the old 15% jump.
    /// Expensive listeners are refreshed once the wheel burst settles instead of on every tick.
    /// </summary>
    private void ApplyWheelZoom(int delta, Point screenAnchor)
    {
        if (delta == 0) return;

        double targetZoom = _zoom * Math.Pow(1.08, delta / 120.0);
        ZoomToCore(targetZoom, screenAnchor, forceDraft: true, notifyState: false);

        ScheduleDeferredZoomState();
    }

    private void ScheduleDeferredZoomState()
    {
        if (_deferredZoomStateTimer is null)
        {
            _deferredZoomStateTimer = new System.Windows.Forms.Timer { Interval = 90 };
            _deferredZoomStateTimer.Tick += (_, _) => FlushDeferredZoomState();
        }
        _deferredZoomStateTimer.Stop();
        _deferredZoomStateTimer.Start();
    }

    private void FlushDeferredZoomState()
    {
        if (_deferredZoomStateTimer?.Enabled != true) return;
        _deferredZoomStateTimer.Stop();
        OnStateChanged();
    }

    public void ZoomToPercent(int percent)
    {
        ZoomToCore(
            percent / 100.0,
            new Point(ClientSize.Width / 2, ClientSize.Height / 2),
            forceDraft: false,
            notifyState: false);
        ScheduleDeferredZoomState();
    }

    private void CenterImage()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        float scaledW = (float)(_baseBitmap.Width * _zoom);
        float scaledH = (float)(_baseBitmap.Height * _zoom);
        _pan = new PointF(
            (ClientSize.Width - scaledW) / 2f,
            (ClientSize.Height - scaledH) / 2f);
    }

    /// <summary>
    /// After a canvas size change (90° rotate swaps W/H), shift pan so the image's
    /// visual center stays put. Zoom is left unchanged.
    /// </summary>
    internal void PreserveViewCenterAfterCanvasSizeChange(int previousWidth, int previousHeight)
    {
        if (_baseBitmap is null) return;
        int dw = previousWidth - _baseBitmap.Width;
        int dh = previousHeight - _baseBitmap.Height;
        if (dw == 0 && dh == 0) return;

        _pan = new PointF(
            _pan.X + (float)(dw * _zoom * 0.5),
            _pan.Y + (float)(dh * _zoom * 0.5));
        _viewFitsWindow = false;
        NotifyScrollbarActivity();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyInitialView();
    }

    /// <summary>How a freshly loaded capture is framed: auto-fit to the canvas, or shown at real 100% size.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FitToWindowOnLoad { get; set; } = true;

    private bool _panModeLockObjects = true;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PanModeLockObjects
    {
        get => _panModeLockObjects;
        set
        {
            if (_panModeLockObjects == value) return;
            _panModeLockObjects = value;
            Invalidate();
            OnStateChanged();
        }
    }

    private bool _showHints = true;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowHints
    {
        get => _showHints;
        set { _showHints = value; }
    }

    private bool _editorAutoCropControls = true;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool EditorAutoCropControls
    {
        get => _editorAutoCropControls;
        set
        {
            if (_editorAutoCropControls == value) return;
            _editorAutoCropControls = value;
            if (_editorAutoCropControls)
            {
                if (_baseBitmap != null && _activeTool != CanvasTool.Crop)
                {
                    _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
                    _cropHasRect = true;
                }
            }
            else
            {
                if (_activeTool != CanvasTool.Crop)
                {
                    ClearCropPending();
                }
            }
            Invalidate();
        }
    }

    /// <summary>Smallest / largest canvas dimension accepted by the resize feature.</summary>
    public const int MinCanvasSize = 16;
    /// <summary>Max width/height for canvas resize. Aligned with trusted image open ceiling
    /// (<see cref="CyberSnap.Helpers.ImageOpenPolicy.MaxTrustedLongestSide"/>) so tall scroll
    /// captures remain editable after open.</summary>
    public const int MaxCanvasSize = 32768;

    private bool _editorShowResizeHandles = true;
    /// <summary>Whether the cyan edge handles on the canvas are shown for resizing
    /// inward (trim) and outward (extend). Toggled from the burger menu.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool EditorShowResizeHandles
    {
        get => _editorShowResizeHandles;
        set
        {
            if (_editorShowResizeHandles == value) return;
            _editorShowResizeHandles = value;
            Invalidate();
        }
    }

    /// <summary>How dragging the canvas-edge handles behaves: false (default) = extend/trim the
    /// canvas area only; true = scale (resample) the image + annotations. Toggled from the
    /// burger menu / Config. Does not affect the modal, which has its own per-use toggle.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ResizeHandlesScaleContent { get; set; }

    /// <summary>True while dragging a canvas resize handle or a pending size is waiting
    /// for Enter / double-click confirm.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsResizingCanvas => _resizeDragging || HasPendingResize;

    /// <summary>Live pending size while dragging or waiting to confirm a canvas resize.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Size ResizePreviewSize => _resizePreviewSize;

    /// <summary>True when a handle resize has been staged and not yet confirmed.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HasPendingResize =>
        _hasPendingResize
        && _baseBitmap is not null
        && (_pendingResizeRect.X != 0
            || _pendingResizeRect.Y != 0
            || _pendingResizeRect.Width != _baseBitmap.Width
            || _pendingResizeRect.Height != _baseBitmap.Height);

    /// <summary>Hit-tests a client point against the resize handles (or -1). Public for the
    /// editor's hover tooltip. Returns -1 unless the handles are currently interactive.</summary>
    public int HitTestResizeHandlePublic(Point client)
        => (EditorShowResizeHandles && _baseBitmap != null && !HideCanvasResizeHandles
            && !IsOverAnnotationGrip(client))
            ? HitTestResizeHandle(client) : -1;

    /// <summary>Client-space bounding box of a resize handle, for tooltip placement.</summary>
    public Rectangle GetResizeHandleClientRect(int index)
    {
        var pts = GetResizeHandlePositionsScreen();
        if (index < 0 || index >= pts.Length) return Rectangle.Empty;
        var h = pts[index];
        int s = 16;
        return new Rectangle((int)(h.X - s / 2f), (int)(h.Y - s / 2f), s, s);
    }

    /// <summary>Factory the host (editor) supplies to rebuild the blank checkerboard at a new
    /// size. When set and the document is still the default blank, resizing regenerates the
    /// checkerboard to fill the new canvas instead of resampling/padding a fixed-size pattern.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, int, Bitmap>? BlankBitmapFactory { get; set; }

    /// <summary>Optional host hook to confirm an on-canvas canvas resize before it is applied.
    /// Called when the user confirms with Enter / double-click, not on each handle drag.
    /// Receives the resulting (width, height) in pixels; return false to cancel.
    /// When null, handle-drag resizes apply without a modal. The resize dialog has its own Apply.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, int, bool>? ConfirmResizeByHandle { get; set; }

    /// <summary>Resizes the canvas to a new pixel size and pushes an undoable command.
    /// <paramref name="scaleContent"/> true = resample (stretch image + annotations);
    /// false = re-canvas at the new size with <paramref name="anchor"/> (extend/trim).</summary>
    public void ResizeCanvas(int width, int height, bool scaleContent, Models.Commands.AnchorPosition anchor)
    {
        width = Math.Clamp(width, MinCanvasSize, MaxCanvasSize);
        height = Math.Clamp(height, MinCanvasSize, MaxCanvasSize);
        if (width == _baseBitmap.Width && height == _baseBitmap.Height) return;

        var command = new Models.Commands.ResizeCanvasCommand(width, height, scaleContent, anchor);
        ApplyResizeCommand(command, preserveView: false);
    }

    /// <summary>Handle-drag confirm: explicit content origin so multiple edge adjustments
    /// compose, and the camera stays put relative to the existing pixels.</summary>
    private void ResizeCanvasFromPending(Rectangle pending, bool scaleContent)
    {
        int width = Math.Clamp(pending.Width, MinCanvasSize, MaxCanvasSize);
        int height = Math.Clamp(pending.Height, MinCanvasSize, MaxCanvasSize);
        if (width == _baseBitmap.Width && height == _baseBitmap.Height
            && pending.X == 0 && pending.Y == 0)
            return;

        int offX = -pending.X;
        int offY = -pending.Y;
        var command = new Models.Commands.ResizeCanvasCommand(width, height, scaleContent, offX, offY);
        ApplyResizeCommand(command, preserveView: true, offX, offY);
    }

    private void ApplyResizeCommand(
        Models.Commands.ResizeCanvasCommand command,
        bool preserveView,
        int offX = 0,
        int offY = 0)
    {
        if (IsDefaultBlank)
            PushClean(command);
        else
            Push(command);

        if (preserveView)
        {
            _pan.X -= (float)(offX * _zoom);
            _pan.Y -= (float)(offY * _zoom);
            _viewFitsWindow = false;
            _userPanned = true;
            RefreshLastUndoAfterView();
        }
        else
        {
            _userPanned = true;
        }

        HideToolBanner();
        DismissWelcomeOverlay();
        Invalidate();
    }

    /// <summary>
    /// Rotates or flips the entire canvas. Annotations are flattened into the base bitmap
    /// (no longer individually editable). Undo restores the pre-transform bitmap + layers.
    /// </summary>
    public void TransformCanvas(Models.Commands.CanvasTransformKind kind)
    {
        // Commit/cancel in-progress drawing so we don't bake half-finished UI state.
        CancelInProgressTool();
        _selectedAnnotationIndex = -1;
        _multiSelectedIndices.Clear();
        _multiDragOriginals = null;
        _selectOriginalAnnotation = null;

        bool hadAnnotations = _annotations.Count > 0;
        bool pristineBlank = IsDefaultBlank && !hadAnnotations;

        var command = new Models.Commands.TransformCanvasCommand(kind);
        if (pristineBlank)
            PushClean(command);
        else
            Push(command);

        // Flattened pixels are a real image; blank checkerboard regeneration stays blank.
        if (hadAnnotations)
            IsBlankCanvas = false;

        HideToolBanner();
        DismissWelcomeOverlay();
        _userPanned = true;
        Invalidate();
    }

    /// <summary>Applies the configured initial view (fit-to-window or 100%) for the current image.</summary>
    public void ApplyInitialView()
    {
        if (FitToWindowOnLoad)
            ZoomFit();
        else
            ZoomReset();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            Invalidate();
            return;
        }

        // Keep the image where the user expects it as the canvas grows/shrinks
        // (e.g. when the window is maximized): re-fit while the view still
        // auto-fits, re-center while it's centered, but preserve a manual pan.
        if (_viewFitsWindow)
        {
            ZoomFit();
        }
        else if (!_userPanned || _pan == PointF.Empty)
        {
            CenterImage();
            Invalidate();
        }
        else
        {
            Invalidate();
        }
    }

    /// <summary>Returns a fresh bitmap with all current annotations baked in.</summary>
    public Bitmap RenderFinal()
    {
        var output = new Bitmap(_baseBitmap.Width, _baseBitmap.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(output);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(_baseBitmap, 0, 0);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _annotations.Count; i++)
            RenderAnnotation(g, _annotations[i]);
        return output;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SelectedAnnotationIndexInternal
    {
        get => _selectedAnnotationIndex;
        set => _selectedAnnotationIndex = value;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal HashSet<int> MultiSelectedIndicesInternal => _multiSelectedIndices;

    internal int HitTestAnnotationInternal(Point pt) => HitTestAnnotation(pt);

    internal void DeleteAnnotationAtInternal(int index) => DeleteAnnotationAt(index);

    internal void DeleteMultiSelectedAnnotationsInternal() => DeleteMultiSelectedAnnotations();

    internal void DuplicateSelectionInternal() => DuplicateSelection();

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsEditingText => _inlineTextBox is not null;

    internal void DeleteSelected()
    {
        if (_multiSelectedIndices.Count > 1)
        {
            DeleteMultiSelectedAnnotations();
        }
        else if (_selectedAnnotationIndex >= 0)
        {
            DeleteAnnotationAt(_selectedAnnotationIndex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bannerTimer?.Stop();
            _bannerTimer?.Dispose();
            _zoomSettleTimer?.Stop();
            _zoomSettleTimer?.Dispose();
            _deferredZoomStateTimer?.Stop();
            _deferredZoomStateTimer?.Dispose();
            DiscardPendingHistory();
            _historyRevealTimer?.Dispose();
            _historyRevealTimer = null;
            _rotateToggleTimer?.Stop();
            _rotateToggleTimer?.Dispose();
            _rotateToggleTimer = null;
            _resizeHandlesTimer?.Stop();
            _resizeHandlesTimer?.Dispose();
            DisposeScrollbarTimers();
            _scaledCache?.Dispose();
            _checkerboardBrush?.Dispose();
            ClearEditHistory();
            _emojiRenderer.Dispose();
            _blurScratch?.Dispose();
            _baseBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    private const int WM_LBUTTONDBLCLK = 0x0203;

    protected override void WndProc(ref Message m)
    {
        // Handle double-click at the message level — UserControl + manual mouse routing can
        // prevent OnMouseDoubleClick from firing even when the OS sends WM_LBUTTONDBLCLK.
        if (m.Msg == WM_LBUTTONDBLCLK && _preSpaceTool == null)
        {
            int lp = unchecked((int)(long)m.LParam);
            var pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            if (TryConfirmPendingResizeFromScreen(pt))
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (_activeTool == CanvasTool.Move)
            {
                // Same entry as MouseDown timing path — text under cursor re-edits; else select-all.
                SelectAllFromDoubleClick();
                m.Result = IntPtr.Zero;
                return;
            }
        }

        base.WndProc(ref m);
    }
}
