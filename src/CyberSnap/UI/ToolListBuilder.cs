using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI;

/// <summary>
/// Shared builder that creates the unified tool list (icon + checkbox + hotkey box)
/// used by both SettingsWindow and SetupWizard.
/// </summary>
public static class ToolListBuilder
{
    public static readonly (string id, string label, char icon)[] ExtraTools =
    {
        ("_fullscreen",    "Fullscreen capture",  ToolGlyphs.FullscreenGlyph),
        ("_activeWindow",  "Active window",       ToolGlyphs.ActiveWindowGlyph),
        ("_repeatLastArea", "Repeat last area",   ToolGlyphs.RepeatLastAreaGlyph),
        ("_scrollCapture", "Scroll capture",      ToolGlyphs.ScrollCaptureGlyph),
        ("_record",        "Record",              ToolGlyphs.RecordGlyph),
    };

    private static readonly Dictionary<TextBox, bool> RecordingFlags = new();

    public static void Build(StackPanel capturePanel, StackPanel annotationPanel, SettingsService settingsService, FrameworkElement owner, Action? hotkeyChanged = null)
    {
        capturePanel.Children.Clear();
        annotationPanel.Children.Clear();
        var s = settingsService.Settings;
        // Icon color for rendering Fluent glyphs to bitmaps
        var iconColor = Theme.IsDark ? System.Drawing.Color.FromArgb(225, 255, 255, 255) : System.Drawing.Color.FromArgb(210, 0, 0, 0);

        void AddToolRow(StackPanel targetPanel, string toolId, string label, char icon, bool showHotkey)
        {
            var card = new Border { Style = (Style)owner.FindResource("CompactItemCard") };

            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (icon != '\0')
            {
                var iconFrame = new Border { Style = (Style)owner.FindResource("CompactItemIconFrame") };
                var img = new System.Windows.Controls.Image
                {
                    Source = ToolIcons.RenderToolIconWpf(toolId, icon, iconColor, 16),
                    Width = 16,
                    Height = 16,
                    Opacity = 0.9,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
                iconFrame.Child = img;
                Grid.SetColumn(iconFrame, 0);
                grid.Children.Add(iconFrame);
            }

            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 12, 0),
                Style = (Style)owner.FindResource("SettingTitle"),
            };
            Grid.SetColumn(labelBlock, 1);
            grid.Children.Add(labelBlock);

            if (showHotkey)
            {
                var right = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                };

                var hkBox = new TextBox();
                hkBox.SetResourceReference(TextBox.StyleProperty, "HotkeyBox");
                hkBox.Height = 28;
                hkBox.MinHeight = 28;
                hkBox.Width = 135;
                hkBox.MinWidth = 135;
                hkBox.FontSize = 11;

                var (initMod, initKey) = settingsService.Settings.GetToolHotkey(toolId);
                hkBox.Text = HotkeyFormatter.Format(initMod, initKey);

                var clearBtn = new Button { Content = "×" };
                clearBtn.SetResourceReference(Button.StyleProperty, "ClearBtn");
                clearBtn.Height = 28;
                clearBtn.MinHeight = 28;
                clearBtn.Width = 28;
                clearBtn.MinWidth = 28;
                clearBtn.Margin = new Thickness(6, 0, 0, 0);
                var capturedBox = hkBox;
                var capturedId = toolId;

                hkBox.GotFocus += (_, _) => capturedBox.Text = LocalizationService.Translate("Press keys...");
                hkBox.LostFocus += (_, _) =>
                {
                    var (m, k) = settingsService.Settings.GetToolHotkey(capturedId);
                    capturedBox.Text = HotkeyFormatter.Format(m, k);
                };

                hkBox.PreviewKeyDown += (_, e) =>
                {
                    e.Handled = true;
                    var key = NormalizeHotkeyKey(e);
                    if (IsModifierOnly(key)) return;

                    uint mod = (uint)Keyboard.Modifiers;
                    uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

                    if (mod == 0 && vk == 0) return;
                    if (IsUnsafeModifierlessHotkey(mod, vk)) return;

                    var conflict = FindHotkeyConflict(settingsService.Settings, capturedId, mod, vk);
                    var (prevMod, prevKey) = settingsService.Settings.GetToolHotkey(capturedId);

                    if (conflict is not null && !settingsService.Settings.AllowHotkeyOverride)
                    {
                        // Safety: don't auto-override other hotkey assignments
                        var conflictLabel = LocalizationService.Translate(conflict.Label);
                        var msg = string.Format(
                            LocalizationService.Translate("\"{0}\" is already assigned to {1}. Enable \"Allow hotkey override\" to reassign."),
                            HotkeyFormatter.Format(mod, vk), conflictLabel);
                        ToastWindow.Show(LocalizationService.Translate("Hotkey conflict"), msg);
                        return;
                    }

                    try
                    {
                        if (conflict is not null)
                            ClearHotkeyConflict(settingsService.Settings, conflict);

                        settingsService.Settings.SetToolHotkey(capturedId, mod, vk);
                        settingsService.Save();
                        capturedBox.Text = HotkeyFormatter.Format(mod, vk);
                        hotkeyChanged?.Invoke();
                        Keyboard.ClearFocus();
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("settings.tool-hotkey-save", ex);
                        settingsService.Settings.SetToolHotkey(capturedId, prevMod, prevKey);
                        if (conflict is not null) RestoreHotkeyConflict(settingsService.Settings, conflict, (prevMod, prevKey));

                        try { settingsService.Save(); } catch { }
                        capturedBox.Text = HotkeyFormatter.Format(prevMod, prevKey);
                        ShowToolHotkeySaveFailed("save", conflict is not null, ex);
                    }
                };

                hkBox.PreviewTextInput += (_, e) => { e.Handled = true; };

                clearBtn.Click += (_, _) =>
                {
                    var (previousMod, previousKey) = settingsService.Settings.GetToolHotkey(capturedId);
                    try
                    {
                        settingsService.Settings.SetToolHotkey(capturedId, 0, 0);
                        settingsService.Save();
                        capturedBox.Text = HotkeyFormatter.Format(0, 0);
                        hotkeyChanged?.Invoke();
                        Keyboard.ClearFocus();
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("settings.tool-hotkey-clear", ex);
                        settingsService.Settings.SetToolHotkey(capturedId, previousMod, previousKey);
                        try
                        {
                            settingsService.Save();
                        }
                        catch (Exception rollbackEx)
                        {
                            AppDiagnostics.LogError("settings.tool-hotkey-clear-rollback", rollbackEx);
                        }

                        capturedBox.Text = HotkeyFormatter.Format(previousMod, previousKey);
                        ShowToolHotkeySaveFailed("clear", restoredConflict: false, ex);
                    }
                };

                right.Children.Add(hkBox);
                right.Children.Add(clearBtn);

                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
            }

