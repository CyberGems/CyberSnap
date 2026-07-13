using CyberSnap.Helpers;

namespace CyberSnap.Models;

public enum AfterCaptureAction
{
    CopyToClipboard,
    PreviewAndCopy,
    PreviewOnly,
    None,
    OpenInSystemViewer
}

public enum ToastPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    TopCenter,
    BottomCenter,
    // Vertically centered on the left/right edge (restored from the pre-grid selector).
    // Appended so existing serialized numeric values stay stable.
    Left,
    Right
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
    Startup,
    Achievement,
    /// <summary>Successful cloud share / upload (file: Assets/Sounds/upload.mp3).</summary>
    Upload,
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

public enum AppThemeMode
{
    System,
    Dark,
    Light,
    Grayscale
}

public enum HistoryClickAction
{
    OpenInEditor,
    CopyToClipboard,
    OpenInDefaultViewer
}

/// <summary>What happens when the user left-clicks the capture preview body on a toast.</summary>
public enum ToastPreviewClickAction
{
    OpenInEditor = 0,
    OpenInDefaultViewer = 1,
    CopyToClipboard = 2,
    Save = 3,
    OpenInGallery = 4,
    Close = 5
}

public enum CaptureKind
{
    Screenshot,
    Recording,
    Ocr,
    ColorPick,
    Scan,
    ScrollCapture,
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
        public ToastButtonSlot SaveSlot { get; set; } = ToastButtonSlot.TopInnerRight;
        public bool ShowCopy { get; set; }
        public ToastButtonSlot CopySlot { get; set; } = ToastButtonSlot.BottomInnerRight;
        public bool ShowOffice { get; set; }
        public ToastButtonSlot OfficeSlot { get; set; } = ToastButtonSlot.BottomLeft;
        public bool ShowDelete { get; set; }
        public ToastButtonSlot DeleteSlot { get; set; } = ToastButtonSlot.BottomLeft;
        public bool ShowHistory { get; set; } = true;
        public ToastButtonSlot HistorySlot { get; set; } = ToastButtonSlot.TopInnerLeft;
        public bool ShowEdit { get; set; }
        public ToastButtonSlot EditSlot { get; set; } = ToastButtonSlot.BottomInnerLeft;
    }

    public bool AllowHotkeyOverride { get; set; }

    public uint HotkeyModifiers { get; set; } = Native.User32.MOD_ALT | Native.User32.MOD_SHIFT;
    public uint HotkeyKey { get; set; } = 0x41; // Alt+Shift+A

    // OCR hotkey: unbound by default
    public uint OcrHotkeyModifiers { get; set; }
    public uint OcrHotkeyKey { get; set; }
    public string OcrLanguageTag { get; set; } = "auto";
    public int OcrModelQuality { get; set; } // 0 = Fast (~1 MB), 1 = Standard (~4 MB)
    public string OcrDefaultTranslateFrom { get; set; } = "auto";
    public string OcrDefaultTranslateTo { get; set; } = "auto";
    public bool OcrAutoCopyToClipboard { get; set; }
    public bool OcrResultWindowPinnedByDefault { get; set; } = true;
    public bool OcrTranslationPanelExpanded { get; set; }
    public string? GoogleTranslateApiKey { get; set; }
    public bool TranslationRuntimeInstalled { get; set; }
    public int TranslationModel { get; set; } = 3; // 1 = Google, 3 = MyMemory (free web)
    public bool AnnotationStrokeShadow { get; set; } = true;
    public float StrokeWidth { get; set; } = 6f;
    public int ToolColorArgb { get; set; } = System.Drawing.Color.FromArgb(0, 136, 255).ToArgb(); // Default blue
    // Color picked for shapes/annotations in the post-capture Editor. Kept separate from the
    // in-capture overlay's ToolColorArgb so each remembers its own choice. Default = editor accent (cyan).
    public int EditorToolColorArgb { get; set; } = System.Drawing.Color.FromArgb(0, 255, 255).ToArgb();
    public int EditorCustomColorArgb { get; set; } = 0; // 0 means no custom color configured yet
    // Font size last chosen for the Text tool in the post-capture Editor (pixels). Editor-specific,
    // like EditorToolColorArgb. Clamped to the same 10..120 range the Text toolbar enforces.
    public float EditorTextFontSize { get; set; } = 24f;

    // Color picker hotkey: unbound by default
    public uint PickerHotkeyModifiers { get; set; }
    public uint PickerHotkeyKey { get; set; }

    // Optional custom-tool hotkeys: unbound by default
    public uint ScanHotkeyModifiers { get; set; }
    public uint ScanHotkeyKey { get; set; }
    public uint CenterHotkeyModifiers { get; set; }
    public uint CenterHotkeyKey { get; set; }
    public uint FullscreenHotkeyModifiers { get; set; }
    public uint FullscreenHotkeyKey { get; set; }
    public uint ActiveWindowHotkeyModifiers { get; set; }
    public uint ActiveWindowHotkeyKey { get; set; }
    public uint RepeatLastAreaHotkeyModifiers { get; set; }
    public uint RepeatLastAreaHotkeyKey { get; set; }
    public uint RulerHotkeyModifiers { get; set; }
    public uint RulerHotkeyKey { get; set; }

    // Toolbar Screen Recorder (MP4): unbound by default
    public uint RecordHotkeyModifiers { get; set; }
    public uint RecordHotkeyKey { get; set; }

    // Toolbar Screen Recorder (GIF): unbound by default
    public uint RecordGifHotkeyModifiers { get; set; }
    public uint RecordGifHotkeyKey { get; set; }

    // Scrolling capture hotkey: unbound by default
    public uint ScrollCaptureHotkeyModifiers { get; set; }
    public uint ScrollCaptureHotkeyKey { get; set; }

    // GIF recording global hotkey: unbound by default
    public uint GifHotkeyModifiers { get; set; }
    public uint GifHotkeyKey { get; set; }
    public int GifFps { get; set; } = 15;

    // Standalone ruler hotkey: unbound by default
    // Naming convention for future standalone tools:
    //   public uint Standalone{Name}HotkeyModifiers { get; set; }
    //   public uint Standalone{Name}HotkeyKey { get; set; }
    public uint StandaloneRulerHotkeyModifiers { get; set; }
    public uint StandaloneRulerHotkeyKey { get; set; }

    /// <summary>When true, the standalone ruler's screen capture (Enter) captures all screens.
    /// When false (default), only the screen(s) the ruler occupies are captured.</summary>
    public bool RulerCaptureAllScreens { get; set; }

    /// <summary>When true (default), right-click on the standalone ruler shows a context menu.
    /// When false, right-click exits the tool immediately.</summary>
    public bool RulerContextMenuEnabled { get; set; } = true;

    // Standalone color picker hotkey: unbound by default
    public uint StandaloneColorPickerHotkeyModifiers { get; set; }
    public uint StandaloneColorPickerHotkeyKey { get; set; }

    // Standalone OCR hotkey: unbound by default
    public uint StandaloneOcrHotkeyModifiers { get; set; }
    public uint StandaloneOcrHotkeyKey { get; set; }

    // Standalone QR/Barcode scan hotkey: unbound by default
    public uint StandaloneScanHotkeyModifiers { get; set; }
    public uint StandaloneScanHotkeyKey { get; set; }

    public AfterCaptureAction AfterCapture { get; set; } = AfterCaptureAction.PreviewAndCopy;
    public bool OpenEditorAfterCapture { get; set; }
    public bool OpenVideoTrimmerAfterCapture { get; set; }
    /// <summary>Last volume level used in the video trimmer (0.0–1.0).</summary>
    public double VideoTrimmerVolume { get; set; } = 1.0;
    /// <summary>When true, exported trimmer output excludes the audio track.</summary>
    public bool VideoTrimmerExportMuted { get; set; }
    // Editor: when a capture loads, auto-fit it to the canvas (true) or show it at real 100% size (false).
    public bool EditorFitToWindowOnOpen { get; set; } = true;
    public bool EditorPanModeLockObjects { get; set; } = true;
    public bool EditorShowBanners { get; set; } = true;
    /// <summary>Show the welcome overlay on a pristine blank canvas in the editor.</summary>
    public bool EditorShowWelcomeBanner { get; set; } = true;
    public bool EditorAutoCropControls { get; set; } = true;
    public bool EditorShowResizeHandles { get; set; } = true;
    // When dragging the canvas resize handles: false = extend/trim the canvas area only
    // (default), true = scale (resample) the image and annotations to the new size.
    public bool EditorResizeHandlesScaleContent { get; set; }
    // When true, the "Don't show again" checkbox was checked in the handle-drag resize
    // confirmation dialog, so future handle-drag resizes apply immediately.
    public bool EditorSuppressResizeConfirm { get; set; }
    // When true, the "Don't show again" checkbox was checked in the paste-replace
    // confirmation dialog, so future pastes replace the document without asking.
    public bool EditorSuppressPasteConfirm { get; set; }
    /// <summary>Maximum undo steps in the editor (1–200). Default 100.</summary>
    public int EditorUndoLimit { get; set; } = 100;
    /// <summary>Preferred image export format: 0 = PNG, 1 = JPEG.</summary>
    public int EditorExportFormat { get; set; } // 0 = PNG, 1 = JPEG
    public bool EditorShowRulers { get; set; } = true;
    public bool EditorShowFrame { get; set; } = true;
    public bool EditorShowHints { get; set; } = true;
    /// <summary>Show cursor X,Y coordinates in the editor status bar.</summary>
    public bool EditorShowCoordinates { get; set; } = true;
    /// <summary>Show hover tooltips on editor toolbar and status-bar controls.</summary>
    public bool EditorShowTooltips { get; set; } = true;
    public bool EditorShowScrollbars { get; set; }
    /// <summary>Last solid fill chosen in the New Canvas dialog; 0 = transparent checkerboard.</summary>
    public int EditorNewCanvasBackgroundColorArgb { get; set; }

    // ── Upload / Share (core model; Settings UI in PR 2) ──────────────────
    /// <summary>Default share host. Imgur is only valid when the user supplied a Client-ID.</summary>
    public UploadProviderKind UploadDefaultProvider { get; set; } = UploadProviderKind.ImgBB;
    /// <summary>Last selected sub-tab in Settings → Uploads: general | imgbb | imgur | custom.</summary>
    public string UploadSettingsSubTab { get; set; } = "general";
    public UploadCustomProtocol UploadCustomProtocol { get; set; } = UploadCustomProtocol.Sftp;
    public bool UploadUseCustomImgurClientId { get; set; }
    public string? UploadImgurClientId { get; set; }
    public bool UploadUseCustomImgBBApiKey { get; set; }
    public string? UploadImgBBApiKey { get; set; }
    public UploadImageFormatPreference UploadImageFormat { get; set; } = UploadImageFormatPreference.Png;
    public int UploadJpegQuality { get; set; } = 90;
    public int UploadMaxBytes { get; set; } = 10 * 1024 * 1024;
    public int UploadMinIntervalMs { get; set; } = 3000;
    /// <summary>Soft daily cap for OOTB (shared) credentials only; 0 = disabled. Custom keys unlimited.</summary>
    public int UploadDailyCapOotb { get; set; } = 50;
    public int UploadHttpTimeoutSeconds { get; set; } = 120;
    public bool UploadOpenUrlAfterSuccess { get; set; }
    public bool UploadSuppressThirdPartyConfirm { get; set; }
    public bool UploadOverwriteOnCollision { get; set; } = true;
    public bool UploadUniqueSuffixOnCollision { get; set; }
    public bool UploadAutoCreateRemoteDirectory { get; set; } = true;
    public string UploadCustomHost { get; set; } = "";
    public int UploadCustomPort { get; set; }
    public string UploadCustomUsername { get; set; } = "";
    public string? UploadCustomPassword { get; set; }
    public string UploadCustomRemoteDirectory { get; set; } = "";
    public string UploadCustomPublicUrlBase { get; set; } = "";
    public bool UploadFtpPassive { get; set; } = true;
    public bool UploadFtpUseTls { get; set; } = true;
    public bool UploadFtpAllowInsecureCertificate { get; set; }
    public string? UploadSftpPrivateKeyPath { get; set; }
    public string? UploadSftpPrivateKeyPassphrase { get; set; }
    public string? UploadSftpTrustedHostKeySha256 { get; set; }
    public string UploadS3Endpoint { get; set; } = "";
    public string UploadS3Region { get; set; } = "";
    public string UploadS3Bucket { get; set; } = "";
    public string? UploadS3AccessKey { get; set; }
    public string? UploadS3SecretKey { get; set; }
    public string? UploadS3SessionToken { get; set; }
    public string UploadS3KeyPrefix { get; set; } = "";
    public bool UploadS3ForcePathStyle { get; set; }
    public bool UploadS3MakePublic { get; set; }

    /// <summary>Most recently opened files (projects and images) shown in the Editor's
    /// burger "Open recent" submenu. Newest first; capped to 6 entries.</summary>
    public List<string> RecentFilePaths { get; set; } = new();

    /// <summary>Most recently selected colors in the color picker. Hex format. Capped to 12 entries.</summary>
    public List<string> RecentColors { get; set; } = new();

    public bool SaveToFile { get; set; } = true;
    public bool AskForFileNameOnSave { get; set; }
    public string FileNameTemplate { get; set; } = Helpers.FileNameTemplate.DefaultTemplate;
    public CaptureImageFormat CaptureImageFormat { get; set; } = CaptureImageFormat.Png;
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
    public bool HasLastCaptureRect { get; set; }
    public int LastCaptureRectX { get; set; }
    public int LastCaptureRectY { get; set; }
    public int LastCaptureRectWidth { get; set; }
    public int LastCaptureRectHeight { get; set; }
    public WindowDetectionMode WindowDetection { get; set; } = WindowDetectionMode.WindowOnly;
    public CaptureDockSide CaptureDockSide { get; set; } = CaptureDockSide.Bottom;
    public ScrollingCaptureMode ScrollingCaptureMode { get; set; } = ScrollingCaptureMode.AssistAutoscroll;
    public int CaptureDelaySeconds { get; set; }
    public bool SaveHistory { get; set; } = true;
    public bool SaveStandaloneToHistory { get; set; } = true;
    public bool MuteSounds { get; set; }
    public bool DisableAnimations { get; set; }
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Dark;
    /// <summary>Real UI LayoutTransform factor (1.0–1.4). Default 1.1 is shown as "100%" in Settings.</summary>
    public double UiScale { get; set; } = 1.1;
    public string InterfaceLanguage { get; set; } = "auto";
    public bool ShowCrosshairGuides { get; set; } = true;
    public bool ShowToolBanners { get; set; } = true;
    public bool ConfirmBeforeExit { get; set; } = true;
    public bool HasSeenCaptureBanner { get; set; }
    public bool ShowCursor { get; set; }
    public bool ShowCaptureMagnifier { get; set; } = true;
    public bool ShowSelectionSize { get; set; } = true;
    public bool OverlayCaptureAllMonitors { get; set; } = true;
    public bool DetectWindows { get; set; } = true;
    public bool ConfirmRegionBeforeCapture { get; set; } = true;
    public bool CompressHistory { get; set; }
    public int JpegQuality { get; set; } = 85;
    public bool HasCompletedSetup { get; set; }
    public ToastPosition ToastPosition { get; set; } = ToastPosition.TopCenter;

    // Celebration mode: occasional milestone toasts get a flourish. Master toggle.
    public bool CelebrationsEnabled { get; set; } = true;
    // Local date (yyyy-MM-dd) of the last "first capture of the day" celebration.
    public string? LastCelebrationDate { get; set; }
    // Running total of all captures (image, OCR, and video/GIF recordings alike). Drives
    // milestone celebrations (50, 100, 250, ...) and the milestone rail in Settings.
    public int CelebrationCaptureCount { get; set; }
    // Per-type capture counters. Start at 0 for new installs; retroactively bootstrapped
    // from the gallery on first load for existing users. All are incremented in RegisterCapture.
    public int ScreenshotCount { get; set; }
    public int RecordingCount { get; set; }
    public int OcrCount { get; set; }
    public int ColorPickCount { get; set; }
    public int ScanCount { get; set; }
    public int ScrollCaptureCount { get; set; }
    // Highest milestone value the user has already seen acknowledged in the Settings rail.
    // When HighestAchieved(count) exceeds this, the rail flares as "new" until viewed.
    public int LastSeenMilestone { get; set; }
    // Consecutive days with at least one capture. Bumped on the first capture of each day when
    // it directly follows the previous capture day; reset to 1 after a gap. Drives streak toasts
    // and the streak shown in the Settings rail.
    public int CurrentStreak { get; set; }
    // Best streak ever reached (kept for records / future display).
    public int LongestStreak { get; set; }
    // First-time achievement flags — set once the user first performs each action, used to
    // unlock the matching "first time" medal in the Achievements tab.
    public bool HasFirstOcr { get; set; }
    public bool HasFirstRecording { get; set; }
    public bool HasFirstScrollingCapture { get; set; }
    public bool HasFirstColorPicker { get; set; }
    public bool HasFirstScan { get; set; }
    public bool HasFirstRuler { get; set; }
    public bool HasFirstEditor { get; set; }
    public int ToastMonitorIndex { get; set; } = -1; // -1 = Auto/Follow, 0+ = fixed index
    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Rectangle;
    public CenterSelectionAspectRatio CenterSelectionAspectRatio { get; set; } = CenterSelectionAspectRatio.Free;
    public bool ShowToolNumberBadges { get; set; } = true;
    public HistoryRetentionPeriod HistoryRetention { get; set; } = HistoryRetentionPeriod.Never;
    public int HistoryCountLimit { get; set; } = 0;
    public bool HistoryDeleteOriginalOnPrune { get; set; }
    public ImageSearchSourceOptions ImageSearchSources { get; set; } = ImageSearchSourceOptions.All;
    public bool ShowImageSearchBar { get; set; } = true;
    public bool ShowAutoPrune { get; set; }
    public bool ImageSearchExactMatch { get; set; }
    public bool AutoIndexImages { get; set; } = false;
    public int HistoryCategoryFilter { get; set; } = 0; // 0=All (default)
    public HistoryClickAction HistoryClickAction { get; set; } = HistoryClickAction.OpenInEditor;

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
    /// <summary>Action when left-clicking the toast capture preview (not the action buttons).</summary>
    public ToastPreviewClickAction ToastPreviewClickAction { get; set; } = ToastPreviewClickAction.OpenInEditor;
    public ToastButtonLayoutSettings ToastButtons { get; set; } = new();
    public Dictionary<string, string> OpenWithApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Per-sound customization: null = use built-in default MP3, string = path to user's custom MP3.
    public Dictionary<SoundEvent, string?> CustomSounds { get; set; } = new();
    // Per-sound mute: true = that specific sound is silenced. Master mute is MuteSounds above.
    public Dictionary<SoundEvent, bool> MutedSounds { get; set; } = new();

    // Floating Quick-Capture Widget Settings
    public bool ShowCaptureWidget { get; set; } = true;
    public bool WidgetAlwaysOnTop { get; set; } = true;
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

    // Editor toolbar hotkeys (Settings → Hotkeys → Annotations Editor). Key = editor tool id, Value = [mod, vk].
    public Dictionary<string, uint[]>? EditorToolHotkeys { get; set; }

    // Editor view/zoom hotkeys (Settings → Hotkeys → Annotations Editor → View). Key = view id, Value = [mod, vk].
    public Dictionary<string, uint[]>? EditorViewHotkeys { get; set; }

    // Virtual key codes for in-capture annotation shortcuts: F1-F12, 1, 2, 3
    private static readonly uint[] AnnotationKeyVks =
    {
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, // F1-F12
        0x31, 0x32, 0x33 // 1, 2, 3
    };

    private Dictionary<string, uint> GetAnnotationDefaults()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["select"] = 0x70,      // F1 (Move & Resize)
            ["eraser"] = 0x71,      // F2 (Eraser)
            ["text"] = 0x72,        // F3 (Text)
            ["arrow"] = 0x73,       // F4 (Arrow)
            ["line"] = 0x74,        // F5 (Line)
            ["draw"] = 0x75,        // F6 (FreeHand)
            ["curvedArrow"] = 0x76, // F7 (Curved Arrow)
            ["circleShape"] = 0x77, // F8 (Circle)
            ["rectShape"] = 0x78,   // F9 (Rectangle)
            ["highlight"] = 0x79,   // F10 (Highlight)
            ["step"] = 0x7A,        // F11 (Step Number)
            ["magnifier"] = 0x7B,   // F12 (Magnifier)
            ["blur"] = 0x31,        // 1 (Blur)
            ["emoji"] = 0x32,       // 2 (Emoji)
            ["ocr"] = 0x4F,         // O (OCR)
            ["picker"] = 0x43,      // C (Color Picker)
            ["scan"] = 0x51,        // Q (QR & Barcodes)
            ["ruler"] = 0x52,       // R (Ruler)
        };
        return result;
    }

    private Dictionary<string, uint> GetEditorToolDefaults() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["editorPan"] = 0x20,         // Space
            ["editorMove"] = 0x70,        // F1
            ["editorEraser"] = 0x71,      // F2
            ["editorText"] = 0x72,        // F3
            ["editorArrow"] = 0x73,       // F4
            ["editorLine"] = 0x74,        // F5
            ["editorDraw"] = 0x75,        // F6
            ["editorCurvedArrow"] = 0x76, // F7
            ["editorCircle"] = 0x77,      // F8
            ["editorRect"] = 0x78,        // F9
            ["editorCrop"] = 0x33,        // 3
            ["editorHighlight"] = 0x79,   // F10
            ["editorStep"] = 0x7A,        // F11
            ["editorMagnifier"] = 0x7B,   // F12
            ["editorBlur"] = 0x31,        // 1
            ["editorEmoji"] = 0x32,       // 2
        };

    /// <summary>Get hotkey (mod, key) for an editor toolbar tool.</summary>
    public (uint mod, uint key) GetEditorToolHotkey(string toolId)
    {
        if (EditorToolHotkeys != null && EditorToolHotkeys.TryGetValue(toolId, out var v) && v.Length >= 2)
            return (v[0], v[1]);
        if (GetEditorToolDefaults().TryGetValue(toolId, out var defKey))
            return (0u, defKey);
        return (0u, 0u);
    }

    public void SetEditorToolHotkey(string toolId, uint mod, uint key)
    {
        EditorToolHotkeys ??= new();
        EditorToolHotkeys[toolId] = new[] { mod, key };
    }

    private Dictionary<string, uint> GetEditorViewDefaults() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["editorZoomIn"] = 0x38,    // 8
            ["editorZoomOut"] = 0x37,   // 7
            ["editorZoomReset"] = 0x30, // 0
            ["editorZoomFit"] = 0x39,   // 9
        };

    public (uint mod, uint key) GetEditorViewHotkey(string viewId)
    {
        if (EditorViewHotkeys != null && EditorViewHotkeys.TryGetValue(viewId, out var v) && v.Length >= 2)
            return (v[0], v[1]);
        if (GetEditorViewDefaults().TryGetValue(viewId, out var defKey))
            return (0u, defKey);
        return (0u, 0u);
    }

    public void SetEditorViewHotkey(string viewId, uint mod, uint key)
    {
        EditorViewHotkeys ??= new();
        EditorViewHotkeys[viewId] = new[] { mod, key };
    }

    public string? FindEditorToolId(uint mod, uint key)
    {
        if (key == 0)
            return null;

        foreach (var (id, _, _) in EditorToolHotkeyDef.Tools)
        {
            var hotkey = GetEditorToolHotkey(id);
            if (hotkey.mod == mod && hotkey.key == key)
                return id;
        }

        return null;
    }

    /// <summary>Get hotkey (mod, key) for a tool ID, checking named properties first then dictionary.</summary>
    public (uint mod, uint key) GetToolHotkey(string toolId) => toolId switch
    {
        "rect" => (HotkeyModifiers, HotkeyKey),
        "ocr" => (OcrHotkeyModifiers, OcrHotkeyKey),
        "picker" => (PickerHotkeyModifiers, PickerHotkeyKey),
        "scan" => (ScanHotkeyModifiers, ScanHotkeyKey),
        "center" => (CenterHotkeyModifiers, CenterHotkeyKey),
        "record" => (RecordHotkeyModifiers, RecordHotkeyKey),
        "recordGif" => (RecordGifHotkeyModifiers, RecordGifHotkeyKey),
        "_fullscreen" => (FullscreenHotkeyModifiers, FullscreenHotkeyKey),
        "_activeWindow" => (ActiveWindowHotkeyModifiers, ActiveWindowHotkeyKey),
        "_repeatLastArea" => (RepeatLastAreaHotkeyModifiers, RepeatLastAreaHotkeyKey),
        "_scrollCapture" => (ScrollCaptureHotkeyModifiers, ScrollCaptureHotkeyKey),
        "_record" => (GifHotkeyModifiers, GifHotkeyKey),
        // Standalone tools use the convention: "_standalone{Name}" → Standalone{Name}HotkeyModifiers/Key
        "_standaloneRuler" => (StandaloneRulerHotkeyModifiers, StandaloneRulerHotkeyKey),
        "_standaloneColorPicker" => (StandaloneColorPickerHotkeyModifiers, StandaloneColorPickerHotkeyKey),
        "_standaloneOcr" => (StandaloneOcrHotkeyModifiers, StandaloneOcrHotkeyKey),
        "_standaloneScan" => (StandaloneScanHotkeyModifiers, StandaloneScanHotkeyKey),
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
            case "record": RecordHotkeyModifiers = mod; RecordHotkeyKey = key; break;
            case "recordGif": RecordGifHotkeyModifiers = mod; RecordGifHotkeyKey = key; break;
            // ruler handled by generic path (annotation tool, no default hotkey)
            case "_fullscreen": FullscreenHotkeyModifiers = mod; FullscreenHotkeyKey = key; break;
            case "_activeWindow": ActiveWindowHotkeyModifiers = mod; ActiveWindowHotkeyKey = key; break;
            case "_repeatLastArea": RepeatLastAreaHotkeyModifiers = mod; RepeatLastAreaHotkeyKey = key; break;
            case "_scrollCapture": ScrollCaptureHotkeyModifiers = mod; ScrollCaptureHotkeyKey = key; break;
            case "_record": GifHotkeyModifiers = mod; GifHotkeyKey = key; break;
            // Standalone tools: "_standalone{Name}" → Standalone{Name}HotkeyModifiers/Key
            case "_standaloneRuler": StandaloneRulerHotkeyModifiers = mod; StandaloneRulerHotkeyKey = key; break;
            case "_standaloneColorPicker": StandaloneColorPickerHotkeyModifiers = mod; StandaloneColorPickerHotkeyKey = key; break;
            case "_standaloneOcr": StandaloneOcrHotkeyModifiers = mod; StandaloneOcrHotkeyKey = key; break;
            case "_standaloneScan": StandaloneScanHotkeyModifiers = mod; StandaloneScanHotkeyKey = key; break;
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

        foreach (var tool in ToolDef.AllTools.Where(t => t.Group is 0 or 1))
        {
            if (visible != null && !visible.Contains(tool.Id))
                continue;

            var hotkey = GetToolHotkey(tool.Id);
            if (hotkey.mod == mod && hotkey.key == key)
                return tool.Id;
        }

        return null;
    }

    public void ResetToDefaultHotkeys()
    {
        HotkeyModifiers = Native.User32.MOD_ALT | Native.User32.MOD_SHIFT;
        HotkeyKey = 0x41;
        OcrHotkeyModifiers = 0;
        OcrHotkeyKey = 0x4F;        // O
        PickerHotkeyModifiers = 0;
        PickerHotkeyKey = 0x43;     // C
        ScanHotkeyModifiers = 0;
        ScanHotkeyKey = 0x51;       // Q
        CenterHotkeyModifiers = 0;
        CenterHotkeyKey = 0;
        FullscreenHotkeyModifiers = 0;
        FullscreenHotkeyKey = 0;
        ActiveWindowHotkeyModifiers = 0;
        ActiveWindowHotkeyKey = 0;
        RepeatLastAreaHotkeyModifiers = 0;
        RepeatLastAreaHotkeyKey = 0;
        RulerHotkeyModifiers = 0;
        RulerHotkeyKey = 0x52;      // R
        RecordHotkeyModifiers = 0;
        RecordHotkeyKey = 0;
        RecordGifHotkeyModifiers = 0;
        RecordGifHotkeyKey = 0;
        ScrollCaptureHotkeyModifiers = 0;
        ScrollCaptureHotkeyKey = 0;
        GifHotkeyModifiers = 0;
        GifHotkeyKey = 0;
        StandaloneRulerHotkeyModifiers = 0;
        StandaloneRulerHotkeyKey = 0;
        StandaloneColorPickerHotkeyModifiers = 0;
        StandaloneColorPickerHotkeyKey = 0;
        StandaloneOcrHotkeyModifiers = 0;
        StandaloneOcrHotkeyKey = 0;
        StandaloneScanHotkeyModifiers = 0;
        StandaloneScanHotkeyKey = 0;
        ToolHotkeys = null;
        EditorToolHotkeys = null;
        EditorViewHotkeys = null;
    }
}

