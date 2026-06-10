using CyberSnap.Helpers;

namespace CyberSnap.Models;

public enum AfterCaptureAction
{
    CopyToClipboard,
    PreviewAndCopy,
    PreviewOnly
}

public enum ToastPosition
{
    Right,
    Left,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum ToastButtonSlot
{
    TopLeft,
    TopInnerLeft,
    TopInnerRight,
    TopRight,
    BottomLeft,
    BottomInnerLeft,
    BottomInnerRight,
    BottomRight
}

public enum SoundEvent
{
    Capture,
    Color,
    Text,
    Scan,
    RecordStart,
    RecordStop,
    Error,
    Startup
}

public enum RecordingFormat
{
    GIF,
    MP4
}

public enum RecordingQuality
{
    Original,
    P1080,
    P720,
    P480
}

public enum HistoryRetentionPeriod
{
    Never,
    OneDay,
    SevenDays,
    ThirtyDays,
    NinetyDays
}

public enum CaptureImageFormat
{
    Png,
    Jpeg,
    Bmp
}

public enum WindowDetectionMode
{
    Off,
    WindowOnly
}

public enum CaptureDockSide
{
    Top,
    Bottom,
    Left,
    Right
}

public enum CenterSelectionAspectRatio
{
    Free,
    Square,
    Widescreen16x9,
    Classic4x3,
    Photo3x2,
    Portrait9x16
}

public enum ScrollingCaptureMode
{
    Automatic,
    Manual,
    AssistAutoscroll
}

[Flags]
public enum ImageSearchSourceOptions
{
    None = 0,
    FileName = 1 << 0,
    Ocr = 1 << 1,
    OcrText = Ocr,
    All = FileName | Ocr
}

public sealed class AppSettings
{
    public sealed class ToastButtonLayoutSettings
    {
        // When true the user has opted into manual editing of each button's placement; the
        // per-button controls in the layout designer are enabled. New users start with this off
        // (a preset is in charge) until they click "Manual" or drag a button in the preview.
        public bool Manual { get; set; }

        public bool ShowClose { get; set; } = true;
        public ToastButtonSlot CloseSlot { get; set; } = ToastButtonSlot.TopRight;
        public bool ShowPin { get; set; } = true;
        public ToastButtonSlot PinSlot { get; set; } = ToastButtonSlot.TopLeft;
        public bool ShowSave { get; set; } = true;
        public ToastButtonSlot SaveSlot { get; set; } = ToastButtonSlot.BottomRight;
        public bool ShowOffice { get; set; }
        public ToastButtonSlot OfficeSlot { get; set; } = ToastButtonSlot.TopInnerLeft;
        public bool ShowDelete { get; set; }
        public ToastButtonSlot DeleteSlot { get; set; } = ToastButtonSlot.BottomLeft;
        public bool ShowHistory { get; set; } = true;
        public ToastButtonSlot HistorySlot { get; set; } = ToastButtonSlot.BottomLeft;
        public bool ShowEdit { get; set; }
        public ToastButtonSlot EditSlot { get; set; } = ToastButtonSlot.BottomInnerLeft;
    }

    public uint HotkeyModifiers { get; set; } = Native.User32.MOD_ALT;
    public uint HotkeyKey { get; set; } = 0xC0; // VK_OEM_3 = backtick/tilde

    // OCR hotkey: Alt+Shift+`
    public uint OcrHotkeyModifiers { get; set; } = Native.User32.MOD_ALT | Native.User32.MOD_SHIFT;
    public uint OcrHotkeyKey { get; set; } = 0xC0;
    public string OcrLanguageTag { get; set; } = "auto";
    public int OcrModelQuality { get; set; } // 0 = Fast (~1 MB), 1 = Standard (~4 MB)
    public string OcrDefaultTranslateFrom { get; set; } = "auto";
    public string OcrDefaultTranslateTo { get; set; } = "auto";
    public bool OcrAutoCopyToClipboard { get; set; }
    public string? GoogleTranslateApiKey { get; set; }
    public bool TranslationRuntimeInstalled { get; set; }
    public int TranslationModel { get; set; } = 2; // 0 = Argos, 1 = Google, 2 = Open-source local
    public bool AnnotationStrokeShadow { get; set; } = true;
    public float StrokeWidth { get; set; } = 6f;
    public int ToolColorArgb { get; set; } = System.Drawing.Color.FromArgb(255, 220, 0).ToArgb(); // Default yellow
    // Color picked for shapes/annotations in the post-capture Editor. Kept separate from the
    // in-capture overlay's ToolColorArgb so each remembers its own choice. Default = editor accent (cyan).
    public int EditorToolColorArgb { get; set; } = System.Drawing.Color.FromArgb(0, 255, 255).ToArgb();
    // Font size last chosen for the Text tool in the post-capture Editor (pixels). Editor-specific,
    // like EditorToolColorArgb. Clamped to the same 10..120 range the Text toolbar enforces.
    public float EditorTextFontSize { get; set; } = 24f;

