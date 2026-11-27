using System.IO;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Tests;

/// <summary>
/// The annotation editor opens About with Ctrl+F1, so bare F1 stays a normal tool key
/// and saved editor bindings are never rewritten while loading a profile.
/// </summary>
public class EditorHotkeyDefaultsTests
{
    [Fact]
    public void PickKeepsF1AndOnlyPickOwnsIt()
    {
        var settings = new AppSettings();

        Assert.Equal((0u, 0x70u), settings.GetEditorToolHotkey("editorMove"));
        foreach (var (id, _, _) in EditorToolHotkeyDef.Tools)
        {
            if (id == "editorMove")
                continue;
            Assert.False(settings.GetEditorToolHotkey(id) is (0, 0x70), $"{id} also defaults to F1");
        }
    }

    [Fact]
    public void AboutReservesCtrlF1AndNoToolUsesIt()
    {
        var settings = new AppSettings();

        // 0x2 is MOD_CONTROL; the editor handles Control+F1 itself, before tool hotkeys.
        foreach (var (id, _, _) in EditorToolHotkeyDef.Tools)
            Assert.False(settings.GetEditorToolHotkey(id) is (0x2u, 0x70u), $"{id} defaults to Ctrl+F1");
    }

    [Fact]
    public void TransformActionsHaveDistinctDefaults()
    {
        var settings = new AppSettings();
        var used = new HashSet<(uint, uint)>();

        foreach (var (id, _, _) in EditorToolHotkeyDef.Tools)
            used.Add(settings.GetEditorToolHotkey(id));
        foreach (var (id, _, _) in EditorViewHotkeyDef.Shortcuts)
            used.Add(settings.GetEditorViewHotkey(id));

        foreach (var (id, _, _, _) in EditorToolHotkeyDef.Actions)
        {
            var hotkey = settings.GetEditorToolHotkey(id);
            Assert.NotEqual((0u, 0u), hotkey);
            Assert.True(used.Add(hotkey), $"{id} shares the default {hotkey}");
        }
    }

    [Fact]
    public void TransformDefaultsUseOneModifierAndStayBindable()
    {
        var settings = new AppSettings();

        // Suite standard: local shortcuts take one modifier; two-modifier chords are global.
        foreach (var (id, _, _, _) in EditorToolHotkeyDef.Actions)
        {
            var (mod, key) = settings.GetEditorToolHotkey(id);
            Assert.NotEqual(0u, key);
            Assert.True(mod != 0 && (mod & (mod - 1)) == 0, $"{id} defaults to more than one modifier ({mod})");
            Assert.Null(CyberSnap.UI.Editor.EditorToolHotkeyHelper.GetReservedEditorBindingMessage(mod, key));
        }
    }