            card.Child = grid;
            targetPanel.Children.Add(card);
        }

        var captureItems = new System.Collections.Generic.List<(string id, string label, char icon)>
        {
            ("rect", "Area Capture", ToolDef.AllTools.First(t => t.Id == "rect").Icon),
            ("_repeatLastArea", "Repeat last area", ToolGlyphs.RepeatLastAreaGlyph),
            ("_repeatLastScrollArea", "Repeat last scroll area", ToolGlyphs.ScrollCaptureGlyph), // TODO: Consider using a dedicated repeat scroll glyph if available
            ("center", "From Center", ToolDef.AllTools.First(t => t.Id == "center").Icon),
            ("_scrollCapture", "Scrolling Capture", ToolGlyphs.ScrollCaptureGlyph),
            ("ocr", "OCR", ToolDef.AllTools.First(t => t.Id == "ocr").Icon),
            ("picker", "Color Picker", ToolDef.AllTools.First(t => t.Id == "picker").Icon),
            ("scan", "QR & Barcodes", ToolDef.AllTools.First(t => t.Id == "scan").Icon),
            ("record", "Screen Recorder (MP4)", ToolDef.AllTools.First(t => t.Id == "record").Icon),
            ("recordGif", "Screen Recorder (GIF)", ToolDef.AllTools.First(t => t.Id == "recordGif").Icon),
            ("_fullscreen", "Fullscreen capture", ToolGlyphs.FullscreenGlyph),
            ("_activeWindow", "Active window", ToolGlyphs.ActiveWindowGlyph),
            ("_standaloneRuler", "Ruler (Standalone)", ToolDef.AllTools.First(t => t.Id == "ruler").Icon),
            ("_standaloneColorPicker", "Color Picker (Standalone)", ToolDef.AllTools.First(t => t.Id == "picker").Icon),
            ("_standaloneOcr", "OCR (Standalone)", ToolDef.AllTools.First(t => t.Id == "ocr").Icon),
            ("_standaloneScan", "QR & Barcodes (Standalone)", ToolDef.AllTools.First(t => t.Id == "scan").Icon)
            // Future standalone tools follow the convention: ("_standalone{name}", "Label", icon)
        };

        foreach (var item in captureItems)
            AddToolRow(capturePanel, item.id, item.label, item.icon, true);

        foreach (var t in ToolDef.AllTools.Where(t => t.Group == 1))
            AddToolRow(annotationPanel, t.Id, t.Label, t.Icon, true);

        LocalizationService.ApplyTo(capturePanel, settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(annotationPanel, settingsService.Settings.InterfaceLanguage);
    }

    public sealed record HotkeyConflict(string ToolId, string Label);

    public static HotkeyConflict? FindHotkeyConflict(AppSettings settings, string? excludeToolId, uint mod, uint key)
    {
        foreach (var tool in ToolDef.AllTools)
        {
            var id = tool.Id;
            if (string.Equals(id, excludeToolId, StringComparison.OrdinalIgnoreCase))
                continue;

            var (existingMod, existingKey) = settings.GetToolHotkey(id);
            if (existingMod == mod && existingKey == key)
                return new HotkeyConflict(tool.Id, tool.Label);
        }

        foreach (var (id, label, _) in ExtraTools)
        {
            if (string.Equals(id, excludeToolId, StringComparison.OrdinalIgnoreCase))
                continue;

            var (existingMod, existingKey) = settings.GetToolHotkey(id);
            if (existingMod == mod && existingKey == key)
                return new HotkeyConflict(id, label);
        }

        return null;
    }

    private static void ShowToolHotkeySaveFailed(string action, bool restoredConflict, Exception ex)
    {
        var conflictCopy = restoredConflict
            ? " Any replaced hotkey was restored."
            : string.Empty;
        ToastWindow.ShowError(
            "Hotkey failed",
            $"The previous hotkey was restored after the failed {action}.{conflictCopy} Check Config -> Tools and try again.\n{ex.Message}");
    }

    private static Key NormalizeHotkeyKey(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed)
            key = e.ImeProcessedKey;
        if (key == Key.DeadCharProcessed)
            key = e.DeadCharProcessedKey;
        return key;
    }

    private static bool IsModifierOnly(Key key) =>
        key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static bool IsUnsafeModifierlessHotkey(uint mod, uint vk) =>
        mod == 0 && vk != Native.User32.VK_SNAPSHOT;

    private static (uint Modifiers, uint Key) ClearHotkeyConflict(AppSettings settings, HotkeyConflict conflict)
    {
        var old = settings.GetToolHotkey(conflict.ToolId);
        settings.SetToolHotkey(conflict.ToolId, 0, 0);
        return old;
    }

    private static void RestoreHotkeyConflict(AppSettings settings, HotkeyConflict conflict, (uint Modifiers, uint Key)? previous)
    {
        if (previous is null)
            return;

        settings.SetToolHotkey(conflict.ToolId, previous.Value.Modifiers, previous.Value.Key);
    }
}
