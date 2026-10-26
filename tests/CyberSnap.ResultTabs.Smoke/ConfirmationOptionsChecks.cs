using System.Drawing;
using System.IO;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    // The overlay's read-only static reader normally uses the live app path. Point
    // its in-process cache at the temporary snapshot, without touching user files.
    private static void UseTemporaryConfirmationSettings(SettingsService settings)
    {
        settings.Save(); settings.FlushPendingWrites();
        var type = typeof(SettingsService);
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        string defaultPath = (string)type.GetMethod("ResolveSettingsPath", flags)!.Invoke(null, new object?[] { null })!;
        type.GetField("s_cachedPath", flags)!.SetValue(null, defaultPath);
    }

    private static void BindConfirmationPreferences(RegionOverlayForm overlay, SettingsService settings)
    {
        void Save() => UseTemporaryConfirmationSettings(settings);
        overlay.ShowAnnotationToolbarChanged += value => { settings.Settings.ShowAnnotationToolbar = value; Save(); };
        overlay.ConfirmDoneShowLabelChanged += value => { settings.Settings.ConfirmDoneShowLabel = value; Save(); };
        overlay.ConfirmPillShowLabelsChanged += value => { settings.Settings.ConfirmPillShowLabels = value; Save(); };
        overlay.RememberAnnotationToolChanged += value => { settings.Settings.RememberAnnotationTool = value; Save(); };
        overlay.EnabledToolsChanged += value => { settings.Settings.EnabledTools = value; Save(); };
        overlay.LastAnnotationToolChanged += value => { settings.Settings.LastAnnotationToolId = value; Save(); };
    }

    private static Forms.ToolStripMenuItem ConfirmOption(Forms.ContextMenuStrip menu, string name) =>
        menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Name == name);

    private static RegionOverlayForm ConfirmationFixture(Bitmap source, SettingsService settings, CaptureMode mode, bool zoom, bool show)
    {
        var overlay = new RegionOverlayForm(source, new Rectangle(0, 0, source.Width, source.Height), mode, WindowDetectionMode.Off)
            { DetectWindows = false, ConfirmRegionBeforeCapture = true };
        overlay.SetEnabledTools(settings.Settings.EnabledTools);
        if (zoom) typeof(RegionOverlayForm).GetProperty("LockConfirmDestination", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(overlay, true);
        BindConfirmationPreferences(overlay, settings);
        if (show) { overlay.Show(); Forms.Application.DoEvents(); }
        Call(overlay, "EnterConfirmMode", new Rectangle(120, 100, 440, 320), null!);
        return overlay;
    }

    private static void CheckConfirmationOptions(SettingsService settings, string? renders = null, bool show = false)
    {
        var previous = System.Text.Json.JsonSerializer.Serialize(settings.Settings);
        using var source = new Bitmap(900, 700);
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (string purpose in new[] { "Image", "Pin", "Zoom", "OCR", "Video", "GIF", "QR", "Scroll" })
            {
                var custom = new List<string> { "rect", "zoom", "pin", "select", "arrow", "draw" };
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.EnabledTools = custom.ToList();
                settings.Settings.ShowAnnotationToolbar = true;
                settings.Settings.RememberAnnotationTool = true;
                settings.Settings.LastAnnotationToolId = "draw";
                settings.Settings.HasSeenQuickStartGuide = true;
                settings.Settings.ConfirmDoneShowLabel = true;
                settings.Settings.DisableAnimations = true;
                UseTemporaryConfirmationSettings(settings);
                LocalizationService.ApplyCurrentCulture(language);
                CaptureMode mode = purpose switch { "Pin" => CaptureMode.Pin, "OCR" => CaptureMode.Ocr,
                    "Video" => CaptureMode.Record, "GIF" => CaptureMode.RecordGif, "QR" => CaptureMode.Scan,
                    "Scroll" => CaptureMode.ScrollCapture, _ => CaptureMode.Rectangle };
                using var overlay = ConfirmationFixture(source, settings, mode, purpose == "Zoom", show);
                using var menu = (Forms.ContextMenuStrip)Call(overlay, "BuildConfirmationOptionsMenu")!;
                var commands = menu.Items.OfType<Forms.ToolStripMenuItem>().ToArray();
                Assert(menu.Items[0].Text == (language == "es" ? "Opciones de confirmación" : "Confirmation options")
                    && ConfirmOption(menu, "primary-label").Text == (language == "es" ? "Mostrar texto del botón principal" : "Show primary button text"),
                    "the confirmation menu and primary action option have contextual localized names: " + purpose + ", " + language);
                Assert(commands.All(item => !item.HasDropDownItems) && commands.All(item => item.Name != "restore-annotation:zoom" && item.Name != "restore-annotation:ocr")
                    && !commands.Any(item => (item.Text ?? "").Contains("banner", StringComparison.OrdinalIgnoreCase)),
                    "confirmation options stay flat and exclude capture tools and global banner preferences");
                Assert(commands.Any(item => item.Name == "mode-labels") == (purpose != "Pin" && purpose != "Zoom"),
                    "mode labels are offered only when confirmation has swappable destinations: " + purpose);
                Assert(commands.Any(item => item.Name == "annotation-toolbar") == (purpose != "OCR"),
                    "annotation options appear only for an annotatable destination: " + purpose);
                ConfirmOption(menu, "primary-label").PerformClick();
                var doneType = typeof(RegionOverlayForm).GetNestedType("ConfirmChromeKind", System.Reflection.BindingFlags.NonPublic)!;
                object done = Enum.Parse(doneType, "Done");
                Assert(settings.Settings.ConfirmDoneShowLabel == false && (bool)Call(overlay, "ConfirmChromeIsIconOnly", done)!,
                    "one primary-label preference controls both Done and Pin without resetting its value");
                if (purpose == "OCR") continue;

                var annotations = (List<Annotation>)Field(overlay, "_undoStack");
                annotations.Add(new LineAnnotation(new Point(250, 220), new Point(350, 220), Color.Lime));
                Call(overlay, "MarkCommittedAnnotationsDirty");
                Call(overlay, "SetMode", CaptureMode.Draw, "draw", false);
                ConfirmOption(menu, "annotation-toolbar").PerformClick();
                Assert(!settings.Settings.ShowAnnotationToolbar && settings.Settings.EnabledTools!.SequenceEqual(custom)
                    && settings.Settings.LastAnnotationToolId == "draw" && overlay.CurrentMode == CaptureMode.Move
                    && Field(overlay, "_activeToolId") is null,
                    "hiding annotation controls neutralizes the tool and preserves enabled tools and the last choice");
                Assert(((Rectangle)Field(overlay, "_toolbarRect")).IsEmpty && Field(overlay, "_toolbarForm") is not Forms.Form { Visible: true },
                    "hidden annotations leave no capture-bar fallback or invisible drawing controls");
                Call(overlay, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.F6));
                Call(overlay, "SetMode", CaptureMode.Arrow, "arrow", false);
                Assert(overlay.CurrentMode == CaptureMode.Move && annotations.Count == 1,
                    "annotation shortcuts and stale tool activation cannot reactivate hidden tools");
                OverlayZoomMouse(overlay, "OnMouseMove", 280, 220);
                Call(overlay, "OnMouseDoubleClick", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 2, 280, 220, 0));
                Assert((int)Field(overlay, "_moveHoverIndex") < 0 && (int)Field(overlay, "_selectedAnnotationIndex") < 0
                    && ((HashSet<int>)Field(overlay, "_multiSelectedIndices")).Count == 0,
                    "hover and double-click do not select or edit invisible annotation tools");
                var before = overlay.ConfirmRect;
                OverlayZoomMouse(overlay, "OnMouseDown", 280, 220);
                Assert((bool)Field(overlay, "_isConfirmDragging"), "dragging inside existing artwork starts moving the crop when annotations are hidden");
                OverlayZoomMouse(overlay, "OnMouseMove", 310, 240);
                OverlayZoomMouse(overlay, "OnMouseUp", 310, 240);
                Assert(overlay.ConfirmRect == new Rectangle(before.X + 30, before.Y + 20, before.Width, before.Height) && annotations.Count == 1,
                    "interior dragging moves the crop and retains existing artwork without drawing anything");
                var targets = ((System.Collections.IEnumerable)Call(overlay, "GetToolbarKeyboardTargets")!).Cast<object>().ToArray();
                var ids = targets.Select(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)!).ToArray();
                Assert(ids.Contains("confirm:Done") && ids.All(id => id.StartsWith("confirm:")),
                    "Tab still reaches visible confirmation actions while annotation tools are hidden");
                if (renders is not null && purpose is "Image" or "Pin")
                {
                    Directory.CreateDirectory(renders);
                    using var hiddenMenu = (Forms.ContextMenuStrip)Call(overlay, "BuildConfirmationOptionsMenu")!;
                    Assert(!ConfirmOption(hiddenMenu, "remember-annotation").Enabled, "remembering a drawing tool is inactive while the toolbar is hidden");
                    hiddenMenu.Size = hiddenMenu.GetPreferredSize(Size.Empty);
                    using var picture = new Bitmap(hiddenMenu.Width, hiddenMenu.Height);
                    hiddenMenu.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
                    picture.Save(Path.Combine(renders, "confirm-options-hidden-" + purpose + "-" + language + ".png"));
                    using var canvas = new Bitmap(900, 700);
                    overlay.DrawToBitmap(canvas, new Rectangle(Point.Empty, canvas.Size));
                    canvas.Save(Path.Combine(renders, "confirm-canvas-hidden-" + purpose + "-" + language + ".png"));
                    if (show)
                    {
                        ConfirmOption(hiddenMenu, "confirmation-guide").PerformClick();
                        Forms.Application.DoEvents();
                        Assert(Field(overlay, "_quickStartGuide") is Forms.Form { Visible: true },
                            "the confirmation guide opens from More even with no annotation toolbar");
                        Call(overlay, "DismissQuickStartGuide");
                    }
                }
                overlay.Dispose(); // A restarted capture has no previous overlay competing for focus.
                settings.Load();
                UseTemporaryConfirmationSettings(settings);
                using var restarted = ConfirmationFixture(source, settings, mode, purpose == "Zoom", show);
                Assert(restarted.CurrentMode == CaptureMode.Move && ((Rectangle)Field(restarted, "_toolbarRect")).IsEmpty
                    && !settings.Settings.ShowAnnotationToolbar && settings.Settings.EnabledTools!.SequenceEqual(custom)
                    && settings.Settings.LastAnnotationToolId == "draw",
                    "a new capture after loading saved settings starts without any invisible annotation tool"
                    + $" (mode={restarted.CurrentMode}, bar={Field(restarted, "_toolbarRect")}, show={settings.Settings.ShowAnnotationToolbar}, tools={string.Join(",", settings.Settings.EnabledTools!)}, last={settings.Settings.LastAnnotationToolId})");
                using var restartedMenu = (Forms.ContextMenuStrip)Call(restarted, "BuildConfirmationOptionsMenu")!;
                ConfirmOption(restartedMenu, "annotation-toolbar").PerformClick();
                Assert(settings.Settings.ShowAnnotationToolbar && settings.Settings.EnabledTools!.SequenceEqual(custom)
                    && restarted.CurrentMode == CaptureMode.Draw,
                    "showing the toolbar restores its configured subset and remembered tool");
                if (show) Assert(Field(restarted, "_toolbarForm") is Forms.Form { Visible: true }, "showing annotations presents the real toolbar");
                using var restoreMenu = (Forms.ContextMenuStrip)Call(restarted, "BuildConfirmationOptionsMenu")!;
                ConfirmOption(restoreMenu, "restore-annotations").PerformClick();
                Assert(settings.Settings.EnabledTools!.Where(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 0)).SequenceEqual(custom.Take(3)),
                    "restoring annotation tools leaves hidden capture buttons unchanged");
                if (renders is not null && purpose is "Image" or "Pin")
                {
                    using var restoredMenu = (Forms.ContextMenuStrip)Call(restarted, "BuildConfirmationOptionsMenu")!;
                    restoredMenu.Size = restoredMenu.GetPreferredSize(Size.Empty);
                    using var picture = new Bitmap(restoredMenu.Width, restoredMenu.Height);
                    restoredMenu.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
                    picture.Save(Path.Combine(renders, "confirm-options-" + purpose + "-" + language + ".png"));
                }
                if (purpose is "Image" or "Pin" or "Zoom")
                {
                    Call(restarted, "ToggleAnnotationToolbar");
                    Call(restarted, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.R));
                    Assert(!(bool)Field(restarted, "_isConfirmingSelection"), "Retry remains available with annotations hidden: " + purpose);
                    var expected = new Rectangle(120, 100, 440, 320);
                    Call(restarted, "EnterConfirmMode", expected, null!);
                    Rectangle committed = Rectangle.Empty;
                    restarted.RegionSelected += rect => committed = rect;
                    if (language == "en")
                    {
                        for (int i = 0; i < 16 && (string?)Field(restarted, "_toolbarKeyboardFocusId") != "confirm:Done"; i++)
                            Call(restarted, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Tab));
                        Assert((string?)Field(restarted, "_toolbarKeyboardFocusId") == "confirm:Done", "Tab focuses the primary confirmation action without an annotation toolbar");
                        Call(restarted, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Space));
                    }
                    else Call(restarted, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Enter));
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    while (committed.IsEmpty && DateTime.UtcNow < deadline) { Forms.Application.DoEvents(); Pump(); Thread.Sleep(10); }
                    Assert(committed == expected, "Space or Enter still confirms the original capture after hiding annotations: " + purpose + ", " + language);
                }
            }
            // Older versions stored bulk-hidden annotations as an empty annotation subset.
            settings.Settings.EnabledTools = new List<string> { "rect", "pin" };
            settings.Settings.ShowAnnotationToolbar = true;
            UseTemporaryConfirmationSettings(settings);
            using var legacy = ConfirmationFixture(source, settings, CaptureMode.Rectangle, false, show);
            Assert(legacy.CurrentMode == CaptureMode.Move, "legacy bulk-hidden annotations also start in neutral frame-movement mode");
            using var legacyMenu = (Forms.ContextMenuStrip)Call(legacy, "BuildConfirmationOptionsMenu")!;
            ConfirmOption(legacyMenu, "annotation-toolbar").PerformClick();
            Assert(settings.Settings.EnabledTools!.Contains("draw") && settings.Settings.EnabledTools.Contains("pin") && settings.Settings.ShowAnnotationToolbar,
                "explicitly showing legacy hidden annotations recovers a usable toolbar");
        }
        finally
        {
            typeof(SettingsService).GetProperty("Settings")!.SetValue(settings, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(previous)!);
            settings.Save(); settings.FlushPendingWrites();
            LocalizationService.ApplyCurrentCulture(settings.Settings.InterfaceLanguage);
        }
    }
}
