namespace CyberSnap.AppModel.Settings;

public static class SettingsSchemaCatalog
{
    public static IReadOnlyList<SettingsPageDefinition> Pages { get; } =
    [
        new(
            "general",
            "General",
            "Core save behavior, startup, and default capture behavior.",
            [
                new SettingsSectionDefinition(
                    "output",
                    "Output",
                    "How captures are saved by default.",
                    [
                        new SettingDefinition("save_to_file", "Save screenshots to file", SettingsValueKind.Toggle, "Store captures in the configured save folder.", "SaveToFile"),
                        new SettingDefinition("auto_open_images", "Auto-open image in system viewer", SettingsValueKind.Toggle, "Automatically open newly taken screenshots in the system default viewer.", "AutoOpenCapturedImages"),
                        new SettingDefinition("save_directory", "Save directory", SettingsValueKind.Folder, "Default output folder for screenshots.", "SaveDirectory"),
                        new SettingDefinition("monthly_folders", "Create monthly subfolders", SettingsValueKind.Toggle, "Store captures under yyyy-MM folders inside the save directory.", "SaveInMonthlyFolders"),
                        new SettingDefinition("capture_format", "Image format", SettingsValueKind.Choice, "Default file format for new screenshots.", "CaptureImageFormat",
                        [
                            new("png", "PNG"),
                            new("jpeg", "JPEG"),
                            new("bmp", "BMP"),
                        ]),
                        new SettingDefinition("filename_template", "File name template", SettingsValueKind.Text, "Pattern used when naming new captures.", "FileNameTemplate"),
                    ]),
                new SettingsSectionDefinition(
                    "startup",
                    "Startup",
                    "App startup and update defaults.",
                    [
                        new SettingDefinition("start_with_windows", "Start with Windows", SettingsValueKind.Toggle, "Launch CyberSnap automatically when the user signs in.", "StartWithWindows"),
                        new SettingDefinition("after_capture", "After capture behavior", SettingsValueKind.Choice, "Default post-capture action.", "AfterCapture"),
                    ]),
                new SettingsSectionDefinition(
                    "standalone_ruler",
                    "Standalone ruler",
                    "Behavior of the standalone ruler tool.",
                    [
                        new SettingDefinition("ruler_capture_all", "Capture all screens", SettingsValueKind.Toggle, "When enabled, the ruler's Enter capture takes all screens. When disabled, only the screen(s) the ruler occupies are captured. Applies only to multi-monitor setups.", "RulerCaptureAllScreens"),
                    ]),
            ]),
        new(
            "capture",
            "Capture",
            "Behavior of the overlay, guides, and screenshot generation.",
            [
                new SettingsSectionDefinition(
                    "overlay",
                    "Overlay",
                    "On-screen helpers used while selecting a capture.",
                    [
                        new SettingDefinition("crosshair", "Show crosshair guides", SettingsValueKind.Toggle, "Render guide lines around the cursor while capturing.", "ShowCrosshairGuides"),
                        new SettingDefinition("magnifier", "Show capture magnifier", SettingsValueKind.Toggle, "Display a zoomed preview near the cursor.", "ShowCaptureMagnifier"),
                        new SettingDefinition("detect_windows", "Detect windows", SettingsValueKind.Toggle, "Offer window-aware selection and detection behavior.", "DetectWindows"),
                        new SettingDefinition("dock_side", "Toolbar Position", SettingsValueKind.Choice, "Choose a position for the capture toolbar.", "CaptureDockSide"),
                    ]),
                new SettingsSectionDefinition(
                    "screenshot_style",
                    "Screenshot styling",
                    "Optional post-processing applied to image captures.",
                    [
                        new SettingDefinition("style_screenshots", "Style screenshots", SettingsValueKind.Toggle, "Enable decorative styling for saved captures.", "StyleScreenshots"),
                        new SettingDefinition("shadow", "Add screenshot shadow", SettingsValueKind.Toggle, "Apply a soft shadow to styled screenshots.", "AddScreenshotShadow"),
                        new SettingDefinition("stroke", "Add screenshot stroke", SettingsValueKind.Toggle, "Apply a stroke to styled screenshots.", "AddScreenshotStroke"),
                    ]),
            ]),
        new(
            "recording",
            "Recording",
            "Video/GIF defaults and audio capture behavior.",
            [
                new SettingsSectionDefinition(
                    "recording_defaults",
                    "Recording defaults",
                    "Primary recording output settings.",
                    [
                        new SettingDefinition("recording_format", "Recording format", SettingsValueKind.Choice, "Default recording container or GIF output.", "RecordingFormat"),
                        new SettingDefinition("recording_quality", "Recording quality", SettingsValueKind.Choice, "Resolution and quality profile for recordings.", "RecordingQuality"),
                        new SettingDefinition("recording_fps", "Frame rate", SettingsValueKind.Number, "Target frames per second for new recordings.", "RecordingFps"),
                    ]),
                new SettingsSectionDefinition(
                    "audio",
                    "Audio",
                    "Microphone and desktop-audio capture options.",
                    [
                        new SettingDefinition("record_microphone", "Record microphone", SettingsValueKind.Toggle, "Capture microphone input during recordings.", "RecordMicrophone"),
                        new SettingDefinition("record_desktop_audio", "Record desktop audio", SettingsValueKind.Toggle, "Capture system audio during recordings.", "RecordDesktopAudio"),
                    ]),
            ]),
        new(
            "ocr",
            "OCR & Translation",
            "Text capture defaults and local/cloud translation runtime settings.",
            [
                new SettingsSectionDefinition(
                    "ocr_defaults",
                    "OCR defaults",
                    "Base OCR and translation selections.",
                    [
                        new SettingDefinition("ocr_language", "OCR language", SettingsValueKind.Choice, "Preferred OCR language or auto-detection.", "OcrLanguageTag"),
                        new SettingDefinition("translate_from", "Translate from", SettingsValueKind.Choice, "Default source language for translation.", "OcrDefaultTranslateFrom"),
                        new SettingDefinition("translate_to", "Translate to", SettingsValueKind.Choice, "Default target language for translation.", "OcrDefaultTranslateTo"),
                        new SettingDefinition("translation_model", "Translation model", SettingsValueKind.Choice, "Runtime used when translating OCR results.", "TranslationModel"),
                    ]),
            ]),
        new(
            "history",
            "History",
            "Retention, indexing, and search behavior for saved captures.",
            [
                new SettingsSectionDefinition(
                    "history_storage",
                    "History storage",
                    "Persistence and retention behavior for saved captures.",
                    [
                        new SettingDefinition("save_history", "Save history", SettingsValueKind.Toggle, "Track captures in local history.", "SaveHistory"),
                        new SettingDefinition("history_retention", "Retention period", SettingsValueKind.Choice, "How long captures stay in history.", "HistoryRetention"),
                        new SettingDefinition("compress_history", "Compress history", SettingsValueKind.Toggle, "Prefer compressed history image formats where applicable.", "CompressHistory"),
                        new SettingDefinition("history_click_action", "Click action", SettingsValueKind.Choice, "Action when clicking a capture thumbnail in the Gallery.", "HistoryClickAction",
                        [
                            new("open_in_editor", "Open in editor"),
                            new("copy_to_clipboard", "Copy to clipboard"),
                            new("open_in_default_viewer", "Open in default viewer"),
                        ]),
                    ]),
                new SettingsSectionDefinition(
                    "search",
                    "Search",
                    "Image indexing and search-surface behavior.",
                    [
                        new SettingDefinition("auto_index_images", "Auto-index images", SettingsValueKind.Toggle, "Continuously index images for history search.", "AutoIndexImages"),
                        new SettingDefinition("show_image_search", "Show image search bar", SettingsValueKind.Toggle, "Display the image-search UI inside history.", "ShowImageSearchBar"),
                        new SettingDefinition("search_sources", "Search sources", SettingsValueKind.Choice, "Sources used by history search.", "ImageSearchSources"),
                    ]),
            ]),
        new(
            "runtimes",
            "Stickers & Upscale",
            "Local runtime-backed media workflows.",
            [
            ]),
        new(
            "about",
            "About",
            "App status, maintenance, and migration utilities.",
            [
                new SettingsSectionDefinition(
                    "maintenance",
                    "Maintenance",
                    "App-level diagnostics and maintenance actions.",
                    [
                        new SettingDefinition("check_updates", "Check for updates", SettingsValueKind.Action, "Run an immediate update check."),
                        new SettingDefinition("export_settings", "Export settings", SettingsValueKind.Action, "Export the current settings payload."),
                        new SettingDefinition("reset_settings", "Reset settings", SettingsValueKind.Action, "Restore default settings."),
                    ]),
            ]),
    ];
}
