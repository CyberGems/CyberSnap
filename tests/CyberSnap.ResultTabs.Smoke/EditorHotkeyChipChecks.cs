using System.Collections;
using System.Drawing;
using System.Reflection;
using CyberSnap.UI.Controls;
using CyberSnap.UI.Editor;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    /// <summary>
    /// Every editor button whose action has a configured shortcut must advertise it in its
    /// hover tooltip. The check reads the same registered providers the tooltip timer uses,
    /// so a button added without its chip fails here instead of in a manual hover pass.
    /// </summary>
    private static void CheckEditorHotkeyTooltipChips()
    {
        using var bitmap = new Bitmap(160, 120);
        using var editor = new EditorForm(bitmap);
        try
        {
            editor.Show();
            Pump();
            AwaitCondition(() => editor.Visible && editor.Opacity == 1, "Editor did not finish opening");

            var toolButtons = (IDictionary)typeof(EditorForm)
                .GetField("_toolButtons", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(editor)!;
            var transforms = ((IEnumerable)typeof(EditorForm)
                    .GetField("_transformButtons", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(editor)!)
                .Cast<object>()
                .Select(entry => (
                    Button: (Forms.Control)entry.GetType().GetField("Item1")!.GetValue(entry)!,
                    HotkeyId: (string)entry.GetType().GetField("Item4")!.GetValue(entry)!))
                .ToArray();
            var tooltipEntries = ((IEnumerable)typeof(EditorForm)
                    .GetField("_tooltipControls", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(editor)!)
                .Cast<object>()
                .ToArray();
            Assert(tooltipEntries.Length > 0, "the Editor registers hover tooltip providers");

            string? ExpectedChip(Forms.Control control)
            {
                if (control.Tag is string chromeShortcut && !string.IsNullOrWhiteSpace(chromeShortcut))
                    return chromeShortcut;

                foreach (DictionaryEntry pair in toolButtons)
                {
                    if (!ReferenceEquals(pair.Value, control))
                        continue;
                    return EditorToolHotkeyHelper.GetHotkeyLabel((AnnotationCanvas.CanvasTool)pair.Key);
                }

                foreach (var (button, hotkeyId) in transforms)
                {
                    if (ReferenceEquals(button, control))
                        return EditorToolHotkeyHelper.GetActionHotkeyLabel(hotkeyId);
                }

                return ReferenceEquals(control, Field(editor, "_zoomResetButton"))
                    ? EditorToolHotkeyHelper.GetViewHotkeyLabel("editorZoomReset")
                    : null;
            }

            int chips = 0;
            foreach (var entry in tooltipEntries)
            {
                var control = (Forms.Control)entry.GetType().GetField("Item1")!.GetValue(entry)!;
                string? chip = ExpectedChip(control);
                if (chip is null)
                    continue;

                var provider = (Func<string?>)entry.GetType().GetField("Item2")!.GetValue(entry)!;
                string? text = provider();
                Assert(text is not null && text.Contains($"({chip})", StringComparison.Ordinal),
                    $"the editor button with shortcut {chip} shows it in its tooltip");
                chips++;
            }

            Assert(chips >= 2, "the check inspects at least one button with a configured shortcut");
        }
        finally
        {
            editor.Hide();
            Pump();
        }
    }
}
