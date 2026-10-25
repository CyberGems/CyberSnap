using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

internal static partial class Program
{
    private static void CheckConfirmationChrome(SettingsService settings, string? renders = null, bool show = false)
    {
        string previous = System.Text.Json.JsonSerializer.Serialize(settings.Settings);
        double previousScale = UiScale.Current;
        try
        {
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            foreach (double scale in new[] { 1.0, 1.4 })
            {
                Theme.SetMode(theme); UiScale.Set(scale);
                using var normal = new Bitmap(44, 24);
                using var layered = new Bitmap(44, 24);
                var drawGrip = typeof(RegionOverlayForm).GetMethod("DrawToolbarGripDots", BindingFlags.Static | BindingFlags.NonPublic)!;
                foreach (var pair in new[] { (normal, CompositingQuality.Default), (layered, CompositingQuality.HighQuality) })
                {
                    using var g = Graphics.FromImage(pair.Item1);
                    g.Clear(UiChrome.SurfaceTier1);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = pair.Item2;
                    drawGrip.Invoke(null, new object[] { g, new Rectangle(0, 0, 44, 24) });
                    Assert(g.CompositingQuality == pair.Item2, "painting a grip preserves the caller's graphics settings");
                }
                Assert(Enumerable.Range(0, 44 * 24).All(i => normal.GetPixel(i % 44, i / 44) == layered.GetPixel(i % 44, i / 44)),
                    "grips have identical pixels on layered and overlay surfaces: " + theme + ", " + scale);
            }
            foreach (string language in new[] { "en", "es" })
            {
                Theme.SetMode(AppThemeMode.Dark); UiScale.Set(1.1);
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.EnabledTools = ToolDef.DefaultEnabledIds();
                settings.Settings.ShowAnnotationToolbar = true;
                settings.Settings.HasSeenQuickStartGuide = true;
                UseTemporaryConfirmationSettings(settings);
                LocalizationService.ApplyCurrentCulture(language);
                using var source = new Bitmap(1000, 800);
                using var overlay = ConfirmationFixture(source, settings, CaptureMode.Rectangle, false, show);
                var host = (Rectangle)Field(overlay, "_annotationToolbarHostRect");
                var toolbar = Field(overlay, "_toolbarForm") as Forms.Form;
                var nativeBounds = toolbar?.Bounds;
                object? surface = toolbar is null ? null : Field(toolbar, "_surface");
                var frameTimes = new List<double>();
                foreach (float amount in new[] { 0f, .25f, .5f, .75f, 1f, .5f, 0f })
                {
                    int version = (int)Field(overlay, "_toolbarRenderVersion");
                    var watch = Stopwatch.StartNew();
                    Call(overlay, "ApplyHistoryUtilitiesReveal", amount);
                    frameTimes.Add(watch.Elapsed.TotalMilliseconds);
                    Assert((Rectangle)Field(overlay, "_annotationToolbarHostRect") == host,
                        "revealing history buttons reserves a stable host rather than resizing it");
                    if (show)
                    {
                        Assert(toolbar!.Bounds == nativeBounds && ReferenceEquals(surface, Field(toolbar, "_surface")),
                            "history frames reuse the same native window and bitmap");
                        Assert((int)Field(overlay, "_toolbarRenderVersion") - version == 1,
                            "a history animation frame presents the toolbar once");
                        if (renders is not null && amount is 0f or .5f or 1f)
                        {
                            System.IO.Directory.CreateDirectory(renders);
                            var dock = (Rectangle)Field(overlay, "_toolbarRect");
                            dock.Offset(overlay.Left - toolbar.Left, overlay.Top - toolbar.Top);
                            using var picture = ((Bitmap)Field(toolbar, "_surface")).Clone(dock, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                            picture.Save(System.IO.Path.Combine(renders, $"annotation-history-{language}-{amount:0.0}.png"));
                        }
                    }
                }
                Console.WriteLine($"History frame time: mean={frameTimes.Average():F2} ms, max={frameTimes.Max():F2} ms, visible={show}");
                var annotations = (List<Annotation>)Field(overlay, "_undoStack");
                annotations.Add(new LineAnnotation(new Point(200, 200), new Point(350, 250), Color.Cyan));
                Call(overlay, "MarkCommittedAnnotationsDirty");
                Call(overlay, "UpdateHistoryUtilitiesVisibility", true);
                Thread.Sleep(220); // A delayed UI frame must catch up by elapsed time.
                Call(overlay, "OnHistoryUtilitiesAnimTick", null!, EventArgs.Empty);
                Assert((float)Field(overlay, "_historyUtilitiesRevealAmt") == 1f
                    && Field(overlay, "_historyUtilitiesAnimTimer") is Forms.Timer { Enabled: false },
                    "history reveal reaches its endpoint after a delayed frame without a long animation tail");
                annotations.Clear();
                Call(overlay, "UpdateHistoryUtilitiesVisibility", true);
                Thread.Sleep(220);
                Call(overlay, "OnHistoryUtilitiesAnimTick", null!, EventArgs.Empty);
                Assert((float)Field(overlay, "_historyUtilitiesRevealAmt") == 0f,
                    "history controls also finish hiding on time");
                if (!show) continue;

                // Real native popup paths: hover cannot open or dismiss this click-owned menu.
                var options = (Rectangle)Field(overlay, "_confirmOptionsPillRect");
                var center = new Point(options.X + options.Width / 2, options.Y + options.Height / 2);
                OverlayZoomMouse(overlay, "OnMouseMove", center.X, center.Y);
                Thread.Sleep(120);
                OverlayZoomMouse(overlay, "OnMouseMove", center.X, center.Y);
                Assert(Field(overlay, "_toolbarContextMenu") is null, "hovering More does not open confirmation options");
                OverlayZoomMouse(overlay, "OnMouseDown", center.X, center.Y);
                using (var menu = (Forms.ContextMenuStrip)Field(overlay, "_toolbarContextMenu"))
                {
                    Assert(menu.Visible, "clicking More opens confirmation options");
                    OverlayZoomMouse(overlay, "OnMouseMove", 400, 300);
                    Call(overlay, "HoverHoldTimer_Tick", null!, EventArgs.Empty);
                    Assert(menu.Visible, "moving away from More leaves a clicked menu available for reading");
                    menu.Close();
                }
                Forms.Application.DoEvents();
                SetField(overlay, "_toolbarKeyboardFocusId", "confirm:options");
                Call(overlay, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Space));
                Assert(Field(overlay, "_toolbarContextMenu") is Forms.ContextMenuStrip { Visible: true },
                    "Space still opens More after keyboard navigation");
                Call(overlay, "OnKeyDown", new Forms.KeyEventArgs(Forms.Keys.Escape));
                Assert(Field(overlay, "_toolbarContextMenu") is null && (bool)Field(overlay, "_isConfirmingSelection"),
                    "Escape dismisses the clicked menu without canceling the capture");
                Call(overlay, "OnKeyUp", new Forms.KeyEventArgs(Forms.Keys.Space));
                Call(overlay, "ClearToolbarKeyboardFocus");

                // Popups still fit their actual native host when explicitly opened.
                foreach (string popup in new[] { "Color", "Stroke", "Emoji" })
                {
                    string flag = "_" + popup.ToLowerInvariant() + "PickerOpen";
                    SetField(overlay, flag, true);
                    Call(overlay, "UpdateToolbarSurfaceOnly");
                    var bounds = (Rectangle)Call(overlay, "Get" + popup + "PickerBounds")!;
                    bounds.Offset(overlay.Location);
                    Assert(toolbar!.Bounds.Contains(bounds), "annotation popup remains inside the native host: " + popup);
                    SetField(overlay, flag, false);
                }
                Call(overlay, "UpdateToolbarSurfaceOnly");
                CheckCaptureModalTooltips(overlay, language, renders);
                if (renders is not null)
                {
                    System.IO.Directory.CreateDirectory(renders);
                    using var picture = new Bitmap(source.Width, source.Height);
                    overlay.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
                    picture.Save(System.IO.Path.Combine(renders, "confirm-more-" + language + ".png"));
                }
            }
        }
        finally
        {
            typeof(SettingsService).GetProperty("Settings")!.SetValue(settings, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(previous)!);
            settings.Save(); settings.FlushPendingWrites();
            UiScale.Set(previousScale); Theme.SetMode(AppThemeMode.Dark);
            LocalizationService.ApplyCurrentCulture(settings.Settings.InterfaceLanguage);
        }
    }

