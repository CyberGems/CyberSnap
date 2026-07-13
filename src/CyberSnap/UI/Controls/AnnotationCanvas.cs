using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

    private double _zoom = 1.0;
    private PointF _pan; // pixel offset of image-space origin relative to control client
    private bool _zoomInteracting;       // user is mid zoom gesture: draw fast, refine on settle
    private System.Windows.Forms.Timer? _zoomSettleTimer;
    private bool _viewFitsWindow = true; // image auto-fits the canvas until the user zooms
    private bool _userPanned;            // user has manually dragged the image
    private bool _welcomeDismissed;      // welcome overlay hidden after first meaningful interaction
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

    /// <summary>Emoji glyph placed by the Emoji tool on the next click. Set by the editor's picker.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedEmoji
    {
        get => _selectedEmoji;
        set => _selectedEmoji = value;
    }

    /// <summary>Pixel size for emoji placed by the Emoji tool (clamped 16–128).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float EmojiPlaceSize
    {
        get => _emojiPlaceSize;
        set => _emojiPlaceSize = Math.Clamp(value, 16f, 128f);
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
    public Color ToolColor { get; set; } = Color.FromArgb(255, 220, 0);

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
                _bannerText = "";
                _bannerTimer?.Stop();
                Invalidate();
            }
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowWelcomeBanner { get; set; } = true;

    private float _bannerOpacity = 0f;
    private string _bannerText = "";
    private System.Windows.Forms.Timer? _bannerTimer;
    private int _bannerHoldTicks = 0;
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
        if (_activeTool == CanvasTool.Crop || _preSpaceTool != null) return;

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

        if (_bannerText == text && _bannerOpacity > 0f)
        {
            _bannerIsSticky = sticky;
            _bannerHoldTicks = 0;
            if (_bannerState == BannerState.FadeOut)
            {
                _bannerState = BannerState.FadeIn;
            }
            return;
        }

        _bannerText = text;
        _bannerIsSticky = sticky;
        _bannerState = BannerState.FadeIn;
        _bannerOpacity = 0f;
        if (_bannerTimer == null)
        {
            _bannerTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _bannerTimer.Tick += BannerTimer_Tick;
        }
        _bannerTimer.Stop();
        _bannerTimer.Start();
        Invalidate();
    }

    public void HideToolBanner()
    {
        _bannerIsSticky = false;
        _bannerState = BannerState.FadeOut;
        if (_bannerTimer == null)
        {
            _bannerTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _bannerTimer.Tick += BannerTimer_Tick;
        }
        _bannerTimer.Start();
    }

    private void BannerTimer_Tick(object? sender, EventArgs e)
    {
        switch (_bannerState)
        {
            case BannerState.FadeIn:
                _bannerOpacity += 0.12f;
                if (_bannerOpacity >= 1.0f)
                {
                    _bannerOpacity = 1.0f;
                    _bannerState = BannerState.Hold;
                    _bannerHoldTicks = 0;
                }
                Invalidate();
                break;

            case BannerState.Hold:
                if (_bannerIsSticky)
                {
                    _bannerHoldTicks = 0;
                    break;
                }
                _bannerHoldTicks++;
                if (_bannerHoldTicks >= 45)
                {
                    _bannerState = BannerState.FadeOut;
                }
                break;

            case BannerState.FadeOut:
                _bannerOpacity -= 0.08f;
                if (_bannerOpacity <= 0.0f)
                {
                    _bannerOpacity = 0.0f;
                    _bannerTimer?.Stop();
                }
                Invalidate();
                break;
        }
    }

    private string GetToolName(CanvasTool tool)
    {
        var key = tool switch
        {
            CanvasTool.Pan => "Pan",
            CanvasTool.Move => "Pick",
            CanvasTool.Crop => "Crop",
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
            _activeTool = value;
            bool cropApplied = false;
            if (leavingCrop)
                cropApplied = FinalizeLeavingCrop();

            if (value == CanvasTool.Crop)
            {
                if (!_cropHasRect || _cropRect.IsEmpty)
                {
                    _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
                    _cropHasRect = true;
                }
            }
            UpdateCursor();
            UpdateResizeHandlesHover();
            ShowToolBanner(cropApplied
                ? LocalizationService.Translate("Crop applied")
                : GetToolName(value));
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
            Invalidate();
        }
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
        CommitOrCancelInlineText(commit: true);
        CancelInProgressTool();
        CancelCropPending();
        _activeTool = CanvasTool.Move;
        _selectedEmoji = null;
        UpdateCursor();
        // CancelCropPending already announced "Crop canceled" when a crop was pending; only
        // fall back to the generic deselect banner when there was no crop to cancel.
        if (!hadPendingCrop)
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

    /// <summary>Raised when the user changes the Text-tool font size (toolbar buttons or wheel).</summary>
    public event Action<float>? TextFontSizeChanged;

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

    /// <summary>Selects all annotations on the canvas. Shows a sticky banner with the count.</summary>
    public void SelectAll()
    {
        if (_annotations.Count == 0) return;
        ActiveTool = CanvasTool.Move;
        _multiSelectedIndices.Clear();
        for (int i = 0; i < _annotations.Count; i++)
            _multiSelectedIndices.Add(i);
        _selectedAnnotationIndex = _annotations.Count - 1;
        var msg = string.Format(LocalizationService.Translate("{0} objects selected"), _multiSelectedIndices.Count);
        ShowToolBanner(msg, sticky: true);
        Invalidate();
        OnStateChanged();
    }

    /// <summary>Pick-tool double-click entry point (mirrors capture overlay Move mode).</summary>
    internal void SelectAllFromDoubleClick()
    {
        if (_activeTool != CanvasTool.Move || _preSpaceTool != null) return;
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
        ActiveTool = CanvasTool.Move;
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
        ActiveTool = CanvasTool.Move;
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

        if (EditorAutoCropControls)
        {
            _cropRect = new Rectangle(0, 0, _baseBitmap.Width, _baseBitmap.Height);
            _cropHasRect = true;
        }
        else
        {
            ClearCropPending();
        }

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

        if (IsDefaultBlank)
        {
            IsDefaultBlank = false;
        }
        command.Apply(this);
        _undoStack.Add(command);
        if (_undoStack.Count > _undoStackLimit)
        {
            var dropped = _undoStack[0];
            _undoStack.RemoveAt(0);
            dropped.Dispose();
        }
        ClearRedo();
        _isDirty = true;
        OnStateChanged();
    }

    /// <summary>Records an undoable command without marking the document dirty or clearing the
    /// <see cref="IsDefaultBlank"/> flag. Used to resize a still-pristine blank canvas: the
    /// change stays reversible via undo/redo, yet the empty document keeps Save disabled and
    /// won't prompt to save on close (the IsDefaultBlank guard short-circuits both).</summary>
    private void PushClean(IEditCommand command)
    {
        command.Apply(this);
        _undoStack.Add(command);
        if (_undoStack.Count > _undoStackLimit)
        {
            var dropped = _undoStack[0];
            _undoStack.RemoveAt(0);
            dropped.Dispose();
        }
        ClearRedo();
        OnStateChanged();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var cmd = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        cmd.Revert(this);
        _redoStack.Add(cmd);
        _isDirty = true;
        OnStateChanged();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var cmd = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        cmd.Apply(this);
        _undoStack.Add(cmd);
        _isDirty = true;
        OnStateChanged();
    }

    private void ClearRedo()
    {
        foreach (var c in _redoStack) c.Dispose();
        _redoStack.Clear();
    }

    private void ClearEditHistory()
    {
        foreach (var c in _undoStack) c.Dispose();
        foreach (var c in _redoStack) c.Dispose();
        _undoStack.Clear();
        _redoStack.Clear();
    }

    // ── Zoom / Pan ─────────────────────────────────────────────────────────

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Zoom => _zoom;

    public void ZoomReset()
    {
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
        var oldZoom = _zoom;
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 1e-6) return;

        var imageAnchor = ScreenToImage(screenAnchor);
        _zoom = newZoom;
        _viewFitsWindow = false;
        _userPanned = true;
        _pan = new PointF(
            screenAnchor.X - (float)(imageAnchor.X * _zoom),
            screenAnchor.Y - (float)(imageAnchor.Y * _zoom));
        BeginZoomInteraction();
        NotifyScrollbarActivity();
        Invalidate();
        OnStateChanged();
    }

    /// <summary>
    /// Marks an active zoom gesture so the next repaints draw the base image fast
    /// (cheap interpolation straight from the source) instead of rebuilding the crisp
    /// pre-scaled cache on every wheel tick. A one-shot timer clears the flag shortly
    /// after the last zoom step and forces one final high-quality repaint.
    /// </summary>
    private void BeginZoomInteraction()
    {
        // Small images rebuild the crisp cache cheaply enough every frame; engaging the
        // draft path would only add a perceptible blur and a snap back to sharp. Reserve
        // it for large bitmaps where the per-frame bicubic rescale is the actual cost.
        if ((long)_baseBitmap.Width * _baseBitmap.Height < DraftZoomPixelThreshold)
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

    public void ZoomToPercent(int percent)
        => ZoomTo(percent / 100.0, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    private void CenterImage()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        float scaledW = (float)(_baseBitmap.Width * _zoom);
        float scaledH = (float)(_baseBitmap.Height * _zoom);
        _pan = new PointF(
            (ClientSize.Width - scaledW) / 2f,
            (ClientSize.Height - scaledH) / 2f);
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
    /// <summary>Whether the square resize handles float around the canvas in the gray margin.
    /// Permanent like <see cref="EditorAutoCropControls"/>; toggled from the burger menu.</summary>
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

    /// <summary>How dragging the square handles behaves: false (default) = extend/trim the
    /// canvas area only; true = scale (resample) the image + annotations. Toggled from the
    /// burger menu / Config. Does not affect the modal, which has its own per-use toggle.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ResizeHandlesScaleContent { get; set; }

    /// <summary>True while the user is dragging a canvas resize handle.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsResizingCanvas => _resizeDragging;

    /// <summary>Live pending size while dragging a resize handle (for the status-bar hint).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Size ResizePreviewSize => _resizePreviewSize;

    /// <summary>Hit-tests a client point against the resize handles (or -1). Public for the
    /// editor's hover tooltip. Returns -1 unless the handles are currently interactive.</summary>
    public int HitTestResizeHandlePublic(Point client)
        => (EditorShowResizeHandles && _baseBitmap != null && _preSpaceTool == null && _activeTool != CanvasTool.Crop)
            ? HitTestResizeHandle(client) : -1;

    /// <summary>Client-space bounding box of a resize handle, for tooltip placement.</summary>
    public Rectangle GetResizeHandleClientRect(int index)
    {
        var pts = GetResizeHandlePositionsScreen();
        if (index < 0 || index >= pts.Length) return Rectangle.Empty;
        var h = pts[index];
        int s = (int)(ResizeHandleSize + 4);
        return new Rectangle((int)(h.X - s / 2f), (int)(h.Y - s / 2f), s, s);
    }

    /// <summary>Factory the host (editor) supplies to rebuild the blank checkerboard at a new
    /// size. When set and the document is still the default blank, resizing regenerates the
    /// checkerboard to fill the new canvas instead of resampling/padding a fixed-size pattern.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, int, Bitmap>? BlankBitmapFactory { get; set; }

    /// <summary>Optional host hook to confirm an interactive (handle-drag) canvas resize before
    /// it is applied. Receives the resulting (width, height) in pixels; return false to cancel
    /// the resize. When null, handle-drag resizes apply without confirmation. Only the on-canvas
    /// resize handles consult this — the resize dialog has its own Apply button.</summary>
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

        // A pristine blank document regenerates its checkerboard at the new size (handled inside
        // the command via BlankBitmapFactory). Record it through PushClean so the resize stays
        // undoable while keeping the document "clean": IsDefaultBlank is preserved, which keeps
        // Save disabled and suppresses the save-on-close prompt for an untouched canvas.
        if (IsDefaultBlank)
            PushClean(command);
        else
            Push(command);

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
        RenderAnnotations(g);
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
            _resizeHandlesTimer?.Stop();
            _resizeHandlesTimer?.Dispose();
            DisposeScrollbarTimers();
            _scaledCache?.Dispose();
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
        if (m.Msg == WM_LBUTTONDBLCLK && _activeTool == CanvasTool.Move && _preSpaceTool == null)
        {
            SelectAllFromDoubleClick();
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }
}
