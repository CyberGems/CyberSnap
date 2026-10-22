using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

internal static partial class Program
{
    private static void CheckPin(string temp, SettingsService settings, string? renders)
    {
        var app = (CyberSnap.App)Application.Current;
        settings.Settings.DisableAnimations = true;
        settings.Settings.HasSeenQuickStartGuide = true;
        if (renders is not null) Directory.CreateDirectory(renders);
        Assert(new AppSettings().PinAlwaysOnTop && new AppSettings().PinSmoothImage
            && !new AppSettings().PinIncludeInCaptures && new AppSettings().PinInitialScale == 1,
            "Pin defaults to a smooth 100% topmost reference excluded from captures");
        using var original = new Bitmap(320, 200, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(original))
        {
            graphics.Clear(Color.Teal);
            graphics.FillRectangle(System.Drawing.Brushes.Cyan, 20, 20, 110, 80);
            using var font = new Font("Segoe UI", 16);
            graphics.DrawString("CyberSnap Pin", font, System.Drawing.Brushes.White, 30, 120);
        }
        original.SetPixel(200, 20, Color.FromArgb(80, 190, 40, 80));
        foreach (string language in new[] { "en", "es" })
        {
            LocalizationService.ApplyCurrentCulture(language);
            using var pin = new PinImageForm((Bitmap)original.Clone(), new Point(500, 350), settings.Settings);
            pin.Show(); Forms.Application.DoEvents();
            var center = new Point(pin.Left + pin.Width / 2, pin.Top + pin.Height / 2);
            pin.SetImageScale(1.2);
            Assert(pin.Size == new Size(386, 242) && Math.Abs(pin.Left + pin.Width / 2 - center.X) <= 1,
                "Pin scales from physical image pixels and preserves its center: " + language);
            for (int index = 0; index < 30; index++) Call(pin, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Control | Forms.Keys.Subtract));
            Assert(pin.Opacity is >= .099 and <= .101, "opacity shortcuts stop at 10% without altering image scale");
            Call(pin, "OnMouseDown", new Forms.MouseEventArgs(Forms.MouseButtons.Middle, 1, 12, 12, 0));
            Assert(pin.ImageScale == 1 && pin.Opacity == 1, "middle-click resets both zoom and opacity");
            pin.SetImageScale(-1); Assert(pin.ImageScale == .2, "Pin zoom stops at 20%");
            pin.SetImageScale(99); Assert(pin.ImageScale == 5, "Pin zoom stops at 500%");
            pin.SetImageScale(1.2);
            using var copy = pin.CopyOriginal();
            Assert(EqualZoomBitmaps(original, copy), "copy keeps every original pixel and alpha after repeated visual changes");
            Bitmap? copied = null;
            pin.CopyRequested += image => copied = image;
            Call(pin, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Control | Forms.Keys.C));
            using (copied) Assert(copied is not null && EqualZoomBitmaps(original, copied), "Ctrl+C delivers a detached original image");
            SetField(pin, "_hover", true);
            using (var render = new Bitmap(pin.Width, pin.Height))
            {
                pin.DrawToBitmap(render, new Rectangle(Point.Empty, render.Size));
                Assert(pin.AccessibilityObject.GetChildCount() == 3
                    && pin.AccessibilityObject.GetChild(0)!.Name == LocalizationService.Translate("Copy original image"),
                    "the hover toolbar exposes localized copy, options and close actions to accessibility");
                if (renders is not null) render.Save(Path.Combine(renders, "pin-window-" + language + ".png"));
            }
            pin.SetImageScale(.2);
            using (var render = new Bitmap(pin.Width, pin.Height))
            {
                pin.DrawToBitmap(render, new Rectangle(Point.Empty, render.Size));
                Assert(pin.AccessibilityObject.GetChildCount() == 0, "tiny pinned images do not squeeze or clip the toolbar");
            }
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var size = pin.Size;
                pin.Location = new Point(screen.WorkingArea.X + 60, screen.WorkingArea.Y + 60);
                Forms.Application.DoEvents();
                Assert(pin.Size == size, "moving Pin between physical monitors preserves its scale: " + screen.DeviceName);
            }
            pin.ApplyCapturePreferences(true, false);
            Assert(!pin.TopMost, "Pin can become a regular window without being closed");
            pin.ApplyCapturePreferences(false, true);
            Assert(pin.TopMost, "Pin can restore topmost and capture exclusion");
            Call(pin, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Escape));
            Assert(pin.IsDisposed, "Esc closes the focused Pin only");

            var chooser = new PinSourceWindow();
            try
            {
                var sources = new List<string>();
                chooser.SourceRequested += sources.Add;
                chooser.Show(); Pump();
                var titleBar = VisualDescendants(chooser).OfType<CyberSnap.UI.Controls.CyberSnapTitleBar>().Single();
                Assert(((System.Windows.Controls.Border)titleBar.FindName("MinimizeBtn")).Visibility == Visibility.Collapsed
                    && ((System.Windows.Controls.Border)titleBar.FindName("MaximizeBtn")).Visibility == Visibility.Collapsed,
                    "the fixed Pin chooser exposes only its supported close action");
                var buttons = VisualDescendants(chooser).OfType<Button>().Where(button => !string.IsNullOrEmpty(AutomationProperties.GetName(button))).ToArray();
                foreach (string label in new[] { "Select region...", "From clipboard", "Select image..." })
                {
                    var button = buttons.Single(button => AutomationProperties.GetName(button) == LocalizationService.Translate(label));
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Assert(sources.SequenceEqual(new[] { "region", "clipboard", "file" }), "the localized chooser routes all three image sources");
                Render(chooser, renders, "pin-chooser-" + language + ".png");
            }
            finally { chooser.Close(); }
        }
        using var clipboardStream = new MemoryStream();
        original.Save(clipboardStream, ImageFormat.Png); clipboardStream.Position = 0;
        var clipboardSource = new System.Windows.Media.Imaging.PngBitmapDecoder(clipboardStream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        using (var decoded = (Bitmap)typeof(CyberSnap.App).GetMethod("ReadPinClipboardImage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { clipboardSource })!)
            Assert(EqualZoomBitmaps(original, decoded), "clipboard image decoding preserves size, pixels and transparency without changing the clipboard");

        string file = Path.Combine(temp, "pin-input.png");
        original.Save(file, ImageFormat.Png);
        app.PinImageFile(file); app.PinImage((Bitmap)original.Clone());
        File.Delete(file);
        var pins = (List<PinImageForm>)ZoomAppField(app, "_pinnedImages")!;
        Assert(pins.Count == 2 && !app.IsSessionBusy(), "multiple modeless Pins keep capture available and release the source file");
        foreach (var format in new[] { ImageFormat.Jpeg, ImageFormat.Gif, ImageFormat.Bmp, ImageFormat.Tiff })
        {
            string imagePath = Path.Combine(temp, "pin-input-" + format.Guid + ".image");
            original.Save(imagePath, format);
            app.PinImageFile(imagePath);
            Assert(pins.Count == 3, "Pin imports supported image pixel formats: " + format);
            File.Delete(imagePath);
            using var detached = pins.Last().CopyOriginal();
            Assert(detached.Size == original.Size, "format conversion keeps dimensions and releases its file decoder", false);
            pins.Last().Close();
        }
        settings.Settings.PinAlwaysOnTop = false; settings.Settings.PinIncludeInCaptures = true;
        app.RefreshPinnedImagePreferences();
        Assert(pins.All(pin => !pin.TopMost), "settings update every existing Pin without recreating its image");
        CheckPinCaptureRoute(app, settings, pins);
        foreach (string language in new[] { "en", "es" })
        {
            settings.Settings.InterfaceLanguage = language;
            LocalizationService.ApplyCurrentCulture(language);
            using var preview = new PreviewLifetime(new CapturePreviewDialog((Bitmap)original.Clone(), settings));
            preview.Window.Show(); Pump();
            var pinTexts = VisualDescendants(Element<Button>(preview.Window, "PinImageBtn")).OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert(pinTexts.Contains(LocalizationService.Translate("Pin to screen")),
                $"Preview exposes the Pin action in the selected interface language: {language}, actual={string.Join('|', pinTexts)}, expected={LocalizationService.Translate("Pin to screen")}, setting={settings.Settings.InterfaceLanguage}");
            Render(preview.Window, renders, "pin-preview-" + language + ".png");
            int count = pins.Count;
            Element<Button>(preview.Window, "PinImageBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(pins.Count == count + 1, "Preview pins an independent copy of its active image");
        }
        CallZoomApp(app, "ClosePinnedImages");
        Assert(pins.Count == 0, "closing the Pin session disposes every reference window");
        settings.Settings.PinInitialScale = 1.5; settings.Settings.PinSmoothImage = false;
        settings.Settings.SetToolHotkey("_standalonePin", 6, 0x50); settings.Save(); settings.FlushPendingWrites();
        using var reload = new SettingsService(Path.Combine(temp, "settings.json"));
        reload.Load();
        Assert(reload.Settings.PinInitialScale == 1.5 && !reload.Settings.PinSmoothImage
            && reload.Settings.PinIncludeInCaptures && reload.Settings.GetToolHotkey("_standalonePin") == (6u, 0x50u),
            "Pin preferences and its independent global shortcut survive reload");
        using var hotkeys = new HotkeyService();
        int hotkeyCalls = 0;
        hotkeys.StandalonePinHotkeyPressed += () => hotkeyCalls++;
        Assert(hotkeys.RegisterStandalonePin(0, 0), "Pin does not claim a default global shortcut");
        object[] args = { new System.Windows.Interop.MSG { message = 0x0312, wParam = new IntPtr(9018) }, false };
        typeof(HotkeyService).GetMethod("OnMsg", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(hotkeys, args);
        Assert(hotkeyCalls == 1 && (bool)args[1], "native Pin hotkeys dispatch exactly once");
    }

    private sealed class PreviewLifetime(CapturePreviewDialog window) : IDisposable
    {
        public CapturePreviewDialog Window => window;
        public void Dispose() { window.Close(); Pump(); }
    }

    private static void CheckPinCaptureRoute(CyberSnap.App app, SettingsService settings, List<PinImageForm> pins)
    {
        using var source = new Bitmap(800, 600);
        using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.Navy);
        var request = new ZoomCaptureRequest(source, new Rectangle(0, 0, 800, 600), false, CaptureMode.Rectangle);
        int before = pins.Count;
        Exception? failure = null;
        SetZoomAppField(app, "_isCapturing", 1);
        using var timer = new Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            var overlay = typeof(RegionOverlayForm).GetField("_currentOverlay", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as RegionOverlayForm;
            if (overlay is not { Visible: true }) return;
            timer.Stop();
            try
            {
                Assert(overlay.PinImageOnConfirm, "region selection uses the Pin capture purpose");
                OverlayZoomMouse(overlay, "OnMouseDown", 180, 100);
                OverlayZoomMouse(overlay, "OnMouseMove", 620, 420);
                OverlayZoomMouse(overlay, "OnMouseUp", 620, 420);
                Assert((bool)Field(overlay, "_isConfirmingSelection") && !(bool)Call(overlay, "IsPreviewToggleVisible")!,
                    "Pin offers normal region confirmation and annotations without a Preview toggle");
                Call(overlay, "SetMode", CaptureMode.Draw, null!, false);
                overlay.SetToolColor(Color.Lime); overlay.StrokeWidth = 8;
                OverlayZoomMouse(overlay, "OnMouseDown", 300, 220);
                OverlayZoomMouse(overlay, "OnMouseMove", 450, 220);
                OverlayZoomMouse(overlay, "OnMouseUp", 450, 220);
                CommitZoomOverlay(overlay);
            }
            catch (Exception ex) { failure = ex; Call(overlay, "CancelFromShortcut"); }
        };
        timer.Start();
        Action<Bitmap, Point> deliver = (image, point) => CallZoomApp(app, "PinCapturedRegion", image, point);
        CallZoomApp(app, "LaunchOverlayNow", CaptureMode.Rectangle, request, deliver);
        Pump();
        if (failure is not null) throw failure;
        Assert(pins.Count == before + 1 && !app.IsSessionBusy() && ZoomAppField(app, "_activePreviewDialog") is null,
            "confirmed Pin capture creates a floating image directly and releases the capture slot");
        using var output = pins.Last().CopyOriginal();
        Assert(output.Size == new Size(440, 320) && output.GetPixel(160, 120).G > 200,
            "the pinned region includes confirmation edits and exact crop dimensions");
    }
}
