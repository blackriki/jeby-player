using EmbyPlayer.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class MaximizedWindowBoundsCalculatorTests
{
    [DataTestMethod]
    [DataRow(0, 0, 2560, 1600, 0, 0, 2560, 1520, 0, 0, 2560, 1520, DisplayName = "Bottom taskbar")]
    [DataRow(0, 0, 2560, 1600, 0, 80, 2560, 1600, 0, 80, 2560, 1520, DisplayName = "Top taskbar")]
    [DataRow(0, 0, 2560, 1600, 96, 0, 2560, 1600, 96, 0, 2464, 1600, DisplayName = "Left taskbar")]
    [DataRow(0, 0, 2560, 1600, 0, 0, 2464, 1600, 0, 0, 2464, 1600, DisplayName = "Right taskbar")]
    [DataRow(-1920, -1080, 0, 0, -1920, -1080, 0, -40, 0, 0, 1920, 1040, DisplayName = "Negative-coordinate monitor")]
    [DataRow(1920, 0, 3840, 1080, 1920, 0, 3840, 1080, 0, 0, 1920, 1080, DisplayName = "Work area equals monitor")]
    public void Calculate_MapsMonitorRelativePositionAndWorkAreaSize(
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var bounds = MaximizedWindowBoundsCalculator.Calculate(
            new NativePixelRect(monitorLeft, monitorTop, monitorRight, monitorBottom),
            new NativePixelRect(workLeft, workTop, workRight, workBottom));

        Assert.AreEqual(
            new MaximizedWindowBounds(expectedX, expectedY, expectedWidth, expectedHeight),
            bounds);
    }
}
