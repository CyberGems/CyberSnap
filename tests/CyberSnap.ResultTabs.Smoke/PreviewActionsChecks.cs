using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

internal static partial class Program
{
    private static void CheckPreviewActions(string temp, string? renders, bool openPaint = false)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        double originalScale = UiScale.Current;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (double scale in new[] { 1.0, 1.4 })
            {
                using var settings = new SettingsService(Path.Combine(temp, $"actions-{language}-{scale}.json"));
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.UiScale = scale;
                settings.Settings.DisableAnimations = true;
                settings.Settings.CapturePreviewTimeoutSeconds = 0;
                settings.Settings.SaveToFile = false;
                settings.Save(); settings.FlushPendingWrites();
                LocalizationService.ApplyCurrentCulture(language);
                Theme.SetMode(AppThemeMode.Dark);
                using var source = new Bitmap(180, 100);
                source.SetPixel(90, 50, DrawingColor.Magenta);
                string savedPath = Path.Combine(temp, $"capture {language} {scale}.png");
                CaptureOutputService.SavePng(source, savedPath);
                var preview = new CapturePreviewDialog((Bitmap)source.Clone(), settings, savedFilePath: savedPath, clipboardAlreadyCopied: true);
                try
                {
                    preview.Show(); Pump();
                    object first = Field(preview, "_active");
                    CheckActionGrid(preview, language);
                    CheckPreviewCenter(preview, "initial small capture");
                    preview.WindowState = WindowState.Maximized; Pump();
                    CheckPreviewCenter(preview, "maximized small capture");
                    preview.WindowState = WindowState.Normal; Pump();
                    CheckPreviewCenter(preview, "restored small capture");
                    CheckActionGrid(preview, language);

                    var menu = (ContextMenu)Call(preview, "BuildMoreMenu")!;
                    string pathLabel = LocalizationService.Translate("Copy image path");
                    string paintLabel = LocalizationService.Translate("Send to Paint");
                    Assert(menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == pathLabel)
                        && menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == paintLabel),
                        $"More offers the existing image path without automatic saving, and Paint in {language}");
                    menu.PlacementTarget = Element<Button>(preview, "MoreBtn");
                    menu.IsOpen = true; Pump();
                    Assert(menu.IsOpen && menu.Items.OfType<MenuItem>().All(i => i.ActualHeight is >= 24 and <= 30),
                        "More menu rows remain compact with the app's real theme resources");
                    Render(menu, renders, $"preview-more-{language}-{scale}.png");
                    menu.IsOpen = false; Pump();

                    Call(first, "SetScaleFactor", 2);
                    Call(first, "SetOutline", true, 3, DrawingColor.Red.ToArgb());
                    Call(first, "SetMargin", true, 12, DrawingColor.Blue.ToArgb());
                    var paint = PreparePaintForTest(first);
                    try
                    {
                        using var exported = new Bitmap(paint.ArgumentList.Single());
                        var effective = (Bitmap)Property(first, "EffectiveBitmap");
                        Assert(paint.FileName == "mspaint.exe" && paint.UseShellExecute
                            && paint.WorkingDirectory == Environment.SystemDirectory && paint.Arguments.Length == 0,
                            "Paint uses the Windows shell entry with one safely separated image argument");
                        Assert(exported.Width == effective.Width && exported.Height == effective.Height
                            && exported.GetPixel(0, 0).ToArgb() == DrawingColor.Blue.ToArgb()
                            && exported.GetPixel(12, 12).ToArgb() == DrawingColor.Red.ToArgb()
                            && exported.GetPixel(195, 115).ToArgb() == effective.GetPixel(195, 115).ToArgb()
                            && exported.GetPixel(16, 16).A == 0,
                            "Paint's PNG preserves scale, margin, outline, capture pixels and transparent pixels");
                        Assert((string)Property(first, "SavedFilePath") == savedPath && new FileInfo(savedPath).Length > 0,
                            "sending a modified capture to Paint preserves its existing saved file association");
                    }
                    finally { File.Delete(paint.ArgumentList.Single()); }

