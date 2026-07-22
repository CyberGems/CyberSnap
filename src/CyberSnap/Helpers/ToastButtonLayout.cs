using System.Windows;
using CyberSnap.Models;

namespace CyberSnap.Helpers;

public enum ToastButtonKind
{
    Close,
    Pin,
    Save,
    Copy,
    Share,
    Delete,
    History,
    Edit
}

public enum ToastCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum ToastButtonPreset
{
    Minimal,
    Standard,
    Full,
    /// <summary>No overlay buttons — relies on preview-body click action.</summary>
    None
}

public static class ToastButtonLayout
{
    /// <summary>
    /// Map a slot to the confirm-bar designer grid. Destinations live on a single horizontal
    /// row (columns 0–4 for the five confirm actions: Save / Copy / Edit / Share / Gallery).
    /// Remaining slots keep unique columns for drag targets but stay on row 0.
    /// </summary>
    public static (int row, int column) ToGridCell(ToastButtonSlot slot) => slot switch
    {
        ToastButtonSlot.TopLeft => (0, 0),
        ToastButtonSlot.TopInnerLeft => (0, 1),
        ToastButtonSlot.TopInnerRight => (0, 2),
        ToastButtonSlot.TopRight => (0, 3),
        ToastButtonSlot.BottomLeft => (0, 4),          // 5th confirm slot (Gallery in Full)
        ToastButtonSlot.BottomInnerLeft => (0, 5),
        ToastButtonSlot.BottomInnerRight => (0, 6),
        _ => (0, 7) // BottomRight
    };

    /// <summary>Confirm-bar designer exposes five destination slots in one row.</summary>
    public const int ConfirmDestinationSlotCount = 5;

    /// <summary>Ordered slots used by the confirm destination strip (left → right).</summary>
    public static readonly ToastButtonSlot[] ConfirmDestinationSlots =
    {
        ToastButtonSlot.TopLeft,
        ToastButtonSlot.TopInnerLeft,
        ToastButtonSlot.TopInnerRight,
        ToastButtonSlot.TopRight,
        ToastButtonSlot.BottomLeft
    };

    public static (System.Windows.HorizontalAlignment horizontal, System.Windows.VerticalAlignment vertical, Thickness margin) ToPlacement(
        ToastButtonSlot slot,
        double inset = 8)
    {
        return slot switch
        {
            ToastButtonSlot.TopLeft => (System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Top, new Thickness(inset, inset, 0, 0)),
            ToastButtonSlot.TopInnerLeft => (System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Top, new Thickness(inset + 40, inset, 0, 0)),
            ToastButtonSlot.TopInnerRight => (System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Top, new Thickness(0, inset, inset + 40, 0)),
            ToastButtonSlot.TopRight => (System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Top, new Thickness(0, inset, inset, 0)),
            ToastButtonSlot.BottomLeft => (System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Bottom, new Thickness(inset, 0, 0, inset)),
            ToastButtonSlot.BottomInnerLeft => (System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Bottom, new Thickness(inset + 40, 0, 0, inset)),
            ToastButtonSlot.BottomInnerRight => (System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Bottom, new Thickness(0, 0, inset + 40, inset)),
            _ => (System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Bottom, new Thickness(0, 0, inset, inset))
        };
    }

    public static ToastButtonSlot GetSlot(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button)
        => button switch
        {
            ToastButtonKind.Close => settings.CloseSlot,
            ToastButtonKind.Pin => settings.PinSlot,
            ToastButtonKind.Save => settings.SaveSlot,
            ToastButtonKind.Copy => settings.CopySlot,
            ToastButtonKind.Share => settings.ShareSlot,
            ToastButtonKind.History => settings.HistorySlot,
            ToastButtonKind.Edit => settings.EditSlot,
            _ => settings.DeleteSlot
        };

    public static bool IsVisible(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button)
        => button switch
        {
            ToastButtonKind.Close => settings.ShowClose,
            ToastButtonKind.Pin => settings.ShowPin,
            ToastButtonKind.Save => settings.ShowSave,
            ToastButtonKind.Copy => settings.ShowCopy,
            ToastButtonKind.Share => settings.ShowShare,
            ToastButtonKind.History => settings.ShowHistory,
            ToastButtonKind.Edit => settings.ShowEdit,
            _ => settings.ShowDelete
        };

    public static void SetVisible(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button, bool visible)
    {
        switch (button)
        {
            case ToastButtonKind.Close: settings.ShowClose = visible; break;
            case ToastButtonKind.Pin: settings.ShowPin = visible; break;
            case ToastButtonKind.Save: settings.ShowSave = visible; break;
            case ToastButtonKind.Copy: settings.ShowCopy = visible; break;
            case ToastButtonKind.Share: settings.ShowShare = visible; break;
            case ToastButtonKind.History: settings.ShowHistory = visible; break;
            case ToastButtonKind.Edit: settings.ShowEdit = visible; break;
            default: settings.ShowDelete = visible; break;
        }
    }

