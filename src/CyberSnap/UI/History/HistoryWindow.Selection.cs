using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Models;
using CyberSnap.Services;
using Brushes = System.Windows.Media.Brushes;

namespace CyberSnap.UI;

/// <summary>
/// Centralized batch-selection model for the Gallery (HistoryWindow).
/// Single source of truth per category so badges, counts and deletes stay in sync
/// across All / Images / Videos&amp;GIFs / Text / Colors / Codes filters.
/// </summary>
public partial class HistoryWindow
{
    private enum GalleryCategory
    {
        All = 0,
        Images = 1,
        Media = 2,
        Text = 3,
        Colors = 4,
        Codes = 5
    }

    // Authoritative selection state. Visual elements (Border.Tag, HistoryItemVM.IsSelected)
    // are treated as views and synced from/to these sets.
    private readonly HashSet<string> _selectedFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<OcrHistoryEntry> _selectedOcr = new();
    private readonly HashSet<ColorHistoryEntry> _selectedColor = new();
    private readonly HashSet<CodeHistoryEntry> _selectedCode = new();

    private GalleryCategory CurrentGalleryCategory()
        => HistoryCategoryCombo == null
            ? GalleryCategory.All
            : (GalleryCategory)Math.Clamp(HistoryCategoryCombo.SelectedIndex, 0, 5);

    /// <summary>When true, selection counts read 0 without touching the visual tree
    /// (used while switching filters, when panels still show the previous category).</summary>
    private bool _suppressSelectionRebuild;

    /// <summary>
    /// Finds a selection badge under a card root. Badges on Text/Color/Code cards live nested
    /// inside the top-area grid (textArea/swatchArea/previewArea), not as direct children of root.
    /// </summary>
    private static Border? FindSelectableBadge(Grid root)
    {
        foreach (var child in root.Children)
        {
            if (child is Border badge && badge.Tag is UIElement)
                return badge;
            if (child is Grid nested)
            {
                var found = FindSelectableBadge(nested);
                if (found is not null)
                    return found;
            }
        }
        return null;
    }

    private static string FileKey(HistoryEntry entry) => entry?.FilePath ?? "";

    private bool IsFileSelected(HistoryEntry? entry)
        => entry != null && !string.IsNullOrEmpty(entry.FilePath) && _selectedFilePaths.Contains(entry.FilePath);

    private void SetFileSelected(HistoryEntry? entry, bool selected)
    {
        if (entry == null || string.IsNullOrEmpty(entry.FilePath))
            return;
        if (selected)
            _selectedFilePaths.Add(entry.FilePath);
        else
            _selectedFilePaths.Remove(entry.FilePath);
    }

    private void SyncVmFromStore(HistoryItemVM vm)
    {
        var shouldBe = IsFileSelected(vm.Entry);
        if (vm.IsSelected != shouldBe)
            vm.IsSelected = shouldBe;
    }

    private void SyncStoreFromVm(HistoryItemVM vm)
    {
        if (vm.Entry == null || string.IsNullOrEmpty(vm.Entry.FilePath))
            return;
        if (vm.IsSelected)
            _selectedFilePaths.Add(vm.Entry.FilePath);
        else
            _selectedFilePaths.Remove(vm.Entry.FilePath);
    }

    /// <summary>Pull legacy per-card state (Tag / VM flags / visual sets) into the central store.</summary>
    private void RebuildSelectionStoreFromCards()
    {
        var cat = CurrentGalleryCategory();

        // File-backed cards: merge VM flags.
        foreach (var vm in GetCurrentHistorySelectionItems())
            SyncStoreFromVm(vm);

        if (cat == GalleryCategory.All)
        {
            // Unified All-tab cards tracked via _selectedCardsInAllTab + Tag.
            foreach (var card in _selectedCardsInAllTab.ToList())
            {
                if (card.Tag is HistoryItemVM vm && vm.Entry != null)
                    _selectedFilePaths.Add(vm.Entry.FilePath);
                else if (card.Tag is bool b && b && _unifiedCardEntries.TryGetValue(card, out var raw))
                    AddRawToStore(raw);
                else if (_unifiedCardEntries.TryGetValue(card, out var rawEntry))
                {
                    // Card is in the selected set: ensure its entry is too.
                    AddRawToStore(rawEntry);
                }
            }
            // Also pick up any card whose Tag was flipped directly (OCR/Color/Code unified cards).
            WalkVisualBorders(HistoryStack, border =>
            {
                if (border.Tag is true && _unifiedCardEntries.TryGetValue(border, out var raw))
                    AddRawToStore(raw);
                else if (border.Tag is HistoryItemVM vm && vm.IsSelected && vm.Entry != null)
                    _selectedFilePaths.Add(vm.Entry.FilePath);
            });
        }
        else if (cat == GalleryCategory.Media)
        {
            foreach (var vm in _filteredGifItems)
                SyncStoreFromVm(vm);
        }
        else if (cat == GalleryCategory.Text)
        {
            // Cards live inside WrapPanels (with date separators/pills as direct children).
            foreach (var card in GetWrappedSelectableCards(OcrStack))
            {
                if (card.Tag is true && card.DataContext is OcrHistoryEntry ocr)
                    _selectedOcr.Add(ocr);
            }
        }
        else if (cat == GalleryCategory.Colors)
        {
            foreach (var card in GetWrappedSelectableCards(ColorStack))
            {
                if (card.Tag is ColorHistoryEntry color)
                    _selectedColor.Add(color);
                else if (card.DataContext is ColorHistoryEntry dc && IsSelectableHistoryCard(card))
                {
                    // Fallback: card cache may hold selection only in the store already.
                }
            }
        }
        else if (cat == GalleryCategory.Codes)
        {
            foreach (var card in GetWrappedSelectableCards(CodeStack))
            {
                if (card.Tag is CodeHistoryEntry code)
                    _selectedCode.Add(code);
            }
        }
    }

