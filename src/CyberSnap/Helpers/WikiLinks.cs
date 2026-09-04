using System.Diagnostics;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>
/// Opens pages of the CyberSnap GitHub wiki (https://github.com/CyberGems/CyberSnap/wiki)
/// in the user's default browser.
/// </summary>
public static class WikiLinks
{
    public const string BaseUrl = "https://github.com/CyberGems/CyberSnap/wiki/";

    // Page names must match the wiki files in docs/wiki exactly.
    public const string HomePage = "Home";
    public const string CapturePage = "Capture";
    public const string CapturePreviewPage = "Capture-Preview";
    public const string AnnotationEditorPage = "Annotation-Editor";
    public const string ScreenRecordingPage = "Screen-Recording";
    public const string OcrAndTranslationPage = "OCR-&-Translation";
    public const string GalleryPage = "Gallery";
    public const string UploadAndSharePage = "Upload-&-Share";
    public const string StandaloneToolsPage = "Standalone-Tools";
    public const string SettingsPage = "Settings";
    public const string SystemTrayPage = "System-Tray";
    public const string NotificationsPage = "Notifications";
    public const string InstallationAndSetupPage = "Installation-&-Setup";
    public const string ConfigurationFilesPage = "Configuration-Files";
    public const string FaqPage = "FAQ";

    // Project homepage (same as the About window's website link).
    public const string HomepageUrl = "https://cybergems.org";

    public static void Open(string page) => OpenUrl(BaseUrl + page);

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("links.open", ex.Message, ex);
        }
    }
}