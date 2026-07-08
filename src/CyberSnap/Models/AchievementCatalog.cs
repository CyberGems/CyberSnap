using System;
using System.Collections.Generic;

namespace CyberSnap.Models;

// Single source of truth for the medal grid shown in the Achievements tab. Assembles the full
// list of achievements (capture milestones, day streaks, and first-time feature unlocks) against
// the live AppSettings, resolving each one's unlocked/progress state. Adding a new achievement is
// just one more entry here — the UI renders whatever this returns.
public static class AchievementCatalog
{
    // Glyphs from Segoe MDL2 Assets matching the exact code points used in ToolDef.AllTools,
    // so each medal shows the same icon as the corresponding toolbar button.
    private static readonly string GlyphStar     = ((char)0xE735).ToString();    // FavoriteStarFill (milestone rail)
    private static readonly string GlyphCapture  = ((char)0xE722).ToString();    // Camera — first capture
    private static readonly string GlyphOcr      = ((char)0xE53C).ToString();    // OCR scan-text (ToolDef "ocr")
    private static readonly string GlyphVideo    = ((char)0xE7C8).ToString();    // Video — record (ToolDef "record")
    private static readonly string GlyphScroll   = ((char)0xE7F0).ToString();    // Scroll capture (ToolDef "scroll" / ToolGlyphs.ScrollCaptureGlyph)
    private static readonly string GlyphPicker   = ((char)0xE2B1).ToString();    // Eyedropper (ToolDef "picker")
    private static readonly string GlyphScan     = ((char)0xE1DE).ToString();    // QR/barcode (ToolDef "scan")
    private static readonly string GlyphRuler    = ((char)0xE14E).ToString();    // Ruler (ToolDef "ruler")
    private static readonly string GlyphEditor   = ((char)0xE70F).ToString();    // Edit/pencil (Fluent "Edit")

    public static IReadOnlyList<Achievement> Build(AppSettings s, Func<string, string> t)
    {
        var list = new List<Achievement>();
        int count = s.CelebrationCaptureCount;

        // Capture milestones — one medal per CelebrationMilestones value.
        var values = CelebrationMilestones.Values;
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            list.Add(new Achievement
            {
                Id = $"captures-{v}",
                Kind = AchievementKind.CaptureMilestone,
                Title = string.Format(t("{0} captures"), v.ToString("N0")),
                Description = string.Format(t("Reach {0} captures"), v.ToString("N0")),
                Glyph = GlyphStar,
                Tier = CaptureTier(i),
                Unlocked = count >= v,
                Progress = (Math.Min(count, v), v)
            });
        }

        // Day streaks — one medal per streak milestone; unlocked off the best streak ever.
        // Progress toward the next streak milestone reflects the current active streak so
        // the bar advances in real time as the user builds their streak today.
        var streaks = CelebrationMilestones.StreakDays;
        for (int i = 0; i < streaks.Length; i++)
        {
            int d = streaks[i];
            list.Add(new Achievement
            {
                Id = $"streak-{d}",
                Kind = AchievementKind.Streak,
                Title = string.Format(t("{0}-day streak"), d),
                Description = string.Format(t("Reach a {0}-day streak"), d),
                Glyph = GlyphStar,
                Tier = Math.Min(4, i / 2),
                Unlocked = s.LongestStreak >= d,
                // Unlocked medals use LongestStreak (historical record); locked ones show
                // CurrentStreak so the bar moves as the user builds today's streak.
                Progress = s.LongestStreak >= d
                    ? (d, d)
                    : (Math.Min(s.CurrentStreak, d), d)
            });
        }

        // First-time feature unlocks — one medal per tool/action, using each tool's own icon.
        list.Add(FirstTime("first-capture",   t("First capture"),           t("Take your first capture"),          GlyphCapture, count > 0));
        list.Add(FirstTime("first-ocr",       t("First OCR"),               t("Extract text from a capture"),      GlyphOcr,     s.HasFirstOcr));
        list.Add(FirstTime("first-recording", t("First recording"),         t("Record a video or GIF"),            GlyphVideo,   s.HasFirstRecording));
        list.Add(FirstTime("first-scroll",    t("First scrolling capture"), t("Capture a scrolling page"),         GlyphScroll,  s.HasFirstScrollingCapture));
        list.Add(FirstTime("first-color",     t("First color pick"),        t("Pick a color from the screen"),     GlyphPicker,  s.HasFirstColorPicker));
        list.Add(FirstTime("first-scan",      t("First scan"),              t("Scan a QR code or barcode"),        GlyphScan,    s.HasFirstScan));
        list.Add(FirstTime("first-ruler",     t("First ruler"),             t("Measure something on screen"),      GlyphRuler,   s.HasFirstRuler));
        list.Add(FirstTime("first-editor",    t("First editor"),            t("Open a capture in the editor"),     GlyphEditor,  s.HasFirstEditor));

        return list;
    }

    private static Achievement FirstTime(string id, string title, string desc, string glyph, bool unlocked) =>
        new()
        {
            Id = id,
            Kind = AchievementKind.FirstTime,
            Title = title,
            Description = desc,
            Glyph = glyph,
            Tier = 0,
            Unlocked = unlocked,
            Progress = null
        };

    // Buckets the 14 capture milestones into 5 color tiers (cyan -> gold).
    private static int CaptureTier(int index) => index switch
    {
        <= 1 => 0,   // 50, 100
        <= 4 => 1,   // 250, 500, 750
        <= 7 => 2,   // 1k, 1.5k, 2k
        <= 9 => 3,   // 3k, 5k
        _ => 4       // 7.5k, 10k, 15k, 25k
    };
}