    private static void CheckCaptureModalTooltips(RegionOverlayForm overlay, string language, string? renders)
    {
        var kinds = ((Array)Field(overlay, "_confirmChromeKinds")).Cast<object>().ToArray();
        int cancel = Array.FindIndex(kinds, kind => kind.ToString() == "Cancel");
        SetField(overlay, "_hoveredConfirmButton", cancel);
        Call(overlay, "ShowConfirmTooltip");
        Assert((bool)Field(overlay, "_tooltipVisible"), "the underlying capture tooltip is visible before the modal opens");
        bool seen = false, clean = false, noRedundantTips = false;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            var dialog = System.Windows.Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "ThemedConfirmDialog");
            if (dialog is null) return;
            timer.Stop();
            try
            {
                seen = (bool)Field(overlay, "_overlayConfirmOpen");
                SetField(overlay, "_hoveredConfirmButton", cancel);
                SetField(overlay, "_hoveredConfirmOptionsPill", true);
                Call(overlay, "ShowConfirmTooltip");
                Call(overlay, "ShowConfirmOptionsTooltip");
                Call(overlay, "ShowToolbarTooltip");
                Call(overlay, "HoverHoldTimer_Tick", null!, EventArgs.Empty);
                clean = !(bool)Field(overlay, "_tooltipVisible") && Field(overlay, "_toolbarToolTip") is Forms.Form { Visible: false };
                noRedundantTips = VisualDescendants(dialog).OfType<Button>().All(b => b.ToolTip is null);
                Render(dialog, renders, "capture-modal-" + language + ".png");
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog.DialogResult = false; }
        };
        timer.Start();
        try
        {
            bool accepted = (bool)Call(overlay, "ShowOverlayConfirm", "Cancel capture?", "This will discard the annotations on this capture.",
                "Confirm cancellation", "Continue selection", "question", true)!;
            if (failure is not null) throw failure;
            Assert(!accepted && seen && clean && noRedundantTips,
                "the discard modal has no redundant button tooltip or lingering capture tooltip: " + language);
            Assert(!(bool)Field(overlay, "_overlayConfirmOpen") && !(bool)Field(overlay, "_tooltipVisible"),
                "closing the modal clears its tooltip suppression state without reviving a stale tooltip");
        }
        finally { timer.Stop(); }
    }
}
