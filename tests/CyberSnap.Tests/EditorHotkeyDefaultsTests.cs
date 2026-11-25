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
    public void AboutChordIsOnlyControlF1()
    {
        // 0x2 is MOD_CONTROL, 0x70 is VK_F1.
        Assert.True(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0x2, 0x70));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0, 0x70));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0x2, 0x71));
        Assert.False(CyberSnap.UI.Editor.EditorToolHotkeyHelper.IsAboutChord(0x6, 0x70));
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