    // Color picker hotkey: Alt+C
    public uint PickerHotkeyModifiers { get; set; } = Native.User32.MOD_ALT;
    public uint PickerHotkeyKey { get; set; } = 0x43; // VK_C

    // Optional custom-tool hotkeys (disabled by default)
    public uint ScanHotkeyModifiers { get; set; }
    public uint ScanHotkeyKey { get; set; }
    public uint CenterHotkeyModifiers { get; set; }
    public uint CenterHotkeyKey { get; set; }
    public uint FullscreenHotkeyModifiers { get; set; }
    public uint FullscreenHotkeyKey { get; set; }
    public uint ActiveWindowHotkeyModifiers { get; set; }
    public uint ActiveWindowHotkeyKey { get; set; }
    public uint RulerHotkeyModifiers { get; set; }
    public uint RulerHotkeyKey { get; set; }

    // Scrolling capture hotkey (disabled by default)
    public uint ScrollCaptureHotkeyModifiers { get; set; }
    public uint ScrollCaptureHotkeyKey { get; set; }

    // GIF recording hotkey (disabled by default)
    public uint GifHotkeyModifiers { get; set; }
    public uint GifHotkeyKey { get; set; }
    public int GifFps { get; set; } = 15;

    public AfterCaptureAction AfterCapture { get; set; } = AfterCaptureAction.PreviewAndCopy;
    public bool OpenEditorAfterCapture { get; set; }
    // Editor: when a capture loads, auto-fit it to the canvas (true) or show it at real 100% size (false).
    public bool EditorFitToWindowOnOpen { get; set; } = true;
    public bool EditorShowBanners { get; set; } = true;
    public bool SaveToFile { get; set; } = true;
    public bool AskForFileNameOnSave { get; set; }
    public string FileNameTemplate { get; set; } = Helpers.FileNameTemplate.DefaultTemplate;
    public CaptureImageFormat CaptureImageFormat { get; set; } = CaptureImageFormat.Png;
    public bool AutoOpenCapturedImages { get; set; }
    public bool StyleScreenshots { get; set; }
    public bool AddScreenshotShadow { get; set; }
    public bool AddScreenshotStroke { get; set; }
    public int CaptureMaxLongEdge { get; set; }
    public string SaveDirectory { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CyberSnap");
    public bool SaveInMonthlyFolders { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    public bool AutoCheckForUpdates { get; set; } = true;
    public CaptureMode LastCaptureMode { get; set; } = CaptureMode.Rectangle;
    public WindowDetectionMode WindowDetection { get; set; } = WindowDetectionMode.WindowOnly;
    public CaptureDockSide CaptureDockSide { get; set; } = CaptureDockSide.Top;
    public ScrollingCaptureMode ScrollingCaptureMode { get; set; } = ScrollingCaptureMode.AssistAutoscroll;
    public int CaptureDelaySeconds { get; set; }
    public bool SaveHistory { get; set; } = true;
    public bool MuteSounds { get; set; }
    public bool DisableAnimations { get; set; }
    public double UiScale { get; set; } = 1.0;
    public string InterfaceLanguage { get; set; } = "auto";
    public bool ShowCrosshairGuides { get; set; } // off by default
    public bool ShowCursor { get; set; }
    public bool ShowCaptureMagnifier { get; set; } = true;
    public bool OverlayCaptureAllMonitors { get; set; } = true;
    public bool DetectWindows { get; set; } = true;
    public bool ConfirmRegionBeforeCapture { get; set; } = true;
    public bool CompressHistory { get; set; }
    public int JpegQuality { get; set; } = 85;
    public bool HasCompletedSetup { get; set; }
    public ToastPosition ToastPosition { get; set; } = ToastPosition.Right;
    public int ToastMonitorIndex { get; set; } = -1; // -1 = Auto/Follow, 0+ = fixed index
    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Rectangle;
    public CenterSelectionAspectRatio CenterSelectionAspectRatio { get; set; } = CenterSelectionAspectRatio.Free;
    public bool ShowToolNumberBadges { get; set; } = true;
    public HistoryRetentionPeriod HistoryRetention { get; set; } = HistoryRetentionPeriod.Never;
    public int HistoryCountLimit { get; set; } = 0;
    public bool HistoryDeleteOriginalOnPrune { get; set; }
    public ImageSearchSourceOptions ImageSearchSources { get; set; } = ImageSearchSourceOptions.All;
    public bool ShowImageSearchBar { get; set; } = true;
    public bool ShowAutoPrune { get; set; } = true;
    public bool ImageSearchExactMatch { get; set; }
    public bool ShowImageSearchDiagnostics { get; set; }
    public bool AutoIndexImages { get; set; } = false;
    public int HistoryCategoryFilter { get; set; } = 0; // 0=All (default)

    public double ToastDurationSeconds { get; set; } = 2.5;
    public double SystemToastDurationSeconds { get; set; } = 4.0;
    // Master switch for all pop-up notifications. When false, no toasts are shown at all.
    public bool NotificationsEnabled { get; set; } = true;
    // Sub-toggle (only meaningful when NotificationsEnabled): controls brief text-only system
    // messages such as "Sent to the editor". Capture previews and error alerts are unaffected.
    public bool SystemNotificationsEnabled { get; set; } = true;
    // Toasts always dismiss with a fade-out; this only controls how long that fade takes.
    public double ToastFadeOutSeconds { get; set; } = 1.0;
    public bool AutoPinPreviews { get; set; }
    public ToastButtonLayoutSettings ToastButtons { get; set; } = new();
    public Dictionary<string, string> OpenWithApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Per-sound customization: null = use built-in default MP3, string = path to user's custom MP3.
    public Dictionary<SoundEvent, string?> CustomSounds { get; set; } = new();
    // Per-sound mute: true = that specific sound is silenced. Master mute is MuteSounds above.
    public Dictionary<SoundEvent, bool> MutedSounds { get; set; } = new();

    // Floating Quick-Capture Widget Settings
    public bool ShowCaptureWidget { get; set; } = true;
    public CaptureDockSide WidgetDockEdge { get; set; } = CaptureDockSide.Top;
    public int WidgetMonitorIndex { get; set; } = -1;
    public double WidgetDockPositionOffset { get; set; } = 0.5;
    public bool WidgetOpenEditor { get; set; }
    public int WidgetHoverDelayMs { get; set; } = 250;

    // Window bounds
    public double SettingsWindowLeft { get; set; } = -1;
    public double SettingsWindowTop { get; set; } = -1;
    public double SettingsWindowWidth { get; set; } = 960;
    public double SettingsWindowHeight { get; set; } = 680;
    public int SettingsWindowState { get; set; } = 0; // 0 = Normal, 2 = Maximized (WPF WindowState)

    // Video recording
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.MP4;
    public RecordingQuality RecordingQuality { get; set; } = RecordingQuality.Original;
    public int RecordingFps { get; set; } = 30;
    public bool RecordMicrophone { get; set; }
    public bool RecordDesktopAudio { get; set; } = true;
    public string? MicrophoneDeviceId { get; set; }
    public string? DesktopAudioDeviceId { get; set; }

    // Toolbar customization: which tools appear in the dock
    // null = all tools enabled (default). List of tool IDs from ToolDef.AllTools.
    public List<string>? EnabledTools { get; set; }

    // Generic hotkeys for any tool by ID. Key = tool id, Value = [modifiers, virtualKey].
    // Tools with dedicated properties (rect, ocr, picker, etc.) are mapped to those properties instead.
    public Dictionary<string, uint[]>? ToolHotkeys { get; set; }

    // Virtual key codes for in-capture annotation shortcuts: 1-9, 0, -, =, [, ]
    private static readonly uint[] AnnotationKeyVks =
    {
        0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, // 1-9
        0x30, 0xBD, 0xBB, 0xDB, 0xDD, 0xDC // 0, -, =, [, ], \
    };

    /// <summary>Compute annotation tool defaults from stable tool order.</summary>
    private Dictionary<string, uint> GetAnnotationDefaults()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["select"] = 0x31,
            ["arrow"] = 0x32,
            ["eraser"] = 0x33,
            ["draw"] = 0x34,
            ["curvedArrow"] = 0x35,
            ["line"] = 0x36,
            ["text"] = 0x37,
            ["circleShape"] = 0x38,
            ["rectShape"] = 0x39,
            ["ruler"] = 0x30,
        };

        var remainingKeys = AnnotationKeyVks.Where(k => !result.Values.Contains(k)).ToList();
        int keyIdx = 0;
        foreach (var t in ToolDef.AllTools.Where(t => t.Group == 1))
        {
            if (!result.ContainsKey(t.Id) && keyIdx < remainingKeys.Count)
                result[t.Id] = remainingKeys[keyIdx++];
        }
        return result;
    }

