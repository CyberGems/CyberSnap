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
    private static readonly Dictionary<TextBox, System.Windows.Threading.DispatcherTimer> BlockedDetectTimers = new();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsAnyNonModifierKeyPhysicallyDown()
    {
        for (int vk = 0x41; vk <= 0x5A; vk++) // A-Z
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        for (int vk = 0x30; vk <= 0x39; vk++) // 0-9
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        for (int vk = 0x70; vk <= 0x7B; vk++) // F1-F12
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        return false;
    }

    public static void Build(StackPanel capturePanel, StackPanel annotationPanel, SettingsService settingsService, FrameworkElement owner, Action? hotkeyChanged = null, StackPanel? editorToolsPanel = null, StackPanel? toolbarUtilitiesPanel = null)
    {
        capturePanel.Children.Clear();
        annotationPanel.Children.Clear();
        editorToolsPanel?.Children.Clear();
        toolbarUtilitiesPanel?.Children.Clear();
        var s = settingsService.Settings;
        // Icon color for rendering Fluent glyphs to bitmaps
        var iconColor = Theme.IsDark ? System.Drawing.Color.FromArgb(225, 255, 255, 255) : System.Drawing.Color.FromArgb(210, 0, 0, 0);

        void AddSubHeader(StackPanel targetPanel, string textKey)
        {
            var header = new TextBlock
            {
                Text = LocalizationService.Translate(textKey),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
                Foreground = (System.Windows.Media.Brush)owner.FindResource("ThemeTextSecondaryBrush"),
                Margin = new Thickness(4, targetPanel.Children.Count == 0 ? 0 : 16, 0, 8)
            };
            LocalizationService.SetSourceText(header, textKey);
            targetPanel.Children.Add(header);
        }

        void AddSectionHeader(StackPanel targetPanel, string textKey)
        {
            var header = new TextBlock
            {
                Text = LocalizationService.Translate(textKey),
                Style = (Style)owner.FindResource("SectionLabel"),
                Margin = new Thickness(4, targetPanel.Children.Count == 0 ? 0 : 16, 0, 10)
            };
            LocalizationService.SetSourceText(header, textKey);
            targetPanel.Children.Add(header);
        }

        void AddToolRow(StackPanel targetPanel, string toolId, string label, char icon, bool showHotkey,
            Func<string, (uint mod, uint key)> getHotkey,
            Action<string, uint, uint> setHotkey)
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
                var tooltip = new System.Windows.Controls.ToolTip
                {
                    Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.")
                };
                hkBox.ToolTip = tooltip;

                var (initMod, initKey) = getHotkey(toolId);
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

                System.Windows.Threading.DispatcherTimer? resetWarningTimer = null;

                hkBox.GotFocus += (_, _) =>
                {
                    capturedBox.Text = LocalizationService.Translate("Press keys...");
                    hkBox.ClearValue(TextBox.ForegroundProperty);
                    hkBox.ClearValue(TextBox.FontWeightProperty);
                    tooltip.Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.");
                    tooltip.IsOpen = false;
                    if (resetWarningTimer != null)
                    {
                        resetWarningTimer.Stop();
                        resetWarningTimer = null;
                    }
                    if (Application.Current is App app)
                    {
                        app.UnregisterAllHotkeys();
                    }
                };
                hkBox.LostFocus += (_, _) =>
                {
                    var (m, k) = getHotkey(capturedId);
                    capturedBox.Text = HotkeyFormatter.Format(m, k);
                    hkBox.ClearValue(TextBox.ForegroundProperty);
                    hkBox.ClearValue(TextBox.FontWeightProperty);
                    tooltip.Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.");
                    tooltip.IsOpen = false;
                    if (resetWarningTimer != null)
                    {
                        resetWarningTimer.Stop();
                        resetWarningTimer = null;
                    }
                    if (BlockedDetectTimers.TryGetValue(hkBox, out var timer))
                    {
                        timer.Stop();
                        BlockedDetectTimers.Remove(hkBox);
                    }
                    if (Application.Current is App app)
                    {
                        app.RegisterHotkeys(showReadyNotification: false);
                    }
                };

                hkBox.PreviewKeyDown += (_, e) =>
                {
                    e.Handled = true;
                    var key = NormalizeHotkeyKey(e);
                    
                    if (IsModifierOnly(key))
                    {
                        if (resetWarningTimer != null)
                        {
                            resetWarningTimer.Stop();
                            resetWarningTimer = null;
                            capturedBox.Text = LocalizationService.Translate("Press keys...");
                            hkBox.ClearValue(TextBox.ForegroundProperty);
                            hkBox.ClearValue(TextBox.FontWeightProperty);
                            tooltip.IsOpen = false;
                        }

                        if (!BlockedDetectTimers.TryGetValue(hkBox, out var timer))
                        {
                            timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                            timer.Tick += (s, args) =>
                            {
                                if (IsAnyNonModifierKeyPhysicallyDown())
                                {
                                    timer.Stop();
                                    capturedBox.Text = LocalizationService.Translate("Taken");
                                    hkBox.Foreground = System.Windows.Media.Brushes.Red;
                                    hkBox.FontWeight = FontWeights.Bold;
                                    tooltip.Content = LocalizationService.Translate("This hotkey is registered by another application.");
                                    tooltip.IsOpen = true;
                                }
                            };
                            BlockedDetectTimers[hkBox] = timer;
                        }
                        timer.Stop();
                        timer.Start();
                        return;
                    }

                    if (BlockedDetectTimers.TryGetValue(hkBox, out var activeTimer))
                    {
                        activeTimer.Stop();
                        BlockedDetectTimers.Remove(hkBox);
                    }
                    if (resetWarningTimer != null)
                    {
                        resetWarningTimer.Stop();
                        resetWarningTimer = null;
                    }
                    hkBox.ClearValue(TextBox.ForegroundProperty);
                    hkBox.ClearValue(TextBox.FontWeightProperty);
                    tooltip.IsOpen = false;

                    uint mod = (uint)Keyboard.Modifiers;
                    uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

                    if (mod == 0 && vk == 0) return;
                    if (IsUnsafeModifierlessHotkey(mod, vk)) return;

                    var conflict = FindHotkeyConflict(settingsService.Settings, capturedId, mod, vk);
                    var (prevMod, prevKey) = getHotkey(capturedId);

                    try
                    {
                        if (conflict is not null)
                        {
                            ClearHotkeyConflict(settingsService.Settings, conflict);
                            var conflictLabel = LocalizationService.Translate(conflict.Label);
                            ToastWindow.Show(
                                LocalizationService.Translate("Hotkey reassigned"),
                                string.Format(LocalizationService.Translate("\"{0}\" reassigned from {1}."), HotkeyFormatter.Format(mod, vk), conflictLabel));
                        }

                        setHotkey(capturedId, mod, vk);
                        settingsService.Save();
                        capturedBox.Text = HotkeyFormatter.Format(mod, vk);
                        hotkeyChanged?.Invoke();
                        Keyboard.ClearFocus();
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("settings.tool-hotkey-save", ex);
                        setHotkey(capturedId, prevMod, prevKey);
                        if (conflict is not null) RestoreHotkeyConflict(settingsService.Settings, conflict, (prevMod, prevKey));

                        try { settingsService.Save(); } catch { }
                        capturedBox.Text = HotkeyFormatter.Format(prevMod, prevKey);
                        ShowToolHotkeySaveFailed("save", conflict is not null, ex);
                    }
                };

                hkBox.PreviewKeyUp += (_, e) =>
                {
                    var key = NormalizeHotkeyKey(e);
                    if (IsModifierOnly(key))
                    {
                        if (Keyboard.Modifiers == ModifierKeys.None)
                        {
                            if (BlockedDetectTimers.TryGetValue(hkBox, out var activeTimer))
                            {
                                activeTimer.Stop();
                                BlockedDetectTimers.Remove(hkBox);
                            }

                            if (capturedBox.Text == LocalizationService.Translate("Taken"))
                            {
                                if (resetWarningTimer != null) resetWarningTimer.Stop();
                                resetWarningTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
                                resetWarningTimer.Tick += (s, args) =>
                                {
                                    resetWarningTimer.Stop();
                                    resetWarningTimer = null;
                                    if (hkBox.IsFocused)
                                    {
                                        capturedBox.Text = LocalizationService.Translate("Press keys...");
                                        hkBox.ClearValue(TextBox.ForegroundProperty);
                                        hkBox.ClearValue(TextBox.FontWeightProperty);
                                        tooltip.Content = LocalizationService.Translate("Click and press your shortcut. If a combination is not captured, it is likely blocked by another running application.");
                                        tooltip.IsOpen = false;
                                    }
                                };
                                resetWarningTimer.Start();
                            }
                            else
                            {
                                hkBox.ClearValue(TextBox.ForegroundProperty);
                                hkBox.ClearValue(TextBox.FontWeightProperty);
                                tooltip.IsOpen = false;
                            }
                        }
                    }
                };

                hkBox.PreviewTextInput += (_, e) => { e.Handled = true; };

                clearBtn.Click += (_, _) =>
                {
                    var (previousMod, previousKey) = getHotkey(capturedId);
                    try
                    {
                        setHotkey(capturedId, 0, 0);
                        settingsService.Save();
                        capturedBox.Text = HotkeyFormatter.Format(0, 0);
                        hotkeyChanged?.Invoke();
                        Keyboard.ClearFocus();
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.LogError("settings.tool-hotkey-clear", ex);
                        setHotkey(capturedId, previousMod, previousKey);
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
            // Core Capture Modes
            ("rect", "Area Capture", ToolDef.AllTools.First(t => t.Id == "rect").Icon),
            ("center", "From Center", ToolDef.AllTools.First(t => t.Id == "center").Icon),
            ("_fullscreen", "Fullscreen capture", ToolGlyphs.FullscreenGlyph),
            ("_activeWindow", "Active window", ToolGlyphs.ActiveWindowGlyph),
            ("_scrollCapture", "Scrolling Capture", ToolGlyphs.ScrollCaptureGlyph),
            ("_repeatLastArea", "Repeat last area", ToolGlyphs.RepeatLastAreaGlyph),

            // Video & Recording
            ("record", "Screen Recorder (MP4)", ToolDef.AllTools.First(t => t.Id == "record").Icon),
            ("recordGif", "Screen Recorder (GIF)", ToolDef.AllTools.First(t => t.Id == "recordGif").Icon),

            // Toolbar utilities (capture overlay toolbar: OCR, picker, scan)
            ("ocr", "OCR", ToolDef.AllTools.First(t => t.Id == "ocr").Icon),
            ("picker", "Color Picker", ToolDef.AllTools.First(t => t.Id == "picker").Icon),
            ("scan", "QR & Barcodes", ToolDef.AllTools.First(t => t.Id == "scan").Icon),

            // Standalone Utilities
            ("_standaloneOcr", "OCR (Standalone)", ToolDef.AllTools.First(t => t.Id == "ocr").Icon),
            ("_standaloneColorPicker", "Color Picker (Standalone)", ToolDef.AllTools.First(t => t.Id == "picker").Icon),
            ("_standaloneScan", "QR & Barcodes (Standalone)", ToolDef.AllTools.First(t => t.Id == "scan").Icon),
            ("_standaloneRuler", "Ruler (Standalone)", ToolDef.AllTools.First(t => t.Id == "ruler").Icon)
            // Future standalone tools follow the convention: ("_standalone{name}", "Label", icon)
        };

        (uint mod, uint key) GetCaptureHotkey(string id) => settingsService.Settings.GetToolHotkey(id);
        void SetCaptureHotkey(string id, uint mod, uint key) => settingsService.Settings.SetToolHotkey(id, mod, key);
        (uint mod, uint key) GetEditorHotkey(string id) => settingsService.Settings.GetEditorToolHotkey(id);
        void SetEditorHotkey(string id, uint mod, uint key) => settingsService.Settings.SetEditorToolHotkey(id, mod, key);

        AddSubHeader(capturePanel, "Core Captures");
        foreach (var item in System.Linq.Enumerable.Take(captureItems, 6))
            AddToolRow(capturePanel, item.id, item.label, item.icon, true, GetCaptureHotkey, SetCaptureHotkey);

        AddSubHeader(capturePanel, "Video & Recording");
        foreach (var item in System.Linq.Enumerable.Take(System.Linq.Enumerable.Skip(captureItems, 6), 2))
            AddToolRow(capturePanel, item.id, item.label, item.icon, true, GetCaptureHotkey, SetCaptureHotkey);

        AddSectionHeader(capturePanel, "Standalone Utilities");
        foreach (var item in System.Linq.Enumerable.Skip(captureItems, 11))
            AddToolRow(capturePanel, item.id, item.label, item.icon, true, GetCaptureHotkey, SetCaptureHotkey);

        if (toolbarUtilitiesPanel is not null)
        {
            AddSubHeader(toolbarUtilitiesPanel, "Toolbar utilities");
            foreach (var item in System.Linq.Enumerable.Skip(captureItems, 8).Take(3))
                AddToolRow(toolbarUtilitiesPanel, item.id, item.label, item.icon, true, GetCaptureHotkey, SetCaptureHotkey);
            LocalizationService.ApplyTo(toolbarUtilitiesPanel, settingsService.Settings.InterfaceLanguage);
        }

        AddSubHeader(annotationPanel, "Annotation tools");
        foreach (var t in ToolDef.AllTools.Where(t => t.Group == 1))
            AddToolRow(annotationPanel, t.Id, t.Label, t.Icon, true, GetCaptureHotkey, SetCaptureHotkey);

        if (editorToolsPanel is not null)
        {
            foreach (var t in EditorToolHotkeyDef.Tools)
                AddToolRow(editorToolsPanel, t.Id, t.Label, t.Icon, true, GetEditorHotkey, SetEditorHotkey);
            LocalizationService.ApplyTo(editorToolsPanel, settingsService.Settings.InterfaceLanguage);
        }

        LocalizationService.ApplyTo(capturePanel, settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(annotationPanel, settingsService.Settings.InterfaceLanguage);
    }

    public sealed record HotkeyConflict(string ToolId, string Label, bool IsEditor = false);

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

        foreach (var (id, label, _) in EditorToolHotkeyDef.Tools)
        {
            if (string.Equals(id, excludeToolId, StringComparison.OrdinalIgnoreCase))
                continue;

            var (existingMod, existingKey) = settings.GetEditorToolHotkey(id);
            if (existingMod == mod && existingKey == key)
                return new HotkeyConflict(id, label, IsEditor: true);
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
        if (conflict.IsEditor)
        {
            var old = settings.GetEditorToolHotkey(conflict.ToolId);
            settings.SetEditorToolHotkey(conflict.ToolId, 0, 0);
            return old;
        }

        var captureOld = settings.GetToolHotkey(conflict.ToolId);
        settings.SetToolHotkey(conflict.ToolId, 0, 0);
        return captureOld;
    }

    private static void RestoreHotkeyConflict(AppSettings settings, HotkeyConflict conflict, (uint Modifiers, uint Key)? previous)
    {
        if (previous is null)
            return;

        if (conflict.IsEditor)
            settings.SetEditorToolHotkey(conflict.ToolId, previous.Value.Modifiers, previous.Value.Key);
        else
            settings.SetToolHotkey(conflict.ToolId, previous.Value.Modifiers, previous.Value.Key);
    }
}
