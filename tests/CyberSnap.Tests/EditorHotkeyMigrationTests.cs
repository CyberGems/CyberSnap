using System.IO;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Tests;

/// <summary>
/// F1 belongs to About in the annotation editor, so Pick starts on V and any saved
/// Pick binding on F1 is migrated once instead of silently shadowing About.
/// </summary>
public class EditorHotkeyMigrationTests
{
    [Fact]
    public void PickDefaultsToVAndNoEditorToolKeepsF1()
    {
        var settings = new AppSettings();

        Assert.Equal((0u, 0x56u), settings.GetEditorToolHotkey("editorMove"));
        foreach (var (id, _, _) in EditorToolHotkeyDef.Tools)
            Assert.False(settings.GetEditorToolHotkey(id) is (0, 0x70), $"{id} still defaults to F1");
    }

    [Fact]
    public void SavedPickOnF1MovesToVOnLoad()
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

            Assert.Equal((0u, 0x56u), settings.GetEditorToolHotkey("editorMove"));
            Assert.Equal(1, settings.EditorHotkeySchemaVersion);
            Assert.Equal((0u, 0u), settings.GetEditorToolHotkey("editorEraser"));
            Assert.Equal((0u, 0x72u), settings.GetEditorToolHotkey("editorText"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }
}