    private void AddRawToStore(object raw)
    {
        switch (raw)
        {
            case HistoryEntry file: SetFileSelected(file, true); break;
            case OcrHistoryEntry ocr: _selectedOcr.Add(ocr); break;
            case ColorHistoryEntry color: _selectedColor.Add(color); break;
            case CodeHistoryEntry code: _selectedCode.Add(code); break;
        }
    }

    private void PushStoreToVisibleCards()
    {
        var cat = CurrentGalleryCategory();

        foreach (var vm in GetCurrentHistorySelectionItems())
        {
            SyncVmFromStore(vm);
            UpdateCardSelection(vm);
        }

        if (cat == GalleryCategory.All)
        {
            // Rebuild the All-tab visual set from the store so paged/virtualized cards agree.
            _selectedCardsInAllTab.Clear();
            WalkVisualBorders(HistoryStack, border =>
            {
                if (border.Tag is HistoryItemVM vm && vm.Entry != null)
                {
                    SyncVmFromStore(vm);
                    UpdateCardSelection(vm);
                    if (vm.IsSelected)
                        _selectedCardsInAllTab.Add(border);
                }
                else if (border.Tag is bool && _unifiedCardEntries.TryGetValue(border, out var raw))
                {
                    var selected = IsRawSelected(raw);
                    border.Tag = selected;
                    if (selected)
                        _selectedCardsInAllTab.Add(border);
                    UpdateUnifiedCardSelectionVisual(border, selected);
                }
            });
        }
        else if (cat == GalleryCategory.Media)
        {
            WalkVisualBorders(GifsPanel, border =>
            {
                if (border.Tag is HistoryItemVM vm && vm.Entry != null)
                {
                    SyncVmFromStore(vm);
                    UpdateCardSelection(vm);
                }
            });
        }

        foreach (var card in GetCurrentSelectableCards())
            SyncSelectableCardFromStore(card);

        UpdateHistoryActionButtons();
    }

    private bool IsRawSelected(object raw) => raw switch
    {
        HistoryEntry file => IsFileSelected(file),
        OcrHistoryEntry ocr => _selectedOcr.Contains(ocr),
        ColorHistoryEntry color => _selectedColor.Contains(color),
        CodeHistoryEntry code => _selectedCode.Contains(code),
        _ => false
    };

    private static object? EntryForSelectableCard(Border card, GalleryCategory cat)
    {
        if (card.Tag is OcrHistoryEntry or ColorHistoryEntry or CodeHistoryEntry)
            return card.Tag;
        if (card.DataContext is OcrHistoryEntry or ColorHistoryEntry or CodeHistoryEntry)
        {
            // Text tab uses Tag=true + DataContext=entry; Colors/Codes use Tag=entry|null.
            if (cat == GalleryCategory.Text)
                return card.DataContext;
            // For Colors/Codes, Tag is authoritative when present; DataContext identifies the entry.
            return card.DataContext;
        }
        return null;
    }