    /// <summary>Get hotkey (mod, key) for a tool ID, checking named properties first then dictionary.</summary>
    public (uint mod, uint key) GetToolHotkey(string toolId) => toolId switch
    {
        "rect" => (HotkeyModifiers, HotkeyKey),
        "ocr" => (OcrHotkeyModifiers, OcrHotkeyKey),
        "picker" => (PickerHotkeyModifiers, PickerHotkeyKey),
        "scan" => (ScanHotkeyModifiers, ScanHotkeyKey),
        "center" => (CenterHotkeyModifiers, CenterHotkeyKey),
        "_fullscreen" => (FullscreenHotkeyModifiers, FullscreenHotkeyKey),
        "_activeWindow" => (ActiveWindowHotkeyModifiers, ActiveWindowHotkeyKey),
        "_scrollCapture" => (ScrollCaptureHotkeyModifiers, ScrollCaptureHotkeyKey),
        "_record" => (GifHotkeyModifiers, GifHotkeyKey),
        _ => GetGenericToolHotkey(toolId),
    };

    private (uint mod, uint key) GetGenericToolHotkey(string toolId)
    {
        // Check user-customized value first (including explicit clears stored as [0,0])
        if (ToolHotkeys != null && ToolHotkeys.TryGetValue(toolId, out var v) && v.Length >= 2)
            return (v[0], v[1]);
        if (ToolDef.AllTools.Any(t => t.Id == toolId && t.Group == 1) &&
            EnabledTools is { Count: > 0 } &&
            !EnabledTools.Contains(toolId))
            return (0u, 0u);
        // Fall back to stable annotation tool defaults (always full stable order, not filtered by enabled tools).
        var defaults = GetAnnotationDefaults();
        if (defaults.TryGetValue(toolId, out var defKey))
            return (0u, defKey);
        return (0u, 0u);
    }

