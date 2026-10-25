using System.Drawing;
using System.Reflection;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckCaptureDestinationPolicy()
    {
        using var source = new Bitmap(800, 600);
        foreach (string language in new[] { "en", "es" })
        {
            LocalizationService.ApplyCurrentCulture(language);
            foreach (string purpose in new[] { "Image", "Pin", "Zoom" })
            {
                using var overlay = new RegionOverlayForm(source, new Rectangle(0, 0, 800, 600),
                    purpose == "Pin" ? CaptureMode.Pin : CaptureMode.Rectangle, WindowDetectionMode.Off)
                    { DetectWindows = false, ConfirmRegionBeforeCapture = true };
                if (purpose == "Zoom")
                    typeof(RegionOverlayForm).GetProperty("LockConfirmDestination", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(overlay, true);
                if (purpose == "Pin")
                    Assert(((Rectangle)Field(overlay, "_toolbarRect")).IsEmpty, "Pin selection has no capture bar before confirmation: " + language);
                Call(overlay, "EnterConfirmMode", new Rectangle(180, 100, 440, 320), null!);
                if (purpose != "Image")
                {
                    CheckLockedConfirmDestination(overlay, purpose + ", " + language);
                    continue;
                }
                var kinds = ((Array)Field(overlay, "_confirmChromeKinds")).Cast<object>().Select(kind => kind.ToString()).ToArray();
                Assert(new[] { "ModeImage", "ModeOcr", "ModeVideo", "ModeGif", "ModeScroll", "ModeQr" }.All(kinds.Contains),
                    "regular captures retain every confirmation destination: " + language);
                Type kindType = typeof(RegionOverlayForm).GetNestedType("ConfirmChromeKind", BindingFlags.NonPublic)!;
                Call(overlay, "SwitchConfirmDestination", Enum.Parse(kindType, "ModeOcr"));
                Assert((CaptureMode)Field(overlay, "_modeBeforeConfirm") == CaptureMode.Ocr,
                    "regular captures can still change to OCR: " + language);
                Call(overlay, "SwitchConfirmDestination", Enum.Parse(kindType, "ModeImage"));
                Assert((CaptureMode)Field(overlay, "_modeBeforeConfirm") == CaptureMode.Rectangle,
                    "regular captures can return to image confirmation: " + language);
            }
        }
    }

    private static void CheckLockedConfirmDestination(RegionOverlayForm overlay, string purpose)
    {
        Assert((bool)Field(overlay, "_isConfirmingSelection"), "the specialized selection stays in confirmation: " + purpose);
        var kinds = ((Array)Field(overlay, "_confirmChromeKinds")).Cast<object>().ToArray();
        Assert(!kinds.Any(kind => kind.ToString()!.StartsWith("Mode"))
            && new[] { "Retry", "Cancel", "Done" }.All(name => kinds.Any(kind => kind.ToString() == name)),
            "specialized confirmation offers editing controls without alternate destinations: " + purpose);
        Assert(kinds.Any(kind => kind.ToString() == "TogglePreview") == !overlay.PinImageOnConfirm,
            "only Zoom retains the image Preview toggle: " + purpose);
        var originalMode = (CaptureMode)Field(overlay, "_modeBeforeConfirm");
        var originalCrop = overlay.ConfirmRect;
        int launches = 0;
        overlay.OcrRegionSelected += _ => launches++;
        overlay.ScanRegionSelected += _ => launches++;
        overlay.RecordingRequested += _ => launches++;
        overlay.ScrollRegionSelected += _ => launches++;
        Type kindType = typeof(RegionOverlayForm).GetNestedType("ConfirmChromeKind", BindingFlags.NonPublic)!;
        foreach (var (name, key) in new[] { ("ModeOcr", Forms.Keys.O), ("ModeVideo", Forms.Keys.V),
            ("ModeGif", Forms.Keys.G), ("ModeScroll", Forms.Keys.S), ("ModeQr", Forms.Keys.Q) })
        {
            object kind = Enum.Parse(kindType, name);
            Call(overlay, "SwitchConfirmDestination", kind);
            Call(overlay, "RunConfirmDestination", kind);
            Call(overlay, "OnKeyDown", new Forms.KeyEventArgs(key));
            Assert(launches == 0 && (CaptureMode)Field(overlay, "_modeBeforeConfirm") == originalMode
                && overlay.ConfirmRect == originalCrop && (bool)Field(overlay, "_isConfirmingSelection"),
                "alternate destination and its shortcut preserve the specialized crop: " + purpose + ", " + name);
        }
        Call(overlay, "ExpandConfirmModes");
        Assert(!(bool)Field(overlay, "_confirmModesExpanded"), "hover cannot reopen hidden destination modes: " + purpose);
        Call(overlay, "LayoutConfirmChromeRects");
        var rectangles = ((Rectangle[])Field(overlay, "_confirmChromeRects")).Where(rect => !rect.IsEmpty).ToArray();
        var wrapper = (Rectangle)Field(overlay, "_confirmChromeWrapperRect");
        Assert(rectangles.Length == kinds.Length && rectangles.All(wrapper.Contains)
            && rectangles.SelectMany((rect, i) => rectangles.Skip(i + 1).Select(other => rect.IntersectsWith(other))).All(overlap => !overlap),
            "the compact confirmation dock contains every remaining action without overlap: " + purpose);
    }
}