    private void SyncSelectableCardFromStore(Border card)
    {
        var cat = CurrentGalleryCategory();
        if (card.Child is not Grid root)
            return;
        var badge = FindSelectableBadge(root);
        if (badge is null)
        {
            RefreshSelectableCardSelection(card);
            return;
        }

        bool selected = false;
        if (cat == GalleryCategory.Text && card.DataContext is OcrHistoryEntry ocr)
        {
            selected = _selectedOcr.Contains(ocr);
            card.Tag = selected;
        }
        else if (cat == GalleryCategory.Colors && card.DataContext is ColorHistoryEntry color)
        {
            selected = _selectedColor.Contains(color);
            card.Tag = selected ? color : null;
        }
        else if (cat == GalleryCategory.Codes && card.DataContext is CodeHistoryEntry code)
        {
            selected = _selectedCode.Contains(code);
            card.Tag = selected ? code : null;
        }
        else
        {
            RefreshSelectableCardSelection(card);
            return;
        }

        UpdateSelectableCardSelection(card, badge, selected);
    }

    /// <summary>Select every item in the current filter (not just rendered cards).</summary>
    private void SelectAllInCategory()
    {
        var cat = CurrentGalleryCategory();
        switch (cat)
        {
            case GalleryCategory.All:
                foreach (var u in _filteredUnifiedEntries)
                    AddRawToStore(u.RawEntry);
                // Keep image VMs in sync so Images tab reuses the same selection.
                foreach (var vm in _allHistoryItems)
                    SyncVmFromStore(vm);
                foreach (var vm in _filteredHistoryItems)
                    SyncVmFromStore(vm);
                _selectAllActive = true;
                break;
            case GalleryCategory.Images:
                EnsureAllImageHistoryItemsMaterialized();
                foreach (var vm in _filteredHistoryItems)
                    SetFileSelected(vm.Entry, true);
                _selectAllActive = true;
                break;
            case GalleryCategory.Media:
                foreach (var vm in _filteredGifItems)
                    SetFileSelected(vm.Entry, true);
                _selectAllActive = true;
                break;
            case GalleryCategory.Text:
                foreach (var e in _filteredOcrEntries)
                    _selectedOcr.Add(e);
                break;
            case GalleryCategory.Colors:
                foreach (var e in _filteredColorEntries)
                    _selectedColor.Add(e);
                break;
            case GalleryCategory.Codes:
                foreach (var e in _filteredCodeEntries)
                    _selectedCode.Add(e);
                break;
        }
        PushStoreToVisibleCards();
    }

    private void ClearGallerySelection()
    {
        _selectedFilePaths.Clear();
        _selectedOcr.Clear();
        _selectedColor.Clear();
        _selectedCode.Clear();
        _selectedCardsInAllTab.Clear();
        _selectAllActive = false;
        _wasSelectAllDelete = false;

        foreach (var vm in _allHistoryItems)
            vm.IsSelected = false;
        foreach (var vm in _filteredHistoryItems)
            vm.IsSelected = false;
        foreach (var vm in _filteredGifItems)
            vm.IsSelected = false;
        foreach (var vm in _allGifItems)
            vm.IsSelected = false;
    }

    private int SelectedCountInCategory()
    {
        if (_suppressSelectionRebuild)
            return 0;
        RebuildSelectionStoreFromCards();
        return CurrentGalleryCategory() switch
        {
            GalleryCategory.All => _selectedFilePaths.Count + _selectedOcr.Count + _selectedColor.Count + _selectedCode.Count,
            GalleryCategory.Images => _filteredHistoryItems.Count(vm => IsFileSelected(vm.Entry)),
            GalleryCategory.Media => _filteredGifItems.Count(vm => IsFileSelected(vm.Entry)),
            GalleryCategory.Text => _selectedOcr.Count,
            GalleryCategory.Colors => _selectedColor.Count,
            GalleryCategory.Codes => _selectedCode.Count,
            _ => 0
        };
    }

    private int DeletableCountInCategory()
    {
        var cat = CurrentGalleryCategory();
        var searchActive = !string.IsNullOrWhiteSpace(ImageSearchBox?.Text);
        // "Elegir toda una categoría/filtro": if a search/filter is active, act on the filtered set.
        return cat switch
        {
            GalleryCategory.All => searchActive ? _filteredUnifiedEntries.Count : GetCurrentTotalHistoryItemCount(),
            GalleryCategory.Images => searchActive ? _filteredHistoryItems.Count : _historyService.ImageEntries.Count,
            GalleryCategory.Media => _historyService.MediaEntries.Count,
            GalleryCategory.Text => searchActive ? _filteredOcrEntries.Count : _historyService.OcrEntries.Count,
            GalleryCategory.Colors => searchActive ? _filteredColorEntries.Count : _historyService.ColorEntries.Count,
            GalleryCategory.Codes => searchActive ? _filteredCodeEntries.Count : _historyService.CodeEntries.Count,
            _ => 0
        };
    }

