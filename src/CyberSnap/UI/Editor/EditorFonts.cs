using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.UI.Editor;

/// <summary>
/// DPI-compensated fonts for the annotation editor.
/// The editor lays out with fixed logical pixels, but point-based fonts grow with the
/// monitor DPI, and WinForms PMV2 additionally multiplies point sizes when a window is
/// created on (or moved into) a monitor whose scale differs from the process DPI. On a
/// 150% monitor that rendered text at roughly twice the chrome design and truncated
/// labels everywhere. Every editor font is therefore compensated so it renders at the
/// same physical pixel height it has on the monitor that was active at process start,
/// no matter which monitor the window settles on.
/// </summary>
internal static class EditorFonts
{
    /// <summary>Device DPI the editor design was measured against (primary monitor at process start).</summary>
    public static int BaseDpi { get; private set; } = 96;

    public static void CaptureBaseDpi(int deviceDpi)
        => BaseDpi = Math.Max(96, deviceDpi);

    public static float Scale(int deviceDpi)
        => BaseDpi / (float)Math.Max(96, deviceDpi);

    /// <summary>UiChrome-equivalent font (shared family + UiScale) compensated to <paramref name="deviceDpi"/>.</summary>
    public static Font Chrome(float logicalPt, FontStyle style, int deviceDpi)
        => new Font(UiChrome.FontFamily.Name, (float)(logicalPt * UiChrome.UiScale * Scale(deviceDpi)), style);

    /// <summary>Fixed-family font compensated to <paramref name="deviceDpi"/>.</summary>
    public static Font Fixed(string family, float logicalPt, FontStyle style, int deviceDpi)
        => new Font(family, logicalPt * Scale(deviceDpi), style);

    /// <summary>
    /// Keeps a control's font DPI-compensated for its lifetime: re-applied when the handle
    /// is created (the monitor DPI is only final then) and again whenever the window
    /// crosses into a monitor with a different scale, which would otherwise multiply the
    /// point size on top of the compensation.
    /// </summary>
    public static void Bind(Control control, Func<int, Font> factory)
    {
        void Apply()
        {
            try
            {
                if (!control.IsDisposed && !control.Disposing)
                    control.Font = factory(control.DeviceDpi);
            }
            catch
            {
                // Font swaps during teardown are irrelevant.
            }
        }

        control.HandleCreated += (_, _) => Apply();
        control.DpiChangedAfterParent += (_, _) => Apply();
        if (control is Form form)
            form.DpiChanged += (_, _) => Apply();
        Apply();
    }
}