    // Move a button to an exact slot, making it visible. If another *visible* button already
    // occupies the target slot, swap them (the occupant takes the dragged button's old slot).
    // Hidden buttons keep stale slot assignments and are ignored so they never displace a
    // visible one nor block a drop.
    public static void AssignSlot(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button, ToastButtonSlot targetSlot)
    {
        var currentSlot = GetSlot(settings, button);

        foreach (var other in AllButtons)
        {
            if (other == button)
                continue;
            if (IsVisible(settings, other) && GetSlot(settings, other) == targetSlot)
            {
                SetSlot(settings, other, currentSlot);
                break;
            }
        }

        SetSlot(settings, button, targetSlot);
        SetVisible(settings, button, true);
    }

    // Place a button that is currently hidden (dragged in from the list) into an exact slot.
    // Unlike AssignSlot, this never swaps an occupant into the hidden button's stale slot — a
    // hidden button has no meaningful current slot, so a naive swap could collide. Instead:
    //   - target free            -> place there;
    //   - target taken, partner free in the same corner -> push the occupant to the partner
    //                               (corners hold 2) and place the new button at the target;
    //   - corner already full     -> return false without mutating.
    public static bool PlaceFromHidden(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button, ToastButtonSlot targetSlot)
    {
        ToastButtonKind? occupant = null;
        foreach (var other in AllButtons)
        {
            if (other == button)
                continue;
            if (IsVisible(settings, other) && GetSlot(settings, other) == targetSlot)
            {
                occupant = other;
                break;
            }
        }

        if (occupant is not null)
        {
            var (outer, inner) = CornerSlots(SlotToCorner(targetSlot));
            var partner = targetSlot == outer ? inner : outer;
            if (IsSlotOccupiedByOther(settings, partner, button))
                return false; // corner full (both slots taken)
            SetSlot(settings, occupant.Value, partner);
        }

        SetSlot(settings, button, targetSlot);
        SetVisible(settings, button, true);
        return true;
    }

    public static ToastButtonKind? FindButtonAt(AppSettings.ToastButtonLayoutSettings settings, ToastButtonSlot slot)
    {
        if (settings.CloseSlot == slot) return ToastButtonKind.Close;
        if (settings.PinSlot == slot) return ToastButtonKind.Pin;
        if (settings.SaveSlot == slot) return ToastButtonKind.Save;
        if (settings.CopySlot == slot) return ToastButtonKind.Copy;
        if (settings.ShareSlot == slot) return ToastButtonKind.Share;
        if (settings.DeleteSlot == slot) return ToastButtonKind.Delete;
        if (settings.HistorySlot == slot) return ToastButtonKind.History;
        if (settings.EditSlot == slot) return ToastButtonKind.Edit;
        return null;
    }

    private static void SetSlot(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button, ToastButtonSlot slot)
    {
        switch (button)
        {
            case ToastButtonKind.Close: settings.CloseSlot = slot; break;
            case ToastButtonKind.Pin: settings.PinSlot = slot; break;
            case ToastButtonKind.Save: settings.SaveSlot = slot; break;
            case ToastButtonKind.Copy: settings.CopySlot = slot; break;
            case ToastButtonKind.Share: settings.ShareSlot = slot; break;
            case ToastButtonKind.History: settings.HistorySlot = slot; break;
            case ToastButtonKind.Edit: settings.EditSlot = slot; break;
            default: settings.DeleteSlot = slot; break;
        }
    }

    // Every notification button, in the order shown in the layout designer.
    public static readonly ToastButtonKind[] AllButtons =
    {
        ToastButtonKind.Pin,
        ToastButtonKind.Close,
        ToastButtonKind.Save,
        ToastButtonKind.Copy,
        ToastButtonKind.History,
        ToastButtonKind.Share,
        ToastButtonKind.Delete,
        ToastButtonKind.Edit
    };

    /// <summary>
    /// Actions that appear as confirm-mode pills after locking a capture region.
    /// Pin / Close / Delete are toast-only chrome leftovers and stay hidden in the designer.
    /// History / Gallery commits with save-to-disk then opens Gallery on the new entry.
    /// </summary>
    public static readonly ToastButtonKind[] ConfirmActionButtons =
    {
        ToastButtonKind.Save,
        ToastButtonKind.Copy,
        ToastButtonKind.Edit,
        ToastButtonKind.Share,
        ToastButtonKind.History
    };

    public static bool IsConfirmActionButton(ToastButtonKind button)
        => button is ToastButtonKind.Save or ToastButtonKind.Copy or ToastButtonKind.Edit
            or ToastButtonKind.Share or ToastButtonKind.History;

    public static ToastCorner SlotToCorner(ToastButtonSlot slot) => slot switch
    {
        ToastButtonSlot.TopLeft or ToastButtonSlot.TopInnerLeft => ToastCorner.TopLeft,
        ToastButtonSlot.TopRight or ToastButtonSlot.TopInnerRight => ToastCorner.TopRight,
        ToastButtonSlot.BottomLeft or ToastButtonSlot.BottomInnerLeft => ToastCorner.BottomLeft,
        _ => ToastCorner.BottomRight
    };

