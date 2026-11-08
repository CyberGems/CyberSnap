namespace CyberSnap.Models;

internal sealed class AchievementProgress
{
    // Local date (yyyy-MM-dd) of the last "first capture of the day" celebration.
    public string? LastCelebrationDate { get; set; }
    // Running total of all captures (image, OCR, and video/GIF recordings alike). Drives
    // milestone celebrations (50, 100, 250, ...) and the milestone rail in Settings.
    public int CelebrationCaptureCount { get; set; }
    public DateOnly? AchievementsStartedOn { get; set; }
    public bool AchievementHistoryInitialized { get; set; }
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

    // Dates (yyyy-MM-dd) when each first-time achievement was unlocked.
    public string? FirstOcrAt { get; set; }
    public string? FirstRecordingAt { get; set; }
    public string? FirstScrollCaptureAt { get; set; }
    public string? FirstColorPickerAt { get; set; }
    public string? FirstScanAt { get; set; }
    public string? FirstRulerAt { get; set; }
    public string? FirstEditorAt { get; set; }

    // Set of achievement IDs already seen by the user in the Achievements window (clears "NEW" badges).
    public HashSet<string> SeenAchievementIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
