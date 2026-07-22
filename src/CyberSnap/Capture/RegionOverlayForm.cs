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

    // Confirm chrome: Cancel / Retry / destination pills (built in RebuildConfirmChrome)
    private enum ConfirmChromeKind { Cancel, Retry, Save, Copy, Edit, Share, History, OcrExtract }
    private ConfirmChromeKind[] _confirmChromeKinds =
    {
        ConfirmChromeKind.Cancel, ConfirmChromeKind.Retry, ConfirmChromeKind.Save
    };
    private Rectangle[] _confirmChromeRects = Array.Empty<Rectangle>();
    /// <summary>Thin divider between fixed Cancel/Retry and destination pills (empty when unused).</summary>
    private Rectangle _confirmChromeSeparatorRect = Rectangle.Empty;
    /// <summary>Rounded dock panel behind the confirm pills so they stay readable on any wallpaper.</summary>
    private Rectangle _confirmChromeWrapperRect = Rectangle.Empty;
    private bool _confirmPillShowLabels;
    private bool _confirmChromeLayoutDirty = true;
    private Rectangle _confirmChromeLaidOutForRect = Rectangle.Empty;
    private bool _confirmChromeLaidOutWithLabels;
    /// <summary>Traveling glint phase around the confirm dock wrapper (0..1).</summary>
    private float _confirmWrapperShinePhase;

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
        if (IsVerticalDock)
        {
            w = pad * 2 + buttonSize;
            h = tier1PrimarySpan;
        }
        else
        {
            int logoSize = UiChrome.ScaleInt(10);
            int textWidth = UiChrome.ScaleInt(60);
            bool canShowText = _mainBarTools.Length >= 6;
            brandWidth = canShowText
                ? logoSize + textWidth + UiChrome.ScaleInt(24)
                : logoSize + UiChrome.ScaleInt(16);
            w = tier1PrimarySpan + brandWidth;
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

        if (IsVerticalDock)
        {
            int col1Height = GetToolbarPrimarySpan(_mainBarTools.Length + tier1UtilityCount, tier1SepCount, buttonSize, buttonSpacing, 0);
            int col1StartY = _toolbarRect.Y + pad + (_toolbarRect.Height - pad * 2 - col1Height) / 2;
            int col1X = _toolbarRect.X + pad;
            _brandRect = new Rectangle(col1X, _toolbarRect.Y + pad, buttonSize, col1StartY - (_toolbarRect.Y + pad));
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
            int row1Width = GetToolbarPrimarySpan(_mainBarTools.Length + tier1UtilityCount, tier1SepCount, buttonSize, buttonSpacing, 0);
            int row1StartX = _toolbarRect.X + pad + (_toolbarRect.Width - pad * 2 - row1Width - activatorW - activatorSpacing) / 2;
            if (row1StartX < _toolbarRect.X + brandWidth)
                row1StartX = _toolbarRect.X + brandWidth;
            int row1Y = _toolbarRect.Y + (h - buttonSize) / 2;
            _brandRect = new Rectangle(_toolbarRect.X, row1Y, brandWidth, buttonSize);
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
    /// Confirm-phase dock: annotation tools only + stroke/color + Move/Close.
    /// Capture tools stay in metadata but are not laid out (empty hit targets).
    /// </summary>
    private void CalcAnnotationOnlyToolbar(Rectangle screenBounds, int pad, int buttonSize, int buttonSpacing)
    {
        var annotSepIndices = GetAnnotationGroupSepFlyoutIndices();
        int annotSepCount = annotSepIndices.Count;
        // Gaps: annotation group seps + before stroke/color + before Move/Close
        int sepCount = annotSepCount + 2;
        int buttonCount = _flyoutTools.Length + 4; // annot + stroke + color + pos + close

        int activatorW = UiChrome.ScaleInt(14);
        int activatorH = buttonSize;
        int activatorSpacing = buttonSpacing;
        int primarySpan = GetToolbarPrimarySpan(buttonCount, sepCount, buttonSize, buttonSpacing, pad);
        if (!IsVerticalDock)
            primarySpan += activatorW + activatorSpacing;

        int w, h;
        int brandWidth = 0;
        if (IsVerticalDock)
        {
            w = pad * 2 + buttonSize;
            h = primarySpan;
        }
        else
        {
            int logoSize = UiChrome.ScaleInt(10);
            int textWidth = UiChrome.ScaleInt(60);
            // Confirm dock always uses full branding (logo + "CyberSnap") so a tools-hidden
            // bar does not collapse to a tiny logo strip.
            brandWidth = logoSize + textWidth + UiChrome.ScaleInt(24);
            w = primarySpan + brandWidth;
            h = pad * 2 + buttonSize;
        }

        AllocateToolbarButtonMetadata();

        _toolbarRect = ToolbarLayout.GetToolbarRect(
            _virtualBounds, screenBounds, w, h, CaptureDockSide, UiChrome.ScaledToolbarTopMargin);
        if (!_toolbarCustomOffset.IsEmpty)
            _toolbarRect.Offset(_toolbarCustomOffset);

        // Capture tools are hidden in confirm mode.
        for (int i = 0; i < _mainBarTools.Length; i++)
            _toolbarButtons[i] = Rectangle.Empty;

        int drawingStartIdx = _mainBarTools.Length + 4;

        if (IsVerticalDock)
        {
            int colHeight = GetToolbarPrimarySpan(buttonCount, sepCount, buttonSize, buttonSpacing, 0);
            int startY = _toolbarRect.Y + pad + (_toolbarRect.Height - pad * 2 - colHeight) / 2;
            int colX = _toolbarRect.X + pad;
            // Brand sits in the small strip above the first annotation tool (same column).
            int brandH = Math.Max(buttonSize, startY - (_toolbarRect.Y + pad));
            _brandRect = new Rectangle(colX, _toolbarRect.Y + pad, buttonSize, brandH);
            int actY = _toolbarRect.Y + (_toolbarRect.Height - activatorH) / 2;
            _menuActivatorRect = new Rectangle(_toolbarRect.Right - pad - activatorW, actY, activatorW, activatorH);

            int cy = startY;
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                _toolbarButtons[drawingStartIdx + i] = new Rectangle(colX, cy, buttonSize, buttonSize);
                cy += buttonSize + buttonSpacing;
                if (annotSepIndices.Contains(i))
                    cy += GroupGap;
            }
            cy += GroupGap;
            _toolbarButtons[StrokeWidthButtonIndex] = new Rectangle(colX, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[ColorButtonIndex] = new Rectangle(colX, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing + GroupGap;
            _toolbarButtons[PositionButtonIndex] = new Rectangle(colX, cy, buttonSize, buttonSize);
            cy += buttonSize + buttonSpacing;
            _toolbarButtons[CloseButtonIndex] = new Rectangle(colX, cy, buttonSize, buttonSize);
        }
        else
        {
            // Pack tools left-to-right immediately after the brand strip (no centering gap).
            int rowWidth = GetToolbarPrimarySpan(buttonCount, sepCount, buttonSize, buttonSpacing, 0);
            int startX = _toolbarRect.X + brandWidth;
            int rowY = _toolbarRect.Y + (h - buttonSize) / 2;
            _brandRect = new Rectangle(_toolbarRect.X, rowY, brandWidth, buttonSize);
            int actY = _toolbarRect.Y + (_toolbarRect.Height - activatorH) / 2;
            _menuActivatorRect = new Rectangle(_toolbarRect.Right - pad - activatorW, actY, activatorW, activatorH);

            int cx = startX;
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                _toolbarButtons[drawingStartIdx + i] = new Rectangle(cx, rowY, buttonSize, buttonSize);
                cx += buttonSize + buttonSpacing;
                if (annotSepIndices.Contains(i))
                    cx += GroupGap;
            }
            cx += GroupGap;
            _toolbarButtons[StrokeWidthButtonIndex] = new Rectangle(cx, rowY, buttonSize, buttonSize);
            cx += buttonSize + buttonSpacing;
            _toolbarButtons[ColorButtonIndex] = new Rectangle(cx, rowY, buttonSize, buttonSize);
            cx += buttonSize + buttonSpacing + GroupGap;
            _toolbarButtons[PositionButtonIndex] = new Rectangle(cx, rowY, buttonSize, buttonSize);
            cx += buttonSize + buttonSpacing;
            _toolbarButtons[CloseButtonIndex] = new Rectangle(cx, rowY, buttonSize, buttonSize);

            // Trim unused right padding from centering leftovers so the dock hugs content.
            int contentRight = Math.Max(cx, _menuActivatorRect.Right + pad);
            int desiredW = Math.Max(contentRight - _toolbarRect.X, brandWidth + rowWidth + pad);
            if (desiredW < _toolbarRect.Width)
                _toolbarRect.Width = desiredW;
        }
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
        var groups = new[] {
            new[] { "select", "eraser", "highlight" },
            new[] { "rectShape" }
        };
        var seps = new HashSet<int>();
        foreach (var group in groups)
        {
            int lastIdx = -1;
            for (int i = 0; i < _flyoutTools.Length; i++)
            {
                if (group.Contains(_flyoutTools[i].Id))
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
            // Repaint so the hold progress arc on the capture button advances.
            if (!_altCapturePopupOpen)
                changed = true;

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

        var primaryColor = Color.FromArgb(34, 197, 94);
        int primaryIdx = IndexOfPrimaryConfirmAction();

        // Hide the focused destination pill (mirrors annotation-tool RMB hide).
        if (focusedKind is { } focus
            && IsConfirmDestinationKind(focus)
            && focus != ConfirmChromeKind.OcrExtract)
        {
            string hideTitle = string.Format(
                LocalizationService.Translate("Hide \"{0}\""),
                ConfirmChromeShortLabel(focus));
            var hideItem = WindowsMenuRenderer.Item(
                hideTitle,
                iconId: ConfirmChromeFluentIcon(focus),
                iconSize: 24);
            int destCount = _confirmChromeKinds.Count(IsConfirmDestinationKind);
            if (destCount <= 1 && focus == ConfirmChromeKind.Save)
            {
                hideItem.Enabled = false;
                hideItem.ToolTipText = LocalizationService.Translate(
                    "Keep at least one destination on the confirm bar.");
            }
            else
            {
                var captured = focus;
                hideItem.Click += (_, _) => HideConfirmDestination(captured);
            }
            menu.Items.Add(hideItem);
            menu.Items.Add(new ToolStripSeparator());
        }

        void AddDestination(ConfirmChromeKind kind, int chromeIdx)
        {
            string title = ConfirmChromeTitle(kind);
            string hotkey = ConfirmChromeHotkeyHint(kind);
            if (!string.IsNullOrEmpty(hotkey) && kind != ConfirmChromeKind.OcrExtract)
                title += "  (" + hotkey + ")";
            string? icon = ConfirmChromeFluentIcon(kind);
            bool disabled = IsConfirmChromeDisabled(kind);
            bool isPrimary = !disabled && chromeIdx == primaryIdx;
            var item = WindowsMenuRenderer.Item(
                title,
                iconId: icon,
                customColor: isPrimary ? primaryColor : null,
                iconSize: 24);
            item.Enabled = !disabled;
            if (!disabled)
            {
                int capturedIdx = chromeIdx;
                item.Click += (_, _) => StartConfirmPress(capturedIdx);
            }
            menu.Items.Add(item);
        }

        for (int i = 0; i < _confirmChromeKinds.Length; i++)
        {
            if (IsConfirmDestinationKind(_confirmChromeKinds[i]))
                AddDestination(_confirmChromeKinds[i], i);
        }

        // Restore hidden destinations (from Settings toast layout).
        var toast = Services.SettingsService.LoadStatic()?.ToastButtons
            ?? new AppSettings.ToastButtonLayoutSettings();
        var hiddenDest = ToastButtonLayout.ConfirmActionButtons
            .Where(b => !ToastButtonLayout.IsVisible(toast, b))
            .ToList();
        if (hiddenDest.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            var header = WindowsMenuRenderer.Item(
                LocalizationService.Translate("Hidden destinations:"),
                iconId: null,
                iconSize: 24);
            header.Enabled = false;
            menu.Items.Add(header);

            foreach (var b in hiddenDest)
            {
                var toastKind = b;
                string label = b switch
                {
                    ToastButtonKind.Save => LocalizationService.Translate("Save"),
                    ToastButtonKind.Copy => LocalizationService.Translate("Copy"),
                    ToastButtonKind.Edit => LocalizationService.Translate("Edit"),
                    ToastButtonKind.Share => LocalizationService.Translate("Share"),
                    ToastButtonKind.History => LocalizationService.Translate("Gallery"),
                    _ => b.ToString()
                };
                var showItem = WindowsMenuRenderer.Item(label, iconId: "add", iconSize: 24);
                showItem.Padding = new Padding(24, 0, 0, 0);
                showItem.Click += (_, _) => ShowConfirmDestination(toastKind);
                menu.Items.Add(showItem);
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        var labelsItem = WindowsMenuRenderer.Item(
            LocalizationService.Translate("Show labels on destination pills"),
            iconId: _confirmPillShowLabels ? "check" : null,
            iconSize: 24);
        labelsItem.Click += (_, _) => ToggleConfirmPillShowLabels();
        menu.Items.Add(labelsItem);

        menu.Items.Add(new ToolStripSeparator());

        var retryItem = WindowsMenuRenderer.Item(
            LocalizationService.Translate("Retry selection"),
            iconId: "redo",
            iconSize: 24);
        retryItem.Click += (_, _) => ExitConfirmMode();
        menu.Items.Add(retryItem);

        if (_modeBeforeConfirm == CaptureMode.Ocr || _mode == CaptureMode.Ocr)
        {
            var autoCopy = IsOcrAutoCopyEnabled();
            var autoCopyItem = WindowsMenuRenderer.Item(
                LocalizationService.Translate("Auto-copy OCR text"),
                iconId: autoCopy ? "check" : null,
                iconSize: 24);
            autoCopyItem.ToolTipText = LocalizationService.Translate(
                "Copy OCR text to the clipboard (uses global Auto-copy; skips the result window)");
            autoCopyItem.Click += (_, _) =>
            {
                SettingsService.SetOcrAutoCopyToClipboard(!IsOcrAutoCopyEnabled());
            };
            menu.Items.Add(autoCopyItem);
            menu.Items.Add(new ToolStripSeparator());
        }

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
