namespace CyberSnap.Models;

// What kind of achievement a medal represents. Used to group the medal grid in the
// Achievements tab and to pick a color ramp.
public enum AchievementKind
{
    CaptureMilestone,
    Streak,
    FirstTime
}

// A single medal in the Achievements grid. Definitions are assembled on demand by
// AchievementCatalog.Build against the live AppSettings, so adding a new achievement type
// is just one more entry there — no UI plumbing per achievement.
public sealed class Achievement
{
    public required string Id { get; init; }
    public required AchievementKind Kind { get; init; }

    // Already localized at build time.
    public required string Title { get; init; }
    public required string Description { get; init; }

    // Segoe Fluent Icons glyph shown on the medal tile.
    public required string Glyph { get; init; }

    // 0..4 — drives the neon color ramp (low → top tier).
    public int Tier { get; init; }

    public bool Unlocked { get; init; }

    // Optional progress toward unlocking (current, target) for a "N / target" hint on
    // locked tiles. Null when not applicable.
    public (int Current, int Target)? Progress { get; init; }
}
