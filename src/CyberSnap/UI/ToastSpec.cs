using Bitmap = System.Drawing.Bitmap;
using Color = System.Windows.Media.Color;
using System.Windows.Media;
using System.Windows;

namespace CyberSnap.UI;

internal sealed record ToastSpec
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public Color? SwatchColor { get; init; }
    public Bitmap? PreviewBitmap { get; init; }
    public Bitmap? InlinePreviewBitmap { get; init; }
    public string? FilePath { get; init; }
    public string? ClickActionUrl { get; init; }
    public string? ClickActionLabel { get; init; }
    public bool PlayCaptureSound { get; init; }
    public bool PlayErrorSound { get; init; }
    public bool SuppressSound { get; init; }
    public bool IsError { get; init; }
    // Brief text-only status message (e.g. "Sent to the editor"). Suppressed by the
    // "System messages" sub-toggle while previews and errors remain visible.
    public bool IsSystemMessage { get; init; }
    public bool AutoPin { get; init; }
    public bool TransparentShell { get; init; }
    public bool ShowOverlayButtons { get; init; }
    public bool HideEditButton { get; init; }
    /// <summary>
    /// When true, delete <see cref="FilePath"/> after the toast closes (temp recordings
    /// when Save to file is off).
    /// </summary>
    public bool DeleteFileOnDismiss { get; init; }
    /// <summary>
    /// Layout-only preview (e.g. Settings → Test capture notification): buttons and body click
    /// are visible but do not run real actions.
    /// </summary>
    public bool DisableInteractiveActions { get; init; }
    public Stretch PreviewStretch { get; init; } = Stretch.Uniform;
    public Thickness PreviewMargin { get; init; }
    public double? PreviewMaxHeight { get; init; }
    public int? MaxWidthOverride { get; init; }
    public int? MinWidthOverride { get; init; }
    public double? DurationSeconds { get; init; }
    // When true, the toast plays a celebratory flourish (animated sweep timeline).
    // Only honored for non-error toasts.
    public bool Celebrate { get; init; }

    // Trailing icon appended after the body text on a celebration toast. Defaults to the cyan
    // "captureRect" capture motif (suits capture-milestone/streak toasts). Achievement toasts
    // override it (e.g. "trophy") so they don't show a capture icon unrelated to the unlock.
    public string? CelebrationBodyIconId { get; init; }

    public static ToastSpec Standard(string title, string body = "", string? filePath = null) => new()
    {
        Title = title,
        Body = body,
        FilePath = filePath,
        IsSystemMessage = true
    };

    public static ToastSpec Error(string title, string body = "", string? filePath = null) => new()
    {
        Title = title,
        Body = body,
        FilePath = filePath,
        PlayErrorSound = true,
        IsError = true
    };

    public static ToastSpec WithColor(string title, string body, Color color) => new()
    {
        Title = title,
        Body = body,
        SwatchColor = color
    };

    public static ToastSpec InlinePreview(Bitmap preview, string title, string body, string? filePath = null) => new()
    {
        Title = title,
        Body = body,
        InlinePreviewBitmap = preview,
        FilePath = filePath
    };

    public static ToastSpec ImagePreview(
        Bitmap preview,
        string title,
        string body,
        string? filePath,
        bool autoPin,
        bool transparentShell,
        bool showOverlayButtons,
        string? clickActionUrl = null,
        string? clickActionLabel = null,
        bool hideEditButton = false,
        bool deleteFileOnDismiss = false) => new()
    {
        Title = title,
        Body = body,
        PreviewBitmap = preview,
        FilePath = filePath,
        ClickActionUrl = clickActionUrl,
        ClickActionLabel = clickActionLabel,
        AutoPin = autoPin,
        TransparentShell = transparentShell,
        ShowOverlayButtons = showOverlayButtons,
        HideEditButton = hideEditButton,
        DeleteFileOnDismiss = deleteFileOnDismiss
    };

    public static ToastSpec Sticker(Bitmap sticker) => new()
    {
        PreviewBitmap = sticker,
        TransparentShell = false,
        PreviewStretch = Stretch.Uniform,
        PreviewMargin = new Thickness(0),
        ShowOverlayButtons = false
    };
}
