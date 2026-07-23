using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm : Form
{
    private readonly Bitmap _screenshot;
    private int[]? _pixelData;
    private readonly int _bmpW, _bmpH;
    private readonly Rectangle _virtualBounds;
    private readonly WindowDetectionMode _windowDetectionMode;
    private readonly CenterSelectionAspectRatio _centerSelectionAspectRatio;

    private CaptureMode _mode = CaptureMode.Rectangle;
    private bool _isSelecting;
    private Point _selectionStart;
    private Point _selectionEnd;
    private Rectangle _selectionRect;
    private bool _hasSelection;
    private bool _hasDragged;
    private bool _isEvading;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ConfirmRegionBeforeCapture { get; set; } = true;

    /// <summary>
    /// Post-confirm destination requested from the confirm-mode action pills
    /// (toast-style Save / Copy / Edit / …). Consumed by <see cref="App"/> after crop.
    /// </summary>
    public enum ConfirmCommitAction
    {
        Default,
        Save,
        Copy,
        Edit,
        Share,
        History
    }

    /// <summary>
    /// Raised when the user hides/shows a confirm destination pill via the right-click menu.
    /// App should persist <see cref="AppSettings.ToastButtons"/> and refresh Settings if open.
    /// </summary>
    public event Action<AppSettings.ToastButtonLayoutSettings>? ToastButtonsChanged;

    /// <summary>Raised when the user toggles icon+label mode on the confirm bar.</summary>
    public event Action<bool>? ConfirmPillShowLabelsChanged;

    /// <summary>Action chosen when the user commits via a destination pill (or Enter / double-click primary).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ConfirmCommitAction PendingCommitAction { get; private set; } = ConfirmCommitAction.Default;

    /// <summary>
    /// Annotation flyout + stroke/color chrome only while the region is locked.
    /// Pre-selection dock stays capture-only (no annotation bar, no color/width).
    /// </summary>
    private bool ShowAnnotationChrome => _isConfirmingSelection;

    private bool _isConfirmingSelection;
    private Rectangle _confirmRect;
    public Rectangle ConfirmRect => _confirmRect;
    private int _confirmHandleDragIndex = -1;
    private bool _isConfirmDragging;
    private Point _confirmDragStart;
    private Point _confirmDragOffset;
    private Rectangle _confirmDragStartRect;
    private int _hoveredConfirmButton = -1; // index into confirm chrome
    private int _pressedConfirmButton = -1; // button currently playing the click squash
    private int _pendingConfirmAction = -1; // action to run when the squash finishes
    private float _confirmPressAmt;          // 0→1→0 squash progress for the pressed button
    private DateTime _pressAnimStart;
    private const int ConfirmShineSlots = 10;
    private readonly float[] _shinePhase = new float[ConfirmShineSlots]; // per-button glint position
    private readonly float[] _shineMain = new float[ConfirmShineSlots]; // primary comet intensity
    private readonly float[] _shineDup = new float[ConfirmShineSlots];  // duplicate comet intensity (hover)

    // Confirm chrome: Cancel / Retry / modes (built in RebuildConfirmChrome)
    private enum ConfirmChromeKind { Cancel, Retry, Done, TogglePreview, ModeImage, ModeOcr, ModeVideo, ModeGif, ModeScroll, ModeQr }
    private ConfirmChromeKind[] _confirmChromeKinds =
    {
        ConfirmChromeKind.ModeImage, ConfirmChromeKind.ModeOcr, ConfirmChromeKind.ModeVideo, ConfirmChromeKind.ModeGif,
        ConfirmChromeKind.ModeScroll, ConfirmChromeKind.ModeQr,
        ConfirmChromeKind.TogglePreview, ConfirmChromeKind.Retry, ConfirmChromeKind.Cancel, ConfirmChromeKind.Done
    };
    private Rectangle[] _confirmChromeRects = Array.Empty<Rectangle>();
    /// <summary>Thin divider between fixed Cancel/Retry and destination pills (empty when unused).</summary>
    private Rectangle _confirmChromeSeparatorRect1 = Rectangle.Empty;
    private Rectangle _confirmChromeSeparatorRect2 = Rectangle.Empty;
    /// <summary>Rounded dock panel behind the confirm pills so they stay readable on any wallpaper.</summary>
    private Rectangle _confirmChromeWrapperRect = Rectangle.Empty;
    private bool _confirmPillShowLabels;
    private bool _confirmChromeLayoutDirty = true;
    private Rectangle _confirmChromeLaidOutForRect = Rectangle.Empty;
    private bool _confirmChromeLaidOutWithLabels;
    /// <summary>Traveling glint phase around the confirm dock wrapper (0..1).</summary>
    private float _confirmWrapperShinePhase;

    /// <summary>
    /// Effective dock for chrome paint / alt-popup direction. Annotation confirm dock is always
    /// a vertical column on the left or right of the capture frame (not the screen edge).
    /// </summary>
    private CaptureDockSide ActiveDockSide
    {
        get
        {
            if (ShowAnnotationChrome)
                return _annotationFrameDockSide;
            return _isEvading ? GetOppositeDockSide(CaptureDockSide) : CaptureDockSide;
        }
    }

    /// <summary>Right/Left of the locked capture frame for the annotation tool column.</summary>
    private CaptureDockSide _annotationFrameDockSide = CaptureDockSide.Right;

    /// <summary>
    /// Client-space bounds of the monitor where the user started the current area selection
    /// (full monitor Bounds, not WorkingArea). Manual selection and region drag stay inside it.
    /// </summary>
    private Rectangle _selectionMonitorClientBounds;

    /// <summary>Confirm-mode permanent size pill + grip (drag handle) — above the frame, top-left.</summary>
    private Rectangle _confirmSizeReadoutRect = Rectangle.Empty;
    private Rectangle _confirmSizeReadoutGripRect = Rectangle.Empty;
    private bool _hoveredConfirmSizeReadout;
    private Rectangle _centerMoveGripRect = Rectangle.Empty;
    private bool _hoveredCenterMoveGrip;
    private float _centerMoveGripOpacity = 0f;
    private float _centerMoveGripTargetOpacity = 0f;
    private System.Windows.Forms.Timer? _centerMoveGripAnimTimer;
    /// <summary>Throttle layered-toolbar presents while the capture frame is being dragged.</summary>
    private DateTime _lastConfirmDragToolbarPresentUtc = DateTime.MinValue;
    /// <summary>
    /// Destination pills + annotation toolbar are fully hidden while the user moves/resizes the
    /// locked frame (avoids freeze-glitches and floating docks). Restored on mouse-up.
    /// </summary>
    private bool _confirmDocksHiddenForFrameManip;

    /// <summary>Left-button down outside the locked frame (no annotations) may become Retry or a new rubber-band.</summary>
    private bool _outsideReselectArmed;
    private Point _outsideReselectDown;
    private bool _outsideReselectMoved;

    private static CaptureDockSide GetOppositeDockSide(CaptureDockSide side) => side switch
    {
        CaptureDockSide.Top => CaptureDockSide.Bottom,
        CaptureDockSide.Bottom => CaptureDockSide.Top,
        CaptureDockSide.Left => CaptureDockSide.Right,
        CaptureDockSide.Right => CaptureDockSide.Left,
        _ => side
    };

    // Dynamic toolbar built from enabled tools + fixed buttons (color, position, close)
    private ToolDef[] _visibleTools = ToolDef.AllTools;
    private ToolDef[] _mainBarTools = Array.Empty<ToolDef>();
    private ToolDef[] _flyoutTools = Array.Empty<ToolDef>();
    private int BtnCount => _mainBarTools.Length + _flyoutTools.Length + 4; // +strokeWidth +color +position +close
    private int StrokeWidthButtonIndex => _mainBarTools.Length;
    private int ColorButtonIndex => _mainBarTools.Length + 1;
    private int PositionButtonIndex => _mainBarTools.Length + 2;
    private int CloseButtonIndex => _mainBarTools.Length + 3;
    private Rectangle[] _toolbarButtons = Array.Empty<Rectangle>();
    private string[] _toolbarIcons = Array.Empty<string>();
    private string[] _toolbarLabels = Array.Empty<string>();
    private string[] _toolbarToolIds = Array.Empty<string>();
    private CaptureMode?[] _toolbarModes = Array.Empty<CaptureMode?>();
    private string? _activeToolId;
    private int _hoveredButton = -1;
    private int _tooltipButton = -1;
    private WindowsToolTip? _toolbarToolTip;

    // Hybrid / hold-to-switch buttons (capture Area↔Center; annotation shape/stroke/mark groups)
    private bool _altCapturePopupOpen = false;
    /// <summary>Union bounds of all alt popup slots (hit-test + invalidation).</summary>
    private Rectangle _altCaptureButtonRect = Rectangle.Empty;
    private bool _hoveredAltCaptureBtn = false;
    private int _hoveredAltSlotIndex = -1;
    private System.Windows.Forms.Timer? _hoverHoldTimer;
    private DateTime _mouseDownStartTime = DateTime.MinValue;
    private bool _isMouseDownOnCaptureBtn = false;
    private int _mergedCaptureButtonIndex = -1;
    /// <summary>Toolbar button currently held for an alt-tool popup (capture or annotation merge).</summary>
    private int _mergedHoldButtonIndex = -1;
    /// <summary>Primary tool id → enabled sibling ids not shown on the bar (annotation merges only).</summary>
    private readonly Dictionary<string, string[]> _annotationMergeAltsByPrimaryId =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Toolbar button index → alternate tool ids for hold-to-switch annotation groups.</summary>
    private readonly Dictionary<int, string[]> _annotationMergeAltsByButton = new();
    /// <summary>Laid-out alt popup slots (container rect, tool id, icon id).</summary>
    private readonly List<(Rectangle Container, string ToolId, string IconId)> _altPopupSlots = new();

    /// <summary>
    /// Annotation tools collapsed into one hold-to-switch slot (primary first = default).
    /// Capture bar merges (rect/center) stay separate and unchanged.
    /// </summary>
    private static readonly string[][] AnnotationMergeGroups =
    {
        new[] { "rectShape", "circleShape" },
        new[] { "arrow", "line", "curvedArrow" },
        new[] { "highlight", "blur" },
    };

    // Tooltip timer variables
    private DateTime _hoverButtonStartTime = DateTime.MinValue;
    private DateTime _tooltipShowTime = DateTime.MinValue;
    private bool _tooltipVisible = false;
    private bool _tooltipDismissed = false;

    private bool _showToolNumberBadges = true;
    private Rectangle _toolbarRect;
    private Rectangle _brandRect;
    private Rectangle _logoRect;
    private Rectangle _menuActivatorRect;
    private Rectangle _annotationGripRect;
    private Rectangle _captureGripRect;
    private Rectangle _confirmGripRect;
    private bool _hoveredBrand;
    private bool _hoveredBrandDragArea;
    private bool _hoveredMenuActivator;
    private DateTime _lastContextMenuClosedTime = DateTime.MinValue;
    private int _lastContextMenuBtnIndex = -999;
    private Rectangle _toolbarAnchorArea;
    private Rectangle _lastOverlayUiBounds;
    private int _toolbarRenderVersion;
    private float _toolbarAnim;

    // Toolbar drag variables
    private bool _isDraggingToolbar;
    private Point _toolbarDragStartMouse;
    private Point _toolbarDragStartOffset;
    private bool _hasMovedToolbarByDrag;
    private Point _toolbarCustomOffset;
    private bool _isDraggingConfirm;
    private Point _confirmDragStartMouse;
    private Point _confirmDragStartOffset;
    private Point _confirmCustomOffset;
    private Point _lastCursorPos;
    private Point _prevCursorPos; // crosshair ghosting fix
    private Rectangle _lastSelectionRect;
    private Rectangle _lastAutoDetectRect;
    private LiveSelectionAdornerForm? _selectionAdorner;
    private CaptureEscapeKeyHook? _escapeHook;
    private StandaloneToolBanner? _banner;
    private CrosshairGuideForm? _verticalCrosshairForm;
    private CrosshairGuideForm? _horizontalCrosshairForm;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly System.Windows.Forms.Timer _autoDetectTimer;
    private readonly System.Windows.Forms.Timer _selectionPaintTimer;
    private readonly System.Windows.Forms.Timer _confirmPressTimer;
    private readonly System.Windows.Forms.Timer _confirmShineTimer;
    private readonly System.Diagnostics.Stopwatch _selectionPaintStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private bool _selectionPaintQueued;
    private DateTime _showTime;
    private ToolbarForm? _toolbarForm;
    private bool _allowDeactivation;
    private bool _cancelRequested;
    private Point _pendingAutoDetectPoint = Point.Empty;

    private const int TopBarHeight = 110;
    private Bitmap? _brandBitmap;

    // Color picker state
    private readonly Bitmap _magBitmap;
    private readonly int[] _magPixels;
    private readonly Graphics _magGfx;
    private readonly Font _hexFont = UiChrome.ChromeFont(11f, FontStyle.Bold);
    private readonly Font _rgbFont = UiChrome.ChromeFont(9f);
    private readonly Font _readoutFont = UiChrome.ChromeFont(9f, FontStyle.Bold);
    private readonly SolidBrush _mutedBrush = new(Color.FromArgb(140, 255, 255, 255));
    private readonly Pen _crossPen = new(Color.FromArgb(210, 255, 255, 255), 1f);
    private Point _pickerCursorPos;
    private Point _lastMagnifierSamplePoint = new(-1, -1);
    private Color _pickedColor = Color.Black;
    private string _hexStr = "000000";
    private string _rgbStr = "0, 0, 0";
    private readonly System.Windows.Forms.Timer _pickerTimer;

    private const int Grid = 11, Cell = 10, Mag = Grid * Cell;
    private const int InfoH = 0, PPad = 0;
    private const int PW = Mag, PH = Mag;
    private const int MagOff = 8, MagMargin = 4;

    // Committed annotations in creation order. Edit commands mutate this list.
    private readonly List<Annotation> _undoStack = new();
    private readonly List<IEditCommand> _editUndoStack = new();
    private readonly List<IEditCommand> _editRedoStack = new();
    private OverlayEditorContext? _overlayEditorContext;
    private Bitmap? _committedAnnotationsBitmap;
    private bool _committedAnnotationsDirty = true;

    // Draw / Blur / Arrow state
    private List<Point>? _currentStroke;
    private Point _blurStart;
    private bool _isBlurring;
    private Bitmap? _blurPreviewBitmap;
    private Size _blurPreviewSize;

    private Point _arrowStart;
    private bool _isArrowDragging;

    // Straight line
    private Point _lineStart;
    private bool _isLineDragging;

    // Ruler tool
    private Point _rulerStart;
    private bool _isRulerDragging;

    // Tracks the union of pixels painted by the most recent PaintAnnotations call,
    // so InvalidateLivePreview can always re-clear the previous frame's live overlay
    // even when a tool's per-frame bounds underestimate the actual paint extent.
    private Rectangle _lastLivePreviewPaintExtent;

    // Curved arrows: freehand path with arrowhead at end
    private List<Point>? _currentCurvedArrow;
    private bool _isCurvedArrowDragging;

    // Highlight rectangles (semi-transparent, yellow default)
    private Point _highlightStart;
    private bool _isHighlighting;
    private static readonly Color DefaultHighlightColor = Color.FromArgb(255, 255, 220, 0);

    // Shape tools
    private Point _shapeStart;
    private bool _isRectShapeDragging;
    private bool _isCircleShapeDragging;
    private bool _isPlacingMagnifier;

    // Step numbering
    private int _nextStepNumber = 1;

    // Region auto-detect
    private Rectangle _autoDetectRect;
    private bool _autoDetectActive;
    /// <summary>
    /// When false, selection-mode dim is suppressed so the first visible frame can include
    /// both the veil and the window hole under the cursor (no full-dim flash).
    /// </summary>
    private bool _selectionDimPrimed;

    /// <summary>Grayscale copy of the capture screenshot for outside-selection desaturation.</summary>
    private Bitmap? _desaturatedScreenshot;

    // Color picker popup state
    private bool _colorPickerOpen;
    private Rectangle _colorPickerRect;
    private PickerMagnifierForm? _captureMagnifierForm;

    private static RegionOverlayForm? _currentOverlay;

    // Select tool state
    private int _selectedAnnotationIndex = -1;
    private readonly HashSet<int> _multiSelectedIndices = new();
    private List<(int Index, Annotation Original)>? _multiDragOriginals;
    private Point _multiDragStart;
    private bool _isMarqueeSelecting;
    private Point _marqueeStart;
    private Point _marqueeEnd;
    private bool _isSelectDragging;
    private bool _isSelectResizing;
    private int _selectResizeHandle = -1; // 0=TL,1=TR,2=BL,3=BR
    private Point _selectDragStart;
    private Point _selectDragOffset;
    private Rectangle _selectHandleBounds; // cached bounds for handle hit-testing
    private Annotation? _selectResizeOriginalAnnotation;
    private Annotation? _selectPreviewAnnotation;
    // Index in _undoStack to skip when rebuilding the committed bitmap.
    // While a select drag/resize is active, the live preview is the only
    // rendering of that annotation â€” without skipping, you see a ghost at
    // the original position.
    private int _renderSkipIndex = -1;

    // Eraser hover highlight
    private int _eraserHoverIndex = -1;

    // Move tool hover highlight (mirrors Editor's _moveHoverIndex)
    private int _moveHoverIndex = -1;

    // After a click-to-place annotation (step/emoji/magnifier) the cursor sits on top of the
    // freshly placed item, which would pop its move/control box immediately. We suppress hover
    // for that one annotation until the cursor leaves it once, so the box only appears on a
    // deliberate re-hover. -1 = nothing suppressed.
    private int _suppressHoverBoxIndex = -1;

    /// <summary>Suppresses the hover/control box for the annotation just appended to the stack,
    /// until the cursor moves off it.</summary>
    private void SuppressHoverBoxForLastPlaced()
    {
        _suppressHoverBoxIndex = _undoStack.Count - 1;
        _moveHoverIndex = -1;
    }

    private bool _isTyping;
    private Point _textPos;
    private string _textBuffer = "";
    private float _textFontSize = 24f;
    private bool _textBold = true; // default bold
    private bool _textItalic;
    private bool _textStroke = true; // outline stroke enabled by default
    private bool _textShadow = true; // shadow enabled by default
    private bool _textBackground;
    private string _textFontFamily = UiChrome.FallbackFamilyName;
    private TextHAlign _textAlign = TextHAlign.Left;
    private float _textMaxWidth; // 0 = auto
    private TextBox? _textBox; // real textbox for native text editing
    /// <summary>When re-editing, index in <c>_undoStack</c> of the original annotation (−1 = new).</summary>
    private int _textEditStackIndex = -1;
    private TextAnnotation? _textEditOriginal;

    // Inline text formatting toolbar hit rects (computed during paint)
    private RectangleF _textToolbarRect;
    private RectangleF _textBoldBtnRect;
    private RectangleF _textItalicBtnRect;
    private RectangleF _textStrokeBtnRect;
    private RectangleF _textShadowBtnRect;
    private RectangleF _textBackgroundBtnRect;
    private RectangleF _textFontBtnRect;
    private RectangleF _textSizeMinusBtnRect;
    private RectangleF _textSizeLabelRect; // clickable size readout
    private RectangleF _textSizePlusBtnRect;
    private RectangleF _textAlignLeftBtnRect;
    private RectangleF _textAlignCenterBtnRect;
    private RectangleF _textAlignRightBtnRect;
    private RectangleF _textGripRect; // drag handle that moves toolbar + text together
    // 0=B 1=I 2=Stroke 3=Shadow 4=Bg 5=Font 6=Size- 7=Size+ 8=Grip 9=AlignL 10=AlignC 11=AlignR 12=SizeLabel
    private int _hoveredTextBtn = -1;
    private string _textBtnTooltip = "";
    private int _textResizeHandle = -1;
    private bool _textResizing;
    private Point _textResizeStart;
    private float _textResizeStartFontSize;
    private bool _textDragging;
    private Point _textDragOffset;
    private Point _lastTextDragLocation = Point.Empty;
    private DateTime _lastTextDragFrameUtc;
    private bool _textSelecting;
    private int _textSelectionAnchor;
    // Timed double-click tracking for Pick/Move (OS double-click often lost after select-drag)
    private int _lastTextDblClickTick;
    private Point _lastTextDblClickLocation = Point.Empty;
    private RectangleF _activeTextRectCache;
    private float _activeTextMeasureWidth;
    private readonly RectangleF[] _activeTextHandleCache = new RectangleF[4];
    private bool _activeTextLayoutDirty = true;
    private bool _snapGuideXVisible;
    private bool _snapGuideYVisible;

    // Font picker popup
    private bool _fontPickerOpen;
    private Rectangle _fontPickerRect;
    private int _fontPickerHovered = -1;
    private int _fontPickerScroll;
    private string _fontSearch = "";
    private string[]? _filteredFonts;
    private TextBox? _fontSearchBox;

    // Font cache for picker rendering (avoid creating Font objects every paint)
    private static readonly Dictionary<string, Font> _fontCache = new();

    // Emoji rendering (Direct2D for color emoji)
    private readonly EmojiRenderer _emojiRenderer = new();

    // Emoji tool state
    private bool _emojiPickerOpen;
    private Rectangle _emojiPickerRect;
    private string _emojiSearch = "";
    private int _emojiHovered = -1;
    private int _emojiScrollOffset;
    private string? _selectedEmoji;
    private bool _isPlacingEmoji;
    private float _emojiPlaceSize = 32f;
    private int _emojiWarmupIndex;
    private bool _emojiWarmupPending;
    private const int EmojiPickerColumns = 8;
    private const int EmojiPickerVisibleRows = 4;
    private const int EmojiPickerIconSize = 32;
    private const int EmojiPickerPadding = 8;
    private const int EmojiPickerSearchBarHeight = 32;
    private const float EmojiPickerRenderSize = 22f;

    // Full emoji palette (searchable by name) — shared with the editor's emoji picker.
    private static readonly (string emoji, string name)[] EmojiPalette = EmojiCatalog.Items;

    // Tool color (shared across draw, arrow, text)
    private Color _toolColor = Color.FromArgb(0, 136, 255);
    private static readonly Color[] ToolColors = {
        Color.Red, Color.FromArgb(255, 136, 0), Color.FromArgb(255, 220, 0),
        Color.FromArgb(0, 200, 0), Color.FromArgb(0, 136, 255), Color.White
    };
    private int _toolColorIndex = 4;

    // Stroke width
    private float _strokeWidth = 6f;
    public static readonly float[] StrokeWidths = { 2f, 3f, 4f, 6f, 10f };
    private const int StrokeWidthCount = 5;
    public event Action<Color>? ToolColorChanged;
    public event Action<CaptureDockSide>? DockSideChanged;
    public event Action<CaptureMode>? DefaultCaptureModeChanged;
    /// <summary>Raised once when the first-run quick-start guide is dismissed so the host can persist HasSeenQuickStartGuide.</summary>
    public event Action? QuickStartGuideDismissed;
    /// <summary>Raised when the user picks a Group-1 annotation tool so the host can persist LastAnnotationToolId.</summary>
    public event Action<string>? LastAnnotationToolChanged;
    private const int ColorPickerColumns = 6;
    private const int ColorPickerRows = 1;
    private const int ColorPickerSwatchSize = 28;
    private const int ColorPickerPadding = 4;
    private const int GlobalCenterSnapThreshold = 8;

    // (typed _undoStack is defined above with annotation state)

    // Blank cursor for color picker (we draw our own crosshair)
    private static readonly Cursor _blankCursor = CreateBlankCursor();

    // Events
    public event Action<Rectangle>? RegionSelected;
    public event Action<Rectangle>? OcrRegionSelected;
    public event Action<string>? ColorPicked;
    public event Action<Rectangle>? ScanRegionSelected;
    public event Action<Rectangle>? StickerRegionSelected;
    public event Action<Rectangle>? UpscaleRegionSelected;
    public event Action<Rectangle>? ScrollRegionSelected;
    public event Action? SelectionCancelled;
    public event Action<Models.RecordingFormat>? RecordingRequested;

    /// <summary>Active capture tool mode on the overlay (may differ from the launch mode).</summary>
    public CaptureMode ActiveMode => _mode;

    public RegionOverlayForm(Bitmap screenshot, Rectangle virtualBounds,
        CaptureMode initialMode = CaptureMode.Rectangle,
        WindowDetectionMode windowDetectionMode = WindowDetectionMode.WindowOnly,
        CenterSelectionAspectRatio centerSelectionAspectRatio = CenterSelectionAspectRatio.Free)
    {
        CyberSnap.UI.Theme.Refresh();
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;
        _windowDetectionMode = windowDetectionMode;
        _centerSelectionAspectRatio = centerSelectionAspectRatio;
        _bmpW = _screenshot.Width;
        _bmpH = _screenshot.Height;
        _mode = initialMode;
        _activeToolId = ToolDef.AllTools.FirstOrDefault(t => t.Mode == _mode)?.Id;
        _showTime = DateTime.UtcNow;
        LoadTextStyleFromSettings();

        // Magnifier bitmap for color picker
        _magBitmap = new Bitmap(PW, PH, PixelFormat.Format32bppArgb);
        _magPixels = new int[PW * PH];
        _magGfx = Graphics.FromImage(_magBitmap);
        _magGfx.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        SetupForm();

        // Seed auto-detect before the form is ever shown so the first paint already has
        // dim + window hole together. No Opacity tricks (those caused a white flash).
        PrimeSelectionDimFromCursor();

        CalcToolbar();

        _animTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _animTimer.Tick += (_, _) =>
        {
            if (UI.Motion.Disabled)
            {
                _toolbarAnim = 1f;
            }
            else
            {
                float elapsed = (float)(DateTime.UtcNow - _showTime).TotalMilliseconds;
                _toolbarAnim = EaseOutCubic(Math.Min(1f, elapsed / 180f));
            }
            UpdateToolbarSurfaceOnly();
            Invalidate(new Rectangle(_toolbarRect.X - 12, _toolbarRect.Y - 48,
                _toolbarRect.Width + 24, _toolbarRect.Height + 160));
            if (_toolbarAnim >= 1f)
            {
                _animTimer.Stop();
            }
        };

        _pickerTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _pickerTimer.Tick += OnPickerTick;
        if (_mode == CaptureMode.ColorPicker) _pickerTimer.Start();

        _centerMoveGripAnimTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _centerMoveGripAnimTimer.Tick += (s, e) => {
            float step = 0.1f; // ~160ms transition
            if (_centerMoveGripOpacity < _centerMoveGripTargetOpacity)
            {
                _centerMoveGripOpacity = Math.Min(_centerMoveGripTargetOpacity, _centerMoveGripOpacity + step);
                InvalidateCenterGrip();
            }
            else if (_centerMoveGripOpacity > _centerMoveGripTargetOpacity)
            {
                _centerMoveGripOpacity = Math.Max(_centerMoveGripTargetOpacity, _centerMoveGripOpacity - step);
                InvalidateCenterGrip();
            }
            else
            {
                _centerMoveGripAnimTimer.Stop();
            }
        };
        InitHoverHoldTimer();

        _autoDetectTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _autoDetectTimer.Tick += (_, _) =>
        {
            _autoDetectTimer.Stop();
            if (_isSelecting || !ToolDef.IsCaptureTool(_mode) || _mode == CaptureMode.Center || IsPointInOverlayUi(_pendingAutoDetectPoint))
                return;

            UpdateAutoDetectRect(_pendingAutoDetectPoint);
        };

        _selectionPaintTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _selectionPaintTimer.Tick += (_, _) => FlushSelectionPaint();

        _confirmPressTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _confirmPressTimer.Tick += (_, _) => ConfirmPressTick();

        _confirmShineTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
        _confirmShineTimer.Tick += (_, _) => ConfirmShineTick();

        _currentOverlay = this;
    }

    public static bool TrySwitchCurrentOverlayMode(CaptureMode mode)
    {
        if (!IsOverlaySwitchableMode(mode))
            return false;

        var overlay = _currentOverlay;
        if (overlay is null || overlay.IsDisposed || overlay.Disposing)
            return false;
        try
        {
            if (overlay.InvokeRequired)
                overlay.BeginInvoke(new Action(() => overlay.SwitchModeFromHotkey(mode)));
            else
                overlay.SwitchModeFromHotkey(mode);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private int[] GetPixelData()
    {
        if (_pixelData != null)
            return _pixelData;

        var pixelData = new int[_bmpW * _bmpH];
        var bits = _screenshot.LockBits(new Rectangle(0, 0, _bmpW, _bmpH),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bits.Scan0, pixelData, 0, pixelData.Length);
        }
        finally
        {
            _screenshot.UnlockBits(bits);
        }

        _pixelData = pixelData;
        return pixelData;
    }

    private static float EaseOutCubic(float t)
        => 1f - (float)Math.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    private void SetupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = string.Empty;
        Bounds = new Rectangle(_virtualBounds.X, _virtualBounds.Y,
            _virtualBounds.Width, _virtualBounds.Height);
        MinimumSize = Size.Empty;
        MinimizeBox = false;
        MaximizeBox = false;
        Cursor = _mode == CaptureMode.ColorPicker
            ? CursorFactory.EyedropperCursor
            : CursorFactory.PrecisionCursor;
        // Match the screenshot fill path so any pre-paint erase is not a bright flash.
        BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Opaque |
                 ControlStyles.StandardDoubleClick |
                 ControlStyles.UserMouse, true);
        KeyPreview = true;
    }

    /// <summary>
    /// Never erase to the default control white. Until OnPaint runs, show the screenshot
    /// (or black) so activation never flashes a bright empty frame.
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        try
        {
            if (_screenshot is not null)
            {
                var g = e.Graphics;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
                var clip = e.ClipRectangle;
                g.DrawImage(_screenshot, clip, clip, GraphicsUnit.Pixel);
                return;
            }
        }
        catch
        {
            // Fall through to solid fill.
        }

        e.Graphics.Clear(Color.Black);
    }

    private static int GroupGap => UiChrome.ScaledToolbarGroupGap; // spacing between tool groups (includes separator line)

    // Separator indices (computed dynamically based on visible tools)
    private int[] _sepAfter = Array.Empty<int>();

    private void CalcToolbar()
    {
        int pad = UiChrome.ScaledToolbarInnerPadding;
        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int buttonSpacing = UiChrome.ScaledToolbarButtonSpacing;
        Point? cursorScreenPoint = null;
        try
        {
            var cursorPos = System.Windows.Forms.Cursor.Position;
            if (_virtualBounds.Contains(cursorPos))
                cursorScreenPoint = cursorPos;
        }
        catch { }

        Rectangle[] screenWorkingAreas = GetScreenWorkingAreas();
        _toolbarAnchorArea = ToolbarLayout.ResolveToolbarAnchorArea(
            _virtualBounds,
            cursorScreenPoint,
            _toolbarAnchorArea,
            screenWorkingAreas);
        Rectangle screenBounds = _toolbarAnchorArea.IsEmpty ? _virtualBounds : _toolbarAnchorArea;

        BuildToolbarToolSplit(screenBounds, buttonSize, buttonSpacing, pad);
        _sepAfter = Array.Empty<int>(); // dividers drawn manually inside PaintToolbar

        // Pre-confirm: capture tools + Move/Close.
        // Confirm: annotation-only dock (no capture tools) — single row/column.
        if (ShowAnnotationChrome)
        {
            CalcAnnotationOnlyToolbar(screenBounds, pad, buttonSize, buttonSpacing);
            return;
        }

        CalcCaptureOnlyToolbar(screenBounds, pad, buttonSize, buttonSpacing);
    }

    /// <summary>
    /// Capture-phase dock: Group 0 tools + Move/Close. No color, stroke, or annotation flyout.
    /// </summary>
    private void CalcCaptureOnlyToolbar(Rectangle screenBounds, int pad, int buttonSize, int buttonSpacing)
    {
        const int tier1UtilityCount = 2; // pos + close
        // Gaps: after capture-tool group + before Move/Close (must match layout below)
        const int tier1SepCount = 2;

        int activatorW = UiChrome.ScaleInt(14);
        // Match tool-button height so the kebab is easy to hit and the dots can spread vertically.
        int activatorH = buttonSize;
        int activatorSpacing = buttonSpacing;

        int tier1PrimarySpan = GetToolbarPrimarySpan(_mainBarTools.Length + tier1UtilityCount, tier1SepCount, buttonSize, buttonSpacing, pad);
        if (!IsVerticalDock)
            tier1PrimarySpan += activatorW + activatorSpacing;

        int w, h;
        int brandWidth = 0;
        int gripSize = UiChrome.ScaleInt(8);
        int gripGap = UiChrome.ScaleInt(4);
        int gripLen = UiChrome.ScaleInt(16);
        int gripToContentGap = UiChrome.ScaleInt(10);

        if (IsVerticalDock)
        {
            w = pad * 2 + buttonSize;
            h = tier1PrimarySpan + gripSize + gripToContentGap;
        }
        else
        {
            int logoSize = UiChrome.ScaleInt(10);
            int textWidth = UiChrome.ScaleInt(60);
            bool canShowText = _mainBarTools.Length >= 6;
            brandWidth = canShowText
                ? logoSize + textWidth + UiChrome.ScaleInt(24)
                : logoSize + UiChrome.ScaleInt(16);
            w = tier1PrimarySpan + brandWidth + gripSize + gripToContentGap;
            h = pad * 2 + buttonSize;
        }

        AllocateToolbarButtonMetadata();

        _toolbarRect = ToolbarLayout.GetToolbarRect(
            _virtualBounds, screenBounds, w, h, CaptureDockSide, UiChrome.ScaledToolbarTopMargin);
        if (!_toolbarCustomOffset.IsEmpty)
            _toolbarRect.Offset(_toolbarCustomOffset);

        var tier1Group = new[] { "rect", "center", "scroll", "recordGif", "record" };
        int sepIdx = -1;
        for (int i = 0; i < _mainBarTools.Length; i++)
        {
            if (tier1Group.Contains(_mainBarTools[i].Id))
                sepIdx = i;
        }

        _toolbarButtons[StrokeWidthButtonIndex] = Rectangle.Empty;
        _toolbarButtons[ColorButtonIndex] = Rectangle.Empty;

        _annotationGripRect = Rectangle.Empty;

        if (IsVerticalDock)
        {
            _captureGripRect = new Rectangle(_toolbarRect.X + (_toolbarRect.Width - gripLen) / 2, _toolbarRect.Y + pad, gripLen, gripSize);
            int col1Height = GetToolbarPrimarySpan(_mainBarTools.Length + tier1UtilityCount, tier1SepCount, buttonSize, buttonSpacing, 0);
            int col1StartY = _toolbarRect.Y + pad + gripSize + gripToContentGap + (_toolbarRect.Height - pad * 2 - gripSize - gripToContentGap - col1Height) / 2;
            int col1X = _toolbarRect.X + pad;
            _brandRect = new Rectangle(col1X, _toolbarRect.Y + pad + gripSize + gripToContentGap, buttonSize, col1StartY - (_toolbarRect.Y + pad + gripSize + gripToContentGap));
            int actY = _toolbarRect.Y + (_toolbarRect.Height - activatorH) / 2;
            _menuActivatorRect = new Rectangle(_toolbarRect.Right - pad - activatorW, actY, activatorW, activatorH);

            int cy = col1StartY;
            for (int i = 0; i < _mainBarTools.Length; i++)
            {
                _toolbarButtons[i] = new Rectangle(col1X, cy, buttonSize, buttonSize);
                cy += buttonSize + buttonSpacing;
                if (i == sepIdx)
                    cy += GroupGap;
            }
            cy += GroupGap;
            _toolbarButtons[PositionButtonIndex] = new Rectangle(col1X, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[CloseButtonIndex] = new Rectangle(col1X, cy, buttonSize, buttonSize);
        }
        else
        {
            int row1Y = _toolbarRect.Y + (h - buttonSize) / 2;
            _captureGripRect = new Rectangle(_toolbarRect.X + pad, row1Y + (buttonSize - gripLen) / 2, gripSize, gripLen);
            _brandRect = new Rectangle(_toolbarRect.X + pad + gripSize + gripToContentGap, row1Y, brandWidth, buttonSize);

            int row1Width = GetToolbarPrimarySpan(_mainBarTools.Length + tier1UtilityCount, tier1SepCount, buttonSize, buttonSpacing, 0);
            int row1StartX = _toolbarRect.X + pad + gripSize + gripToContentGap + (_toolbarRect.Width - pad * 2 - gripSize - gripToContentGap - row1Width - activatorW - activatorSpacing) / 2;
            if (row1StartX < _brandRect.Right)
                row1StartX = _brandRect.Right;
            int actY = _toolbarRect.Y + (_toolbarRect.Height - activatorH) / 2;
            _menuActivatorRect = new Rectangle(_toolbarRect.Right - pad - activatorW, actY, activatorW, activatorH);

            int cx = row1StartX;
            for (int i = 0; i < _mainBarTools.Length; i++)
            {
                _toolbarButtons[i] = new Rectangle(cx, row1Y, buttonSize, buttonSize);
                cx += buttonSize + buttonSpacing;
                if (i == sepIdx)
                    cx += GroupGap;
            }
            cx += GroupGap;
            _toolbarButtons[PositionButtonIndex] = new Rectangle(cx, row1Y, buttonSize, buttonSize);
            cx += buttonSize + buttonSpacing;
            _toolbarButtons[CloseButtonIndex] = new Rectangle(cx, row1Y, buttonSize, buttonSize);
        }
    }

    /// <summary>
    /// Confirm-phase dock: vertical annotation column anchored to the capture frame
    /// (prefer right edge, flip to left when there is no room). No Position/Close.
    /// </summary>
    private void CalcAnnotationOnlyToolbar(Rectangle screenBounds, int pad, int buttonSize, int buttonSpacing)
    {
        var annotSepIndices = GetAnnotationGroupSepFlyoutIndices();
        int annotSepCount = annotSepIndices.Count;
        // Gaps: annotation group seps + before stroke/color. Position/Close are omitted.
        int sepCount = annotSepCount + 1;
        int buttonCount = _flyoutTools.Length + 2; // annot + stroke + color

        int activatorW = buttonSize;
        int activatorH = UiChrome.ScaleInt(14);
        int toolsSpan = GetToolbarPrimarySpan(buttonCount, sepCount, buttonSize, buttonSpacing, 0);
        // Logo strip above the first tool (compact; no horizontal brand text).
        int brandStripH = UiChrome.ScaleInt(22);
        int gapBrandToTools = UiChrome.ScaleInt(4);
        int gapToolsToActivator = buttonSpacing;

        int gripH = UiChrome.ScaleInt(8);
        int gripGap = UiChrome.ScaleInt(4);
        int gripToContentGap = UiChrome.ScaleInt(10);

        int w = pad * 2 + buttonSize;
        int h = pad + gripH + gripToContentGap + brandStripH + gapBrandToTools + toolsSpan + gapToolsToActivator + activatorH + pad;

        AllocateToolbarButtonMetadata();

        // Capture tools + chrome buttons not on this dock.
        for (int i = 0; i < _mainBarTools.Length; i++)
            _toolbarButtons[i] = Rectangle.Empty;
        _toolbarButtons[PositionButtonIndex] = Rectangle.Empty;
        _toolbarButtons[CloseButtonIndex] = Rectangle.Empty;

        int frameGap = UiChrome.ScaleInt(10);
        var frame = _confirmRect;
        if (frame.Width <= 0 || frame.Height <= 0)
            frame = screenBounds;

        // Prefer the selection monitor so the column stays on the same display as the crop.
        var clampBounds = !_selectionMonitorClientBounds.IsEmpty
            ? _selectionMonitorClientBounds
            : GetMonitorClientBoundsAtClientPoint(new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2));
        if (clampBounds.IsEmpty)
            clampBounds = new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));

        int edgePad = UiChrome.ScaleInt(4);
        // Escuadra: bottom-align with the capture frame so the annotation column and the
        // destination pills meet at the bottom corner (L-shape), not hanging past the frame.
        int y = frame.Bottom - h;
        int xRight = frame.Right + frameGap;
        int xLeft = frame.Left - frameGap - w;

        bool fitsRight = xRight + w <= clampBounds.Right - edgePad;
        bool fitsLeft = xLeft >= clampBounds.Left + edgePad;

        if (fitsRight || (!fitsLeft && xRight >= clampBounds.Left))
        {
            _annotationFrameDockSide = CaptureDockSide.Right;
            _toolbarRect = new Rectangle(xRight, y, w, h);
        }
        else
        {
            _annotationFrameDockSide = CaptureDockSide.Left;
            _toolbarRect = new Rectangle(xLeft, y, w, h);
        }

        // Keep the whole column on the clamp monitor; bottom-flush with the frame (escuadra).
        int maxX = Math.Max(clampBounds.Left + edgePad, clampBounds.Right - w - edgePad);
        int maxY = Math.Max(clampBounds.Top + edgePad, clampBounds.Bottom - h - edgePad);
        int preferredX = _toolbarRect.X + _toolbarCustomOffset.X;
        int preferredY = frame.Bottom - h + _toolbarCustomOffset.Y;
        _toolbarRect.X = Math.Clamp(preferredX, clampBounds.Left + edgePad, maxX);
        _toolbarRect.Y = Math.Clamp(preferredY, clampBounds.Top + edgePad, maxY);

        _captureGripRect = Rectangle.Empty;

        int drawingStartIdx = _mainBarTools.Length + 4;
        int colX = _toolbarRect.X + pad;
        int cy = _toolbarRect.Y + pad;

        int gripW = UiChrome.ScaleInt(16);
        _annotationGripRect = new Rectangle(_toolbarRect.X + (_toolbarRect.Width - gripW) / 2, cy, gripW, gripH);
        cy += gripH + gripToContentGap;

        _brandRect = new Rectangle(colX, cy, buttonSize, brandStripH);
        cy += brandStripH + gapBrandToTools;

        // Color + stroke width belong with the shape tools that consume them, so they render
        // directly above the rectangle (rectShape) button. Fall back to the top of the column
        // when the rectangle tool isn't enabled on the bar.
        int rectToolFlyoutIdx = -1;
        for (int i = 0; i < _flyoutTools.Length; i++)
        {
            if (string.Equals(_flyoutTools[i].Id, "rectShape", StringComparison.OrdinalIgnoreCase))
            {
                rectToolFlyoutIdx = i;
                break;
            }
        }

        void PlaceColorAndStroke()
        {
            _toolbarButtons[ColorButtonIndex] = new Rectangle(colX, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[StrokeWidthButtonIndex] = new Rectangle(colX, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing;
        }

        if (rectToolFlyoutIdx < 0)
            PlaceColorAndStroke();

        // Reversed layout: last tool (emoji) sits near the top, first tool (pick) ends up at the
        // bottom of the column. Group separators sit between the same tool pairs.
        for (int i = _flyoutTools.Length - 1; i >= 0; i--)
        {
            if (i == rectToolFlyoutIdx)
                PlaceColorAndStroke();

            _toolbarButtons[drawingStartIdx + i] = new Rectangle(colX, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing;
            // A separator originally sat after tool (i-1); in reverse it falls between i and i-1.
            if (annotSepIndices.Contains(i - 1))
                cy += GroupGap;
        }

        // ⋮ at the bottom of the column (full button width hit target, compact glyph height).
        int actY = Math.Min(cy, _toolbarRect.Bottom - pad - activatorH);
        _menuActivatorRect = new Rectangle(colX, actY, activatorW, activatorH);
    }

    /// <summary>Monitor Bounds (full display) containing a client point, in overlay client coords.</summary>
    private Rectangle GetMonitorClientBoundsAtClientPoint(Point clientPt)
    {
        var screenPt = new Point(_virtualBounds.X + clientPt.X, _virtualBounds.Y + clientPt.Y);
        var bounds = Screen.FromPoint(screenPt).Bounds;
        return new Rectangle(
            bounds.X - _virtualBounds.X,
            bounds.Y - _virtualBounds.Y,
            bounds.Width,
            bounds.Height);
    }

    private void CaptureSelectionMonitorAt(Point clientPt)
    {
        _selectionMonitorClientBounds = GetMonitorClientBoundsAtClientPoint(clientPt);
    }

    private Point ClampPointToSelectionMonitor(Point p)
    {
        var b = _selectionMonitorClientBounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0)
            return p;
        int maxX = Math.Max(b.Left, b.Right - 1);
        int maxY = Math.Max(b.Top, b.Bottom - 1);
        return new Point(Math.Clamp(p.X, b.Left, maxX), Math.Clamp(p.Y, b.Top, maxY));
    }

    /// <summary>
    /// Keeps a rectangle fully inside the selection monitor (shrinks if larger than the monitor).
    /// Used for live selection, confirm move, and confirm resize.
    /// </summary>
    private Rectangle ClampRectToSelectionMonitor(Rectangle rect)
    {
        var b = _selectionMonitorClientBounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0 || rect.Width <= 0 || rect.Height <= 0)
            return rect;

        int w = Math.Min(rect.Width, b.Width);
        int h = Math.Min(rect.Height, b.Height);
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        int x = Math.Clamp(rect.X, b.Left, b.Right - w);
        int y = Math.Clamp(rect.Y, b.Top, b.Bottom - h);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>
    /// Moves/resizes the locked crop during confirm with a lightweight invalidation path.
    /// Destination + annotation docks are hidden for the gesture (see
    /// <see cref="HideConfirmDocksForFrameManip"/>) so only the hole/frame/size-pill update live.
    /// </summary>
    private void ApplyConfirmRectDrag(Rectangle newRect)
    {
        newRect = ClampRectToSelectionMonitor(newRect);
        if (newRect == _confirmRect)
            return;

        var oldRect = _confirmRect;
        int dx = newRect.X - oldRect.X;
        int dy = newRect.Y - oldRect.Y;
        var oldPill = _confirmSizeReadoutRect;

        _confirmRect = newRect;

        // Size chip and center move grip follow the frame; docks are hidden for the whole gesture.
        if (dx != 0 || dy != 0)
        {
            if (!_confirmSizeReadoutRect.IsEmpty)
                _confirmSizeReadoutRect.Offset(dx, dy);
            if (!_centerMoveGripRect.IsEmpty)
                _centerMoveGripRect.Offset(dx, dy);
        }

        InvalidateConfirmHoleMove(oldRect, newRect, oldPill, _confirmSizeReadoutRect);
    }

    /// <summary>
    /// Vanish destination pills + annotation toolbar while adjusting the capture frame.
    /// Clears their pixels once so nothing freezes mid-screen or glitches over the dim veil.
    /// </summary>
    private void HideConfirmDocksForFrameManip()
    {
        if (_confirmDocksHiddenForFrameManip)
            return;

        // Snapshot dock bounds to erase before we stop painting them.
        LayoutConfirmChromeRects();
        var clear = UnionConfirmChromeRects();
        if (!_confirmChromeWrapperRect.IsEmpty)
            clear = clear.IsEmpty
                ? InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad)
                : Rectangle.Union(clear, InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad));
        else if (!clear.IsEmpty)
            clear = InflateForRepaint(clear, ConfirmChromeInvalidatePad);

        if (ShowAnnotationChrome && !_toolbarRect.IsEmpty)
        {
            var tb = InflateForRepaint(_toolbarRect, UiChrome.ScaleInt(20));
            clear = clear.IsEmpty ? tb : Rectangle.Union(clear, tb);
        }

        _confirmDocksHiddenForFrameManip = true;
        _hoveredConfirmButton = -1;
        _confirmShineTimer.Stop();

        // Annotation dock first (layered), then clear destination pixels in the same tick.
        if (_toolbarForm is { IsDisposed: false })
        {
            try
            {
                _toolbarForm.SurfaceAlpha = 0;
                _toolbarForm.Hide();
            }
            catch { }
        }

        if (!clear.IsEmpty)
            Invalidate(clear);
        try { Update(); } catch { }
    }

    /// <summary>Bring both docks back after the frame gesture, re-seated to the new rect.</summary>
    private void ShowConfirmDocksAfterFrameManip()
    {
        // Layout both docks BEFORE revealing either, so they appear in the same visual tick.
        InvalidateConfirmChromeLayout();
        CalcToolbar();
        _confirmChromeLayoutDirty = true;
        LayoutConfirmChromeRects();
        RefreshConfirmSizeReadoutRect();

        _confirmDocksHiddenForFrameManip = false;

        EnsureToolbarReady();
        if (_toolbarForm is { IsDisposed: false })
        {
            try
            {
                PositionToolbarForm();
                _toolbarForm.SurfaceAlpha = 255;
                MarkToolbarRenderDirty();
                // Paint layered surface while still deciding visibility.
                _toolbarForm.UpdateSurface();
                if (!_toolbarForm.Visible)
                    _toolbarForm.Show(this);
                else
                    _toolbarForm.UpdateSurface();
            }
            catch { }
        }

        var dirty = Rectangle.Empty;
        if (!_confirmRect.IsEmpty)
            dirty = InflateForRepaint(_confirmRect, UiChrome.ScaleInt(28));
        if (!_confirmChromeWrapperRect.IsEmpty)
            dirty = dirty.IsEmpty
                ? InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad)
                : Rectangle.Union(dirty, InflateForRepaint(_confirmChromeWrapperRect, ConfirmChromeInvalidatePad));
        if (!_confirmSizeReadoutRect.IsEmpty)
            dirty = dirty.IsEmpty
                ? InflateForRepaint(_confirmSizeReadoutRect, UiChrome.ScaleInt(12))
                : Rectangle.Union(dirty, InflateForRepaint(_confirmSizeReadoutRect, UiChrome.ScaleInt(12)));
        if (ShowAnnotationChrome && !_toolbarRect.IsEmpty)
            dirty = dirty.IsEmpty
                ? InflateForRepaint(_toolbarRect, UiChrome.ScaleInt(20))
                : Rectangle.Union(dirty, InflateForRepaint(_toolbarRect, UiChrome.ScaleInt(20)));
        if (!dirty.IsEmpty)
            Invalidate(dirty);

        // Force overlay paint now so destination pills don't lag a frame behind the annotation dock.
        try { Update(); } catch { }

        if (_isConfirmingSelection && !UI.Motion.Disabled && !_confirmShineTimer.Enabled)
            _confirmShineTimer.Start();
    }

    /// <summary>
    /// Invalidates only the dim-hole strips that actually changed (XOR of old/new frames)
    /// plus size-pill bounds — not the whole chrome dock. Matches pre-confirm selection fluidness.
    /// </summary>
    private void InvalidateConfirmHoleMove(
        Rectangle oldHole,
        Rectangle newHole,
        Rectangle oldSizePill,
        Rectangle newSizePill)
    {
        int pad = UiChrome.ScaleInt(22); // frame stroke + handles
        using var region = new Region();
        region.MakeEmpty();

        if (oldHole.Width > 0 && oldHole.Height > 0)
        {
            var o = oldHole;
            o.Inflate(pad, pad);
            region.Union(o);
        }
        if (newHole.Width > 0 && newHole.Height > 0)
        {
            var n = newHole;
            n.Inflate(pad, pad);
            region.Union(n);
        }

        // Prefer XOR for the hole fill so large moves only dirty the edge strips...
        // (Union of padded frames already covers borders; XOR of unpadded holes tightens fill.)
        if (oldHole.Width > 2 && oldHole.Height > 2 && newHole.Width > 2 && newHole.Height > 2)
        {
            using var xor = new Region(oldHole);
            xor.Xor(newHole);
            region.Union(xor);
        }

        if (!oldSizePill.IsEmpty)
        {
            var p = oldSizePill;
            p.Inflate(UiChrome.ScaleInt(8), UiChrome.ScaleInt(8));
            region.Union(p);
        }
        if (!newSizePill.IsEmpty)
        {
            var p = newSizePill;
            p.Inflate(UiChrome.ScaleInt(8), UiChrome.ScaleInt(8));
            region.Union(p);
        }

        region.Intersect(ClientRectangle);
        Invalidate(region);
    }

    private void PresentAnnotationToolbarNow()
    {
        _lastConfirmDragToolbarPresentUtc = DateTime.UtcNow;
        PositionToolbarForm();
        UpdateToolbarSurfaceOnly();
    }

    /// <summary>Restore docks after frame drag/resize ends.</summary>
    private void EndConfirmFrameDragVisuals()
    {
        ShowConfirmDocksAfterFrameManip();
    }

    private void OffsetConfirmChromeBy(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;
        for (int i = 0; i < _confirmChromeRects.Length; i++)
        {
            if (_confirmChromeRects[i].Width > 0)
                _confirmChromeRects[i].Offset(dx, dy);
        }
        if (!_confirmChromeWrapperRect.IsEmpty)
            _confirmChromeWrapperRect.Offset(dx, dy);
        if (!_confirmChromeSeparatorRect1.IsEmpty)
            _confirmChromeSeparatorRect1.Offset(dx, dy);
        if (!_confirmChromeSeparatorRect2.IsEmpty)
            _confirmChromeSeparatorRect2.Offset(dx, dy);
    }

    private void OffsetAnnotationToolbarBy(int dx, int dy)
    {
        if (!ShowAnnotationChrome || (dx == 0 && dy == 0))
            return;

        if (!_toolbarRect.IsEmpty)
            _toolbarRect.Offset(dx, dy);
        if (!_brandRect.IsEmpty)
            _brandRect.Offset(dx, dy);
        if (!_logoRect.IsEmpty)
            _logoRect.Offset(dx, dy);
        if (!_menuActivatorRect.IsEmpty)
            _menuActivatorRect.Offset(dx, dy);

        for (int i = 0; i < _toolbarButtons.Length; i++)
        {
            if (_toolbarButtons[i].Width > 0)
                _toolbarButtons[i].Offset(dx, dy);
        }
    }

    private bool AnnotationToolbarNeedsRelayout()
    {
        if (!ShowAnnotationChrome || _toolbarRect.IsEmpty || _confirmRect.IsEmpty)
            return false;

        int gap = UiChrome.ScaleInt(10);
        int edgePad = UiChrome.ScaleInt(4);
        var clamp = !_selectionMonitorClientBounds.IsEmpty
            ? _selectionMonitorClientBounds
            : GetMonitorClientBoundsAtClientPoint(new Point(
                _confirmRect.X + _confirmRect.Width / 2,
                _confirmRect.Y + _confirmRect.Height / 2));

        // Off the monitor — must re-seat.
        if (_toolbarRect.Left < clamp.Left + edgePad - 2
            || _toolbarRect.Top < clamp.Top + edgePad - 2
            || _toolbarRect.Right > clamp.Right - edgePad + 2
            || _toolbarRect.Bottom > clamp.Bottom - edgePad + 2)
            return true;

        // Horizontal seat relative to the frame (allow 2px slack after integer offset).
        if (_annotationFrameDockSide == CaptureDockSide.Right)
        {
            int expectedX = _confirmRect.Right + gap;
            if (Math.Abs(_toolbarRect.X - expectedX) > 2)
                return true;
        }
        else if (_annotationFrameDockSide == CaptureDockSide.Left)
        {
            int expectedX = _confirmRect.Left - gap - _toolbarRect.Width;
            if (Math.Abs(_toolbarRect.X - expectedX) > 2)
                return true;
        }

        // Escuadra: bottom of the column flush with the frame bottom (when it fits).
        int idealY = _confirmRect.Bottom - _toolbarRect.Height;
        if (idealY >= clamp.Top + edgePad
            && Math.Abs(_toolbarRect.Bottom - _confirmRect.Bottom) > 2)
            return true;

        return false;
    }

    private void RefreshConfirmSizeReadoutRect()
    {
        if (!_isConfirmingSelection || _confirmRect.Width <= 2)
        {
            _confirmSizeReadoutRect = Rectangle.Empty;
            _confirmSizeReadoutGripRect = Rectangle.Empty;
            // Erase any glow ghost before clearing the field.
            InvalidateCenterGripArea(_centerMoveGripRect);
            _centerMoveGripRect = Rectangle.Empty;
            return;
        }

        // The drag grip on the size pill is always shown in confirm mode — it lets the user
        // reposition the frame regardless of whether any annotations have been drawn.
        const bool showGrip = true;

        _confirmSizeReadoutRect = SelectionSizeReadout.GetConfirmDragPillBounds(
            _confirmRect,
            _readoutFont,
            ClientRectangle,
            GetConfirmReadoutAvoidRects(),
            showGrip);

        _confirmSizeReadoutGripRect = SelectionSizeReadout.GetConfirmDragGripBounds(
            _confirmRect,
            _readoutFont,
            ClientRectangle,
            GetConfirmReadoutAvoidRects(),
            showGrip);

        // Center badge complements the grip: show it only when there are no annotations
        // (clean canvas) so it offers a large drag target in the middle of an empty selection.
        bool isPickOrNonAnnot = _mode == CaptureMode.Move || !ToolDef.IsAnnotationTool(_mode);
        if (!HasConfirmAnnotations() && isPickOrNonAnnot && !_confirmRect.IsEmpty)
        {
            int d = UiChrome.ScaleInt(36);
            _centerMoveGripRect = new Rectangle(
                _confirmRect.X + (_confirmRect.Width - d) / 2,
                _confirmRect.Y + (_confirmRect.Height - d) / 2,
                d, d);
        }
        else
        {
            // Erase any glow ghost before hiding the badge.
            InvalidateCenterGripArea(_centerMoveGripRect);
            _centerMoveGripRect = Rectangle.Empty;
        }
    }

    private bool HitTestConfirmSizeReadout(Point p)
        => !_confirmSizeReadoutGripRect.IsEmpty && _confirmSizeReadoutGripRect.Contains(p);

    private void InvalidateCenterGrip()
    {
        if (!_centerMoveGripRect.IsEmpty)
            InvalidateCenterGripArea(_centerMoveGripRect);
    }

    /// <summary>
    /// Dirties the area occupied by the center-move grip badge including its glow halo.
    /// Pass the OLD rect explicitly when the field is about to be cleared, so the ghost is erased.
    /// </summary>
    private void InvalidateCenterGripArea(Rectangle r)
    {
        if (!r.IsEmpty)
            // 16px covers the glow halo (up to ~9px scaled) plus stroke/AA fringe.
            Invalidate(InflateForRepaint(r, UiChrome.ScaleInt(16)));
    }

    private void BeginConfirmFrameDrag(Point location)
    {
        if (_captureMagnifierForm is { Visible: true })
            HideCaptureMagnifier();
        _isConfirmDragging = true;
        _confirmHandleDragIndex = -1;
        _confirmDragStart = location;
        _confirmDragOffset = new Point(location.X - _confirmRect.X, location.Y - _confirmRect.Y);
        _confirmDragStartRect = _confirmRect;
        Cursor = CursorFactory.GrabbingCursor;
        HideToolbarTooltip();
        HideConfirmDocksForFrameManip();
    }

    private int GetFirstVisibleToolbarButtonX()
    {
        int best = int.MaxValue;
        for (int i = 0; i < _toolbarButtons.Length; i++)
        {
            var b = _toolbarButtons[i];
            if (b.Width <= 0 || b.Height <= 0) continue;
            if (b.X < best) best = b.X;
        }
        return best == int.MaxValue ? -1 : best;
    }

    private HashSet<int> GetAnnotationGroupSepFlyoutIndices()
    {
        // Separators after edit tools and after shape tools (ids may be merge primaries).
        var groups = new[] {
            new[] { "select", "eraser", "highlight", "blur" },
            new[] { "rectShape", "circleShape" }
        };
        var seps = new HashSet<int>();
        foreach (var group in groups)
        {
            int lastIdx = -1;
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                if (group.Contains(_flyoutTools[i].Id, StringComparer.OrdinalIgnoreCase))
                    lastIdx = i;
            }
            if (lastIdx >= 0)
                seps.Add(lastIdx);
        }
        return seps;
    }

    private void AllocateToolbarButtonMetadata()
    {
        _toolbarButtons = new Rectangle[BtnCount];
        _toolbarIcons = new string[BtnCount];
        _toolbarLabels = new string[BtnCount];
        _toolbarToolIds = new string[BtnCount];
        _toolbarModes = new CaptureMode?[BtnCount];
        _annotationMergeAltsByButton.Clear();

        for (int i = 0; i < _mainBarTools.Length; i++)
        {
            _toolbarIcons[i] = _mainBarTools[i].Id switch { "crop" => "rect", "rect" => "captureRect", var id => id };
            _toolbarLabels[i] = LocalizationService.Translate(_mainBarTools[i].Label);
            _toolbarToolIds[i] = _mainBarTools[i].Id;
            _toolbarModes[i] = _mainBarTools[i].Mode;
        }

        int swIdx = StrokeWidthButtonIndex;
        _toolbarIcons[swIdx] = "strokeWidth";
        _toolbarLabels[swIdx] = LocalizationService.Translate("Shape stroke width");
        _toolbarToolIds[swIdx] = "strokeWidth";
        _toolbarModes[swIdx] = null;

        int colorIdx = ColorButtonIndex;
        _toolbarIcons[colorIdx] = "color";
        _toolbarLabels[colorIdx] = LocalizationService.Translate("Active drawing and text color");
        _toolbarToolIds[colorIdx] = "color";
        _toolbarModes[colorIdx] = null;

        int positionIdx = PositionButtonIndex;
        _toolbarIcons[positionIdx] = "position";
        _toolbarLabels[positionIdx] = LocalizationService.Translate("Toolbar Position");
        _toolbarToolIds[positionIdx] = "position";
        _toolbarModes[positionIdx] = null;

        int closeIdx = CloseButtonIndex;
        _toolbarIcons[closeIdx] = "signOut";
        _toolbarLabels[closeIdx] = LocalizationService.Translate("Cancel");
        _toolbarToolIds[closeIdx] = "close";
        _toolbarModes[closeIdx] = null;

        int drawingStartIdx = _mainBarTools.Length + 4;
        for (int i = 0; i < _flyoutTools.Length; i++)
        {
            int btnIdx = drawingStartIdx + i;
            _toolbarIcons[btnIdx] = _flyoutTools[i].Id;
            _toolbarLabels[btnIdx] = LocalizationService.Translate(_flyoutTools[i].Label);
            _toolbarToolIds[btnIdx] = _flyoutTools[i].Id;
            _toolbarModes[btnIdx] = _flyoutTools[i].Mode;

            if (_annotationMergeAltsByPrimaryId.TryGetValue(_flyoutTools[i].Id, out var alts)
                && alts.Length > 0)
            {
                _annotationMergeAltsByButton[btnIdx] = alts;
            }
        }
    }

    private void BuildToolbarToolSplit(Rectangle screenBounds, int buttonSize, int buttonSpacing, int pad)
    {
        var flyoutIds = ToolDef.FlyoutToolIds();
        var mainBarTools = _visibleTools.Where(t => !flyoutIds.Contains(t.Id)).ToList();
        var flyoutTools = _visibleTools.Where(t => flyoutIds.Contains(t.Id)).ToList();

        var rectTool = mainBarTools.FirstOrDefault(t => t.Id == "rect");
        var centerTool = mainBarTools.FirstOrDefault(t => t.Id == "center");

        _mergedCaptureButtonIndex = -1;
        if (rectTool != null && centerTool != null)
        {
            var settings = Services.SettingsService.LoadStatic();
            var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;

            if (defaultMode == CaptureMode.Center)
            {
                mainBarTools.Remove(rectTool);
            }
            else
            {
                mainBarTools.Remove(centerTool);
            }
        }

        flyoutTools = CollapseAnnotationMergeGroups(flyoutTools);

        _mainBarTools = mainBarTools.ToArray();
        _flyoutTools = flyoutTools.ToArray();

        for (int i = 0; i < _mainBarTools.Length; i++)
        {
            if (_mainBarTools[i].Id == "rect" || _mainBarTools[i].Id == "center")
            {
                _mergedCaptureButtonIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Collapses each annotation merge group into one bar slot. The preferred tool is the
    /// currently active annotation tool (if in the group), else LastAnnotationToolId, else
    /// the first enabled member in group definition order. Siblings become hold-to-switch alts.
    /// </summary>
    private List<ToolDef> CollapseAnnotationMergeGroups(List<ToolDef> flyoutTools)
    {
        _annotationMergeAltsByPrimaryId.Clear();
        if (flyoutTools.Count == 0)
            return flyoutTools;

        var settings = Services.SettingsService.LoadStatic();
        var lastId = settings?.LastAnnotationToolId;
        string? preferredId = null;
        if (!string.IsNullOrEmpty(_activeToolId)
            && ToolDef.AllTools.Any(t => t.Group == 1
                && string.Equals(t.Id, _activeToolId, StringComparison.OrdinalIgnoreCase)))
        {
            preferredId = _activeToolId;
        }
        else if (!string.IsNullOrWhiteSpace(lastId))
        {
            preferredId = lastId;
        }

        var byId = flyoutTools.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ToolDef>(flyoutTools.Count);

        foreach (var tool in flyoutTools)
        {
            if (consumed.Contains(tool.Id))
                continue;

            var group = FindAnnotationMergeGroup(tool.Id);
            if (group is null)
            {
                result.Add(tool);
                continue;
            }

            var enabled = new List<ToolDef>();
            foreach (var id in group)
            {
                if (byId.TryGetValue(id, out var member))
                    enabled.Add(member);
            }

            if (enabled.Count <= 1)
            {
                result.Add(tool);
                foreach (var m in enabled)
                    consumed.Add(m.Id);
                continue;
            }

            ToolDef? primary = null;
            if (!string.IsNullOrEmpty(preferredId))
            {
                primary = enabled.FirstOrDefault(t =>
                    string.Equals(t.Id, preferredId, StringComparison.OrdinalIgnoreCase));
            }
            if (primary is null)
            {
                foreach (var id in group)
                {
                    primary = enabled.FirstOrDefault(t =>
                        string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (primary is not null)
                        break;
                }
            }
            primary ??= enabled[0];

            result.Add(primary);
            var alts = enabled
                .Where(t => !string.Equals(t.Id, primary.Id, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Id)
                .ToArray();
            if (alts.Length > 0)
                _annotationMergeAltsByPrimaryId[primary.Id] = alts;

            foreach (var m in enabled)
                consumed.Add(m.Id);
        }

        return result;
    }

    private static string[]? FindAnnotationMergeGroup(string toolId)
    {
        foreach (var group in AnnotationMergeGroups)
        {
            if (group.Any(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase)))
                return group;
        }
        return null;
    }

    private bool IsAnnotationMergeButton(int buttonIndex) =>
        _annotationMergeAltsByButton.ContainsKey(buttonIndex);

    private bool IsMergedHoldButton(int buttonIndex) =>
        buttonIndex == _mergedCaptureButtonIndex || IsAnnotationMergeButton(buttonIndex);

    private void BeginMergedButtonHold(int buttonIndex)
    {
        _mergedHoldButtonIndex = buttonIndex;
        _isMouseDownOnCaptureBtn = true;
        _mouseDownStartTime = DateTime.UtcNow;
        HideToolbarTooltip();
        DismissQuickStartGuide();
        InvalidateToolbarArea();
    }

    private void CloseAltToolPopup(bool invalidate = true)
    {
        if (!_altCapturePopupOpen && _mergedHoldButtonIndex < 0 && _altPopupSlots.Count == 0)
            return;

        _altCapturePopupOpen = false;
        _hoveredAltCaptureBtn = false;
        _hoveredAltSlotIndex = -1;
        _mergedHoldButtonIndex = -1;
        _altPopupSlots.Clear();
        _altCaptureButtonRect = Rectangle.Empty;
        if (invalidate)
            InvalidateToolbarArea();
    }

    private void SelectAltPopupTool(string toolId)
    {
        var targetTool = ToolDef.AllTools.FirstOrDefault(t =>
            string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (targetTool is null)
        {
            CloseAltToolPopup();
            return;
        }

        // Capture Area ↔ From Center: persist default capture mode (same as before).
        if (targetTool.Mode is CaptureMode.Rectangle or CaptureMode.Center)
            DefaultCaptureModeChanged?.Invoke(targetTool.Mode.Value);

        SetTool(targetTool);
        // SetTool → RefreshToolbar rebuilds merge primaries so the chosen tool becomes the slot.
        CloseAltToolPopup();
    }

    private bool IsPointInAltToolPopup(Point location)
    {
        if (!_altCapturePopupOpen)
            return false;
        if (!_altCaptureButtonRect.IsEmpty && _altCaptureButtonRect.Contains(location))
            return true;
        return GetAltPopupSlotAt(location) >= 0;
    }

    /// <summary>
    /// Click (or release) on an open hold-to-switch alt slot, or dismiss when clicking away.
    /// Returns true when the event was fully handled.
    /// </summary>
    private bool TryHandleAltToolPopupClick(Point location)
    {
        if (!_altCapturePopupOpen)
            return false;

        int altSlot = GetAltPopupSlotAt(location);
        if (altSlot >= 0 && altSlot < _altPopupSlots.Count)
        {
            SelectAltPopupTool(_altPopupSlots[altSlot].ToolId);
            return true;
        }

        // Click outside the alt slots (and not on the primary held button) dismisses the popup.
        int btnUnder = GetToolbarButtonAt(location);
        if (btnUnder == _mergedHoldButtonIndex
            || (btnUnder == _mergedCaptureButtonIndex && _mergedCaptureButtonIndex >= 0))
        {
            return false; // let primary button handlers run (re-hold / short press)
        }

        CloseAltToolPopup();
        return true;
    }

    private static int[] GetToolbarSeparatorIndices(IReadOnlyList<ToolDef> mainBarTools, bool hasMore)
    {
        var gaps = new List<int>();
        for (int i = 0; i < mainBarTools.Count - 1; i++)
        {
            if (mainBarTools[i].Group != mainBarTools[i + 1].Group)
                gaps.Add(i);
        }

        if (mainBarTools.Count > 0)
            gaps.Add(mainBarTools.Count - 1 + (hasMore ? 1 : 0));

        return gaps.ToArray();
    }

    private int GetToolbarPrimarySpan(int buttonCount, int separatorsCount, int buttonSize, int buttonSpacing, int pad)
    {
        return pad * 2
            + buttonCount * buttonSize
            + (buttonCount - 1) * buttonSpacing
            + separatorsCount * GroupGap;
    }

    public bool CheckEvasion(Point cursor)
    {
        bool wasEvading = _isEvading;
        float cy = cursor.Y - _virtualBounds.Y;
        float cx = cursor.X - _virtualBounds.X;
        float h = _virtualBounds.Height;
        float w = _virtualBounds.Width;

        if (CaptureDockSide == CaptureDockSide.Top)
        {
            if (cy < h * 0.4f) _isEvading = true;
            else if (cy > h * 0.5f) _isEvading = false;
        }
        else if (CaptureDockSide == CaptureDockSide.Bottom)
        {
            if (cy > h * 0.6f) _isEvading = true;
            else if (cy < h * 0.5f) _isEvading = false;
        }
        else if (CaptureDockSide == CaptureDockSide.Left)
        {
            if (cx < w * 0.4f) _isEvading = true;
            else if (cx > w * 0.5f) _isEvading = false;
        }
        else if (CaptureDockSide == CaptureDockSide.Right)
        {
            if (cx > w * 0.6f) _isEvading = true;
            else if (cx < w * 0.5f) _isEvading = false;
        }

        if (_isEvading != wasEvading)
        {
            CalcToolbar();
            PositionToolbarForm();
            return true;
        }
        return false;
    }

    public void ToggleToolbarPosition()
    {
        CaptureDockSide = CaptureDockSide == CaptureDockSide.Top ? CaptureDockSide.Bottom : CaptureDockSide.Top;
        DockSideChanged?.Invoke(CaptureDockSide);
        _toolbarCustomOffset = Point.Empty;
        var oldUiBounds = _lastOverlayUiBounds;
        CalcToolbar();
        PositionToolbarForm();
        MarkToolbarRenderDirty();
        _toolbarForm?.UpdateSurface();

        var newUiBounds = GetOverlayUiBounds();
        _lastOverlayUiBounds = newUiBounds;
        if (!oldUiBounds.IsEmpty && !newUiBounds.IsEmpty)
            Invalidate(Rectangle.Union(InflateForRepaint(oldUiBounds, 20), InflateForRepaint(newUiBounds, 20)));
        else if (!newUiBounds.IsEmpty)
            Invalidate(InflateForRepaint(newUiBounds, 20));
    }

    private void CycleStrokeWidth()
    {
        int idx = Array.IndexOf(StrokeWidths, _strokeWidth);
        idx = (idx + 1) % StrokeWidths.Length;
        StrokeWidth = StrokeWidths[idx];
    }

    public void ResetEvasion()
    {
        if (_isEvading)
        {
            _isEvading = false;
            CalcToolbar();
            PositionToolbarForm();
        }
    }

    private static Cursor CreateBlankCursor()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        return new Cursor(bmp.GetHicon());
    }

    private static Rectangle NormRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private static GraphicsPath RRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void InitHoverHoldTimer()
    {
        if (_hoverHoldTimer != null) return;
        _hoverHoldTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _hoverHoldTimer.Tick += HoverHoldTimer_Tick;
        _hoverHoldTimer.Start();
    }

    private void HoverHoldTimer_Tick(object? sender, EventArgs e)
    {
        bool changed = false;

        // 1. Mouse hold check (0.3 seconds) + progress ring redraw while holding
        if (_isMouseDownOnCaptureBtn && _mouseDownStartTime != DateTime.MinValue)
        {
            // Repaint so the hold progress arc on the merged button advances.
            if (!_altCapturePopupOpen)
                changed = true;

            if ((DateTime.UtcNow - _mouseDownStartTime).TotalMilliseconds >= 300)
            {
                if (!_altCapturePopupOpen && _mergedHoldButtonIndex >= 0
                    && IsMergedHoldButton(_mergedHoldButtonIndex))
                {
                    _altCapturePopupOpen = true;
                    EnsureAltPopupSlotsLaidOut();
                    // Grow ToolbarForm so multi-slot alts stay painted inside the layered surface.
                    PositionToolbarForm();
                    changed = true;
                    HideToolbarTooltip();
                }
            }
        }

        // 2. Tooltip delay and auto-hide check
        if (_isConfirmingSelection && _hoveredConfirmButton >= 0)
        {
            if (!_tooltipVisible && !_tooltipDismissed)
            {
                if (_hoverButtonStartTime != DateTime.MinValue)
                {
                    // Match toolbar discoverability (~450ms); icon-only pills need a quicker hint.
                    if ((DateTime.UtcNow - _hoverButtonStartTime).TotalMilliseconds >= 450)
                    {
                        ShowConfirmTooltip();
                    }
                }
                else
                {
                    _hoverButtonStartTime = DateTime.UtcNow;
                }
            }
            else if (_tooltipVisible)
            {
                if (_tooltipShowTime != DateTime.MinValue && (DateTime.UtcNow - _tooltipShowTime).TotalMilliseconds >= 5000)
                {
                    HideToolbarTooltip();
                }
            }
        }
        else if (IsToolbarInteractive())
        {
            if (_isMouseDownOnCaptureBtn)
            {
                if (_tooltipVisible)
                {
                    HideToolbarTooltip();
                }
                _hoverButtonStartTime = DateTime.MinValue;
            }
            else if (_hoveredButton >= 0
                     || _hoveredMenuActivator
                     || _hoveredBrand
                     || _hoveredBrandDragArea
                     || (_hoveredAltCaptureBtn && _altCapturePopupOpen))
            {
                if (!_tooltipVisible && !_tooltipDismissed)
                {
                    if (_hoverButtonStartTime != DateTime.MinValue)
                    {
                        if ((DateTime.UtcNow - _hoverButtonStartTime).TotalMilliseconds >= 500)
                        {
                            ShowToolbarTooltip();
                        }
                    }
                    else
                    {
                        _hoverButtonStartTime = DateTime.UtcNow;
                    }
                }
                else if (_tooltipVisible)
                {
                    if (_tooltipShowTime != DateTime.MinValue && (DateTime.UtcNow - _tooltipShowTime).TotalMilliseconds >= 5000)
                    {
                        HideToolbarTooltip();
                    }
                }
            }
            else
            {
                if (_tooltipVisible)
                {
                    HideToolbarTooltip();
                }
                _hoverButtonStartTime = DateTime.MinValue;
            }
        }
        else
        {
            if (_tooltipVisible)
            {
                HideToolbarTooltip();
            }
            _hoverButtonStartTime = DateTime.MinValue;
        }

        if (changed)
        {
            InvalidateToolbarArea();
        }
    }

    private void InvalidateToolbarArea()
    {
        UpdateToolbarSurfaceOnly();
        Invalidate(new Rectangle(_toolbarRect.X - 50, _toolbarRect.Y - 50, _toolbarRect.Width + 100, _toolbarRect.Height + 150));
    }

    private static int DistToRect(Point p, Rectangle r)
    {
        int dx = Math.Max(0, Math.Max(r.X - p.X, p.X - r.Right));
        int dy = Math.Max(0, Math.Max(r.Y - p.Y, p.Y - r.Bottom));
        return (int)Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Right-click menu while confirming. When <paramref name="focusedKind"/> is a destination,
    /// offers Hide (like the annotation bar). Lists destinations, label toggle, Retry/Cancel.
    /// </summary>
    private void ShowConfirmContextMenu(Point clickLocation, ConfirmChromeKind? focusedKind = null)
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 260);
        menu.Font = UiChrome.ChromeFont(11.0f);
        _confirmContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            _lastContextMenuClosedTime = DateTime.UtcNow;
            _confirmContextMenu = null;
        };

        var labelsItem = WindowsMenuRenderer.Item(
            LocalizationService.Translate("Show labels on buttons"),
            iconId: _confirmPillShowLabels ? "check" : null,
            iconSize: 24);
        labelsItem.Click += (_, _) => ToggleConfirmPillShowLabels();
        menu.Items.Add(labelsItem);

        menu.Items.Add(new ToolStripSeparator());

        var retryItem = WindowsMenuRenderer.Item(
            LocalizationService.Translate("Retry selection"),
            iconId: "redo",
            iconSize: 24);
        if (HasConfirmAnnotations())
            retryItem.ToolTipText = LocalizationService.Translate("This will discard the annotations on this capture.");
        retryItem.Click += (_, _) => RequestRetrySelection();
        menu.Items.Add(retryItem);

        menu.Items.Add(new ToolStripSeparator());

        var cancelItem = WindowsMenuRenderer.Item(
            LocalizationService.Translate("Cancel capture and exit"),
            iconId: "signOut",
            danger: true,
            iconSize: 24);
        cancelItem.Click += (_, _) => ConfirmAndCancelCapture();
        menu.Items.Add(cancelItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 260, itemHeight: 46);
        menu.Show(PointToScreen(clickLocation));
    }

    /// <summary>
    /// Right-click menu shown on empty area during capture mode selection.
    /// Offers full-screen capture, cancel, or close — no more abrupt Esc-like cancel.
    /// </summary>
    private void ShowEmptyAreaContextMenu(Point clickLocation)
    {
        // Respect the master switch — when disabled, fall back to immediate cancel.
        if (!Services.SettingsService.LoadStatic()?.ConfirmBeforeExit ?? true)
        {
            Cancel();
            return;
        }

        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 220);
        menu.Font = UiChrome.ChromeFont(11.0f);

        var isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        if (_mode == CaptureMode.ScrollCapture)
        {
            var cancelLabel = isSpanish ? "Cancelar captura por desplazamiento" : "Cancel scroll capture";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "signOut", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else if (_mode == CaptureMode.Ruler)
        {
            var cancelRulerLabel = isSpanish ? "Cancelar dibujo de reglas" : "Cancel ruler drawing";
            var cancelRulerItem = WindowsMenuRenderer.Item(cancelRulerLabel, iconId: "signOut", danger: true, iconSize: 24);
            cancelRulerItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelRulerItem);

            menu.Items.Add(new ToolStripSeparator());

            var fsLabel = isSpanish ? "Capturar pantalla completa" : "Capture full screen";
            var fsItem = WindowsMenuRenderer.Item(fsLabel, iconId: "captureRect", iconSize: 24);
            fsItem.Click += (_, _) => RegionSelected?.Invoke(_virtualBounds);
            menu.Items.Add(fsItem);

            var cancelLabel = isSpanish ? "Cancelar captura y salir" : "Cancel capture and exit";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "signOut", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else if (_mode == CaptureMode.ColorPicker)
        {
            var cancelLabel = isSpanish ? "Cancelar selección de color" : "Cancel color picker";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "signOut", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else if (_mode == CaptureMode.Ocr)
        {
            var autoCopy = Services.SettingsService.LoadStatic()?.OcrAutoCopyToClipboard ?? false;
            var autoCopyItem = WindowsMenuRenderer.Item(
                isSpanish ? "Auto-copiar texto OCR" : "Auto-copy OCR text",
                iconSize: 24);
            autoCopyItem.ToolTipText = isSpanish
                ? "Copia el texto OCR al portapapeles (usa el Auto-copy global; no abre la ventana de resultados)"
                : "Copy OCR text to the clipboard (uses global Auto-copy; skips the result window)";
            autoCopyItem.Image = autoCopy ? FluentIcons.RenderBitmap("check",
                UiChrome.IsDark ? Color.FromArgb(75, 130, 246) : Color.FromArgb(0, 120, 215), 20, true) : null;
            autoCopyItem.Click += (_, _) =>
            {
                var current = Services.SettingsService.LoadStatic()?.OcrAutoCopyToClipboard ?? false;
                SettingsService.SetOcrAutoCopyToClipboard(!current);
            };
            menu.Items.Add(autoCopyItem);
            menu.Items.Add(new ToolStripSeparator());

            var cancelLabel = isSpanish ? "Cancelar extracción de texto" : "Cancel text extraction";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "signOut", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else
        {
            var fsLabel = isSpanish ? "Capturar pantalla completa" : "Capture full screen";
            var fsItem = WindowsMenuRenderer.Item(fsLabel, iconId: "captureRect", iconSize: 24);
            fsItem.Click += (_, _) => RegionSelected?.Invoke(_virtualBounds);
            menu.Items.Add(fsItem);

            var cancelLabel = isSpanish ? "Cancelar captura y salir" : "Cancel capture and exit";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "signOut", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }

        menu.Items.Add(new ToolStripSeparator());

        var closeLabel = isSpanish ? "Cerrar menú y continuar" : "Close menu and continue";
        var closeItem = WindowsMenuRenderer.Item(closeLabel, iconId: "close", iconSize: 24);
        closeItem.Click += (_, _) => menu.Close();
        menu.Items.Add(closeItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 220, itemHeight: 46);
        menu.Show(PointToScreen(clickLocation));
    }

    private void ShowAnnotationContextMenu(Point clickLocation)
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 220);
        menu.Font = UiChrome.ChromeFont(11.0f); // unified style with confirm context menu
        var isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        bool multi = _multiSelectedIndices.Count > 1;
        int count = multi ? _multiSelectedIndices.Count : 1;

        // Smart duplicate label with type and count
        string duplicateLabel;
        if (_selectedAnnotationIndex >= 0 || multi)
        {
            var ann = multi
                ? _multiSelectedIndices
                    .Where(i => i >= 0 && i < _undoStack.Count)
                    .Select(i => GetAnnotationTypeLabel(_undoStack[i]))
                    .Distinct()
                    .ToList()
                : (_selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _undoStack.Count
                    ? new List<string> { GetAnnotationTypeLabel(_undoStack[_selectedAnnotationIndex]) }
                    : new List<string>());

            if (ann.Count == 1)
            {
                var typeName = ann[0];
                if (count == 1)
                    duplicateLabel = isSpanish ? $"Duplicar {typeName}" : $"Duplicate {typeName}";
                else
                    duplicateLabel = isSpanish ? $"Duplicar {count} {typeName}s" : $"Duplicate {count} {typeName}s";
            }
            else
            {
                duplicateLabel = isSpanish ? "Duplicar selección" : "Duplicate selection";
            }
        }
        else
        {
            duplicateLabel = isSpanish ? "Duplicar" : "Duplicate";
        }

        var duplicateItem = WindowsMenuRenderer.Item(duplicateLabel, iconId: "copy", iconSize: 24);
        duplicateItem.Click += (s, e) => DuplicateSelection();

        var deleteLabel = multi
            ? (isSpanish ? "Eliminar selección" : "Delete selection")
            : (isSpanish ? "Eliminar" : "Delete");
        var deleteItem = WindowsMenuRenderer.Item(deleteLabel, iconId: "trash", danger: true, iconSize: 24);
        deleteItem.Click += (s, e) => {
            if (_multiSelectedIndices.Count > 1)
                DeleteMultiSelectedAnnotations();
            else if (_selectedAnnotationIndex >= 0)
                DeleteAnnotationAt(_selectedAnnotationIndex);
        };

        menu.Items.Add(duplicateItem);
        menu.Items.Add(deleteItem);

        menu.Items.Add(new ToolStripSeparator());

        var captureFsLabel = isSpanish ? "Capturar pantalla completa" : "Capture full screen";
        var captureItem = WindowsMenuRenderer.Item(captureFsLabel, iconId: "captureRect", iconSize: 24);
        captureItem.Click += (s, e) =>
        {
            RegionSelected?.Invoke(_virtualBounds);
        };
        menu.Items.Add(captureItem);

        var cancelCaptureLabel = isSpanish ? "Cancelar captura y salir" : "Cancel capture and exit";
        var cancelCapItem = WindowsMenuRenderer.Item(cancelCaptureLabel, iconId: "signOut", iconSize: 24);
        cancelCapItem.Click += (s, e) => Cancel();
        menu.Items.Add(cancelCapItem);

        menu.Items.Add(new ToolStripSeparator());

        var closeMenuLabel = isSpanish ? "Cerrar menú y continuar" : "Close menu and continue";
        var closeItem = WindowsMenuRenderer.Item(closeMenuLabel, iconId: "close", iconSize: 24);
        closeItem.Click += (s, e) => menu.Close();
        menu.Items.Add(closeItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 220, itemHeight: 46);

        var screenPoint = PointToScreen(clickLocation);
        menu.Show(screenPoint);
    }

    private static string GetAnnotationTypeLabel(Annotation a)
    {
        var mode = a switch
        {
            RulerAnnotation => CaptureMode.Ruler,
            ArrowAnnotation => CaptureMode.Arrow,
            LineAnnotation => CaptureMode.Line,
            CurvedArrowAnnotation => CaptureMode.CurvedArrow,
            DrawStroke => CaptureMode.Draw,
            RectShapeAnnotation => CaptureMode.RectShape,
            CircleShapeAnnotation => CaptureMode.CircleShape,
            TextAnnotation => CaptureMode.Text,
            HighlightAnnotation => CaptureMode.Highlight,
            BlurRect => CaptureMode.Blur,
            StepNumberAnnotation => CaptureMode.StepNumber,
            MagnifierAnnotation => CaptureMode.Magnifier,
            EmojiAnnotation => CaptureMode.Emoji,
            EraserFill => CaptureMode.Eraser,
            _ => (CaptureMode?)null,
        };
        if (mode != null)
        {
            var tool = ToolDef.AllTools.FirstOrDefault(t => t.Mode == mode.Value);
            if (tool != null)
                return LocalizationService.Translate(tool.Label).ToLowerInvariant();
        }
        return "object";
    }

    private void ShowToolBanner(string text, bool persistent = false)
    {
        DisposeToolBannerClearingGhost();
        var bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        // Place opposite the active dock so the banner never sits under the toolbar.
        _banner = new StandaloneToolBanner(
            text,
            bannerWorkingArea,
            Bounds,
            persistent: persistent,
            onInvalidateRect: r => Invalidate(r),
            anchorBottom: IsTopDock);
        Invalidate(_banner.InvalidateBounds);
    }

    private void ShowToolBanner(IReadOnlyList<BannerSegment> segments, bool persistent = false, string? iconId = null)
    {
        DisposeToolBannerClearingGhost();
        var bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        // Place opposite the active dock so the banner never sits under the toolbar.
        _banner = new StandaloneToolBanner(
            segments,
            bannerWorkingArea,
            Bounds,
            persistent: persistent,
            onInvalidateRect: r => Invalidate(r),
            iconId: iconId,
            anchorBottom: IsTopDock);
        Invalidate(_banner.InvalidateBounds);
    }

    /// <summary>
    /// Soft-dismiss the current tool banner with a short fade. In confirm mode the dimmed
    /// overlay is sensitive to partial invalidates, so dismiss immediately to avoid ghosts.
    /// </summary>
    private void HideToolBanner()
    {
        if (_isConfirmingSelection)
            HideToolBannerImmediate();
        else
            _banner?.Dismiss();
    }

    private void DisposeToolBannerClearingGhost()
    {
        if (_banner == null) return;
        var bounds = _banner.InvalidateBounds;
        _banner.Dispose();
        _banner = null;
        if (bounds.Width > 0 && bounds.Height > 0)
            Invalidate(bounds);
    }

    /// <summary>Hard-hide and dispose the banner (no fade). Prefer <see cref="HideToolBanner"/>.</summary>
    private void HideToolBannerImmediate()
    {
        DisposeToolBannerClearingGhost();
    }
}
