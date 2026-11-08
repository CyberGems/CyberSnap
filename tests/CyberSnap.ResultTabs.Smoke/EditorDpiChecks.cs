using System.Drawing;
using System.IO;
using System.Reflection;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Editor;
using Forms = System.Windows.Forms;

internal static partial class Program
{
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
        foreach (string field in new[] { "_scaleButton", "_coordsLabel", "_zoomSlider", "_zoomLabel", "_zoomViewBtn", "_toggleFitSwitch", "_togglePanLockSwitch" })
        {
            var control = GetField<Forms.Control>(editor, field);
            if (control.Visible)
                Assert(ContainsControl(status, control), $"the {field} control fits the compact status bar: {context}", false);
        }
        var zoom = GetField<Forms.Label>(editor, "_zoomLabel");
        using var graphics = zoom.CreateGraphics();
        var maxZoom = Forms.TextRenderer.MeasureText(graphics, $"{CyberSnap.UI.Controls.AnnotationCanvas.MaxZoomPercent}%", zoom.Font,
            Size.Empty, Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix);
        Assert(maxZoom.Width <= zoom.ClientSize.Width && maxZoom.Height <= zoom.ClientSize.Height,
            $"the complete zoom percentage fits ({maxZoom} in {zoom.ClientSize}): {context}", false);
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