/// <summary>Definition of a toolbar tool with id, label, icon, mode, and group.</summary>
public sealed record ToolDef(string Id, string Label, char Icon, CaptureMode? Mode, int Group)
{
    /// <summary>All available tools in display order. Group 0=capture, 1=annotation.</summary>
    public static readonly ToolDef[] AllTools =
    {
        new("rect",        "Area Capture", '\uE257', CaptureMode.Rectangle, 0), // scan-line
        new("center",      "From Center",    '\uE257', CaptureMode.Center,    0),
        new("scroll",      "Scroll Capture",   '\uE7F0', CaptureMode.ScrollCapture, 0),
        new("record",      "Screen Recorder (MP4)", '\uE7C8', CaptureMode.Record,      0), // video
        new("recordGif",   "Screen Recorder (GIF)", '\uE790', CaptureMode.RecordGif,   0), // gif
        new("ocr",         "OCR",          '\uE53C', CaptureMode.Ocr,         0), // scan-text
        new("scan",        "QR & Barcodes",   '\uE1DE', CaptureMode.Scan,        0), // qr-code
        new("picker",      "Color Picker", '\uE2B1', CaptureMode.ColorPicker, 0), // eyedropper
        new("ruler",       "Ruler",        '\uE14E', CaptureMode.Ruler,       0), // ruler (annotation, but kept in capture row)
        new("select",      "Pick",                 '\uE1E3', CaptureMode.Move,        1),
        new("eraser",      "Eraser",       '\uE28E', CaptureMode.Eraser,      1),
        new("text",        "Text",         '\uE197', CaptureMode.Text,        1),
        new("arrow",       "Arrow",        '\uE051', CaptureMode.Arrow,       1),
        new("line",        "Line",         '\uE11F', CaptureMode.Line,        1),
        new("draw",        "FreeHand",     '\uE70F', CaptureMode.Draw,        1),
        new("curvedArrow", "Curved Arrow", '\uE146', CaptureMode.CurvedArrow, 1),
        new("circleShape", "Circle",       '\uE07A', CaptureMode.CircleShape, 1),
        new("rectShape",   "Rectangle",    '\uE16A', CaptureMode.RectShape,   1),
        new("highlight",   "Highlight",    '\uE0F7', CaptureMode.Highlight,   1),
        new("step",        "Steps",       '\uE1D0', CaptureMode.StepNumber,  1),
        new("magnifier",   "Magnifier",    '\uE721', CaptureMode.Magnifier,   1),
        new("blur",        "Blur",         '\uE5A0', CaptureMode.Blur,        1),
        new("emoji",       "Emoji",        '\uE167', CaptureMode.Emoji,       1),
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
