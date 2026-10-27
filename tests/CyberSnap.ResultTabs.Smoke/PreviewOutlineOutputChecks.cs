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
        => CheckPreviewFrameCommit(app, settings, temp, withMargin: false);

    private static void CheckPreviewFrameCommit(CyberSnap.App app, SettingsService settings, string temp, bool withMargin)
    {
        var s = settings.Settings;
        s.CapturePreviewTimeoutSeconds = 0; s.SaveHistory = false; s.SaveToFile = true;
        s.AutoShareAfterCapture = s.OpenEditorAfterCapture = s.OpenInSystemViewerAfterCapture = false;
        s.AfterCapture = AfterCaptureAction.None; s.ShowCapturePreview = true;
        s.CaptureImageFormat = CaptureImageFormat.Png; s.CaptureMaxLongEdge = 12;
        s.CelebrationsEnabled = false; s.PreviewOutlineEnabled = false;
        s.PreviewMarginEnabled = false;
        var marginColor = DrawingColor.FromArgb(36, 104, 172);
        if (withMargin) s.PreviewBackgroundDarkColorArgb = s.PreviewBackgroundLightColorArgb = marginColor.ToArgb();
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
                // Cover margin-only automatic PNG output as well as a combined,
                // scaled JPEG. A 32px JPEG margin gives the sampled corner its
                // own compression blocks, away from the contrasting outline.
                if (!withMargin || scaled) Call(preview, "ApplyOutlineSettings", true, 4, DrawingColor.Magenta.ToArgb());
                if (withMargin) Call(preview, "ApplyMarginSettings", true, scaled ? 32 : 9);
                string target = early;
                if (scaled)
                {
                    target = Path.Combine(temp, "outline-named-output.jpg");
                    CaptureOutputService.SaveBitmap(preview.EffectiveBitmap, target, CaptureImageFormat.Jpeg, 95);
                    SetProperty(Field(preview, "_active"), "SavedFilePath", target);
                    Call(preview, "ApplyScaleFactor", 2);
                }
                int expectedWidth = preview.EffectiveBitmap.Width, expectedHeight = preview.EffectiveBitmap.Height;
                if (withMargin) Assert(preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == marginColor.ToArgb(),
                    "the final unencoded image contains the exact selected margin color");
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
                            if (!scaled) Assert(saved.GetPixel(0, 0).ToArgb() == (withMargin ? marginColor : DrawingColor.Magenta).ToArgb()
                                && saved.GetPixel(withMargin ? 9 : 4, withMargin ? 9 : 4).A == 0,
                                "automatic saving replaces the eager original with final frame pixels and source transparency");
                            if (withMargin) Assert(Math.Abs(saved.GetPixel(0, 0).R - marginColor.R) <= 12
                                && Math.Abs(saved.GetPixel(0, 0).G - marginColor.G) <= 12
                                && Math.Abs(saved.GetPixel(0, 0).B - marginColor.B) <= 12,
                                $"both PNG and named JPEG output contain the selected solid margin color: actual={saved.GetPixel(0, 0)}, expected={marginColor}, scaled={scaled}");
                        }
                    }
                    if (!delivered) Thread.Sleep(20);
                }
                Assert(delivered, "the real completion handler saves the final frame without applying the resize limit again");
                Pump();
            }
            finally { if (preview.IsVisible) preview.CloseFromReplace(); Pump(); }
            s.PreviewOutlineEnabled = false;
            s.PreviewMarginEnabled = false;
        }
    }
}
