using System.Windows;
using Xunit;
using CyberSnap.Helpers;
using CyberSnap.Models;

namespace CyberSnap.Tests;

public sealed class ToastButtonLayoutTests
{
    [Fact]
    public void AssignSlot_SwapsButtons_WhenTargetSlotIsOccupied()
    {
        var settings = new AppSettings.ToastButtonLayoutSettings
        {
            CloseSlot = ToastButtonSlot.TopRight,
            PinSlot = ToastButtonSlot.TopLeft,
            SaveSlot = ToastButtonSlot.BottomRight
        };

        ToastButtonLayout.AssignSlot(settings, ToastButtonKind.Save, ToastButtonSlot.TopRight);

        Assert.Equal(ToastButtonSlot.BottomRight, settings.CloseSlot);
        Assert.Equal(ToastButtonSlot.TopRight, settings.SaveSlot);
    }

    [Fact]
    public void ToPlacement_ReturnsExpectedCornerAlignment()
    {
        var placement = ToastButtonLayout.ToPlacement(ToastButtonSlot.BottomLeft, 10);

        Assert.Equal(HorizontalAlignment.Left, placement.horizontal);
        Assert.Equal(VerticalAlignment.Bottom, placement.vertical);
        Assert.Equal(new Thickness(10, 0, 0, 10), placement.margin);
    }

    [Fact]
    public void ToPlacement_ReturnsExpectedInnerAlignment()
    {
        var placement = ToastButtonLayout.ToPlacement(ToastButtonSlot.TopInnerRight, 8);

        Assert.Equal(HorizontalAlignment.Right, placement.horizontal);
        Assert.Equal(VerticalAlignment.Top, placement.vertical);
        Assert.Equal(new Thickness(0, 8, 48, 0), placement.margin);
    }

    [Fact]
    public void AssignCorner_FillsOuterThenInner()
    {
        var settings = HiddenLayout();

        Assert.True(ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Pin, ToastCorner.TopLeft));
        Assert.Equal(ToastButtonSlot.TopLeft, settings.PinSlot);
        Assert.True(settings.ShowPin);

        Assert.True(ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Close, ToastCorner.TopLeft));
        Assert.Equal(ToastButtonSlot.TopInnerLeft, settings.CloseSlot);
    }

    [Fact]
    public void AssignCorner_ReturnsFalse_AndDoesNotMutate_WhenCornerFull()
    {
        var settings = HiddenLayout();
        ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Pin, ToastCorner.TopLeft);
        ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Close, ToastCorner.TopLeft);

        var saveSlotBefore = settings.SaveSlot;
        Assert.False(ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Save, ToastCorner.TopLeft));
        Assert.False(settings.ShowSave);
        Assert.Equal(saveSlotBefore, settings.SaveSlot);
    }

    [Fact]
    public void HidingButton_FreesCornerSlot()
    {
        var settings = HiddenLayout();
        ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Pin, ToastCorner.TopLeft);
        ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Close, ToastCorner.TopLeft);

        ToastButtonLayout.SetVisible(settings, ToastButtonKind.Pin, false);

        Assert.True(ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Save, ToastCorner.TopLeft));
        Assert.Equal(ToastButtonSlot.TopLeft, settings.SaveSlot);
    }

    [Fact]
    public void ApplyPreset_Minimal_ShowsOnlyClose()
    {
        var settings = new AppSettings.ToastButtonLayoutSettings();
        ToastButtonLayout.ApplyPreset(settings, ToastButtonPreset.Minimal);

        Assert.True(settings.ShowClose);
        Assert.Equal(ToastCorner.TopRight, ToastButtonLayout.GetCorner(settings, ToastButtonKind.Close));
        foreach (var button in ToastButtonLayout.AllButtons)
        {
            if (button != ToastButtonKind.Close)
                Assert.False(ToastButtonLayout.IsVisible(settings, button));
        }
    }

    [Fact]
    public void ApplyPreset_Full_ShowsEveryButton()
    {
        var settings = new AppSettings.ToastButtonLayoutSettings();
        ToastButtonLayout.ApplyPreset(settings, ToastButtonPreset.Full);

        foreach (var button in ToastButtonLayout.AllButtons)
            Assert.True(ToastButtonLayout.IsVisible(settings, button));
    }

    [Fact]
    public void ApplyPreset_Standard_MatchesDefaultVisibleSet()
    {
        var settings = new AppSettings.ToastButtonLayoutSettings();
        ToastButtonLayout.ApplyPreset(settings, ToastButtonPreset.Standard);

        Assert.True(settings.ShowPin);
        Assert.True(settings.ShowClose);
        Assert.True(settings.ShowSave);
        Assert.True(settings.ShowHistory);
        Assert.False(settings.ShowOffice);
        Assert.False(settings.ShowDelete);
        Assert.False(settings.ShowEdit);
    }

    [Theory]
    [InlineData(ToastButtonPreset.Minimal)]
    [InlineData(ToastButtonPreset.Standard)]
    [InlineData(ToastButtonPreset.Full)]
    public void DetectPreset_RecognizesEachPreset(ToastButtonPreset preset)
    {
        var settings = new AppSettings.ToastButtonLayoutSettings();
        ToastButtonLayout.ApplyPreset(settings, preset);

        Assert.Equal(preset, ToastButtonLayout.DetectPreset(settings));
    }

    [Fact]
    public void DetectPreset_ReturnsNull_ForCustomLayout()
    {
        var settings = new AppSettings.ToastButtonLayoutSettings();
        ToastButtonLayout.ApplyPreset(settings, ToastButtonPreset.Standard);
        ToastButtonLayout.AssignCorner(settings, ToastButtonKind.Pin, ToastCorner.BottomLeft);

        Assert.Null(ToastButtonLayout.DetectPreset(settings));
    }

    private static AppSettings.ToastButtonLayoutSettings HiddenLayout()
    {
        var settings = new AppSettings.ToastButtonLayoutSettings();
        foreach (var button in ToastButtonLayout.AllButtons)
            ToastButtonLayout.SetVisible(settings, button, false);
        return settings;
    }
}
