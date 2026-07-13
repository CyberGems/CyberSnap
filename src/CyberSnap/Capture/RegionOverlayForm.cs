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

    private bool _isConfirmingSelection;
    private Rectangle _confirmRect;
    private int _confirmHandleDragIndex = -1;
    private bool _isConfirmDragging;
    private Point _confirmDragStart;
    private Point _confirmDragOffset;
    private Rectangle _confirmDragStartRect;
    private int _hoveredConfirmButton = -1; // 0 = Confirm, 1 = Cancel
    private int _pressedConfirmButton = -1; // button currently playing the click squash
    private int _pendingConfirmAction = -1; // action to run when the squash finishes
    private float _confirmPressAmt;          // 0→1→0 squash progress for the pressed button
    private DateTime _pressAnimStart;
    private readonly float[] _shinePhase = { 0f, 0.5f, 0.25f }; // per-button glint position (0=confirm,1=retry,2=cancel)
    private readonly float[] _shineMain = { 1f, 1f, 1f }; // primary comet intensity per button (0=confirm,1=retry,2=cancel)
    private readonly float[] _shineDup = { 0f, 0f, 0f };  // duplicate comet intensity (fades in on hover)

    private CaptureDockSide ActiveDockSide => _isEvading ? GetOppositeDockSide(CaptureDockSide) : CaptureDockSide;

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

    // Hybrid button variables
    private bool _altCapturePopupOpen = false;
    private Rectangle _altCaptureButtonRect = Rectangle.Empty;
    private bool _hoveredAltCaptureBtn = false;
    private System.Windows.Forms.Timer? _hoverHoldTimer;
    private DateTime _mouseDownStartTime = DateTime.MinValue;
    private bool _isMouseDownOnCaptureBtn = false;
    private int _mergedCaptureButtonIndex = -1;

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
    private bool _hoveredBrand;
    private bool _hoveredMenuActivator;
    private Rectangle _toolbarAnchorArea;
    private Rectangle _lastOverlayUiBounds;
    private int _toolbarRenderVersion;
    private float _toolbarAnim;
    private Point _lastCursorPos;
    private Point _prevCursorPos; // crosshair ghosting fix
    private Rectangle _lastSelectionRect;
    private Rectangle _lastAutoDetectRect;
    private LiveSelectionAdornerForm? _selectionAdorner;
    private CaptureEscapeKeyHook? _escapeHook;
    private StandaloneToolBanner? _banner;
    private bool _captureBannerShown; // first-run banner displayed this session
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
    private TextBox? _textBox; // real textbox for native text editing

    // Inline text formatting toolbar hit rects (computed during paint)
    private RectangleF _textToolbarRect;
    private RectangleF _textBoldBtnRect;
    private RectangleF _textItalicBtnRect;
    private RectangleF _textStrokeBtnRect;
    private RectangleF _textShadowBtnRect;
    private RectangleF _textBackgroundBtnRect;
    private RectangleF _textFontBtnRect;
    private RectangleF _textSizeMinusBtnRect;
    private RectangleF _textSizePlusBtnRect;
    private RectangleF _textGripRect; // drag handle that moves toolbar + text together
    private int _hoveredTextBtn = -1; // 0=B 1=I 2=Stroke 3=Shadow 4=Background 5=Font 6=Size- 7=Size+ 8=Grip, -1=none
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
    public event Action? CaptureBannerDismissed;
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

        // Magnifier bitmap for color picker
        _magBitmap = new Bitmap(PW, PH, PixelFormat.Format32bppArgb);
        _magPixels = new int[PW * PH];
        _magGfx = Graphics.FromImage(_magBitmap);
        _magGfx.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        SetupForm();
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

        // ── First-time capture banner ──
        // CaptureDockSide is assigned via object-initializer after the ctor, so read the
        // preferred dock from settings here (default Bottom matches the property default).
        var settings = SettingsService.LoadStatic();
        if (settings != null && !settings.HasSeenCaptureBanner)
        {
            var bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            bool toolbarAtTop = settings.CaptureDockSide == CaptureDockSide.Top;
            // Anchor opposite the toolbar so the pill does not cover the dock.
            _banner = new StandaloneToolBanner(
                LocalizationService.Translate("Click & drag to capture · Toolbar below · Right-click or Esc to cancel"),
                bannerWorkingArea,
                Bounds,
                persistent: true,
                onInvalidateRect: r => Invalidate(r),
                anchorBottom: toolbarAtTop);
            _captureBannerShown = true;
        }

        _currentOverlay = this;
    }

    /// <summary>
    /// Hide the first-run instruction banner from view (e.g. on first interaction).
    /// This is visual only — it does NOT mark the banner as seen. The "seen" flag is
    /// persisted in <see cref="Dispose(bool)"/> only when the overlay closes via an
    /// actual capture, so cancelling without capturing still shows the hint next time.
    /// </summary>
    private void HideCaptureBanner()
    {
        if (_banner == null) return;
        _banner.Dismiss();
        _banner = null;
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
        BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Opaque, true);
        KeyPreview = true;
    }

    private static int GroupGap => UiChrome.ScaledToolbarGroupGap; // spacing between tool groups (includes separator line)

    // Separator indices (computed dynamically based on visible tools)
    private int[] _sepAfter = Array.Empty<int>();

    private void CalcToolbar()
    {
        int pad = UiChrome.ScaledToolbarInnerPadding;
        int buttonSize = UiChrome.ScaledToolbarButtonSize;
        int buttonSpacing = UiChrome.ScaledToolbarButtonSpacing;
        int toolbarHeight = UiChrome.ScaledToolbarHeight;
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

        int activatorWidth = UiChrome.ScaleInt(14);
        int activatorSpacing = buttonSpacing;

        int tier1PrimarySpan = GetToolbarPrimarySpan(_mainBarTools.Length + 4, 2, buttonSize, buttonSpacing, pad);
        if (!IsVerticalDock)
        {
            tier1PrimarySpan += activatorWidth + activatorSpacing;
        }
        int tier2PrimarySpan = GetToolbarPrimarySpan(_flyoutTools.Length, 2, buttonSize, buttonSpacing, pad);
        int maxPrimarySpan = Math.Max(tier1PrimarySpan, tier2PrimarySpan);

        int w, h;
        int brandWidth = 0;
        if (IsVerticalDock)
        {
            w = _flyoutTools.Length > 0 ? (pad * 2 + buttonSize * 2 + buttonSpacing) : (pad * 2 + buttonSize);
            h = maxPrimarySpan;
        }
        else
        {
            int logoSize = UiChrome.ScaleInt(10);
            int textWidth = UiChrome.ScaleInt(60); // "CyberSnap" text estimate at 5.8pt bold
            
            // Check if there is enough space to show the text.
            // If the annotation bar is significantly wider than the main bar, or if we have at least 6 tools enabled in the main bar, show the text.
            int tier1Width = GetToolbarPrimarySpan(_mainBarTools.Length + 4, 2, buttonSize, buttonSpacing, 0);
            int tier2Width = GetToolbarPrimarySpan(_flyoutTools.Length, 2, buttonSize, buttonSpacing, 0);
            bool canShowText = (tier2Width - tier1Width >= UiChrome.ScaleInt(80)) || (_mainBarTools.Length >= 6);
            
            if (canShowText)
            {
                brandWidth = logoSize + textWidth + UiChrome.ScaleInt(24); // logo + text + padding+buffer
            }
            else
            {
                brandWidth = logoSize + UiChrome.ScaleInt(16); // only logo + small padding
            }
            
            w = maxPrimarySpan + brandWidth;
            h = _flyoutTools.Length > 0 ? (pad * 2 + buttonSize * 2 + buttonSpacing) : (pad * 2 + buttonSize);
        }

        _toolbarButtons = new Rectangle[BtnCount];
        _toolbarIcons = new string[BtnCount];
        _toolbarLabels = new string[BtnCount];
        _toolbarToolIds = new string[BtnCount];
        _toolbarModes = new CaptureMode?[BtnCount];

        // 1. Group 0 (Capture tools)
        for (int i = 0; i < _mainBarTools.Length; i++)
        {
            _toolbarIcons[i] = _mainBarTools[i].Id switch { "crop" => "rect", "rect" => "captureRect", var id => id };
            _toolbarLabels[i] = LocalizationService.Translate(_mainBarTools[i].Label);
            _toolbarToolIds[i] = _mainBarTools[i].Id;
            _toolbarModes[i] = _mainBarTools[i].Mode;
        }

        // 2. Stroke width button
        int swIdx = StrokeWidthButtonIndex;
        _toolbarIcons[swIdx] = "strokeWidth";
        _toolbarLabels[swIdx] = LocalizationService.Translate("Shape stroke width");
        _toolbarToolIds[swIdx] = "strokeWidth";
        _toolbarModes[swIdx] = null;

        // 3. Color picker button
        int colorIdx = ColorButtonIndex;
        _toolbarIcons[colorIdx] = "color";
        _toolbarLabels[colorIdx] = LocalizationService.Translate("Active drawing and text color");
        _toolbarToolIds[colorIdx] = "color";
        _toolbarModes[colorIdx] = null;

        // 4. Position button
        int positionIdx = PositionButtonIndex;
        _toolbarIcons[positionIdx] = "position";
        _toolbarLabels[positionIdx] = LocalizationService.Translate("Toolbar Position");
        _toolbarToolIds[positionIdx] = "position";
        _toolbarModes[positionIdx] = null;

        // 5. Close (Cancel) button
        int closeIdx = CloseButtonIndex;
        _toolbarIcons[closeIdx] = "close";
        _toolbarLabels[closeIdx] = LocalizationService.Translate("Cancel");
        _toolbarToolIds[closeIdx] = "close";
        _toolbarModes[closeIdx] = null;

        // 6. Group 1 (Annotation & Drawing tools)
        int drawingStartIdx = _mainBarTools.Length + 4;
        for (int i = 0; i < _flyoutTools.Length; i++)
        {
            int btnIdx = drawingStartIdx + i;
            _toolbarIcons[btnIdx] = _flyoutTools[i].Id;
            _toolbarLabels[btnIdx] = LocalizationService.Translate(_flyoutTools[i].Label);
            _toolbarToolIds[btnIdx] = _flyoutTools[i].Id;
            _toolbarModes[btnIdx] = _flyoutTools[i].Mode;
        }

        _toolbarRect = ToolbarLayout.GetToolbarRect(
            _virtualBounds,
            screenBounds,
            w,
            h,
            CaptureDockSide,
            UiChrome.ScaledToolbarTopMargin);

        // Last visible in the pre-utility group (matches Paint.Toolbar.cs tier1)
        var tier1Group = new[] { "rect", "center", "scroll", "recordGif", "record" };
        int sepIdx = -1;
        for (int i = 0; i < _mainBarTools.Length; i++)
        {
            if (tier1Group.Contains(_mainBarTools[i].Id))
                sepIdx = i;
        }

        if (IsVerticalDock)
        {
            // Column 1: Capture & System Tools
            int col1Height = GetToolbarPrimarySpan(_mainBarTools.Length + 4, 2, buttonSize, buttonSpacing, 0);
            int col1StartY = _toolbarRect.Y + pad + (_toolbarRect.Height - pad * 2 - col1Height) / 2;
            int col1X = _toolbarRect.X + pad;

            _brandRect = new Rectangle(col1X, _toolbarRect.Y + pad, buttonSize, col1StartY - (_toolbarRect.Y + pad));
            _menuActivatorRect = new Rectangle(_toolbarRect.Right - pad - activatorWidth, _toolbarRect.Y + pad + UiChrome.ScaleInt(4), activatorWidth, activatorWidth);

            int cy = col1StartY;
            for (int i = 0; i < _mainBarTools.Length; i++)
            {
                _toolbarButtons[i] = new Rectangle(col1X, cy, buttonSize, buttonSize);
                cy += buttonSize + buttonSpacing;
                if (i == sepIdx)
                    cy += GroupGap;
            }
            cy += GroupGap;
            _toolbarButtons[StrokeWidthButtonIndex] = new Rectangle(col1X, cy, buttonSize, buttonSize); // Stroke width
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[ColorButtonIndex] = new Rectangle(col1X, cy, buttonSize, buttonSize); // Color
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[PositionButtonIndex] = new Rectangle(col1X, cy, buttonSize, buttonSize); // Position
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[CloseButtonIndex] = new Rectangle(col1X, cy, buttonSize, buttonSize); // Close

            // Column 2: Annotation Tools
            int col2X = _toolbarRect.X + pad + buttonSize + buttonSpacing;
            int col2Height = GetToolbarPrimarySpan(_flyoutTools.Length, 2, buttonSize, buttonSpacing, 0);
            int col2StartY = _toolbarRect.Y + pad + (_toolbarRect.Height - pad * 2 - col2Height) / 2;
            int cy2 = col2StartY;
            // Separator after last visible in each group (matches Paint.Toolbar.cs)
            var tier2GroupsV = new[] {
                new[] { "select", "eraser", "highlight" },
                new[] { "rectShape" }
            };
            var tier2SepFlyoutIndicesV = new HashSet<int>();
            foreach (var group in tier2GroupsV)
            {
                int lastIdx = -1;
                for (int i = 0; i < _flyoutTools.Length; i++)
                {
                    if (group.Contains(_flyoutTools[i].Id))
                        lastIdx = i;
                }
                if (lastIdx >= 0)
                    tier2SepFlyoutIndicesV.Add(lastIdx);
            }
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                int btnIdx = _mainBarTools.Length + 4 + i;
                _toolbarButtons[btnIdx] = new Rectangle(col2X, cy2, buttonSize, buttonSize);
                cy2 += buttonSize + buttonSpacing;
                if (tier2SepFlyoutIndicesV.Contains(i))
                    cy2 += GroupGap;
            }
        }
        else
        {
            // Row 1: Capture & System Tools
            int row1Width = GetToolbarPrimarySpan(_mainBarTools.Length + 4, 2, buttonSize, buttonSpacing, 0);
            int row1StartX = _toolbarRect.X + pad + (_toolbarRect.Width - pad * 2 - row1Width - activatorWidth - activatorSpacing) / 2;
            if (row1StartX < _toolbarRect.X + brandWidth)
            {
                row1StartX = _toolbarRect.X + brandWidth;
            }
            int row1Y = _toolbarRect.Y + ((_flyoutTools.Length > 0 ? h / 2 : h) - buttonSize) / 2;
            _brandRect = new Rectangle(_toolbarRect.X, row1Y, brandWidth, buttonSize);
            _menuActivatorRect = new Rectangle(_toolbarRect.Right - pad - activatorWidth, _toolbarRect.Y + pad + UiChrome.ScaleInt(4), activatorWidth, activatorWidth);

            int cx = row1StartX;
            for (int i = 0; i < _mainBarTools.Length; i++)
            {
                _toolbarButtons[i] = new Rectangle(cx, row1Y, buttonSize, buttonSize);
                cx += buttonSize + buttonSpacing;
                if (i == sepIdx)
                    cx += GroupGap;
            }
            cx += GroupGap;
            _toolbarButtons[StrokeWidthButtonIndex] = new Rectangle(cx, row1Y, buttonSize, buttonSize); // Stroke width
            cx += buttonSize + buttonSpacing;
            _toolbarButtons[ColorButtonIndex] = new Rectangle(cx, row1Y, buttonSize, buttonSize); // Color
            cx += buttonSize + buttonSpacing;
            _toolbarButtons[PositionButtonIndex] = new Rectangle(cx, row1Y, buttonSize, buttonSize); // Position
            cx += buttonSize + buttonSpacing;
            cx += GroupGap;
            _toolbarButtons[CloseButtonIndex] = new Rectangle(cx, row1Y, buttonSize, buttonSize); // Close

            // Row 2: Annotation Tools
            int row2Y = _toolbarRect.Y + h / 2 + (h - h / 2 - buttonSize) / 2;
            int row2Width = GetToolbarPrimarySpan(_flyoutTools.Length, 2, buttonSize, buttonSpacing, 0);
            int row2StartX = _toolbarRect.X + pad + (_toolbarRect.Width - pad * 2 - row2Width) / 2;
            int cx2 = row2StartX;
            // Separator after last visible in each group (matches Paint.Toolbar.cs)
            var tier2Groups = new[] {
                new[] { "select", "eraser", "highlight" },
                new[] { "rectShape" }
            };
            var tier2SepFlyoutIndices = new HashSet<int>();
            foreach (var group in tier2Groups)
            {
                int lastIdx = -1;
                for (int i = 0; i < _flyoutTools.Length; i++)
                {
                    if (group.Contains(_flyoutTools[i].Id))
                        lastIdx = i;
                }
                if (lastIdx >= 0)
                    tier2SepFlyoutIndices.Add(lastIdx);
            }
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                int btnIdx = _mainBarTools.Length + 4 + i;
                _toolbarButtons[btnIdx] = new Rectangle(cx2, row2Y, buttonSize, buttonSize);
                cx2 += buttonSize + buttonSpacing;
                if (tier2SepFlyoutIndices.Contains(i))
                    cx2 += GroupGap;
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
        CalcToolbar();
        PositionToolbarForm();
        RefreshToolbar();
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

        // 1. Mouse hold check (0.3 seconds)
        if (_isMouseDownOnCaptureBtn && _mouseDownStartTime != DateTime.MinValue)
        {
            if ((DateTime.UtcNow - _mouseDownStartTime).TotalMilliseconds >= 300)
            {
                if (!_altCapturePopupOpen)
                {
                    _altCapturePopupOpen = true;
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
                    // Slow tooltip: 1200ms delay
                    if ((DateTime.UtcNow - _hoverButtonStartTime).TotalMilliseconds >= 1200)
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
            else if (_hoveredButton >= 0 || (_hoveredAltCaptureBtn && _altCapturePopupOpen))
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
    /// Right-click menu shown while confirming a selection. Mirrors the three on-screen action pills:
    /// Confirm (green ✓), Retry the selection (neutral arrow), and Cancel &amp; exit (red close).
    /// Action-only, no branding header — it's a quick contextual fallback for the buttons.
    /// </summary>
    private void ShowConfirmContextMenu(Point clickLocation)
    {
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 240);
        menu.Font = UiChrome.ChromeFont(11.0f); // larger text as requested

        var isSpanish = string.Equals(
            Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
            "es", StringComparison.OrdinalIgnoreCase);

        // Confirm — primary action (matches the green "Listo / Ready" pill).
        var confirmColor = Color.FromArgb(34, 197, 94); // Premium vibrant success green
        var confirmItem = WindowsMenuRenderer.Item(
            isSpanish ? "Confirmar captura" : "Confirm capture",
            iconId: "check", customColor: confirmColor, iconSize: 24);
        confirmItem.Click += (_, _) => CommitConfirmedSelection();
        menu.Items.Add(confirmItem);

        // Retry — discard this crop and select the area again (matches the Retry pill → ExitConfirmMode).
        var retryItem = WindowsMenuRenderer.Item(
            isSpanish ? "Reintentar selección" : "Retry selection",
            iconId: "redo", iconSize: 24);
        retryItem.Click += (_, _) => ExitConfirmMode();
        menu.Items.Add(retryItem);

        if (_mode == CaptureMode.Ocr)
        {
            var autoCopy = Services.SettingsService.LoadStatic()?.OcrAutoCopyToClipboard ?? false;
            var autoCopyItem = WindowsMenuRenderer.Item(
                isSpanish ? "Auto-copiar OCR" : "Auto-copy OCR",
                iconSize: 24);
            autoCopyItem.ToolTipText = isSpanish
                ? "Copiar el texto reconocido sin abrir la ventana de resultados"
                : "Copy recognized text without opening the result window";
            autoCopyItem.Image = autoCopy ? FluentIcons.RenderBitmap("check",
                UiChrome.IsDark ? Color.FromArgb(75, 130, 246) : Color.FromArgb(0, 120, 215), 20, true) : null;
            autoCopyItem.Click += (_, _) =>
            {
                var current = Services.SettingsService.LoadStatic()?.OcrAutoCopyToClipboard ?? false;
                SettingsService.SetOcrAutoCopyToClipboard(!current);
            };
            menu.Items.Add(autoCopyItem);
            menu.Items.Add(new ToolStripSeparator());
        }

        // Cancel everything and close the overlay (matches the red Cancel pill — routes through
        // ConfirmAndCancelCapture so pending annotations get the same "will be lost" warning).
        var cancelItem = WindowsMenuRenderer.Item(
            isSpanish ? "Cancelar captura y salir" : "Cancel capture and exit",
            iconId: "close", danger: true, iconSize: 24);
        cancelItem.Click += (_, _) => ConfirmAndCancelCapture();
        menu.Items.Add(cancelItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 240, itemHeight: 46);
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
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "close", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else if (_mode == CaptureMode.Ruler)
        {
            var cancelRulerLabel = isSpanish ? "Cancelar dibujo de reglas" : "Cancel ruler drawing";
            var cancelRulerItem = WindowsMenuRenderer.Item(cancelRulerLabel, iconId: "close", danger: true, iconSize: 24);
            cancelRulerItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelRulerItem);

            menu.Items.Add(new ToolStripSeparator());

            var fsLabel = isSpanish ? "Capturar pantalla completa" : "Capture full screen";
            var fsItem = WindowsMenuRenderer.Item(fsLabel, iconId: "captureRect", iconSize: 24);
            fsItem.Click += (_, _) => RegionSelected?.Invoke(_virtualBounds);
            menu.Items.Add(fsItem);

            var cancelLabel = isSpanish ? "Cancelar captura y salir" : "Cancel capture and exit";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "close", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else if (_mode == CaptureMode.ColorPicker)
        {
            var cancelLabel = isSpanish ? "Cancelar selección de color" : "Cancel color picker";
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "close", danger: true, iconSize: 24);
            cancelItem.Click += (_, _) => Cancel();
            menu.Items.Add(cancelItem);
        }
        else if (_mode == CaptureMode.Ocr)
        {
            var autoCopy = Services.SettingsService.LoadStatic()?.OcrAutoCopyToClipboard ?? false;
            var autoCopyItem = WindowsMenuRenderer.Item(
                isSpanish ? "Auto-copiar OCR" : "Auto-copy OCR",
                iconSize: 24);
            autoCopyItem.ToolTipText = isSpanish
                ? "Copiar el texto reconocido sin abrir la ventana de resultados"
                : "Copy recognized text without opening the result window";
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
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "close", danger: true, iconSize: 24);
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
            var cancelItem = WindowsMenuRenderer.Item(cancelLabel, iconId: "close", danger: true, iconSize: 24);
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
        var cancelCapItem = WindowsMenuRenderer.Item(cancelCaptureLabel, iconId: "close", iconSize: 24);
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
        if (_banner != null)
        {
            _banner.Dispose();
            _banner = null;
        }
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
        if (_banner != null)
        {
            _banner.Dispose();
            _banner = null;
        }
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

    private void HideToolBanner()
    {
        if (_banner != null)
        {
            var bounds = _banner.InvalidateBounds;
            _banner.Dispose();
            _banner = null;
            Invalidate(bounds);
        }
    }
}
