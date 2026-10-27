using System.IO;
using System.Reflection;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

internal static partial class Program
{
    private static void CheckPreviewOutlineCommit(CyberSnap.App app, SettingsService settings, string temp)
    {
        var s = settings.Settings;
        s.CapturePreviewTimeoutSeconds = 0; s.SaveHistory = false; s.SaveToFile = true;
        s.AutoShareAfterCapture = s.OpenEditorAfterCapture = s.OpenInSystemViewerAfterCapture = false;
        s.AfterCapture = AfterCaptureAction.None; s.ShowCapturePreview = true;
        s.CaptureImageFormat = CaptureImageFormat.Png; s.CaptureMaxLongEdge = 12;
        s.CelebrationsEnabled = false; s.PreviewOutlineEnabled = false;
        AutoCopyPreferences.SetKindEnabled(s, AutoCopyKind.Image, false);
        settings.Save(); settings.FlushPendingWrites();
        SetZoomAppField(app, "_settingsService", settings);
        foreach (bool scaled in new[] { false, true })
        {
            using var source = OutlineFixture();
            string early = Path.Combine(temp, "outline-automatic-" + scaled + ".png");
            CaptureOutputService.SavePng(source, early);
            var preview = new CapturePreviewDialog((Bitmap)source.Clone(), settings, savedFilePath: early, clipboardAlreadyCopied: true);
            try
            {
                preview.Show(); Pump();
                Call(preview, "ApplyOutlineSettings", true, 4, DrawingColor.Magenta.ToArgb());
                string target = early;
                if (scaled)
                {
                    target = Path.Combine(temp, "outline-named-output.jpg");
                    CaptureOutputService.SaveBitmap(preview.EffectiveBitmap, target, CaptureImageFormat.Jpeg, 95);
                    SetProperty(Field(preview, "_active"), "SavedFilePath", target);
                    Call(preview, "ApplyScaleFactor", 2);
                }
                int expectedWidth = preview.EffectiveBitmap.Width, expectedHeight = preview.EffectiveBitmap.Height;
                var completion = typeof(CapturePreviewDialog).GetEvent("SessionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var handler = typeof(CyberSnap.App).GetMethod("CompletePreviewSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
                completion.GetAddMethod(true)!.Invoke(preview, [Delegate.CreateDelegate(completion.EventHandlerType!, app, handler)]);
                Call(preview, "CommitActiveSession");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                bool delivered = false;
                while (DateTime.UtcNow < deadline && !delivered)
                {
                    Pump();
                    if (File.Exists(target))
                    {
                        using var bytes = new MemoryStream(File.ReadAllBytes(target));
                        using var saved = new Bitmap(bytes);
                        delivered = saved.Width == expectedWidth && saved.Height == expectedHeight;
                        if (delivered)
                        {
                            Assert(saved.RawFormat.Guid == (scaled ? System.Drawing.Imaging.ImageFormat.Jpeg.Guid : System.Drawing.Imaging.ImageFormat.Png.Guid),
                                "final saving keeps the selected format even when the capture default differs");
                            if (!scaled) Assert(saved.GetPixel(0, 0).ToArgb() == DrawingColor.Magenta.ToArgb() && saved.GetPixel(4, 4).A == 0,
                                "automatic saving replaces the eager original with outlined pixels and source transparency");
                        }
                    }
                    if (!delivered) Thread.Sleep(20);
                }
                Assert(delivered, "the real completion handler saves the final outline without applying the resize limit again");
                Pump();
            }
            finally { if (preview.IsVisible) preview.CloseFromReplace(); Pump(); }
            s.PreviewOutlineEnabled = false;
        }
    }
}
