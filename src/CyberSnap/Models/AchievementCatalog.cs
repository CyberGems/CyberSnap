using System;
using System.Collections.Generic;

namespace CyberSnap.Models;

// Single source of truth for the medal grid shown in the Achievements tab. Assembles the full
// list of achievements (capture milestones, day streaks, and first-time feature unlocks) against
// the live AppSettings, resolving each one's unlocked/progress state. Adding a new achievement is
// just one more entry here — the UI renders whatever this returns.
public static class AchievementCatalog
{
    // Glyphs (Segoe Fluent Icons) used on the medal tiles, built from code points so the source
    // stays free of literal private-use characters. These match glyphs used elsewhere in the app.
    private static readonly string GlyphStar = ((char)0xE735).ToString();    // FavoriteStarFill
    private static readonly string GlyphCapture = ((char)0xE722).ToString(); // camera (present in both Fluent + MDL2)
    private static readonly string GlyphOcr = ((char)0xE8C8).ToString();     // OCR
    private static readonly string GlyphVideo = ((char)0xE768).ToString();   // video

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

        // First-time feature unlocks.
        list.Add(FirstTime("first-capture", t("First capture"), t("Take your first capture"), GlyphCapture, count > 0));
        list.Add(FirstTime("first-ocr", t("First OCR"), t("Extract text from a capture"), GlyphOcr, s.HasFirstOcr));
        list.Add(FirstTime("first-recording", t("First recording"), t("Record a video or GIF"), GlyphVideo, s.HasFirstRecording));
        list.Add(FirstTime("first-scroll", t("First scrolling capture"), t("Capture a scrolling page"), GlyphCapture, s.HasFirstScrollingCapture));

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
