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
                var sizes = new Dictionary<int, (int Button, int Text, int Tool)>();
                foreach (var screen in screens.Append(screens[0]))
                {
                    var work = screen.WorkingArea;
                    editor.Bounds = work;
                    Pump();
                    var min = editor.MinimumSize;
                    editor.Bounds = new Rectangle(work.Location, min);
                    Pump();
                    int dpi = editor.DeviceDpi;
                    var context = $"{language}, UI {uiScale}, DPI {dpi}, {editor.ClientSize}";
                    Assert(GetDpiForWindow(editor.Handle) == dpi && Forms.Screen.FromHandle(editor.Handle).DeviceName == screen.DeviceName,
                        "the Editor uses the destination monitor DPI: " + context, false);

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

                    var palette = GetField<Forms.Panel>(editor, "_toolbarPanel");
                    var lastStroke = ((System.Collections.IEnumerable)Field(editor, "_strokeWidthButtons")!).Cast<Forms.Control>().Last();
                    palette.ScrollControlIntoView(lastStroke);
                    Pump();
                    var lastStrokeBounds = palette.RectangleToClient(lastStroke.RectangleToScreen(lastStroke.ClientRectangle));
                    Assert(palette.ClientRectangle.Contains(lastStrokeBounds),
                        $"the last stroke control is reachable ({lastStrokeBounds} in {palette.ClientRectangle}): {context}", false);
                    palette.AutoScrollPosition = Point.Empty;

                    var command = GetField<Forms.Button>(editor, "_galleryButton");
                    var tool = ((System.Collections.IDictionary)Field(editor, "_toolButtons")!).Values.OfType<Forms.Button>().First();
                    using var graphics = command.CreateGraphics();
                    var dimensions = (command.Height, Forms.TextRenderer.MeasureText(graphics, command.Text, command.Font).Height, tool.Height);
                    if (sizes.TryGetValue(dpi, out var previous))
                        Assert(dimensions == previous, "returning to the same monitor restores the chrome metrics: " + context, false);
                    sizes[dpi] = dimensions;
                    Console.WriteLine($"PASS: Editor {context}, command {command.Size}, text {dimensions.Item2}px, tools {tool.Height}px.");

                    if (renders is not null && language == "es" && uiScale == UiScale.Default)
                    {
                        Directory.CreateDirectory(renders);
                        using var snapshot = new Bitmap(editor.Width, editor.Height);
                        editor.DrawToBitmap(snapshot, editor.ClientRectangle);
                        snapshot.Save(Path.Combine(renders, $"editor-{dpi}.png"));
                    }
                }
                if (sizes.Count > 1)
                {
                    int lowDpi = sizes.Keys.Min(), highDpi = sizes.Keys.Max();
                    var low = sizes[lowDpi];
                    var high = sizes[highDpi];
                    Assert(high.Button > low.Button && high.Text > low.Text && high.Tool > low.Tool,
                        "command buttons, text and tool rows grow together with monitor DPI");
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
}