    [Fact]
    public void ProfilesWithCtrlF1OnEditorRowsAreCleaned()
    {
        string path = Path.Combine(Path.GetTempPath(), "CyberSnap-hotkeys-" + Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var service = new SettingsService(path))
            {
                service.Load();
                service.Settings.EditorToolHotkeys = new Dictionary<string, uint[]>
                {
                    ["editorMove"] = new uint[] { 0x2, 0x70 },
                    ["editorRotateCw"] = new uint[] { 0x2, 0x70 },
                    ["editorEraser"] = new uint[] { 0, 0x73 }
                };
                service.Settings.EditorViewHotkeys = new Dictionary<string, uint[]>
                {
                    ["editorZoomIn"] = new uint[] { 0x2, 0x70 }
                };
                service.Save();
                service.FlushPendingWrites();
            }

            using var reloaded = new SettingsService(path);
            reloaded.Load();
            var settings = reloaded.Settings;

            Assert.DoesNotContain("editorMove", settings.EditorToolHotkeys!.Keys);
            Assert.DoesNotContain("editorRotateCw", settings.EditorToolHotkeys.Keys);
            Assert.DoesNotContain("editorZoomIn", settings.EditorViewHotkeys!.Keys);
            Assert.Equal((0u, 0x70u), settings.GetEditorToolHotkey("editorMove"));
            Assert.Equal((0x2u, 0x52u), settings.GetEditorToolHotkey("editorRotateCw"));
            Assert.Equal((0u, 0x38u), settings.GetEditorViewHotkey("editorZoomIn"));
            Assert.Equal((0u, 0x73u), settings.GetEditorToolHotkey("editorEraser"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AboutChordIsOnlyControlF1()
    {
        // 0x2 is MOD_CONTROL, 0x70 is VK_F1.
        Assert.True(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0x2, 0x70));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0, 0x70));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0x2, 0x71));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0x6, 0x70));
    }

    [Fact]
    public void WindowAndShellGesturesAreReservedForEditorRows()
    {
        // 0x1 is MOD_ALT; 0x26/0x28 are the arrow keys, 0x4D is M and 0x73 is F4.
        foreach (uint vk in new uint[] { 0x26, 0x28, 0x4D, 0x73 })
            Assert.NotNull(CyberSnap.UI.Editor.EditorToolHotkeyHelper.GetReservedEditorBindingMessage(0x1, vk));

        Assert.Null(CyberSnap.UI.Editor.EditorToolHotkeyHelper.GetReservedEditorBindingMessage(0x2, 0x52));
        Assert.Null(CyberSnap.UI.Editor.EditorToolHotkeyHelper.GetReservedEditorBindingMessage(0x1, 0x52));
        Assert.Null(CyberSnap.UI.Editor.EditorToolHotkeyHelper.GetReservedEditorBindingMessage(0, 0x73));
        Assert.NotNull(CyberSnap.UI.Editor.EditorToolHotkeyHelper.GetReservedEditorBindingMessage(0x2, 0x70));
    }

    [Fact]
    public void ShareChordIsReservedForEditorRows()
    {
        // Share is Ctrl+U and resolves before tool and view hotkeys, so Settings must refuse it.
        Assert.True(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsReservedEditorChord(
            System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.U));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsReservedEditorChord(
            System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.U));
    }

    [Fact]
    public void SavedEditorBindingsSurviveLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), "CyberSnap-hotkeys-" + Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var service = new SettingsService(path))
            {
                service.Load();
                service.Settings.EditorToolHotkeys = new Dictionary<string, uint[]>
                {
                    ["editorMove"] = new uint[] { 0, 0x70 },
                    ["editorEraser"] = new uint[] { 0, 0x70 },
                    ["editorText"] = new uint[] { 0, 0x72 }
                };
                service.Settings.EditorHotkeySchemaVersion = 0;
                service.Save();
                service.FlushPendingWrites();
            }

            using var reloaded = new SettingsService(path);
            reloaded.Load();
            var settings = reloaded.Settings;

            Assert.Equal(0, settings.EditorHotkeySchemaVersion);
            Assert.Equal((0u, 0x70u), settings.GetEditorToolHotkey("editorMove"));
            Assert.Equal((0u, 0x70u), settings.GetEditorToolHotkey("editorEraser"));
            Assert.Equal((0u, 0x72u), settings.GetEditorToolHotkey("editorText"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ProfilesFromThePickOnVBuildKeepTheirBinding()
    {
        string path = Path.Combine(Path.GetTempPath(), "CyberSnap-hotkeys-" + Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var service = new SettingsService(path))
            {
                service.Load();
                service.Settings.EditorToolHotkeys = new Dictionary<string, uint[]>
                {
                    ["editorMove"] = new uint[] { 0, 0x56 }
                };
                service.Settings.EditorHotkeySchemaVersion = 1;
                service.Save();
                service.FlushPendingWrites();
            }

            using var reloaded = new SettingsService(path);
            reloaded.Load();

            Assert.Equal((0u, 0x56u), reloaded.Settings.GetEditorToolHotkey("editorMove"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }
}
