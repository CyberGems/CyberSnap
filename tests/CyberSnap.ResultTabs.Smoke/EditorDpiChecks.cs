using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Controls;
using CyberSnap.UI.Editor;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr EditorWindowAtPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void EditorMouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);

    private static void CheckEditorTitleAbout(CyberSnap.App app)
    {
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
        using var bitmap = new Bitmap(128, 128);
        using var editor = new EditorForm(bitmap);
        var close = typeof(EditorForm).GetMethod("OnFormClosing",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .CreateDelegate<Forms.FormClosingEventHandler>(editor);
        editor.FormClosing -= close;
        var instance = typeof(EditorForm).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        instance.SetValue(null, editor);
        var originalCursor = Forms.Cursor.Position;
        try
        {
            editor.Show();
            AwaitCondition(() => editor.Opacity == 1, "Editor did not finish opening");
            var work = Forms.Screen.FromControl(editor).WorkingArea;
            editor.Bounds = new Rectangle(work.Location + new Size(40, 40), editor.MinimumSize);
            editor.Activate();
            AwaitCondition(() => GetForegroundWindow() == editor.Handle, "The fixture could not activate the Editor", 5000);
            var title = GetField<Forms.Button>(editor, "_editorTitleButton");
            GetField<Forms.Timer>(editor, "_tooltipTimer").Stop();
            var titleCenter = new Point(title.Width / 2, title.Height / 2);
            var start = title.PointToScreen(titleCenter);
            Forms.Cursor.Position = start;
            Assert(EditorWindowAtPoint(start) == title.Handle && Forms.Control.MouseButtons == Forms.MouseButtons.None,
                "the pointer is on the test Editor title before sending mouse input", false);

            Point originalLocation = editor.Location;
            Task? move = null;
            EditorMouseEvent(0x0002, 0, 0, 0, 0); // MOUSEEVENTF_LEFTDOWN
            try
            {
                AwaitCondition(() => title.Capture, "The title did not capture the press", 5000);
                move = Task.Run(() =>
                {
                    try
                    {
                        Forms.Cursor.Position = start + new Size(30, 0);
                        Thread.Sleep(250);
                        Forms.Cursor.Position = start + new Size(90, 40);
                        Thread.Sleep(100);
                    }
                    finally { EditorMouseEvent(0x0004, 0, 0, 0, 0); } // MOUSEEVENTF_LEFTUP
                });
                AwaitCondition(() => move.IsCompleted, "The title drag did not complete", 5000);
                move.GetAwaiter().GetResult();
            }
            finally
            {
                if (move is null)
                    EditorMouseEvent(0x0004, 0, 0, 0, 0);
            }
            Assert(editor.Location != originalLocation && !title.Focused && ZoomAppField(app, "_aboutWindow") is null,
                "dragging the title moves the Editor without opening About");

            Forms.Cursor.Position = title.PointToScreen(titleCenter);
            var position = (IntPtr)((titleCenter.Y << 16) | titleCenter.X);
            SendMessageW(title.Handle, 0x0201, (IntPtr)1, position);
            SendMessageW(title.Handle, 0x0202, IntPtr.Zero, position);
            AwaitCondition(() => ZoomAppField(app, "_aboutWindow") is AboutWindow { IsVisible: true },
                "Clicking the title did not open About", 5000);
            var about = (AboutWindow)ZoomAppField(app, "_aboutWindow")!;
            var aboutHandle = new System.Windows.Interop.WindowInteropHelper(about).Handle;
            AwaitCondition(() => GetForegroundWindow() == aboutHandle, "About did not come to the foreground", 5000);
            Console.WriteLine("PASS: clicking the Editor title opens About in front.");
            about.Close();
            editor.Activate();
            Assert(title.Focus(), "the Editor title can receive keyboard focus", false);
            SendMessageW(title.Handle, 0x0100, (IntPtr)(int)Forms.Keys.Space, IntPtr.Zero);
            SendMessageW(title.Handle, 0x0101, (IntPtr)(int)Forms.Keys.Space, IntPtr.Zero);
            AwaitCondition(() => ZoomAppField(app, "_aboutWindow") is AboutWindow { IsVisible: true },
                "Space on the focused Editor title did not open About", 5000);
            Console.WriteLine("PASS: Space on the focused Editor title opens About.");
        }
        finally
        {
            (ZoomAppField(app, "_aboutWindow") as AboutWindow)?.Close();
            instance.SetValue(null, null);
            editor.Close();
            Forms.Cursor.Position = originalCursor;
        }
    }

    private static void CheckEditorScaleMenuTooltip()
    {
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
        using var bitmap = new Bitmap(128, 128);
        using var editor = new EditorForm(bitmap);
        var savePlacement = typeof(EditorForm).GetMethod("OnFormClosing",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .CreateDelegate<Forms.FormClosingEventHandler>(editor);
        editor.FormClosing -= savePlacement;

        Point originalPointer = Forms.Cursor.Position;
        using var other = new Forms.Form { Size = new Size(240, 160), StartPosition = Forms.FormStartPosition.Manual };
        try
        {
            editor.SetShowTooltips(true);
            editor.Show();
            AwaitCondition(() => editor.Opacity == 1, "Editor did not finish opening");
            editor.Activate();
            AwaitCondition(() => GetForegroundWindow() == editor.Handle, "The fixture could not activate the Editor", 5000);

            var scale = GetField<Forms.Button>(editor, "_scaleButton");
            var menuOpen = scale.GetType().GetProperty("MenuOpen")!;
            var tick = typeof(EditorForm).GetMethod("TooltipTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler>(editor);
            GetField<Forms.Timer>(editor, "_tooltipTimer").Stop();
            bool MenuIsOpen() => (bool)menuOpen.GetValue(scale)!;
            bool TipIsVisible() => Field(editor, "_hoverToolTip") is Forms.Form { Visible: true };
            Point anchor = scale.PointToScreen(new Point(scale.Width / 2, scale.Height / 2));
            Forms.Cursor.Position = anchor;
            Assert(scale.RectangleToScreen(scale.ClientRectangle).Contains(Forms.Cursor.Position),
                $"the pointer reaches the Scale button (expected {anchor}, actual {Forms.Cursor.Position})", false);
            bool TickUntilVisible()
            {
                tick(null, EventArgs.Empty);
                return TipIsVisible();
            }
            AwaitCondition(TickUntilVisible, "The Scale hover tooltip did not open", 5000);
            Assert(GetForegroundWindow() == editor.Handle, "showing the Scale tooltip keeps the Editor in the foreground");

            scale.PerformClick();
            Assert(MenuIsOpen() && !TipIsVisible(), "the Scale menu dismisses its visible tooltip");
            for (int i = 0; i < 8; i++)
                tick(null, EventArgs.Empty);
            Assert(MenuIsOpen() && !TipIsVisible(), "the Scale menu stays open past the tooltip dwell time");

            other.Location = editor.Location + new Size(30, 80);
            other.Show();
            other.Activate();
            AwaitCondition(() => GetForegroundWindow() == other.Handle && !MenuIsOpen(),
                "Switching windows did not dismiss the Scale menu", 5000);
            editor.Activate();
            AwaitCondition(() => GetForegroundWindow() == editor.Handle, "The fixture could not reactivate the Editor", 5000);
            Forms.Cursor.Position = scale.PointToScreen(new Point(scale.Width / 2, scale.Height / 2));
            AwaitCondition(TickUntilVisible, "The Scale tooltip did not return after the Editor regained focus", 5000);
            Assert(GetForegroundWindow() == editor.Handle, "the reused Scale tooltip keeps the Editor in the foreground");
            other.Activate();
            Assert(GetForegroundWindow() == other.Handle && !TipIsVisible(), "switching windows immediately hides the Scale tooltip");
        }
        finally
        {
            editor.Close();
            Forms.Cursor.Position = originalPointer;
        }
    }

    private static void CheckEditorDpi(string? renders)
    {
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
        double originalScale = UiScale.Current;
        string originalLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            foreach (string language in new[] { "es", "en" })
            foreach (double uiScale in new[] { UiScale.Max, UiScale.Default })
            {
                UiScale.Set(uiScale);
                LocalizationService.ApplyCurrentCulture(language);
                using var bitmap = new Bitmap(128, 128);
                using var editor = new EditorForm(bitmap);
                // Closing normally releases the global canvas message filter. Detach
                // placement persistence so the fixture doesn't update the user profile.
                var close = typeof(EditorForm).GetMethod("OnFormClosing",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
                    .CreateDelegate<Forms.FormClosingEventHandler>(editor);
                editor.FormClosing -= close;
                editor.Show();
                AwaitCondition(() => editor.Opacity == 1, "Editor did not finish opening");
                var screens = Forms.Screen.AllScreens;
                var sizes = new Dictionary<int, (int Button, int Text, int Tool, int ToolText)>();
                var checkedZoomDpis = new HashSet<int>();
                var palette = GetField<Forms.Panel>(editor, "_toolbarPanel");
                var toolViewport = GetField<Forms.Panel>(editor, "_toolViewport");
                var tools = ((System.Collections.IDictionary)Field(editor, "_toolButtons")).Values.Cast<Forms.Button>()
                    .Concat(((System.Collections.IEnumerable)Field(editor, "_transformButtons")).Cast<System.Runtime.CompilerServices.ITuple>()
                        .Select(entry => (Forms.Button)entry[0]!)).ToArray();
                var swatches = ((System.Collections.IDictionary)Field(editor, "_colorButtons")).Values.Cast<Forms.Control>()
                    .Append(GetField<Forms.Control>(editor, "_customColorButton"))
                    .Concat(((System.Collections.IEnumerable)Field(editor, "_strokeWidthButtons")).Cast<Forms.Control>()).ToArray();
                foreach (var screen in screens.Append(screens[0]))
                {
                    var work = screen.WorkingArea;
                    var moveSize = new Size(Math.Min(1400, work.Width), Math.Min(920, work.Height));
                    editor.Bounds = new Rectangle(work.Left + (work.Width - moveSize.Width) / 2,
                        work.Top + (work.Height - moveSize.Height) / 2, moveSize.Width, moveSize.Height);
                    AwaitCondition(() => GetDpiForWindow(editor.Handle) == editor.DeviceDpi,
                        $"WinForms did not settle on {screen.DeviceName}: native {GetDpiForWindow(editor.Handle)}, managed {editor.DeviceDpi}", 4000);
                    editor.Bounds = work;
                    Pump();
                    CheckEditorSidebar(palette, toolViewport, tools, swatches, $"{language}, UI {uiScale}, full work area {editor.ClientSize}");
                    SaveEditorRender(editor, renders, language, uiScale, "full");
                    var min = editor.MinimumSize;
                    editor.Bounds = new Rectangle(work.Location, min);
                    Pump();
                    int dpi = editor.DeviceDpi;
                    var context = $"{language}, UI {uiScale}, DPI {dpi}, {editor.ClientSize}";
                    uint nativeDpi = GetDpiForWindow(editor.Handle);
                    string actualScreen = Forms.Screen.FromHandle(editor.Handle).DeviceName;
                    Assert(nativeDpi == dpi && actualScreen == screen.DeviceName,
                        $"the Editor uses the destination monitor DPI (native DPI {nativeDpi}, {actualScreen}, expected {screen.DeviceName}, bounds {editor.Bounds}, work area {work}): {context}", false);

                    var row = GetField<Forms.FlowLayoutPanel>(editor, "_commandActions");
                    var viewport = GetField<Forms.Panel>(editor, "_commandViewport");
                    var scroll = GetField<Forms.HScrollBar>(editor, "_commandScroll");
                    int availableHeight = viewport.ClientSize.Height - (scroll.Visible ? scroll.Height : 0);
                    foreach (var button in row.Controls.OfType<Forms.Button>().Where(button => button.Text.Length > 0))
                    {
                        using var g = button.CreateGraphics();
                        Size text = Forms.TextRenderer.MeasureText(g, button.Text, button.Font);
                        var caption = (Rectangle)button.GetType().GetProperty("CaptionBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button)!;
                        Assert(text.Width <= caption.Width && text.Height <= caption.Height
                            && button.Top >= 0 && row.Top + button.Bottom <= availableHeight,
                            $"the command caption and button fit ({button.Text}, text={text}, caption={caption}, viewport={viewport.Bounds}, bar={viewport.Parent!.Bounds}, scroll={scroll.Bounds}/{scroll.Visible}, button={button.Bounds}): {context}", false);
                    }
                    var lastCommand = row.Controls[row.Controls.Count - 1];
                    if (scroll.Visible)
                        scroll.Value = scroll.Maximum - scroll.LargeChange + 1;
                    Pump();
                    var lastCommandBounds = viewport.RectangleToClient(lastCommand.RectangleToScreen(lastCommand.ClientRectangle));
                    Assert(viewport.ClientRectangle.Contains(lastCommandBounds),
                        $"the last command is fully reachable ({lastCommandBounds} in {viewport.ClientRectangle},"
                        + $" scroll={scroll.Visible}, offset={scroll.Value}): {context}", false);
                    scroll.Value = 0;

                    CheckEditorSidebar(palette, toolViewport, tools, swatches, context);
                    CheckEditorTitleTooltip(editor, context);
                    CheckEditorStatusBar(editor, context);
                    if (language == "es" && uiScale == UiScale.Default && checkedZoomDpis.Add(dpi))
                        CheckEditorZoomInteraction(editor, context);
                    SaveEditorRender(editor, renders, language, uiScale, "restored");

                    var command = GetField<Forms.Button>(editor, "_galleryButton");
                    var tool = tools[0];
                    using var graphics = command.CreateGraphics();
                    var dimensions = (command.Height, Forms.TextRenderer.MeasureText(graphics, command.Text, command.Font).Height,
                        tool.Height, tool.Font.Height);
                    Assert(command.Font.Size == tool.Font.Size, "command and tool labels use the same size: " + context, false);
                    if (sizes.TryGetValue(dpi, out var previous))
                        Assert(dimensions == previous, "returning to the same monitor restores the chrome metrics: " + context, false);
                    sizes[dpi] = dimensions;
                    Console.WriteLine($"PASS: Editor {context}, command {command.Size}, text {dimensions.Item2}px, tools {tool.Height}px.");

                    if (dpi == 144 && language == "es" && uiScale == UiScale.Default)
                    {
                        tools[0].Focus();
                        editor.MinimumSize = new Size(min.Width, 700);
                        editor.Height = 700;
                        Pump();
                        Assert(!ContainsControl(toolViewport, tools[^1]) && swatches.All(swatch => ContainsControl(palette, swatch)),
                            "a short window scrolls the tool grid while keeping the palette visible", false);
                        Assert(tools[^1].Focus() && ContainsControl(toolViewport, tools[^1]),
                            "keyboard focus reveals the last tool in the short window", false);
                        SaveEditorRender(editor, renders, language, uiScale, "short");
                        editor.MinimumSize = min;
                        editor.Height = min.Height;
                        Pump();
                        CheckEditorSidebar(palette, toolViewport, tools, swatches, "restored after scrolling");
                    }
                }
                if (sizes.Count > 1)
                {
                    int lowDpi = sizes.Keys.Min(), highDpi = sizes.Keys.Max();
                    var low = sizes[lowDpi];
                    var high = sizes[highDpi];
                    Assert(high.Button > low.Button && high.Text > low.Text && high.ToolText > low.ToolText,
                        "command buttons and all captions grow with monitor DPI");
                }
                editor.Close();
                Pump();
            }
        }
        finally
        {
            UiScale.Set(originalScale);
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }

    private static bool ContainsControl(Forms.Control parent, Forms.Control child)
        => child.Visible && parent.ClientRectangle.Contains(parent.RectangleToClient(child.RectangleToScreen(child.ClientRectangle)));

    private static void CheckEditorTitleTooltip(EditorForm editor, string context)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var titleButton = GetField<Forms.Button>(editor, "_editorTitleButton");
        Assert(titleButton.Text == LocalizationService.Translate("Annotations Editor")
            && !titleButton.Text.Contains("CyberSnap", StringComparison.OrdinalIgnoreCase),
            "the visible title only shows the editor name: " + context, false);
        Assert(titleButton.AccessibleDescription == (context.StartsWith("es,") ? "Sobre CyberSnap..." : "About CyberSnap..."),
            "the title has the localized About hint: " + context, false);
        var title = GetField<Forms.Panel>(editor, "_titleFileNameLabel");
        var indicator = (Rectangle)typeof(EditorForm).GetMethod("GetTitleStatusIndicatorBounds", flags)!.Invoke(editor, null)!;
        var hitTest = typeof(EditorForm).GetMethod("GetTitleStatusTooltipBounds", flags)!
            .CreateDelegate<Func<Point, Rectangle?>>(editor);
        int centerY = indicator.Top + indicator.Height / 2;
        Assert(hitTest(title.PointToScreen(new Point(indicator.Left + indicator.Width / 2, centerY))) == title.RectangleToScreen(indicator),
            "the status tooltip is anchored to the document indicator: " + context, false);
        Assert(hitTest(title.PointToScreen(new Point(indicator.Left - 1, centerY))) is null
            && hitTest(title.PointToScreen(new Point(indicator.Right, centerY))) is null
            && hitTest(title.PointToScreen(new Point(indicator.Left, indicator.Top - 1))) is null,
            "the rest of the title bar does not show the status tooltip: " + context, false);
    }

    private static void CheckEditorStatusBar(EditorForm editor, string context)
    {
        var status = GetField<Forms.Panel>(editor, "_statusBarPanel");
        foreach (string field in new[] { "_scaleButton", "_coordsLabel", "_zoomSlider", "_zoomResetButton", "_zoomViewBtn", "_toggleFitSwitch", "_togglePanLockSwitch" })
        {
            var control = GetField<Forms.Control>(editor, field);
            if (control.Visible)
                Assert(ContainsControl(status, control), $"the {field} control fits the compact status bar: {context}", false);
        }
        var zoom = GetField<Forms.Button>(editor, "_zoomResetButton");
        using var graphics = zoom.CreateGraphics();
        var maxZoom = Forms.TextRenderer.MeasureText(graphics, $"{AnnotationCanvas.MaxZoomPercent}%", zoom.Font,
            Size.Empty, Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix);
        Assert(maxZoom.Width <= zoom.ClientSize.Width && maxZoom.Height <= zoom.ClientSize.Height,
            $"the complete zoom percentage fits ({maxZoom} in {zoom.ClientSize}): {context}", false);

        var slider = GetField<Forms.Control>(editor, "_zoomSlider");
        var fit = GetField<Forms.Button>(editor, "_zoomViewBtn");
        var rail = GetZoomRail(slider);
        Assert(slider.Parent == zoom.Parent && zoom.Parent == fit.Parent && slider.Right == zoom.Left && zoom.Right == fit.Left,
            "the rail, percentage and fit button share one row without gaps: " + context, false);
        Assert(rail.Width >= Math.Round(74 * editor.DeviceDpi / 120.0) && rail.Height < 6,
            $"the compact slider has a longer, thinner rail ({rail.Size}): {context}", false);
    }

    private static Rectangle GetZoomRail(Forms.Control slider)
        => (Rectangle)slider.GetType().GetMethod("GetTrackRect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(slider, null)!;

    private static void CheckEditorZoomInteraction(EditorForm editor, string context)
    {
        var canvas = (AnnotationCanvas)typeof(EditorForm).GetProperty("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        var slider = GetField<Forms.Control>(editor, "_zoomSlider");
        var reset = GetField<Forms.Button>(editor, "_zoomResetButton");
        var rail = GetZoomRail(slider);
        int y = rail.Top + rail.Height / 2;
        var center = new Point(rail.Left + rail.Width / 2, y);
        var end = new Point(rail.Right, y);
        SendMessageW(slider.Handle, 0x0201, (IntPtr)1, MousePositionParameter(center));
        SendMessageW(slider.Handle, 0x0200, (IntPtr)1, MousePositionParameter(end));
        SendMessageW(slider.Handle, 0x0202, IntPtr.Zero, MousePositionParameter(end));
        Assert(canvas.Zoom == AnnotationCanvas.MaxZoomPercent / 100.0, "dragging the compact rail reaches maximum zoom: " + context, false);

        SendMessageW(slider.Handle, 0x020A, (IntPtr)(-120 << 16), MousePositionParameter(slider.PointToScreen(center)));
        double afterWheel = canvas.Zoom;
        Assert(afterWheel < AnnotationCanvas.MaxZoomPercent / 100.0, "the wheel still changes zoom: " + context, false);

        typeof(EditorForm).GetMethod("ShowHoverTooltip", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, new object?[] { slider, "Zoom", true, null });
        var left = Forms.Message.Create(slider.Handle, 0x0100, (IntPtr)(int)Forms.Keys.Left, IntPtr.Zero);
        Assert(!((Forms.IMessageFilter)editor).PreFilterMessage(ref left), "a visible tooltip leaves the zoom key with its target: " + context, false);
        SendMessageW(left.HWnd, (uint)left.Msg, left.WParam, left.LParam);
        Assert(canvas.Zoom < afterWheel, "the focused slider responds to the arrow key: " + context, false);

        reset.PerformClick();
        Assert(canvas.Zoom == 1.0 && reset.Text == "100%", "clicking the percentage restores the canvas and label to 100%: " + context, false);
        canvas.ZoomToPercent(175);
        Assert(reset.Focus(), "the percentage button can receive keyboard focus: " + context, false);
        SendMessageW(reset.Handle, 0x0100, (IntPtr)(int)Forms.Keys.Space, IntPtr.Zero);
        SendMessageW(reset.Handle, 0x0101, (IntPtr)(int)Forms.Keys.Space, IntPtr.Zero);
        Assert(canvas.Zoom == 1.0 && reset.Text == "100%", "Space activates the focused percentage button: " + context, false);

        static IntPtr MousePositionParameter(Point point) => (IntPtr)((point.Y << 16) | (point.X & 0xffff));
    }

    private static void CheckEditorSidebar(Forms.Panel palette, Forms.Panel viewport, Forms.Button[] tools, Forms.Control[] swatches, string context)
    {
        Assert(!palette.VerticalScroll.Visible && !viewport.VerticalScroll.Visible,
            "there is no native vertical scrollbar: " + context, false);
        foreach (var swatch in swatches)
            Assert(ContainsControl(palette, swatch), $"all colors and stroke widths fit without scrolling ({palette.RectangleToClient(swatch.RectangleToScreen(swatch.ClientRectangle))} in {palette.ClientRectangle}): {context}", false);
        foreach (var tool in tools)
            Assert(ContainsControl(viewport, tool) && tool.Height > tool.Font.Height,
                $"the {tool.Text} tool fits in the viewport ({tool.Bounds}, viewport={viewport.Bounds}): {context}", false);
    }

    private static void SaveEditorRender(EditorForm editor, string? renders, string language, double uiScale, string state)
    {
        if (renders is null || language != "es" || uiScale != UiScale.Default) return;
        Directory.CreateDirectory(renders);
        using var snapshot = new Bitmap(editor.Width, editor.Height);
        editor.DrawToBitmap(snapshot, editor.ClientRectangle);
        snapshot.Save(Path.Combine(renders, $"editor-{editor.DeviceDpi}-{state}.png"));
    }
}