                    using var large = new Bitmap(1200, 800);
                    string secondPath = Path.Combine(temp, $"second {language} {scale}.png");
                    CaptureOutputService.SavePng(large, secondPath);
                    Call(preview, "AddSession", (Bitmap)large.Clone(), secondPath, true, CaptureKind.Screenshot); Pump();
                    object second = Field(preview, "_active");
                    Assert(SavedPreviewPath(first) == savedPath && SavedPreviewPath(second) == secondPath,
                        "image paths remain independent for multiple tabs");
                    paint = PreparePaintForTest(second);
                    try
                    {
                        using var exported = new Bitmap(paint.ArgumentList.Single());
                        var effective = (Bitmap)Property(second, "EffectiveBitmap");
                        Assert(exported.Size == effective.Size && paint.ArgumentList.Single() != savedPath
                            && paint.ArgumentList.Single() != secondPath,
                            "Paint exports the requested tab to its own snapshot");
                    }
                    finally { File.Delete(paint.ArgumentList.Single()); }
                    Call(preview, "ZoomToFitWindow"); Pump();
                    preview.WindowState = WindowState.Maximized; Pump();
                    preview.WindowState = WindowState.Normal; Pump();
                    CheckPreviewCenter(preview, "restored fitted capture");
                    var image = Element<Image>(preview, "PreviewImage");
                    var viewport = Element<ScrollViewer>(preview, "ZoomViewport");
                    Assert(image.ActualWidth <= viewport.ViewportWidth + 1 && image.ActualHeight <= viewport.ViewportHeight + 1,
                        "restoring a fitted capture keeps its whole image visible");
                    Call(preview, "ZoomActualSize"); Pump();
                    CheckPreviewCenter(preview, "actual-size capture before maximize");
                    preview.WindowState = WindowState.Maximized; Pump();
                    preview.WindowState = WindowState.Normal; Pump();
                    CheckPreviewCenter(preview, "restored actual-size capture");
                    Call(preview, "SetZoom", 2.0); Pump();
                    viewport.ScrollToHorizontalOffset(viewport.ScrollableWidth);
                    viewport.ScrollToVerticalOffset(viewport.ScrollableHeight); Pump();
                    preview.WindowState = WindowState.Maximized; Pump();
                    preview.WindowState = WindowState.Normal; Pump();
                    Assert((double)Property(second, "Zoom") == 2.0 && !(bool)Property(second, "ZoomToFit")
                        && viewport.HorizontalOffset <= viewport.ScrollableWidth && viewport.VerticalOffset <= viewport.ScrollableHeight,
                        "maximize/restore keeps a manual zoom and valid pan offsets");
                    CheckPreviewCenter(preview, "restored manual zoom capture");
                    Call(preview, "ActivateSessionAt", 0); Pump();
                    Assert(SavedPreviewPath(Field(preview, "_active")) == savedPath,
                        "returning to a tab restores its image path");
                    File.Delete(savedPath);
                    menu = (ContextMenu)Call(preview, "BuildMoreMenu")!;
                    Assert(!menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == pathLabel),
                        "More omits image path when the active tab's file no longer exists");
                    SetProperty(first, "SavedFilePath", "");
                    menu = (ContextMenu)Call(preview, "BuildMoreMenu")!;
                    Assert(!menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == pathLabel)
                        && menu.Items.OfType<MenuItem>().Any(i => (string)i.Header == paintLabel),
                        "unsaved captures can be sent to Paint without offering a nonexistent path");
                    Render(preview, renders, $"preview-actions-{language}-{scale}.png");
                    preview.Width = 580 * scale; Pump();
                    CheckActionGrid(preview, language);
                    // Opt-in manual QA only; automated runs never launch external apps.
                    if (openPaint && language == "es" && scale == 1.4)
                        Call(preview, "SendToPaint", first);
                }
                finally { preview.CloseFromReplace(); Pump(); }
            }
        }
        finally
        {
            LocalizationService.ApplyCurrentCulture(originalLanguage);
            UiScale.Set(originalScale);
            Theme.SetMode(AppThemeMode.Dark);
        }
    }

    private static ProcessStartInfo PreparePaintForTest(object session) =>
        (ProcessStartInfo)typeof(CapturePreviewDialog).GetMethod("PreparePaintImage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [session])!;

    private static string? SavedPreviewPath(object session) =>
        (string?)typeof(CapturePreviewDialog).GetMethod("SavedPathIfExists", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [session]);

    private static void CheckActionGrid(CapturePreviewDialog preview, string language)
    {
        var panel = Element<UniformGrid>(preview, "OptionalActionsPanel");
        var buttons = panel.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible).ToArray();
        string[] order = ["SaveBtn", "SaveAsBtn", "CopyBtn", "ExtractTextBtn", "OpenViewerBtn", "PinImageBtn", "PrintBtn", "DeleteBtn", "MoreBtn"];
        Assert(buttons.Select(b => Array.IndexOf(order, b.Name)).SequenceEqual(buttons.Select(b => Array.IndexOf(order, b.Name)).Order()),
            "optional action layout preserves the existing action order");
        Assert(buttons.All(b => Math.Abs(b.ActualWidth - buttons[0].ActualWidth) < 0.1),
            "optional actions share equal column widths");
        foreach (var button in buttons.Append(Element<Button>(preview, "EditBtn")).Where(b => b.Visibility == Visibility.Visible))
        {
            var content = (StackPanel)button.Content;
            var label = content.Children.OfType<TextBlock>().Single();
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert(label.FontSize == 12.5 && content.DesiredSize.Width + button.Padding.Left + button.Padding.Right + 2 <= button.ActualWidth + 0.5,
                $"{button.Name} uses the larger label without clipping in {language}: content {content.DesiredSize.Width:0.0}, button {button.ActualWidth:0.0}");
        }
        preview.UpdateLayout();
    }

    private static void CheckPreviewCenter(CapturePreviewDialog preview, string context)
    {
        var image = Element<Image>(preview, "PreviewImage");
        var viewport = Element<ScrollViewer>(preview, "ZoomViewport");
        var center = image.TranslatePoint(new Point(image.ActualWidth / 2, image.ActualHeight / 2), viewport);
        Assert(Math.Abs(center.X - viewport.ViewportWidth / 2) < 1 && Math.Abs(center.Y - viewport.ViewportHeight / 2) < 1,
            $"{context} is centered in the actual viewport ({center.X:0.0}, {center.Y:0.0}; viewport {viewport.ViewportWidth:0.0} x {viewport.ViewportHeight:0.0})");
    }
}
