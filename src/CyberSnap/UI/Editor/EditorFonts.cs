using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;

namespace CyberSnap.UI.Editor;

/// <summary>
/// Fonts measured in destination-monitor pixels so GDI cannot apply DPI a second time.
/// Bind restores the intended font after WinForms rescales a control.
/// </summary>
internal static class EditorFonts
{
    public static Font Chrome(float logicalPt, FontStyle style, int deviceDpi)
        => Fixed(UiChrome.FontFamily.Name, (float)(logicalPt * UiChrome.UiScale), style, deviceDpi);

    public static Font Fixed(string family, float logicalPt, FontStyle style, int deviceDpi)
        => new Font(family, logicalPt * Math.Max(96, deviceDpi) / 72f, style, GraphicsUnit.Pixel);

    /// <summary>
    /// Reapply the pixel font after the handle adopts the monitor DPI.
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
