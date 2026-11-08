namespace CyberSnap.UI.Editor;

internal static class EditorChromeMetrics
{
    // The existing editor chrome dimensions are physical pixels at 125%.
    private const int DesignDpi = 120;

    public static float Scale(int deviceDpi) => deviceDpi / (float)DesignDpi;

    public static int Pixels(float designPixels, int deviceDpi)
        => (int)Math.Round(designPixels * Scale(deviceDpi));
}