    /// <summary>Set hotkey (mod, key) for a tool ID.</summary>
    public void SetToolHotkey(string toolId, uint mod, uint key)
    {
        switch (toolId)
        {
            case "rect": HotkeyModifiers = mod; HotkeyKey = key; break;
            case "ocr": OcrHotkeyModifiers = mod; OcrHotkeyKey = key; break;
            case "picker": PickerHotkeyModifiers = mod; PickerHotkeyKey = key; break;
            case "scan": ScanHotkeyModifiers = mod; ScanHotkeyKey = key; break;
            case "center": CenterHotkeyModifiers = mod; CenterHotkeyKey = key; break;
            // ruler handled by generic path (annotation tool with default key 9)
            case "_fullscreen": FullscreenHotkeyModifiers = mod; FullscreenHotkeyKey = key; break;
            case "_activeWindow": ActiveWindowHotkeyModifiers = mod; ActiveWindowHotkeyKey = key; break;
            case "_scrollCapture": ScrollCaptureHotkeyModifiers = mod; ScrollCaptureHotkeyKey = key; break;
            case "_record": GifHotkeyModifiers = mod; GifHotkeyKey = key; break;
            default:
                ToolHotkeys ??= new();
                ToolHotkeys[toolId] = new[] { mod, key };
                break;
        }
    }

