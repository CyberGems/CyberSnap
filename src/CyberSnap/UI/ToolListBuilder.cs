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
        ("_scrollCapture", "Scroll capture",      ToolGlyphs.ScrollCaptureGlyph),
        ("_record",        "Record",              ToolGlyphs.RecordGlyph),
    };

    private static readonly Dictionary<TextBox, bool> RecordingFlags = new();
    private static readonly HashSet<StackPanel> RestoringEnabledToolPanels = new();

    public static void Build(StackPanel capturePanel, StackPanel annotationPanel, SettingsService settingsService, FrameworkElement owner, Action? hotkeyChanged = null)
    {
        capturePanel.Children.Clear();
        annotationPanel.Children.Clear();
        var s = settingsService.Settings;
        var enabled = s.EnabledTools ?? ToolDef.DefaultEnabledIds();
        // Icon color for rendering Fluent glyphs to bitmaps
        var iconColor = Theme.IsDark ? System.Drawing.Color.FromArgb(225, 255, 255, 255) : System.Drawing.Color.FromArgb(210, 0, 0, 0);
        var segoe = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName);

        void AddToolRow(StackPanel targetPanel, string toolId, string label, char icon, bool hasToolbarToggle, bool showHotkey)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 0, 6),
                MinHeight = 44,
                BorderThickness = new Thickness(1),
            };
            card.SetResourceReference(Border.BackgroundProperty, "ThemeCardBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "ThemeWindowBorderBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (icon != '\0')
            {
                var iconFrame = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                iconFrame.SetResourceReference(Border.BackgroundProperty, "ThemeTabActiveBrush");
                iconFrame.SetResourceReference(Border.BorderBrushProperty, "ThemeInputBorderBrush");

                var img = new System.Windows.Controls.Image
                {
                    Source = ToolIcons.RenderToolIconWpf(toolId, icon, iconColor, 14),
                    Width = 14,
                    Height = 14,
                    Opacity = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
                iconFrame.Child = img;
                left.Children.Add(iconFrame);
            }

            if (hasToolbarToggle)
            {
                var cb = new CheckBox
                {
                    IsChecked = enabled.Contains(toolId),
                    Tag = toolId,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                };
                cb.Checked += (_, _) => SaveEnabledTools(capturePanel, annotationPanel, settingsService);
                cb.Unchecked += (_, _) => SaveEnabledTools(capturePanel, annotationPanel, settingsService);
                left.Children.Add(cb);
            }

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontFamily = segoe,
                VerticalAlignment = VerticalAlignment.Center,
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
            left.Children.Add(labelBlock);

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            if (showHotkey)
            {
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var hkBox = new TextBox();
                hkBox.SetResourceReference(TextBox.StyleProperty, "HotkeyBox");
                hkBox.Height = 28;
                hkBox.MinHeight = 28;
                hkBox.Width = 110;
                hkBox.MinWidth = 110;
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
                clearBtn.FontSize = 12;
                var capturedBox = hkBox;
                var capturedId = toolId;

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

                    try
                    {
                        if (conflict is not null)
                            ClearHotkeyConflict(settingsService.Settings, conflict);

                        settingsService.Settings.SetToolHotkey(capturedId, mod, vk);
                        settingsService.Save();
                        capturedBox.Text = HotkeyFormatter.Format(mod, vk);
                        hotkeyChanged?.Invoke();
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

                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
            }

            card.Child = grid;
            targetPanel.Children.Add(card);
        }

        foreach (var t in ToolDef.AllTools.Where(t => t.Group == 0))
            AddToolRow(capturePanel, t.Id, t.Label, t.Icon, true, true);

        foreach (var (id, label, icon) in ExtraTools)
            AddToolRow(capturePanel, id, label, icon, false, true);

        foreach (var t in ToolDef.AllTools.Where(t => t.Group == 1))
            AddToolRow(annotationPanel, t.Id, t.Label, t.Icon, true, true);

        LocalizationService.ApplyTo(capturePanel, settingsService.Settings.InterfaceLanguage);
        LocalizationService.ApplyTo(annotationPanel, settingsService.Settings.InterfaceLanguage);
    }

    private static void SaveEnabledTools(StackPanel capturePanel, StackPanel annotationPanel, SettingsService svc)
    {
        if (RestoringEnabledToolPanels.Contains(capturePanel) || RestoringEnabledToolPanels.Contains(annotationPanel))
            return;

        var previous = (svc.Settings.EnabledTools ?? ToolDef.DefaultEnabledIds()).ToList();
        var enabledIds = new System.Collections.Generic.List<string>();

        void ScanPanel(StackPanel p)
        {
            foreach (var card in p.Children.OfType<Border>())
            {
                if (card.Child is not Grid g) continue;
                foreach (var sp in g.Children.OfType<StackPanel>())
                foreach (var cb in sp.Children.OfType<CheckBox>())
                {
                    if (cb.Tag is string id && cb.IsChecked == true)
                        enabledIds.Add(id);
                }
            }
        }

        ScanPanel(capturePanel);
        ScanPanel(annotationPanel);

        if (!enabledIds.Any(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 0)))
        {
            RestoreEnabledToolChecks(capturePanel, annotationPanel, previous);
            ToastWindow.ShowError("Tool required", "Keep at least one capture tool enabled.");
            return;
        }

        try
        {
            svc.Settings.EnabledTools = enabledIds;
            svc.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.enabled-tools", ex);
            svc.Settings.EnabledTools = previous;
            try
            {
                svc.Save();
            }
            catch (Exception rollbackEx)
            {
                AppDiagnostics.LogError("settings.enabled-tools-rollback", rollbackEx);
            }

            RestoreEnabledToolChecks(capturePanel, annotationPanel, previous);
            ShowEnabledToolsSaveFailed(ex);
        }
    }

    private static void ShowEnabledToolsSaveFailed(Exception ex)
    {
        ToastWindow.ShowError(
            "Tool setting failed",
            $"The previous enabled tools were restored. Check Config -> Tools and try again.\n{ex.Message}");
    }

    private static void RestoreEnabledToolChecks(StackPanel capturePanel, StackPanel annotationPanel, IReadOnlyCollection<string> enabledIds)
    {
        RestoringEnabledToolPanels.Add(capturePanel);
        RestoringEnabledToolPanels.Add(annotationPanel);
        try
        {
            void RestorePanel(StackPanel p)
            {
                foreach (var card in p.Children.OfType<Border>())
                {
                    if (card.Child is not Grid g) continue;
                    foreach (var sp in g.Children.OfType<StackPanel>())
                    foreach (var cb in sp.Children.OfType<CheckBox>())
                    {
                        if (cb.Tag is string id)
                            cb.IsChecked = enabledIds.Contains(id);
                    }
                }
            }
            RestorePanel(capturePanel);
            RestorePanel(annotationPanel);
        }
        finally
        {
            RestoringEnabledToolPanels.Remove(capturePanel);
            RestoringEnabledToolPanels.Remove(annotationPanel);
        }
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