    // Each corner holds at most two buttons: the outer slot (at the very corner) and the
    // inner slot (offset inward by ~40px).
    public static (ToastButtonSlot outer, ToastButtonSlot inner) CornerSlots(ToastCorner corner) => corner switch
    {
        ToastCorner.TopLeft => (ToastButtonSlot.TopLeft, ToastButtonSlot.TopInnerLeft),
        ToastCorner.TopRight => (ToastButtonSlot.TopRight, ToastButtonSlot.TopInnerRight),
        ToastCorner.BottomLeft => (ToastButtonSlot.BottomLeft, ToastButtonSlot.BottomInnerLeft),
        _ => (ToastButtonSlot.BottomRight, ToastButtonSlot.BottomInnerRight)
    };

    public static ToastCorner GetCorner(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button)
        => SlotToCorner(GetSlot(settings, button));

    // Place a button into a corner, filling the outer slot first then the inner slot. Hidden
    // buttons keep their slot but do not occupy space, so only visible buttons count toward the
    // 2-per-corner cap. Returns false (without mutating) when both slots are taken by other
    // visible buttons.
    public static bool AssignCorner(AppSettings.ToastButtonLayoutSettings settings, ToastButtonKind button, ToastCorner corner)
    {
        if (IsVisible(settings, button) && GetCorner(settings, button) == corner)
            return true;

        var (outer, inner) = CornerSlots(corner);
        ToastButtonSlot? target =
            !IsSlotOccupiedByOther(settings, outer, button) ? outer :
            !IsSlotOccupiedByOther(settings, inner, button) ? inner :
            null;

        if (target is null)
            return false;

        SetSlot(settings, button, target.Value);
        SetVisible(settings, button, true);
        return true;
    }

    private static bool IsSlotOccupiedByOther(AppSettings.ToastButtonLayoutSettings settings, ToastButtonSlot slot, ToastButtonKind exclude)
    {
        foreach (var other in AllButtons)
        {
            if (other == exclude)
                continue;
            if (IsVisible(settings, other) && GetSlot(settings, other) == slot)
                return true;
        }

        return false;
    }

    public static void ApplyPreset(AppSettings.ToastButtonLayoutSettings settings, ToastButtonPreset preset)
    {
        foreach (var button in AllButtons)
            SetVisible(settings, button, false);

        switch (preset)
        {
            case ToastButtonPreset.None:
                // "Basic": Cancel/Retry only on the confirm bar. Capture chrome still injects
                // Save as a fallback so the region can always be committed.
                break;

            case ToastButtonPreset.Minimal:
                // Legacy alias of Standard — Save stays in the leftmost destination slot.
                Place(ToastButtonKind.Save, ToastButtonSlot.TopLeft);
                break;

            case ToastButtonPreset.Full:
                // All five confirm destinations in one row: Save · Copy · Edit · Share · Gallery.
                Place(ToastButtonKind.Save, ToastButtonSlot.TopLeft);
                Place(ToastButtonKind.Copy, ToastButtonSlot.TopInnerLeft);
                Place(ToastButtonKind.Edit, ToastButtonSlot.TopInnerRight);
                Place(ToastButtonKind.Share, ToastButtonSlot.TopRight);
                Place(ToastButtonKind.History, ToastButtonSlot.BottomLeft);
                break;

            default: // Standard — Save only, same slot as in Full.
                Place(ToastButtonKind.Save, ToastButtonSlot.TopLeft);
                break;
        }

        void Place(ToastButtonKind button, ToastButtonSlot slot)
        {
            SetSlot(settings, button, slot);
            SetVisible(settings, button, true);
        }
    }

    // Returns the preset whose visible buttons and corners exactly match the current layout,
    // or null when the layout has been customized.
    public static ToastButtonPreset? DetectPreset(AppSettings.ToastButtonLayoutSettings settings)
    {
        // Prefer named UI presets; Minimal is a legacy alias of Standard (same Save slot).
        foreach (var preset in new[]
                 {
                     ToastButtonPreset.Full,
                     ToastButtonPreset.Standard,
                     ToastButtonPreset.None,
                     ToastButtonPreset.Minimal
                 })
        {
            if (MatchesPreset(settings, preset))
                return preset;
        }

        return null;
    }

    private static bool MatchesPreset(AppSettings.ToastButtonLayoutSettings settings, ToastButtonPreset preset)
    {
        var expected = new AppSettings.ToastButtonLayoutSettings();
        ApplyPreset(expected, preset);

        foreach (var button in AllButtons)
        {
            bool visible = IsVisible(settings, button);
            if (visible != IsVisible(expected, button))
                return false;
            if (visible && GetCorner(settings, button) != GetCorner(expected, button))
                return false;
        }

        return true;
    }
}
