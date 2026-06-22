using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using CyberSnap.Models;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class HistoryWindow
{
    private sealed record MediaCardShell(Border Card, Grid ImageContainer, StackPanel InfoPanel, System.Windows.Controls.Image Image, Border SelectionBadge, Grid Root);

    private static bool IsDraggableFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static bool HasHistoryFilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path);

    private static void DetachElementFromParent(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case System.Windows.Controls.Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, element):
                contentControl.Content = null;
                break;
        }
    }

    private MediaCardShell BuildMediaCardShell(HistoryItemVM vm, Action copyAction)
    {
        bool suppressOpenAction = false;
        var kindLabel = GetHistoryKindLabel(vm.Entry.Kind);
        if (vm.ThumbnailLoaded && IsStaleHistoryPlaceholder(vm.ThumbnailSource, vm.Entry.Kind))
        {
            vm.ThumbnailLoaded = false;
            vm.ThumbnailSource = null;
        }
        if ((vm.ThumbnailSource is null || !vm.ThumbnailLoaded) &&
            TryGetThumbFromCache(vm.Entry.FilePath, out var cachedThumb))
        {
            vm.ThumbnailSource = cachedThumb;
            vm.ThumbnailLoaded = true;
            RememberHistoryItemThumbnailFingerprint(vm, vm.Entry);
        }
        var img = new System.Windows.Controls.Image
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 1
        };
        vm.ThumbnailImage = img;
        img.Source = vm.ThumbnailSource ?? GetHistoryPlaceholder(vm.Entry.Kind);
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        img.Loaded += (_, _) => RefreshCardThumbnail(vm);

        var selectionBadge = CreateSelectionBadge(vm.IsSelected);

        var root = new Grid();
        var imageRow = new RowDefinition { Height = new GridLength(GetHistoryCardImageHeight(HistoryCardPreferredWidth)) };
        root.RowDefinitions.Add(imageRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var imgContainer = new Grid();
        imgContainer.Children.Add(img);
        imgContainer.Children.Add(selectionBadge);
        Grid.SetRow(imgContainer, 0);
        root.Children.Add(imgContainer);

        var info = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        var infoBorder = new Border
        {
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Theme.Brush(Theme.BgSecondary),
            Child = info
        };
        Grid.SetRow(infoBorder, 1);
        root.Children.Add(infoBorder);

        var hoverBorder = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            IsHitTestVisible = false,
            Background = Brushes.Transparent
        };
        Grid.SetRow(hoverBorder, 0);
        Grid.SetRowSpan(hoverBorder, 2);
        root.Children.Add(hoverBorder);

        var card = new Border
        {
            Width = HistoryCardPreferredWidth,
            MinWidth = HistoryCardMinWidth,
            MaxWidth = HistoryCardMaxWidth,
            Margin = new Thickness(HistoryCardMargin),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.BgCard),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = LocalizationService.Translate("Open in Editor"),
            Child = root,
            Tag = vm,
        };
        AutomationProperties.SetName(card, $"{kindLabel} history item");
        AutomationProperties.SetHelpText(card, "Press Enter or Space to open this history item. Press Ctrl+C to copy it. In select mode, press Enter or Space to select it.");

        // Context menu
        var actionMenu = CreateCardActionMenu();
        if (HasHistoryFilePath(vm.Entry.FilePath))
        {
            actionMenu.Items.Add(CreateCardActionMenuItem("Open", () =>
            {
                suppressOpenAction = true;
                OpenFileWithDefaultApp(vm.Entry.FilePath);
            }, "Open this file with the system default viewer."));
        }
        if (vm.Entry.Kind == HistoryKind.Image && HasHistoryFilePath(vm.Entry.FilePath))
        {
            actionMenu.Items.Add(CreateCardActionMenuItem("Open in editor", () =>
            {
                suppressOpenAction = true;
                try
                {
                    using var bmp = new System.Drawing.Bitmap(vm.Entry.FilePath);
                    CyberSnap.UI.Editor.EditorForm.ShowEditor(new System.Drawing.Bitmap(bmp), vm.Entry.FilePath);
                }
                catch (Exception ex)
                {
                    ToastWindow.ShowError("Editor failed", $"Could not open editor: {ex.Message}");
                }
            }, "Open this image in the post-capture editor."));
        }
        actionMenu.Items.Add(CreateCardActionMenuItem(GetHistoryCopyMenuLabel(vm.Entry), () =>
        {
            suppressOpenAction = true;
            copyAction();
        }, GetHistoryCopyMenuHelpText(vm.Entry, kindLabel)));

        if (vm.Entry.Kind == HistoryKind.Image && HasHistoryFilePath(vm.Entry.FilePath))
        {
            actionMenu.Items.Add(CreateCardActionMenuItem("Extract text", async () =>
            {
                suppressOpenAction = true;
                if (!File.Exists(vm.Entry.FilePath))
                {
                    ShowHistoryFileMissingError(vm.Entry.FilePath);
                    return;
                }
                try
                {
                    string? text = null;
                    if (_imageSearchIndexService != null &&
                        _imageSearchIndexService.TryGetRecord(vm.Entry.FilePath, out var record) &&
                        !string.IsNullOrWhiteSpace(record.OcrText))
                    {
                        text = record.OcrText;
                    }
                    else
                    {
                        using var bmp = new System.Drawing.Bitmap(vm.Entry.FilePath);
                        var langTag = _settingsService.Settings.OcrLanguageTag;
                        text = await OcrService.RecognizeAsync(bmp, langTag);
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var window = new OcrResultWindow(text, _settingsService)
                        {
                            Owner = this
                        };
                        window.Show();
                    }
                    else
                    {
                        ToastWindow.Show("OCR", "No text found");
                    }
                }
                catch (Exception ex)
                {
                    ToastWindow.ShowError("OCR failed", $"Failed to extract text: {ex.Message}");
                }
            }, "Extract text from this image using OCR."));
        }

        if (HasHistoryFilePath(vm.Entry.FilePath))
        {
            actionMenu.Items.Add(CreateCardActionMenuItem("Show in folder", () =>
            {
                suppressOpenAction = true;
                ShowFileInFolder(vm.Entry.FilePath);
            }, "Show this file in File Explorer."));
        }
        actionMenu.Items.Add(CreateCardActionMenuItem("Delete from disk", () =>
        {
            suppressOpenAction = true;
            if (!ThemedConfirmDialog.Confirm(this,
                    $"Delete {vm.Entry.FileName}?",
                    $"This will permanently delete the file and its history entry.\n\n{vm.Entry.FilePath}",
                    "Delete", "Cancel"))
                return;

            _historyService.DeleteEntry(vm.Entry);
            LoadCurrentHistoryTab();
        }, "Permanently delete this file from disk and history."));
        actionMenu.PlacementTarget = card;
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            suppressOpenAction = true;
            actionMenu.IsOpen = true;
        };

        var actionMenuBtn = new System.Windows.Controls.Button
        {
            ToolTip = LocalizationService.Translate("Actions"),
            Focusable = true,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Width = 24,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 255, 255, 255)),
            Content = "\u22EF",
            FontSize = 14,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(actionMenuBtn, $"{kindLabel} actions");
        AutomationProperties.SetHelpText(actionMenuBtn, "Press Enter or Space to open this history item's actions.");

        void UpdateActionMenuBtnVisibility()
        {
            if (card.IsMouseOver || card.IsKeyboardFocusWithin || actionMenu.IsOpen)
            {
                actionMenuBtn.Visibility = Visibility.Visible;
            }
            else
            {
                actionMenuBtn.Visibility = Visibility.Collapsed;
            }
        }

        void OpenActionMenu()
        {
            actionMenu.PlacementTarget = actionMenuBtn;
            actionMenu.IsOpen = true;
            UpdateActionMenuBtnVisibility();
        }

        actionMenuBtn.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenActionMenu();
        };

        actionMenuBtn.KeyDown += (_, e) =>
        {
            if (!IsHistoryCardActivationKey(e))
                return;
            e.Handled = true;
            OpenActionMenu();
        };

        actionMenuBtn.GotKeyboardFocus += (_, _) => UpdateActionMenuBtnVisibility();
        actionMenuBtn.LostKeyboardFocus += (_, _) => UpdateActionMenuBtnVisibility();
        actionMenu.Closed += (_, _) => UpdateActionMenuBtnVisibility();

        Grid.SetRow(actionMenuBtn, 1);
        System.Windows.Controls.Panel.SetZIndex(actionMenuBtn, 999);
        root.Children.Add(actionMenuBtn);

        card.SizeChanged += (s, _) =>
        {
            var b = (Border)s!;
            imageRow.Height = new GridLength(GetHistoryCardImageHeight(b.ActualWidth));
            b.Clip = new System.Windows.Media.RectangleGeometry(
                new System.Windows.Rect(0, 0, b.ActualWidth, b.ActualHeight), 8.5, 8.5);
        };

        card.MouseEnter += (s, _) =>
        {
            hoverBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
            UpdateActionMenuBtnVisibility();
        };
        card.MouseLeave += (s, _) =>
        {
            if (!card.IsKeyboardFocusWithin)
                hoverBorder.BorderBrush = Brushes.Transparent;
            UpdateActionMenuBtnVisibility();
        };
        card.GotKeyboardFocus += (_, _) =>
        {
            hoverBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
            UpdateActionMenuBtnVisibility();
        };
        card.LostKeyboardFocus += (_, _) =>
        {
            UpdateActionMenuBtnVisibility();
            if (card.IsKeyboardFocusWithin)
                return;

            hoverBorder.BorderBrush = Brushes.Transparent;
        };

        void ActivateCard(RoutedEventArgs e)
        {
            if (suppressOpenAction)
            {
                suppressOpenAction = false;
                e.Handled = true;
                return;
            }

            if (!_selectMode)
            {
                OpenFileWithDefaultApp(vm.Entry.FilePath);
                e.Handled = true;
                return;
            }

            vm.IsSelected = !vm.IsSelected;
            UpdateCardSelection(vm);
            UpdateImageSearchActionButtons();
            UpdateHistoryActionButtons();
            e.Handled = true;
        }

        card.MouseLeftButtonUp += (_, e) => ActivateCard(e);
        card.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                copyAction();
                return;
            }

            if (!IsHistoryCardActivationKey(e))
                return;

            ActivateCard(e);
        };

        // Drag-and-drop support: drag the file out of the history card
        System.Windows.Point? dragStart = null;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(card);
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (dragStart is null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(card);
            var diff = pos - dragStart.Value;
            if (Math.Abs(diff.X) < 5 && Math.Abs(diff.Y) < 5)
                return;

            var filePath = vm.Entry.FilePath;
            if (!IsDraggableFile(filePath))
                return;

            dragStart = null;
            suppressOpenAction = true;
            var data = new System.Windows.DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath });
            System.Windows.DragDrop.DoDragDrop(card, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
        };
        card.PreviewMouseLeftButtonUp += (_, _) => { dragStart = null; };

        vm.Card = card;
        vm.SelectionBadge = selectionBadge;
        UpdateCardSelection(vm);

        return new MediaCardShell(card, imgContainer, info, img, selectionBadge, root);
    }

    private static string GetHistoryKindLabel(HistoryKind kind) => kind switch
    {
        HistoryKind.Gif => "GIF",
        HistoryKind.Video => "video",
        HistoryKind.Sticker => "sticker",
        _ => "screenshot"
    };

    private static string GetHistoryCopyMenuLabel(HistoryEntry entry)
    {
        return entry.Kind switch
        {
            HistoryKind.Gif => "Copy GIF",
            HistoryKind.Video => "Copy video",
            HistoryKind.Image or HistoryKind.Sticker => "Copy image",
            _ => "Copy"
        };
    }

    private static string GetHistoryCopyMenuHelpText(HistoryEntry entry, string kindLabel)
    {
        return $"Copy this {kindLabel} history item.";
    }

    private ContextMenu CreateCardActionMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(ContextMenu.StyleProperty, "HistoryActionsMenuStyle");
        return menu;
    }

    private MenuItem CreateCardActionMenuItem(string label, Action action, string? helpText = null)
    {
        var translatedLabel = LocalizationService.Translate(label);
        var translatedHelpText = helpText != null ? LocalizationService.Translate(helpText) : LocalizationService.Translate("Run this history action.");
        var item = new MenuItem
        {
            Header = translatedLabel,
            ToolTip = translatedHelpText
        };
        item.SetResourceReference(MenuItem.StyleProperty, "HistoryActionsMenuItem");
        AutomationProperties.SetName(item, translatedLabel);
        AutomationProperties.SetHelpText(item, translatedHelpText);
        item.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return item;
    }

    private static Border CreateSelectionBadge(bool isSelected)
    {
        var checkPath = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M6,14 L11,19 L22,8"),
            Stroke = Brushes.White,
            StrokeThickness = 2.6,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8),
            Visibility = isSelected ? Visibility.Visible : Visibility.Hidden
        };

        var badge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
            Opacity = isSelected ? 1 : 0.45,
            Child = checkPath,
            Tag = checkPath
        };
        UpdateSelectionBadgeAccessibility(badge, isSelected);
        Grid.SetRowSpan(badge, 2);
        System.Windows.Controls.Panel.SetZIndex(badge, 20);
        return badge;
    }

    private static void UpdateSelectionBadgeAccessibility(FrameworkElement badge, bool isSelected)
    {
        badge.ToolTip = isSelected ? "Selected history item" : "History item selection marker";
        AutomationProperties.SetName(badge, isSelected ? "Selected history item" : "History item selection marker");
        AutomationProperties.SetHelpText(badge, isSelected
            ? "This history item is selected."
            : "Shows whether this history item is selected in select mode.");
    }

    private static bool ShowFileInFolder(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ShowHistoryFileMissingError(filePath);
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Open failed",
                $"CyberSnap could not open the file location. Try again from Config -> History, or open the folder manually.\n{ex.Message}",
                filePath);
            return false;
        }
    }

    private static bool OpenFileWithDefaultApp(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ShowHistoryFileMissingError(filePath);
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
                Verb = "open"
            });
            if (process is null)
            {
                ToastWindow.ShowError("Open failed", "Windows did not open the saved file. Try again from Config -> History, or open it from disk manually.", filePath);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Open failed",
                $"CyberSnap could not open the saved file. Try again from Config -> History, or open it from disk manually.\n{ex.Message}",
                filePath);
            return false;
        }
    }
}
