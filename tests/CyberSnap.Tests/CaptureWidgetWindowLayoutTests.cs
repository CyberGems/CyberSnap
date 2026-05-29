using System.Windows;
using CyberSnap.Models;
using CyberSnap.UI;
using Xunit;

namespace CyberSnap.Tests;

public sealed class CaptureWidgetWindowLayoutTests
{
    [Fact]
    public void CalculateWidgetBounds_LeftCollapsed_PeeksFromWorkingAreaLeftEdge()
    {
        var workingArea = new Rect(78, 0, 1202, 720);

        var bounds = CaptureWidgetWindow.CalculateWidgetBounds(
            workingArea,
            CaptureDockSide.Left,
            dockOffset: 0.5,
            isExpanded: false,
            uiScale: 1.0);

        Assert.Equal(78, bounds.Left);
        Assert.Equal(16, bounds.Width);
        Assert.Equal(78 + 16, bounds.Right);
        Assert.Equal(250, bounds.Height);
        Assert.Equal((720 - 250) / 2.0, bounds.Top);
    }

    [Fact]
    public void CalculateWidgetBounds_BottomCollapsed_SitsOnWorkingAreaBottomEdge()
    {
        var workingArea = new Rect(0, 0, 1920, 1040);

        var bounds = CaptureWidgetWindow.CalculateWidgetBounds(
            workingArea,
            CaptureDockSide.Bottom,
            dockOffset: 0.5,
            isExpanded: false,
            uiScale: 1.0);

        Assert.Equal(16, bounds.Height);
        Assert.Equal(1040 - 16, bounds.Top);
        Assert.Equal(1040, bounds.Bottom);
    }

    [Theory]
    [InlineData(CaptureDockSide.Left)]
    [InlineData(CaptureDockSide.Right)]
    public void CalculateWidgetBounds_SideExpanded_KeepsFullPanelHeightVisible(CaptureDockSide dockSide)
    {
        var workingArea = new Rect(78, 20, 1202, 720);

        var bounds = CaptureWidgetWindow.CalculateWidgetBounds(
            workingArea,
            dockSide,
            dockOffset: 0.5,
            isExpanded: true,
            uiScale: 1.0);

        Assert.Equal(196, bounds.Width);
        Assert.Equal(250, bounds.Height);
        Assert.Equal(20 + (720 - 250) / 2.0, bounds.Top);

        if (dockSide == CaptureDockSide.Left)
        {
            Assert.Equal(78 - 6, bounds.Left);
            Assert.Equal(78 + 196 - 6, bounds.Right);
        }
        else
        {
            Assert.Equal(78 + 1202 - 196 + 6, bounds.Left);
            Assert.Equal(78 + 1202 + 6, bounds.Right);
        }
    }

    [Fact]
    public void CalculateWidgetBounds_LeftCollapsed_LeavesPeekInsideTaskbarAwareWorkingArea()
    {
        var workingArea = new Rect(-1458, 0, 1202, 720);

        var bounds = CaptureWidgetWindow.CalculateWidgetBounds(
            workingArea,
            CaptureDockSide.Left,
            dockOffset: 0.5,
            isExpanded: false,
            uiScale: 1.0);

        Assert.Equal(-1458, bounds.Left);
        Assert.Equal(-1458 + 16, bounds.Right);
    }

    [Fact]
    public void CalculateWidgetBounds_BottomCollapsed_LeavesPeekInsideWorkingArea()
    {
        var workingArea = new Rect(0, 0, 1536, 864);

        var bounds = CaptureWidgetWindow.CalculateWidgetBounds(
            workingArea,
            CaptureDockSide.Bottom,
            dockOffset: 0.5,
            isExpanded: false,
            uiScale: 1.0);

        Assert.Equal(864 - 16, bounds.Top);
        Assert.Equal(864, bounds.Bottom);
    }
}
