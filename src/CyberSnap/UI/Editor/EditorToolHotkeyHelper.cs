using System.Windows.Forms;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

internal static class EditorToolHotkeyHelper
{
    /// <summary>Key-up shorter than this counts as a tap when Space is Pan's hotkey.</summary>
    public const int SpacePanTapThresholdMs = 200;

    public static bool IsBareSpaceKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var mod = keyData & Keys.Modifiers;
        return key == Keys.Space && mod == Keys.None;
    }

    public static bool IsSpaceAssignedAsPanHotkey()
    {
        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return false;
        var (mod, key) = settings.GetEditorToolHotkey("editorPan");
        return mod == 0 && key == Native.User32.VK_SPACE;
    }
    public static AnnotationCanvas.CanvasTool? ToCanvasTool(string editorToolId) => editorToolId switch
    {
        "editorPan" => AnnotationCanvas.CanvasTool.Pan,
        "editorMove" => AnnotationCanvas.CanvasTool.Move,
        "editorDraw" => AnnotationCanvas.CanvasTool.Draw,
        "editorArrow" => AnnotationCanvas.CanvasTool.Arrow,
        "editorCurvedArrow" => AnnotationCanvas.CanvasTool.CurvedArrow,
        "editorLine" => AnnotationCanvas.CanvasTool.Line,
        "editorRect" => AnnotationCanvas.CanvasTool.Rect,
        "editorCircle" => AnnotationCanvas.CanvasTool.Circle,
        "editorText" => AnnotationCanvas.CanvasTool.Text,
        "editorCrop" => AnnotationCanvas.CanvasTool.Crop,
        "editorEraser" => AnnotationCanvas.CanvasTool.Eraser,
        "editorHighlight" => AnnotationCanvas.CanvasTool.Highlight,
        "editorBlur" => AnnotationCanvas.CanvasTool.Blur,
        "editorStep" => AnnotationCanvas.CanvasTool.StepNumber,
        "editorMagnifier" => AnnotationCanvas.CanvasTool.Magnifier,
        "editorEmoji" => AnnotationCanvas.CanvasTool.Emoji,
        _ => null,
    };

    public static string? ToEditorToolId(AnnotationCanvas.CanvasTool tool) => tool switch
    {
        AnnotationCanvas.CanvasTool.Pan => "editorPan",
        AnnotationCanvas.CanvasTool.Move => "editorMove",
        AnnotationCanvas.CanvasTool.Draw => "editorDraw",
        AnnotationCanvas.CanvasTool.Arrow => "editorArrow",
        AnnotationCanvas.CanvasTool.CurvedArrow => "editorCurvedArrow",
        AnnotationCanvas.CanvasTool.Line => "editorLine",
        AnnotationCanvas.CanvasTool.Rect => "editorRect",
        AnnotationCanvas.CanvasTool.Circle => "editorCircle",
        AnnotationCanvas.CanvasTool.Text => "editorText",
        AnnotationCanvas.CanvasTool.Crop => "editorCrop",
        AnnotationCanvas.CanvasTool.Eraser => "editorEraser",
        AnnotationCanvas.CanvasTool.Highlight => "editorHighlight",
        AnnotationCanvas.CanvasTool.Blur => "editorBlur",
        AnnotationCanvas.CanvasTool.StepNumber => "editorStep",
        AnnotationCanvas.CanvasTool.Magnifier => "editorMagnifier",
        AnnotationCanvas.CanvasTool.Emoji => "editorEmoji",
        _ => null,
    };

    /// <summary>Formatted hotkey for tooltips, or null when unassigned.</summary>
    public static string? GetHotkeyLabel(AnnotationCanvas.CanvasTool tool)
    {
        var id = ToEditorToolId(tool);
        if (id is null)
            return null;
        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return null;
        var (mod, key) = settings.GetEditorToolHotkey(id);
        if (key == 0)
            return null;
        return Helpers.HotkeyFormatter.Format(mod, key);
    }

    /// <summary>Formatted hotkey for an editor view/zoom shortcut, or null when unassigned.</summary>
    public static string? GetViewHotkeyLabel(string viewId)
    {
        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return null;
        var (mod, key) = settings.GetEditorViewHotkey(viewId);
        if (key == 0)
            return null;
        return Helpers.HotkeyFormatter.Format(mod, key);
    }

    /// <summary>Formatted hotkey for the global capture shortcut, or null when unassigned.</summary>
    public static string? GetCaptureHotkeyLabel()
    {
        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return null;
        var (mod, key) = (settings.HotkeyModifiers, settings.HotkeyKey);
        if (key == 0)
            return null;
        return Helpers.HotkeyFormatter.Format(mod, key);
    }

    /// <summary>File/edit chords that must not be intercepted as tool-selection hotkeys.</summary>
    public static bool IsReservedEditorChord(Keys keyData)
    {
        var mod = keyData & Keys.Modifiers;
        var key = keyData & Keys.KeyCode;
        if (mod == Keys.Control)
        {
            return key is Keys.N or Keys.O or Keys.S or Keys.C or Keys.V or Keys.Z or Keys.Y or Keys.A or Keys.D;
        }
        if (mod == (Keys.Control | Keys.Shift))
        {
            return key is Keys.S or Keys.Z;
        }
        return false;
    }

    /// <summary>Activates an editor tool from a keyboard shortcut. Returns true when handled.</summary>
    public static bool TryActivateTool(AnnotationCanvas canvas, Keys keyData)
    {
        if (IsBareSpaceKey(keyData))
            return false;

        var settings = SettingsService.LoadStatic();
        if (settings is null)
            return false;

        uint mod = 0;
        if ((keyData & Keys.Control) != 0) mod |= Native.User32.MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mod |= Native.User32.MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mod |= Native.User32.MOD_SHIFT;
        uint vk = unchecked((uint)(keyData & Keys.KeyCode));

        var toolId = settings.FindEditorToolId(mod, vk);
        if (toolId is null)
            return false;

        var tool = ToCanvasTool(toolId);
        if (tool is null)
            return false;

        canvas.ActiveTool = tool.Value;
        canvas.Focus();
        return true;
    }
}