    private System.Windows.Controls.ScrollViewer? CurrentGalleryScrollViewer()
        => CurrentGalleryCategory() switch
        {
            GalleryCategory.Media => GifsPanel,
            GalleryCategory.Text => TextPanel,
            GalleryCategory.Colors => ColorsPanel,
            GalleryCategory.Codes => CodesPanel,
            _ => ImagesPanel,
        };

    /// <summary>
    /// Restores a scroll offset after a background re-render (deferred to let layout settle).
    /// Only used for same-filter background refreshes — never for searches, filter switches
    /// or navigate-to-item, which all have intentional scroll targets of their own.
    /// </summary>
    private void RestoreGalleryScroll(System.Windows.Controls.ScrollViewer viewer, double offset)
    {
        if (offset > 1)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (IsLoaded)
                    viewer.ScrollToVerticalOffset(Math.Min(offset, viewer.ScrollableHeight));
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void DeleteFilteredCategory(GalleryCategory cat)
    {
        switch (cat)
        {
            case GalleryCategory.All:
            {
                var files = new List<HistoryEntry>();
                var ocrs = new List<OcrHistoryEntry>();
                var colors = new List<ColorHistoryEntry>();
                var codes = new List<CodeHistoryEntry>();
                foreach (var u in _filteredUnifiedEntries)
                {
                    switch (u.RawEntry)
                    {
                        case HistoryEntry fe: files.Add(fe); break;
                        case OcrHistoryEntry ocr: ocrs.Add(ocr); break;
                        case ColorHistoryEntry color: colors.Add(color); break;
                        case CodeHistoryEntry code: codes.Add(code); break;
                    }
                }
                if (files.Count > 0) _historyService.DeleteEntries(files);
                if (ocrs.Count > 0) _historyService.DeleteOcrEntries(ocrs);
                if (colors.Count > 0) _historyService.DeleteColorEntries(colors);
                if (codes.Count > 0) _historyService.DeleteCodeEntries(codes);
                _allGifItems.Clear();
                break;
            }
            case GalleryCategory.Images:
                if (_filteredHistoryItems.Count > 0)
                    _historyService.DeleteEntries(_filteredHistoryItems.Select(vm => vm.Entry).ToList());
                break;
            case GalleryCategory.Text:
                if (_filteredOcrEntries.Count > 0)
                    _historyService.DeleteOcrEntries(_filteredOcrEntries.ToList());
                break;
            case GalleryCategory.Colors:
                if (_filteredColorEntries.Count > 0)
                    _historyService.DeleteColorEntries(_filteredColorEntries.ToList());
                break;
            case GalleryCategory.Codes:
                if (_filteredCodeEntries.Count > 0)
                    _historyService.DeleteCodeEntries(_filteredCodeEntries.ToList());
                break;
        }
    }

    /// <summary>Single shared badge style: top-left pill so it never covers play icons or text.</summary>
    private static void ApplyGalleryBadgeVisual(Border badge, bool selected)
    {
        badge.Width = 30;
        badge.Height = 30;
        badge.CornerRadius = new CornerRadius(15);
        badge.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        badge.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        badge.Margin = new Thickness(6);
        badge.IsHitTestVisible = false;
        System.Windows.Controls.Panel.SetZIndex(badge, 30);
        badge.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        // Keep layout stable: hidden badges collapse (no ghost ring). Ring only in select mode
        // is handled by the caller via SelectModeBadgeVisibility.
        badge.Opacity = selected ? 1 : 0.45;
        if (selected)
        {
            badge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 0, 210, 100));
            badge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 0, 210, 100));
            badge.BorderThickness = new Thickness(1.5);
        }
        else
        {
            // Dark scrim + white ring: visible on white previews (QR), color swatches
            // and dark screenshots alike. The old translucent-gray ring was invisible on white.
            badge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, 12, 12, 14));
            badge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255));
            badge.BorderThickness = new Thickness(2);
        }
        UpdateSelectionBadgeAccessibility(badge, selected);
        if (badge.Tag is UIElement check)
            check.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
    }

    private void ApplyGalleryBadgeWithMode(Border badge, bool selected)
    {
        ApplyGalleryBadgeVisual(badge, selected);
        // In select mode show the empty ring so affordance is discoverable;
        // outside select mode hide unselected badges entirely (fixes "sometimes visible" ghosts).
        if (_selectMode && !selected)
            badge.Visibility = Visibility.Visible;
        else if (!selected)
            badge.Visibility = Visibility.Collapsed;
    }
}
