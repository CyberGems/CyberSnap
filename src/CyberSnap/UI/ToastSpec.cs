using Bitmap = System.Drawing.Bitmap;
using Color = System.Windows.Media.Color;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows;

namespace CyberSnap.UI;

/// <summary>One outcome row in an enriched after-capture toast ("✓ Image saved — C:\…\shot.png").</summary>
internal sealed record ToastStatusLine
{
    /// <summary>Fluent icon id: "check", "dismiss", "info", "share", "folder", …</summary>
    public required string IconId { get; init; }
    public required string Label { get; init; }
    /// <summary>Optional right-side detail (shortened path, URL, size…).</summary>
    public string? Detail { get; init; }
    /// <summary>When true, the row is rendered in the error accent (red).</summary>
    public bool IsError { get; init; }
    /// <summary>When set, the row hosts an inline "Copy this text" button.</summary>
    public string? CopyableText { get; init; }
    /// <summary>Tooltip for the inline copy button (already localized).</summary>
    public string? CopyableTooltip { get; init; }
}

internal sealed record ToastSpec
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public Color? SwatchColor { get; init; }
    public Bitmap? InlinePreviewBitmap { get; init; }
    /// <summary>
    /// Optional Fluent icon id rendered inside <see cref="InlinePreviewHost"/> when neither
    /// <see cref="IsWelcomeToast"/> nor <see cref="InlinePreviewBitmap"/> are set.
    /// Used to give brief status toasts (e.g. "Video recorded" / "GIF recorded") a clear
    /// left-side glyph that balances the layout against the top-right action buttons.
    /// </summary>
    public string? InlineIconId { get; init; }
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
    /// <summary>
    /// Settings → Test notification: still show the preview while Quiet Hours would otherwise mute it.
    /// </summary>
    public bool BypassQuietHours { get; init; }
    public double? DurationSeconds { get; init; }
    // When true, the toast plays a celebratory flourish (animated sweep timeline).
    // Only honored for non-error toasts.
    public bool Celebrate { get; init; }
    /// <summary>
    /// Lower shows first when several celebrations are queued. First-time unlocks beat
    /// milestones, which beat streaks, which beat the daily greeting.
    /// </summary>
    public int CelebrationRank { get; init; } = RankDaily;
    public const int RankFirstTime = 0;
    public const int RankMilestone = 1;
    public const int RankStreak = 2;
    public const int RankDaily = 3;

    // Trailing icon appended after the body text on a celebration toast. Defaults to the cyan
    // "captureRect" capture motif (suits capture-milestone/streak toasts). Achievement toasts
    // override it (e.g. "trophy") so they don't show a capture icon unrelated to the unlock.
    public string? CelebrationBodyIconId { get; init; }
    public bool IsWelcomeToast { get; init; }

    /// <summary>When set, replaces <see cref="Body"/> with a vertical list of action-status
    /// rows (icon + label + optional detail + optional inline "Copy this text" button).
    /// Used by the enriched after-capture toast.</summary>
    public IReadOnlyList<ToastStatusLine>? StatusLines { get; init; }

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

}
