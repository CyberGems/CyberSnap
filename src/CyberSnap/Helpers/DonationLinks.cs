using System.Diagnostics;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

public static class DonationLinks
{
    public const string ReadmeUrl = "https://github.com/CyberGems/CyberSnap#%EF%B8%8F-donate";
    private static readonly object OpenGate = new();
    private static DateTime _lastOpenUtc = DateTime.MinValue;
    private static readonly TimeSpan DuplicateClickWindow = TimeSpan.FromMilliseconds(800);

    public static void Open()
    {
        lock (OpenGate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastOpenUtc < DuplicateClickWindow)
                return;
            _lastOpenUtc = now;
        }

        try
        {
            Process.Start(new ProcessStartInfo(ReadmeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            lock (OpenGate)
                _lastOpenUtc = DateTime.MinValue;
            AppDiagnostics.LogWarning("donation.open", ex.Message, ex);
        }
    }
}
