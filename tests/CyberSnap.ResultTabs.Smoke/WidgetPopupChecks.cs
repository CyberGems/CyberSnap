using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    private static Forms.Screen[] WidgetScreens() => (Forms.Screen[])typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!
        .GetMethod("GetSortedScreens")!.Invoke(null, null)!;

    private static void CheckWidgetPopups(SettingsService settings, string? output)
    {
        var original = WidgetPreferences.FromSettings(settings.Settings);
        string originalLanguage = settings.Settings.InterfaceLanguage;
        double originalScale = UiScale.Current;
        bool originalAnimations = settings.Settings.DisableAnimations;
        var screens = WidgetScreens();
        settings.Settings.DisableAnimations = true;
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        try
        {
            widget.Show();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            foreach (string language in new[] { "en", "es" })
            foreach (double scale in new[] { 1.1, 1.4 })
            foreach (int monitor in Enumerable.Range(0, screens.Length))
            foreach (var edge in Enum.GetValues<CaptureDockSide>())
            foreach (double offset in new[] { 0.0, 0.5, 1.0 })
            {
                // The largest scale covers both end positions in Spanish as well.
                if (scale == 1.4 && (language == "en" || offset == 0.5)) continue;
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.WidgetMonitorIndex = monitor;
                settings.Settings.WidgetDockEdge = edge;
                settings.Settings.WidgetDockPositionOffset = offset;
                settings.Save();
                UiScale.Set(scale);
                widget.RefreshLocalization();
                widget.RefreshLayout();
                widget.CollapseImmediately();
                Pump();
                var panel = Element<Border>(widget, "MainPanelBorder");
                bool horizontal = edge is CaptureDockSide.Top or CaptureDockSide.Bottom;
                Assert(Math.Abs(panel.ActualWidth - (horizontal ? 196 : 9)) < 1
                    && Math.Abs(panel.ActualHeight - (horizontal ? 9 : 332)) < 1,
                    "the peek draws its own complete outline, without clipping a full-size panel", false);
                var clip = LayoutInformation.GetLayoutClip(panel);
                Assert(clip is null || clip.Bounds.Contains(new Rect(panel.RenderSize)), "the peek outline fits its layout clip", false);
                var fill = Element<Border>(widget, "PeekGrip");
                var thickness = panel.BorderThickness;
                double dockedBorder = edge switch { CaptureDockSide.Top => thickness.Top, CaptureDockSide.Bottom => thickness.Bottom,
                    CaptureDockSide.Left => thickness.Left, _ => thickness.Right };
                Assert(dockedBorder == 0 && thickness.Left + thickness.Top + thickness.Right + thickness.Bottom == 3,
                    "the peek has three outlined sides and an open edge against the screen", false);
                Assert(Math.Abs(fill.ActualWidth - (panel.ActualWidth - thickness.Left - thickness.Right)) < 1
                    && Math.Abs(fill.ActualHeight - (panel.ActualHeight - thickness.Top - thickness.Bottom)) < 1,
                    "the peek fill fits inside the outline without hidden drag strips stretching it", false);
                if (output is not null && offset == 0.5)
                    RenderNativeWidget(widget, output, $"peek-{monitor}-{edge}-{language}.png");

                Call(widget, "ExpandWidget");
                AwaitCondition(() => !widget.HasAnimatedProperties, "Widget expansion did not settle");
                Assert(panel.BorderThickness == new Thickness(1), "expanding restores the complete panel border", false);
                // Isolate geometry measurements from unrelated applications taking
                // foreground on the shared desktop. Production modal dismissal is
                // checked separately below and by CheckWidgetMenu, with AutoClose on.
                var root = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
                KeepVisibleForMeasurement(root);
                Call(widget, "ShowThemedMenu", root, true);
                Pump();
                string closeReason = "none";
                root.Closed += (_, e) =>
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
                    closeReason = e.CloseReason + $" (foreground process {process}, fixture {Environment.ProcessId})";
                };
                var work = WidgetStatic<System.Drawing.Rectangle>("GetPhysicalWorkingArea", screens[monitor]);
                double dpi = GetDpiForWindow(new WindowInteropHelper(widget).Handle) / 96.0;
                string context = $"monitor={monitor}, edge={edge}, offset={offset}, ui={scale}, lang={language}, dpi={dpi}";
                int count = 0;
                Inspect(root, "More");
                Assert(count >= 10, "the popup check visits every nested submenu: " + context, false);
                var button = Element<Button>(widget, "WidgetMoreButton");
                var buttonStart = button.PointToScreen(new Point());
                var buttonEnd = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                var buttonRect = System.Drawing.Rectangle.FromLTRB((int)buttonStart.X, (int)buttonStart.Y, (int)Math.Ceiling(buttonEnd.X), (int)Math.Ceiling(buttonEnd.Y));
                Assert(!root.Bounds.IntersectsWith(buttonRect), "the menu leaves More available for a second click: " + context, false);
                Call(widget, "More_Click", button, new RoutedEventArgs());
                Pump();
                Assert(root.IsDisposed, "nested navigation still permits second-click dismissal", false);
                if (edge == CaptureDockSide.Top && offset == 0)
                    Console.WriteLine($"PASS: popup bounds, scale and all nested levels, monitor {monitor}, {language}, UI {scale}.");

                void Inspect(Forms.ToolStripDropDown menu, string path)
                {
                    count++;
                    Assert(menu.Visible && work.Contains(menu.Bounds), "every popup stays on the widget's monitor: " + context + " " + path + " " + menu.Bounds
                        + $", visible={menu.Visible}, rootVisible={root.Visible}, closed={closeReason}", false);
                    Assert(menu.Font.Unit == System.Drawing.GraphicsUnit.Pixel && Math.Abs(menu.Font.Size - 12 * scale * dpi) < 0.1,
                        "every popup uses its target monitor's physical font scale: " + context + " " + path, false);
                    foreach (Forms.ToolStripItem item in menu.Items)
                    {
                        Assert(item.Bounds.Top >= 0 && item.Bounds.Bottom <= menu.Height, "every menu row remains visible: " + context + " " + path, false);
                        if (item is not Forms.ToolStripMenuItem command) continue;
                        Assert(Math.Abs(command.Height - 38 * scale * dpi) <= 1,
                            "menu row padding follows both monitor DPI and UI scale: " + context + " " + path, false);
                        int textWidth = Forms.TextRenderer.MeasureText(command.Text, command.Font, System.Drawing.Size.Empty,
                            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix).Width;
                        int gutter = (int)Math.Round((menu is Forms.ToolStripDropDownMenu dd && dd.ShowImageMargin ? 43 : 14) * scale * dpi);
                        int right = (int)Math.Round((command.HasDropDownItems ? 32 : 12) * scale * dpi);
                        Assert(textWidth <= command.Width - gutter - right + 2,
                            "menu text fits without truncation or colliding with arrows: " + context + " " + path + "/" + command.Text, false);
                        if (!command.HasDropDownItems) continue;
                        command.Select();
                        command.ShowDropDown();
                        Pump();
                        Inspect(command.DropDown, path + "/" + command.Text);
                        command.HideDropDown();
                        Pump();
                    }
                    if (output is not null && language == "es" && offset == 0.5 && path is "More" or "More/Widget" or "More/Widget/Posición")
                    {
                        using var image = new System.Drawing.Bitmap(menu.Width, menu.Height);
                        menu.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
                        image.Save(Path.Combine(output, $"popup-{monitor}-{edge}-{path.Replace('/', '-')}.png"));
                    }
                }
            }
            Console.WriteLine("PASS: More and all submenu levels fit on both actual DPI monitors, all four edges, both end positions/center, EN/ES and default/largest UI scales. Peek outlines fit their full bounds.");
        }
        finally
        {
            widget.Close();
            original.ApplyTo(settings.Settings);
            settings.Settings.InterfaceLanguage = originalLanguage;
            settings.Settings.DisableAnimations = originalAnimations;
            UiScale.Set(originalScale);
            settings.Save();
        }
    }

    private static void RenderNativeWidget(CaptureWidgetWindow widget, string output, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(widget);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(widget.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(widget.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(widget);
        Directory.CreateDirectory(output);
        using var file = File.Create(Path.Combine(output, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    private static void KeepVisibleForMeasurement(Forms.ToolStripDropDown menu)
    {
        menu.AutoClose = false;
        foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
            if (item.HasDropDownItems) KeepVisibleForMeasurement(item.DropDown);
    }

    private static void CheckWidgetActiveMonitor(SettingsService settings)
    {
        var original = WidgetPreferences.FromSettings(settings.Settings);
        var screens = WidgetScreens();
        settings.Settings.WidgetMonitorIndex = -1;
        settings.Save();
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        var focus = new Window { Width = 300, Height = 200, ShowInTaskbar = false, Title = "Widget focus verification" };
        try
        {
            widget.Show();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            focus.Show();
            Call(widget, "ExpandWidget");
            AwaitCondition(() => !widget.HasAnimatedProperties, "Widget expansion did not settle for focus testing");
            Call(widget, "More_Click", Element<Button>(widget, "WidgetMoreButton"), new RoutedEventArgs());
            Pump();
            var modalMenu = GetField<Forms.ContextMenuStrip>(widget, "_contextMenu");
            Assert(modalMenu.AutoClose && modalMenu.Visible, "production menus retain normal modal dismissal", false);
            string reason = "none";
            modalMenu.Closed += (_, e) => reason = e.CloseReason.ToString();
            Assert(focus.Activate(), "the focus fixture receives foreground outside the modal menu", false);
            Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty);
            try { AwaitCondition(() => modalMenu.IsDisposed, "The modal menu did not close after a focus change", 1500); }
            catch (Exception ex)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
                throw new InvalidOperationException($"Modal focus dismissal: visible={modalMenu.Visible}, reason={reason}, foreground={GetForegroundWindow()}, "
                    + $"focusWindow={new WindowInteropHelper(focus).Handle}, process={foregroundProcess}, fixture={Environment.ProcessId}", ex);
            }
            Assert(modalMenu.IsDisposed && reason == "AppFocusChange" && !GetField<bool>(widget, "_moreMenuOpen"),
                "real foreground focus outside the menu dismisses it and clears More's highlight", false);
            widget.CollapseImmediately();
            var helper = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!;
            foreach (var screen in screens.Reverse())
            {
                var point = new System.Drawing.Point(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2);
                helper.GetMethod("CenterWindowOnPhysicalMonitor")!.Invoke(null, [focus, point]);
                Assert(focus.Activate(), "the owned focus fixture activates on each monitor", false);
                Pump();
                Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty);
                Thread.Sleep(450);
                Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty);
                Pump();
                Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName == screen.DeviceName,
                    "actual foreground focus moves the collapsed widget to that monitor", false);
                widget.RefreshLayout();
                widget.CollapseImmediately();
                Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName == screen.DeviceName,
                    "layout and expansion state no longer reselect the cursor's monitor", false);
            }
            if (screens.Length > 1)
            {
                var before = screens[0];
                var target = screens[1];
                Call(widget, "PositionWindowOnScreen", before);
                Call(widget, "ObserveForegroundMonitor", new IntPtr(111), target, 1000L);
                Call(widget, "ObserveForegroundMonitor", new IntPtr(111), target, 1399L);
                Assert(Here() == before.DeviceName, "brief foreground changes do not cause immediate monitor jumps", false);
                Call(widget, "ObserveForegroundMonitor", new IntPtr(111), target, 1400L);
                Assert(Here() == target.DeviceName, "stable foreground focus moves the widget after the delay", false);
                long time = 2000;
                foreach (string flag in new[] { "_mouseInWindow", "_isExpanded", "_contextMenuOpen", "_isDragArmed", "_isDragging", "_awaitingSessionRestore", "_sessionRestoreInProgress", "_suppressHoverExpand" })
                {
                    var next = Here() == before.DeviceName ? target : before;
                    string current = Here();
                    SetField(widget, flag, true);
                    Call(widget, "ObserveForegroundMonitor", new IntPtr(time), next, time);
                    Call(widget, "ObserveForegroundMonitor", new IntPtr(time), next, time + 500);
                    Assert(Here() == current, "foreground tracking pauses during " + flag, false);
                    SetField(widget, flag, false);
                    Call(widget, "ObserveForegroundMonitor", new IntPtr(time), next, time + 600);
                    Assert(Here() == current, "interaction resumes with a fresh stability delay after " + flag, false);
                    Call(widget, "ObserveForegroundMonitor", new IntPtr(time), next, time + 1000);
                    Assert(Here() == next.DeviceName, "foreground tracking resumes after " + flag, false);
                    time += 1000;
                }
                settings.Settings.WidgetMonitorIndex = 0;
                widget.PositionWindow();
                Call(widget, "ObserveForegroundMonitor", new IntPtr(999), target, time + 2000);
                Call(widget, "ObserveForegroundMonitor", new IntPtr(999), target, time + 2500);
                Assert(Here() == before.DeviceName, "a fixed monitor is never overridden by foreground tracking", false);
                settings.Settings.WidgetMonitorIndex = -1;
                CheckWidgetForegroundTransitions(widget, focus, settings, screens);
            }
            Console.WriteLine("PASS: real foreground focus on both monitors, stable-delay filtering, no cursor/layout jumps, paused interaction and fixed-monitor isolation.");
            string Here() => Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName;
        }
        finally
        {
            focus.Close();
            widget.Close();
            original.ApplyTo(settings.Settings);
            settings.Save();
        }
    }
}
