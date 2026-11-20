using CyberSnap.AppModel;

internal static partial class Program
{
    private static void CheckTrayTooltip()
    {
        Assert(
            TrayTooltipText.Format("Alt+Shift+S") == "CyberSnap · Alt+Shift+S",
            "the tray tooltip uses the suite format with a single segment");
        Assert(
            TrayTooltipText.Format("  Recording  ") == "CyberSnap · Recording",
            "the tray tooltip trims the segment before formatting");

        foreach (string? empty in new string?[] { null, "", "   " })
        {
            Assert(
                TrayTooltipText.Format(empty) == "CyberSnap",
                "the tray tooltip without a segment is the bare app name");
        }

        string capped = TrayTooltipText.Format(new string('X', 80));
        Assert(capped.Length == 63, "the tray tooltip is capped at 63 characters");
        Assert(capped.EndsWith("…", StringComparison.Ordinal), "the tray tooltip cap ends in an ellipsis");
        Assert(capped.StartsWith("CyberSnap · ", StringComparison.Ordinal), "the capped tray tooltip keeps the app prefix");
    }
}
