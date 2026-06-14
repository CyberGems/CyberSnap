using System.Linq;

namespace CyberSnap.Models;

// Single source of truth for the capture-count milestones that earn a celebration.
// Shared by the capture pipeline (which fires the in-the-moment flourish on an exact
// match) and the Settings milestone rail (which derives progress from the running count).
public static class CelebrationMilestones
{
    // Checked by exact match in the capture pipeline, so each fires its flourish once.
    public static readonly int[] Values = { 50, 100, 250, 500, 750, 1000, 1500, 2000, 3000, 5000 };

    // True when this exact count lands on a milestone (drives the in-the-moment flourish).
    public static bool IsMilestone(int count) => System.Array.IndexOf(Values, count) >= 0;

    // The highest milestone reached at the given count, or 0 if none reached yet.
    public static int HighestAchieved(int count) => Values.Where(m => m <= count).DefaultIfEmpty(0).Max();

    // The next milestone above the given count, or null when all are reached.
    public static int? Next(int count)
    {
        foreach (var m in Values)
            if (m > count) return m;
        return null;
    }
}