    public string? FindAnnotationToolId(uint mod, uint key, IEnumerable<string>? visibleToolIds = null)
    {
        if (key == 0)
            return null;

        HashSet<string>? visible = visibleToolIds != null
            ? new HashSet<string>(visibleToolIds, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var tool in ToolDef.AllTools.Where(t => t.Group == 1))
        {
            if (visible != null && !visible.Contains(tool.Id))
                continue;

            var hotkey = GetToolHotkey(tool.Id);
            if (hotkey.mod == mod && hotkey.key == key)
                return tool.Id;
        }

        return null;
    }
}

/// <summary>Definition of a toolbar tool with id, label, icon, mode, and group.</summary>
public sealed record ToolDef(string Id, string Label, char Icon, CaptureMode? Mode, int Group)
{
    /// <summary>All available tools in display order. Group 0=capture, 1=annotation.</summary>
    public static readonly ToolDef[] AllTools =
    {
        new("rect",        "Area Capture", '\uE257', CaptureMode.Rectangle, 0), // scan-line
        new("center",      "Center Select",    '\uE257', CaptureMode.Center,    0),
        new("scroll",      "Scroll Capture",   '\uE7F0', CaptureMode.ScrollCapture, 0),
        new("ocr",         "OCR",          '\uE53C', CaptureMode.Ocr,         0), // scan-text
        new("picker",      "Color Picker", '\uE2B1', CaptureMode.ColorPicker, 0), // eyedropper
        new("scan",        "QR/Barcode",   '\uE1DE', CaptureMode.Scan,        0), // qr-code
        new("record",      "Screen Recorder (MP4)", '\uE7C8', CaptureMode.Record,      0), // video
        new("recordGif",   "Screen Recorder (GIF)", '\uE790', CaptureMode.RecordGif,   0), // gif
        new("select",      "Select",       '\uE1E3', CaptureMode.Select,      1), // cursor-click   → 0x31
        new("eraser",      "Eraser",       '\uE28E', CaptureMode.Eraser,      1), // eraser         → 0x33
        new("text",        "Text",         '\uE197', CaptureMode.Text,        1), // type           → 0x37
        new("arrow",       "Arrow",        '\uE051', CaptureMode.Arrow,       1), // arrow-up-right → 0x32
        new("line",        "Line",         '\uE11F', CaptureMode.Line,        1), // minus          → 0x36
        new("draw",        "FreeHand",     '\uE70F', CaptureMode.Draw,        1), // edit           → 0x34
        new("curvedArrow", "Curved Arrow", '\uE146', CaptureMode.CurvedArrow, 1), // redo           → 0x35
        new("circleShape", "Circle",       '\uE07A', CaptureMode.CircleShape, 1), // circle         → 0x38
        new("rectShape",   "Rectangle",    '\uE16A', CaptureMode.RectShape,   1), // square         → 0x39
        new("highlight",   "Highlight",    '\uE0F7', CaptureMode.Highlight,   1), // highlighter
        new("ruler",       "Ruler",        '\uE14E', CaptureMode.Ruler,       1), // ruler          → 0x30
        new("step",        "Step Number",  '\uE1D0', CaptureMode.StepNumber,  1), // list-ordered   → 0x3A
        new("magnifier",   "Magnifier",    '\uE721', CaptureMode.Magnifier,   1),
        new("blur",        "Blur",         '\uE5A0', CaptureMode.Blur,        1), // blend
        new("emoji",       "Emoji",        '\uE167', CaptureMode.Emoji,       1), // smile
    };

    public static bool IsCaptureTool(CaptureMode mode) =>
        AllTools.Any(t => t.Mode == mode && t.Group == 0);

    public static bool IsAnnotationTool(CaptureMode mode) =>
        AllTools.Any(t => t.Mode == mode && t.Group == 1);

    public static List<string> DefaultEnabledIds() =>
        AllTools.Select(t => t.Id).ToList();

    /// <summary>All Group 1 (annotation) tool IDs â€” these go in the flyout panel.</summary>
    public static HashSet<string> FlyoutToolIds() =>
        new(AllTools.Where(t => t.Group == 1).Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
}
