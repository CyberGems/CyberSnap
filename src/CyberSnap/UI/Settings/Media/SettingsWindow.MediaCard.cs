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

public partial class SettingsWindow
{
    private sealed record MediaCardShell(Border Card, Grid ImageContainer, StackPanel InfoPanel, System.Windows.Controls.Image Image, Border SelectionBadge);

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
        Grid.SetRow(info, 1);
        root.Children.Add(info);

        var cardFocusBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 255, 255, 255));
        var card = new Border
        {
            Width = HistoryCardPreferredWidth,
            MinWidth = HistoryCardMinWidth,
            MaxWidth = HistoryCardMaxWidth,
            Margin = new Thickness(HistoryCardMargin),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.BgCard),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = $"Open this {kindLabel} history item",
            Child = root,
            Tag = vm,
        };
        AutomationProperties.SetName(card, $"{kindLabel} history item");
        AutomationProperties.SetHelpText(card, "Press Enter or Space to open this history item. Press Ctrl+C to copy it. In select mode, press Enter or Space to select it.");

        // Context menu on right-click
        var contextMenu = CreateCardActionMenu();
        contextMenu.Items.Add(CreateCardActionMenuItem(GetHistoryCopyMenuLabel(vm.Entry), () =>
        {
            suppressOpenAction = true;
            copyAction();
        }, GetHistoryCopyMenuHelpText(vm.Entry, kindLabel)));
        if (HasHistoryFilePath(vm.Entry.FilePath))
        {
            contextMenu.Items.Add(CreateCardActionMenuItem("Show in folder", () =>
            {
                suppressOpenAction = true;
                ShowFileInFolder(vm.Entry.FilePath);
            }, "Show this file in File Explorer."));
        }
        contextMenu.Items.Add(CreateCardActionMenuItem("Delete from disk", () =>
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
        contextMenu.PlacementTarget = card;
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            suppressOpenAction = true;
            contextMenu.IsOpen = true;
        };

        card.SizeChanged += (s, _) =>
        {
            var b = (Border)s!;
            imageRow.Height = new GridLength(GetHistoryCardImageHeight(b.ActualWidth));
            b.Clip = new System.Windows.Media.RectangleGeometry(
                new System.Windows.Rect(0, 0, b.ActualWidth, b.ActualHeight), 8, 8);
        };

        card.MouseEnter += (s, _) =>
        {
            card.BorderBrush = cardFocusBrush;
        };
        card.MouseLeave += (s, _) =>
        {
            if (!card.IsKeyboardFocusWithin)
                card.BorderBrush = Brushes.Transparent;
        };
        card.GotKeyboardFocus += (_, _) =>
        {
            card.BorderBrush = cardFocusBrush;
        };
        card.LostKeyboardFocus += (_, _) =>
        {
            if (card.IsKeyboardFocusWithin)
                return;

            card.BorderBrush = Brushes.Transparent;
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

        return new MediaCardShell(card, imgContainer, info, img, selectionBadge);
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
        helpText ??= "Run this history action.";
        var item = new MenuItem
        {
            Header = label,
            ToolTip = helpText
        };
        item.SetResourceReference(MenuItem.StyleProperty, "HistoryActionsMenuItem");
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetHelpText(item, helpText);
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
                $"CyberSnap could not open the file location. Try again from Settings -> History, or open the folder manually.\n{ex.Message}",
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
                ToastWindow.ShowError("Open failed", "Windows did not open the saved file. Try again from Settings -> History, or open it from disk manually.", filePath);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(
                "Open failed",
                $"CyberSnap could not open the saved file. Try again from Settings -> History, or open it from disk manually.\n{ex.Message}",
                filePath);
            return false;
        }
    }

}
