using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckUnifiedToolHotkeys(string temp, SettingsService service, CyberSnap.App app, string? renders)
    {
        var aliases = new[] { ("ocr", "_standaloneOcr"), ("picker", "_standaloneColorPicker"),
            ("scan", "_standaloneScan"), ("ruler", "_standaloneRuler"), ("zoom", "_standaloneZoom"),
            ("pin", "_standalonePin"), ("scroll", "_scrollCapture") };
        var settings = new AppSettings
        {
            OcrHotkeyModifiers = 2, OcrHotkeyKey = 0x4F,
            PickerHotkeyModifiers = 2, PickerHotkeyKey = 0x43,
            ScanHotkeyModifiers = 2, ScanHotkeyKey = 0x51,
            ToolHotkeys = new() { ["ruler"] = new uint[] { 0, 0x52 }, ["zoom"] = new uint[] { 2, 0x5A } }
        };
        uint next = 0x31;
        foreach (var (alias, canonical) in aliases)
        {
            Assert(settings.GetToolHotkey(alias) == (0u, 0u), "legacy local assignments do not become surprise global launchers: " + alias);
            settings.SetToolHotkey(canonical, 6, next++);
            Assert(settings.GetToolHotkey(alias) == settings.GetToolHotkey(canonical), "toolbar and Settings share the actual global binding: " + alias);
            Assert(ToolListBuilder.FindHotkeyConflict(settings, canonical, 6, next - 1) is null,
                "an alias does not conflict with its own global assignment: " + alias);
            Assert(settings.FindAnnotationToolId(6, next - 1) is null, "global launchers are not also dispatched by the annotation keyboard handler");
        }
        settings.HotkeyModifiers = 3; settings.HotkeyKey = 0x41;
        settings.SetToolHotkey("arrow", 0, 0x74);
        settings.SetEditorToolHotkey("editorArrow", 2, 0x4A);
        settings.SetEditorViewHotkey("editorZoomIn", 2, 0x6B);
        Assert(SettingsService.TryDeserialize(JsonSerializer.Serialize(settings), out var reloaded), "existing preferences can be loaded");
        foreach (var (alias, canonical) in aliases)
            Assert(reloaded.GetToolHotkey(alias) == settings.GetToolHotkey(canonical), "custom global binding survives reload: " + alias);
        Assert(reloaded.GetToolHotkey("rect") == (3u, 0x41u) && reloaded.GetToolHotkey("arrow") == (0u, 0x74u)
            && reloaded.GetEditorToolHotkey("editorArrow") == (2u, 0x4Au)
            && reloaded.GetEditorViewHotkey("editorZoomIn") == (2u, 0x6Bu), "main capture, annotation and editor custom shortcuts are preserved");
        Assert(reloaded.OcrHotkeyKey == 0x4F && reloaded.ToolHotkeys!["ruler"][1] == 0x52,
            "retired settings remain stored without silently replacing current global shortcuts");
        Assert(ToolListBuilder.FindHotkeyConflict(settings, "pin", 6, 0x31)?.ToolId == "ocr", "conflicts with another tool are still detected");
        settings.SetToolHotkey("ruler", 0, 0);
        Assert(settings.GetToolHotkey("_standaloneRuler") == (0u, 0u), "clearing through a toolbar alias clears its only global assignment");
        settings.ResetToDefaultHotkeys();
        foreach (var (alias, _) in aliases) Assert(settings.GetToolHotkey(alias) == (0u, 0u), "reset introduces no tool shortcuts, including inherited R: " + alias);

        foreach (string language in new[] { "en", "es" })
        {
            LocalizationService.ApplyCurrentCulture(language);
            settings.InterfaceLanguage = language;
            foreach (var tool in ToolDef.AllTools.Where(t => t.Group == 0))
            {
                string tooltip = ToolTipText(tool, settings);
                Assert(tooltip.Split('\n').Length == 2 && !tooltip.Contains("Unassigned") && !tooltip.Contains("Sin asignar"),
                    "every capture tool has a title and useful helper without a phantom shortcut: " + tool.Id + ", " + language);
                settings.SetToolHotkey(tool.Id, 6, 0x39);
                tooltip = ToolTipText(tool, settings);
                Assert(tooltip.Split('\n')[0].Contains(HotkeyFormatter.Format(6, 0x39)), "tooltip displays the configured global combination: " + tool.Id);
                settings.SetToolHotkey(tool.Id, 0, 0);
            }
            Assert(ToolTipText(ToolDef.AllTools.Single(t => t.Id == "pin"), settings).Contains(LocalizationService.Translate("Select an area to keep on top of other windows.")),
                "Pin explains the toolbar's region action rather than promising a source chooser");
            CheckHotkeySettingsCopy(service, language, renders);
            CheckToolbarTooltipRendering(language, renders);
        }
        LocalizationService.ApplyCurrentCulture("en");
        CheckGlobalToolDispatch(app);
        CheckToolbarKeyboardNavigation(renders);
        CheckStageKeyboardControls();
    }

    private static string ToolTipText(ToolDef tool, AppSettings settings) => (string)typeof(RegionOverlayForm)
        .GetMethod("BuildToolTooltip", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { tool, settings, false, true })!;

    private static void CheckHotkeySettingsCopy(SettingsService settings, string language, string? renders)
    {
        settings.Settings.InterfaceLanguage = language;
        settings.Save(); settings.FlushPendingWrites();
        Theme.SetMode(AppThemeMode.Dark);
        using var history = new HistoryService();
        using var search = new ImageSearchIndexService();
        var window = new SettingsWindow(settings, history, search);
        try
        {
            window.Width = 1200; window.Height = 850;
            window.Show(); Call(window, "SelectSettingsTab", "hotkeys"); Pump();
            Assert(LocalizationService.CurrentLanguageCode == language, "Settings renders in the requested language: " + language);
            var capture = Element<StackPanel>(window, "CaptureToolsPanel");
            var labels = VisualDescendants(capture).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert(labels.Contains(LocalizationService.Translate("Tools"))
                && !labels.Any(s => s.Contains("Standalone", StringComparison.OrdinalIgnoreCase) || s.Contains("independiente", StringComparison.OrdinalIgnoreCase)),
                "Settings lists plain tool names under Tools in both languages: " + language);
            var tags = capture.Children.OfType<Border>().Select(b => b.Tag as string).ToArray();
            Assert(tags.Where(t => t?.StartsWith("_standalone") == true).Count() == 6, "Settings has exactly one row per tool");
            Render(window, renders, "hotkeys-settings-" + language + ".png");
        }
        finally { window.Close(); Pump(); }
    }

    private static void CheckToolbarTooltipRendering(string language, string? renders)
    {
        LocalizationService.ApplyCurrentCulture(language);
        Assert(LocalizationService.CurrentLanguageCode == language, "native tooltip fixtures use their requested language");
        foreach (bool dark in new[] { true, false })
        {
            Theme.SetMode(dark ? AppThemeMode.Dark : AppThemeMode.Light);
            using var owner = new Forms.Form(); owner.Show();
            foreach (var tool in ToolDef.AllTools.Where(t => t.Group == 0))
            {
                using var tooltip = new WindowsToolTip { EmphasizeTitle = true };
                string text = ToolTipText(tool, new AppSettings());
                tooltip.ShowNear(owner, text, new Rectangle(500, 300, 40, 40), ToolTipPlacement.Below);
                Forms.Application.DoEvents();
                Assert(tooltip.Width <= 360 && tooltip.Height >= 38 && (string)Field(tooltip, "_description") == text.Split('\n')[1],
                    "the native tooltip measures and separates its title/helper: " + tool.Id + ", " + language);
                if (renders is not null && tool.Id is "pin" or "ruler" or "scroll")
                {
                    Directory.CreateDirectory(renders);
                    using var image = new Bitmap(tooltip.Width, tooltip.Height);
                    tooltip.DrawToBitmap(image, tooltip.ClientRectangle);
                    image.Save(Path.Combine(renders, $"toolbar-tip-{tool.Id}-{language}-{(dark ? "dark" : "light")}.png"));
                }
            }
            owner.Close();
        }
    }

    private static void CheckGlobalToolDispatch(CyberSnap.App app)
    {
        using var source = new Bitmap(1200, 800);
        using var overlay = new RegionOverlayForm(source, new Rectangle(0, 0, 1200, 800), windowDetectionMode: WindowDetectionMode.Off);
        overlay.SetEnabledTools(null); overlay.Show(); Forms.Application.DoEvents();
        var launches = new Dictionary<CaptureMode, int>();
        void Launched(CaptureMode mode) => launches[mode] = launches.GetValueOrDefault(mode) + 1;
        overlay.StandaloneOcrRequested += () => Launched(CaptureMode.Ocr);
        overlay.StandaloneScanRequested += () => Launched(CaptureMode.Scan);
        overlay.StandaloneColorPickerRequested += () => Launched(CaptureMode.ColorPicker);
        overlay.StandaloneRulerRequested += () => Launched(CaptureMode.Ruler);
        overlay.StandaloneZoomRequested += () => Launched(CaptureMode.Zoom);
        var handlers = new[] { ("OnStandaloneOcrHotkeyPressed", CaptureMode.Ocr),
            ("OnStandaloneScanHotkeyPressed", CaptureMode.Scan), ("OnStandaloneColorPickerHotkeyPressed", CaptureMode.ColorPicker),
            ("OnStandaloneRulerHotkeyPressed", CaptureMode.Ruler), ("OnStandaloneZoomHotkeyPressed", CaptureMode.Zoom) };
        foreach (var (handler, mode) in handlers)
        {
            CallZoomApp(app, handler);
            Assert(launches.GetValueOrDefault(mode) == 1 && (int)ZoomAppField(app, "_activeStandaloneTools")! == 0,
                "global tool hotkey uses the existing overlay handoff exactly once: " + mode);
        }
        app.OnPinToolProxy();
        Assert(overlay.PinImageOnConfirm && overlay.CurrentMode == CaptureMode.Rectangle && ZoomAppField(app, "_pinChooser") is null,
            "global Pin reuses the frozen selection instead of opening a competing chooser");
        Call(overlay, "SetMode", CaptureMode.Rectangle, "rect", false);
        SetField(overlay, "_isConfirmingSelection", true);
        foreach (var (handler, mode) in handlers)
        {
            CallZoomApp(app, handler);
            Assert(launches[mode] == 1 && overlay.CurrentMode == CaptureMode.Rectangle,
                "global launchers preserve active confirmation instead of resetting annotations: " + mode);
        }
        app.OnPinToolProxy();
        Assert(!overlay.PinImageOnConfirm, "Pin cannot overwrite the destination of an active confirmation");
        SetField(overlay, "_isConfirmingSelection", false);
        overlay.Close();
        foreach (var (handler, id) in new[] { ("OnFullscreenHotkeyPressed", "_fullscreen"),
            ("OnRepeatLastAreaHotkeyPressed", "_repeatLastArea") })
        {
            using var immediate = new RegionOverlayForm(source, new Rectangle(0, 0, 1200, 800), windowDetectionMode: WindowDetectionMode.Off);
            int calls = 0;
            immediate.ImmediateCaptureRequested += action => { Assert(action == id, "the global capture action keeps its identity"); calls++; };
            immediate.Show(); Forms.Application.DoEvents();
            CallZoomApp(app, handler);
            Assert(calls == 1 && !immediate.Visible, "global immediate captures reuse the toolbar's handoff: " + id);
        }
    }

    private static string[] ToolbarKeyboardIds(RegionOverlayForm overlay) => ((IEnumerable)Call(overlay, "GetToolbarKeyboardTargets")!)
        .Cast<object>().Select(t => (string)t.GetType().GetProperty("Id")!.GetValue(t)!).ToArray();

    private static void CheckToolbarKeyboardNavigation(string? renders)
    {
        foreach (var dock in Enum.GetValues<CaptureDockSide>())
        {
            using var source = new Bitmap(1200, 800);
            using var overlay = new RegionOverlayForm(source, new Rectangle(0, 0, 1200, 800), windowDetectionMode: WindowDetectionMode.Off)
                { CaptureDockSide = dock, ConfirmRegionBeforeCapture = true };
            overlay.SetEnabledTools(new List<string> { "rect", "pin", "ruler" });
            overlay.Show(); Forms.Application.DoEvents();
            var ids = ToolbarKeyboardIds(overlay);
            Assert(ids.Contains("bar:pin") && !ids.Contains("bar:zoom") && !ids.Contains("bar:ocr"), "Tab includes only visible toolbar tools: " + dock);
            foreach (string id in ids)
            {
                Assert((bool)Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Tab)!, "Tab is handled inside the capture toolbar");
                Assert((string)Field(overlay, "_toolbarKeyboardFocusId") == id, "Tab follows visible button order: " + id);
            }
            Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Tab);
            Assert((string)Field(overlay, "_toolbarKeyboardFocusId") == ids[0], "Tab wraps to the first visible button");
            Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Shift | Forms.Keys.Tab);
            Assert((string)Field(overlay, "_toolbarKeyboardFocusId") == ids[^1], "Shift+Tab wraps backwards");
            SetField(overlay, "_isTyping", true);
            Assert(!(bool)Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Tab)!, "Tab leaves inline text editing alone");
            SetField(overlay, "_isTyping", false);
            SetField(overlay, "_emojiPickerOpen", true);
            Assert(!(bool)Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Space)!, "Space remains available to emoji search");
            SetField(overlay, "_emojiPickerOpen", false);
            var mode = overlay.CurrentMode;
            SetField(overlay, "_mode", CaptureMode.Emoji); SetField(overlay, "_isPlacingEmoji", true);
            Assert(!(bool)Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Tab)!, "Tab retains its existing emoji-placement action");
            SetField(overlay, "_mode", mode); SetField(overlay, "_isPlacingEmoji", false);
            SetField(overlay, "_toolbarKeyboardFocusId", "bar:pin");
            Assert(!(bool)Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Enter)!, "Enter is reserved for capture confirmation");
            Assert((bool)Call(overlay, "TryHandleToolbarNavigation", Forms.Keys.Space)! && overlay.PinImageOnConfirm,
                "Space activates the focused Pin through the normal click path: " + dock);
            Call(overlay, "OnKeyUp", new Forms.KeyEventArgs(Forms.Keys.Space));
            if (renders is not null && dock == CaptureDockSide.Top)
            {
                var bounds = (Rectangle)Field(overlay, "_toolbarRect");
                using var image = new Bitmap(bounds.Width + 16, bounds.Height + 16);
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.FromArgb(13, 15, 23)); graphics.TranslateTransform(8 - bounds.X, 8 - bounds.Y);
                overlay.PaintToolbarTo(graphics);
                image.Save(Path.Combine(renders, "toolbar-keyboard-focus.png"));
            }
            Call(overlay, "ClearToolbarKeyboardFocus");
            Assert(Field(overlay, "_toolbarKeyboardFocusId") is null, "returning to pointer interaction clears keyboard focus");
            overlay.Close();
        }
    }

    private static void CheckStageKeyboardControls()
    {
        using var source = new Bitmap(1200, 800);
        using var overlay = new RegionOverlayForm(source, new Rectangle(0, 0, 1200, 800), windowDetectionMode: WindowDetectionMode.Off)
            { ConfirmRegionBeforeCapture = true };
        overlay.SetEnabledTools(null); overlay.Show(); Forms.Application.DoEvents();
        void Select()
        {
            OverlayZoomMouse(overlay, "OnMouseDown", 240, 220);
            OverlayZoomMouse(overlay, "OnMouseMove", 700, 520);
            OverlayZoomMouse(overlay, "OnMouseUp", 700, 520);
            Assert((bool)Field(overlay, "_isConfirmingSelection"), "region selection still enters normal confirmation");
        }
        Select();
        var ids = ToolbarKeyboardIds(overlay);
        Assert(ids.Any(id => id.StartsWith("confirm:")) && !ids.Contains("bar:pin"),
            "confirmation keyboard navigation follows its visible controls rather than the hidden capture tools");
        var retry = new Forms.KeyEventArgs(Forms.Keys.R);
        Call(overlay, "OnKeyDown", retry);
        Assert(retry.Handled && !(bool)Field(overlay, "_isConfirmingSelection") && overlay.CurrentMode == CaptureMode.Rectangle,
            "R keeps its contextual Retry action instead of activating Ruler");
        Select();
        int committed = 0; overlay.RegionSelected += _ => committed++;
        SetField(overlay, "_toolbarKeyboardFocusId", "confirm:Cancel");
        var enter = new Forms.KeyEventArgs(Forms.Keys.Enter);
        Call(overlay, "OnKeyDown", enter);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (committed == 0 && DateTime.UtcNow < deadline) { Forms.Application.DoEvents(); Pump(); Thread.Sleep(10); }
        Assert(enter.Handled && committed == 1, "Enter confirms capture even when another toolbar action has keyboard focus");
        overlay.Close();
    }
}
