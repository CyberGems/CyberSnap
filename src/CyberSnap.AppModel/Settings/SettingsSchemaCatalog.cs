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
                    ]),
                new SettingsSectionDefinition(
                    "behavior_after_captures",
                    "After capture",
                    "What happens when an image capture is confirmed.",
                    [
                        new SettingDefinition("after_capture", "When finished", SettingsValueKind.Choice, "Steps that run after an image capture is confirmed.", "AfterCapture"),
                        new SettingDefinition("auto_copy", "Auto-copy results", SettingsValueKind.Toggle, "Master switch for copying results to the clipboard.", "AutoCopyToClipboard"),
                        new SettingDefinition("auto_copy_exclude_images", "Don't auto-copy screenshots", SettingsValueKind.Toggle, "When global Auto-copy is on, still don't copy image captures.", "AutoCopyExcludeImages"),
                        new SettingDefinition("auto_copy_exclude_ocr", "Don't auto-copy OCR text", SettingsValueKind.Toggle, "When global Auto-copy is on, still don't copy OCR text.", "AutoCopyExcludeOcr"),
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
                        new SettingDefinition("auto_copy_exclude_recording", "Auto-copy video", SettingsValueKind.Toggle, "Copy the finished MP4 to the clipboard.", "AutoCopyExcludeRecording"),
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
                        new SettingDefinition("auto_copy_exclude_gif", "Auto-copy GIF", SettingsValueKind.Toggle, "Copy the finished GIF to the clipboard.", "AutoCopyExcludeGif"),
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
    ];
}
