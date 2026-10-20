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
                    "saving",
                    "Saving",
                    "Where captures are stored and how files are named. Enable Save file under Capture to write to disk.",
                    [
                        new SettingDefinition("save_to_file", "Save file (images)", SettingsValueKind.Toggle, "Write image captures to the configured save folder.", "SaveToFile"),
                        new SettingDefinition("save_video_to_file", "Save file (video)", SettingsValueKind.Toggle, "Write MP4 recordings to the configured save folder.", "SaveVideoToFile"),
                        new SettingDefinition("save_gif_to_file", "Save file (GIF)", SettingsValueKind.Toggle, "Write GIF recordings to the configured save folder.", "SaveGifToFile"),
                        new SettingDefinition("save_directory", "Save folder", SettingsValueKind.Folder, "Default output folder for screenshots.", "SaveDirectory"),
                        new SettingDefinition("monthly_folders", "Create monthly subfolders", SettingsValueKind.Toggle, "Store captures under yyyy-MM folders inside the save directory.", "SaveInMonthlyFolders"),
                        new SettingDefinition("filename_template", "File name pattern", SettingsValueKind.Text, "Pattern used when naming new captures.", "FileNameTemplate"),
                        new SettingDefinition("ask_file_name", "Ask for file name every time", SettingsValueKind.Toggle, "Prompt for a file name before each saved capture.", "AskForFileNameOnSave"),
                    ]),
                new SettingsSectionDefinition(
                    "startup",
                    "Startup",
                    "App startup and update defaults.",
                    [
                        new SettingDefinition("start_with_windows", "Start with Windows", SettingsValueKind.Toggle, "Launch CyberSnap automatically when the user signs in.", "StartWithWindows"),
                        new SettingDefinition("pin_tray_icon", "Keep CyberSnap visible in the tray", SettingsValueKind.Action, "Windows hides new tray icons behind the overflow (^). Drag CyberSnap onto the taskbar, or pin it in Windows Settings."),
                        new SettingDefinition("setup_wizard", "Setup wizard", SettingsValueKind.Action, "Review language, theme, capture options, and hotkeys from the first-launch walkthrough."),
                    ]),
                new SettingsSectionDefinition(
                    "behavior_after_captures",
                    "After capture",
                    "What happens when an image capture is confirmed.",
                    [
                        new SettingDefinition("after_capture", "When finished", SettingsValueKind.Choice, "Steps that run after an image capture is confirmed.", "AfterCapture"),
                        new SettingDefinition("image_auto_copy", "Auto-copy screenshots", SettingsValueKind.Toggle, "Copy each finished screenshot to the clipboard.", "ImageAutoCopyToClipboard"),
                        new SettingDefinition("ocr_auto_copy", "Auto-copy OCR text", SettingsValueKind.Toggle, "Copy recognized text to the clipboard.", "OcrAutoCopyToClipboard"),
                        new SettingDefinition("scan_auto_copy", "Auto-copy QR/barcode results", SettingsValueKind.Toggle, "Copy decoded QR and barcode content to the clipboard.", "ScanAutoCopyToClipboard"),
                    ]),
                new SettingsSectionDefinition(
                    "standalone_ruler",
                    "Standalone ruler",
                    "Behavior of the standalone ruler tool.",
                    [
                        new SettingDefinition("ruler_capture_all", "Capture all screens", SettingsValueKind.Toggle, "When enabled, the ruler's Enter capture takes all screens. When disabled, only the screen(s) the ruler occupies are captured. Applies only to multi-monitor setups.", "RulerCaptureAllScreens"),
                        new SettingDefinition("ruler_context_menu", "Enable context menu", SettingsValueKind.Toggle, "When disabled, right-click exits the ruler instead of showing the context menu.", "RulerContextMenuEnabled"),
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
                        new SettingDefinition("scroll_auto_copy", "Auto-copy scrolling captures", SettingsValueKind.Toggle, "Copy each finished scrolling capture to the clipboard.", "ScrollAutoCopyToClipboard"),
                        new SettingDefinition("scroll_preview", "Show preview after scrolling capture", SettingsValueKind.Toggle, "Open the scrolling capture preview independently of screenshots.", "ShowScrollCapturePreview"),
                        new SettingDefinition("color_auto_copy", "Auto-copy picked colors", SettingsValueKind.Toggle, "Copy the picked color in its favorite format.", "ColorDetailAutoCopy"),
                        new SettingDefinition("color_window", "Show the color result window", SettingsValueKind.Toggle, "Open the result window after picking a color.", "ShowColorDetailWindow"),
                    ]),
                new SettingsSectionDefinition(
                    "image_output",
                    "Image output",
                    "File format, quality, and size for image captures.",
                    [
                        new SettingDefinition("capture_format", "Default format", SettingsValueKind.Choice, "Default file format for new screenshots.", "CaptureImageFormat",
                        [
                            new("png", "PNG"),
                            new("jpeg", "JPEG"),
                            new("bmp", "BMP"),
                        ]),
                        new SettingDefinition("jpeg_quality", "JPG quality", SettingsValueKind.Number, "JPEG compression quality for image captures.", "JpegQuality"),
                        new SettingDefinition("capture_max_long_edge", "Max image size", SettingsValueKind.Number, "Resize oversized captures so the longest edge stays within this limit.", "CaptureMaxLongEdge"),
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
            "Video & GIF",
            "Independent MP4 and GIF recording defaults.",
            [
                new SettingsSectionDefinition(
                    "video_recording_mp4",
                    "Video recording (MP4)",
                    "Resolution, FPS, cursor, after-recording steps, and audio for MP4.",
                    [
                        new SettingDefinition("recording_quality", "Quality", SettingsValueKind.Choice, "Maximum resolution for MP4 recordings.", "RecordingQuality"),
                        new SettingDefinition("recording_fps", "Video FPS", SettingsValueKind.Number, "Frames per second for MP4 recordings.", "RecordingFps"),
                        new SettingDefinition("video_show_cursor", "Show cursor in video", SettingsValueKind.Toggle, "Include the mouse pointer in MP4 recordings.", "VideoShowCursor"),
                        new SettingDefinition("open_video_trimmer", "Open trimmer after video", SettingsValueKind.Toggle, "Open the video trimmer when an MP4 recording finishes.", "OpenVideoTrimmerAfterCapture"),
                        new SettingDefinition("video_auto_copy", "Auto-copy video", SettingsValueKind.Toggle, "Copy the finished MP4 to the clipboard.", "VideoAutoCopyToClipboard"),
                        new SettingDefinition("record_microphone", "Record microphone", SettingsValueKind.Toggle, "Capture microphone input during MP4 recordings.", "RecordMicrophone"),
                        new SettingDefinition("record_desktop_audio", "Record desktop audio", SettingsValueKind.Toggle, "Capture system audio during MP4 recordings.", "RecordDesktopAudio"),
                    ]),
                new SettingsSectionDefinition(
                    "gif_recording",
                    "GIF recording",
                    "FPS, cursor, and after-recording steps for GIF.",
                    [
                        new SettingDefinition("gif_fps", "GIF FPS", SettingsValueKind.Number, "Frames per second for GIF recordings (15 or 30).", "GifFps"),
                        new SettingDefinition("gif_show_cursor", "Show cursor in GIF", SettingsValueKind.Toggle, "Include the mouse pointer in GIF recordings.", "GifShowCursor"),
                        new SettingDefinition("open_gif_trimmer", "Open trimmer after GIF", SettingsValueKind.Toggle, "Open the trimmer when a GIF recording finishes.", "OpenGifTrimmerAfterCapture"),
                        new SettingDefinition("gif_auto_copy", "Auto-copy GIF", SettingsValueKind.Toggle, "Copy the finished GIF to the clipboard.", "GifAutoCopyToClipboard"),
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
            "uploads",
            "Uploads",
            "Cloud share destinations, API keys, and custom FTP/SFTP/S3 settings.",
            [
                new SettingsSectionDefinition(
                    "share_defaults",
                    "Share defaults",
                    "Default host and encoding for Share.",
                    [
                        new SettingDefinition("default_provider", "Default share destination", SettingsValueKind.Choice, "Host used when Share is clicked.", "UploadDefaultProvider"),
                        new SettingDefinition("upload_format", "Upload image format", SettingsValueKind.Choice, "PNG or JPEG for shared images.", "UploadImageFormat"),
                        new SettingDefinition("open_url_after_success", "Open link after upload", SettingsValueKind.Toggle, "Open the public URL after a successful share.", "UploadOpenUrlAfterSuccess"),
                    ]),
                new SettingsSectionDefinition(
                    "cybergems",
                    "CyberSnap Share",
                    "Default temporary public links at cybersnap.cybergems.org (48h TTL).",
                    [
                        new SettingDefinition("cybergems_base_url", "Share server URL", SettingsValueKind.Text, "Empty uses the official CyberGems host.", "UploadCyberGemsBaseUrl"),
                        new SettingDefinition("use_custom_cybergems_key", "Use my own Share API key", SettingsValueKind.Toggle, "Override the shared CyberSnap Share key.", "UploadUseCustomCyberGemsApiKey"),
                        new SettingDefinition("cybergems_api_key", "CyberSnap Share API key", SettingsValueKind.Text, "Optional personal share API key.", "UploadCyberGemsApiKey"),
                    ]),
                new SettingsSectionDefinition(
                    "imgbb",
                    "ImgBB",
                    "Alternative anonymous host for public links.",
                    [
                        new SettingDefinition("use_custom_imgbb_key", "Use my own ImgBB API key", SettingsValueKind.Toggle, "Override the shared ImgBB key.", "UploadUseCustomImgBBApiKey"),
                        new SettingDefinition("imgbb_api_key", "ImgBB API key", SettingsValueKind.Text, "Optional personal ImgBB API key.", "UploadImgBBApiKey"),
                    ]),
                new SettingsSectionDefinition(
                    "imgur",
                    "Imgur (optional)",
                    "Only available when you supply a Client-ID.",
                    [
                        new SettingDefinition("use_custom_imgur_client_id", "Use my own Imgur Client-ID", SettingsValueKind.Toggle, "Show Imgur as a Share option.", "UploadUseCustomImgurClientId"),
                        new SettingDefinition("imgur_client_id", "Imgur Client-ID", SettingsValueKind.Text, "Your Imgur application Client-ID.", "UploadImgurClientId"),
                    ]),
                new SettingsSectionDefinition(
                    "custom_destination",
                    "Custom destination",
                    "Single FTP, SFTP, S3-compatible, or HTTP webhook destination.",
                    [
                        new SettingDefinition("custom_protocol", "Protocol", SettingsValueKind.Choice, "FTP, SFTP, S3, or Webhook.", "UploadCustomProtocol"),
                        new SettingDefinition("custom_host", "Host", SettingsValueKind.Text, "Server host name.", "UploadCustomHost"),
                        new SettingDefinition("custom_public_url_base", "Public URL base", SettingsValueKind.Text, "Optional public base URL for shared links.", "UploadCustomPublicUrlBase"),
                        new SettingDefinition("webhook_url", "Webhook URL", SettingsValueKind.Text, "HTTPS endpoint for webhook uploads.", "UploadWebhookUrl"),
                    ]),
            ]),
        new(
            "runtimes",
            "Stickers & Upscale",
            "Local runtime-backed media workflows.",
            [
            ]),
        new(
            "backup",
            "Backup & Data",
            "Automatic backups of settings and achievements.",
            [
                new SettingsSectionDefinition("automatic_backups", "Automatic backups", "Schedule, retain, and restore settings backups.",
                [
                    new SettingDefinition("automatic_backup_enabled", "Enable automatic backups", SettingsValueKind.Toggle, "Back up settings and achievements.", "AutomaticBackup.Enabled"),
                    new SettingDefinition("automatic_backup_frequency", "Backup frequency", SettingsValueKind.Choice, "On startup, daily, weekly, or monthly.", "AutomaticBackup.Frequency"),
                    new SettingDefinition("automatic_backup_retention", "Backups to keep", SettingsValueKind.Choice, "Keep between 1 and 50 backups.", "AutomaticBackup.MaxBackups"),
                    new SettingDefinition("automatic_backup_directory", "Backup folder", SettingsValueKind.Folder, "Choose where backup copies are stored.", "AutomaticBackup.CustomDirectory"),
                    new SettingDefinition("automatic_backup_create", "Back up now", SettingsValueKind.Action, "Create and verify a backup now."),
                    new SettingDefinition("automatic_backup_restore", "Restore backup", SettingsValueKind.Action, "Restore settings and achievements after creating a recovery copy."),
                ]),
            ]),
    ];
}
