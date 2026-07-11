using System.Diagnostics;
using Microsoft.Win32;
using CyberSnap.Native;

namespace CyberSnap.Services;

/// <summary>
/// Best-effort detection of hotkey conflicts, focused on the Print Screen key which is
/// commonly claimed by other screenshot tools.
///
/// Two distinct failure modes:
///   Case A — another app owns the key via <c>RegisterHotKey</c>. This is exclusive, so our
///            own <c>RegisterHotKey</c> would fail. <see cref="CanRegister"/> probes for this.
///   Case B — another app swallows the key via a low-level keyboard hook (Snagit, the Win11
///            Snipping Tool). Our registration "succeeds" but never fires. We cannot prove
///            this via API, so <see cref="DetectPrintScreenInterceptors"/> names known culprits
///            found running / enabled. It is an advisory, never a block.
/// </summary>
public static class HotkeyConflictProbe
{
    // Transient id used only for the probe register/unregister round-trip; distinct from the
    // 9001–9016 range owned by HotkeyService.
    private const int ProbeId = 9999;

    /// <summary>
    /// Case A: returns false if the (mod, vk) combination is already owned by another app.
    /// The caller MUST release its own hotkeys first (App.UnregisterAllHotkeys) or this reports
    /// a false conflict against CyberSnap itself.
    /// </summary>
    public static bool CanRegister(uint mod, uint vk)
    {
        try
        {
            User32.UnregisterHotKey(IntPtr.Zero, ProbeId);
            bool ok = User32.RegisterHotKey(IntPtr.Zero, ProbeId, mod | User32.MOD_NOREPEAT, vk);
            if (ok)
                User32.UnregisterHotKey(IntPtr.Zero, ProbeId);
            return ok;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("hotkey.probe.can-register", ex);
            // On probe failure assume no conflict; the real registration path still reports errors.
            return true;
        }
    }

    // Process name (as reported by Process.GetProcessesByName, no extension) → human label.
    // Conservative set: only tools that are known to grab Print Screen globally. OneDrive/Dropbox
    // are excluded because their capture-sync is off by default and ambiguous.
    private static readonly (string ProcessName, string Label)[] KnownInterceptorProcesses =
    {
        ("Snagit32", "Snagit"),
        ("SnagitEditor", "Snagit"),
        ("SnagPriv", "Snagit"),
        ("ShareX", "ShareX"),
        ("Greenshot", "Greenshot"),
        ("Lightshot", "Lightshot"),
    };

    /// <summary>
    /// Case B: returns human-readable names of apps/features likely to intercept Print Screen.
    /// Best-effort and heuristic — never throws.
    /// </summary>
    public static IReadOnlyList<string> DetectPrintScreenInterceptors()
    {
        var found = new List<string>();

        try
        {
            if (IsWindowsSnippingToolBound())
                found.Add(LocalizationService.Translate("Windows Snipping Tool"));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("hotkey.probe.snipping-tool", ex);
        }

        foreach (var (processName, label) in KnownInterceptorProcesses)
        {
            try
            {
                if (found.Contains(label))
                    continue;
                if (Process.GetProcessesByName(processName).Length > 0)
                    found.Add(label);
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("hotkey.probe.process", ex);
            }
        }

        return found;
    }

    /// <summary>
    /// Windows 11: "Use the Print screen key to open Snipping Tool" writes
    /// PrintScreenKeyForSnippingEnabled=1 under HKCU\Control Panel\Keyboard.
    /// When on, the OS consumes Print Screen before our hotkey ever fires.
    /// </summary>
    private static bool IsWindowsSnippingToolBound()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
        return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int v && v == 1;
    }
}
