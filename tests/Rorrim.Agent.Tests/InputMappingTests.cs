using Rorrim.Agent.Input;

namespace Rorrim.Agent.Tests;

public class InputMappingTests
{
    [Fact]
    public void SinglePrimaryDisplay_Center_MapsToMiddle()
    {
        var display = new DisplayRect(0, 0, 1920, 1080);
        var vs = new VirtualScreenBounds(0, 0, 1920, 1080);

        var (x, y) = InputInjector.MapNormalizedToVirtual(0.5, 0.5, display, vs);
        Assert.Equal(32768, x);
        Assert.Equal(32768, y);
    }

    [Fact]
    public void SecondMonitorRight_Center_MapsIntoRightHalf()
    {
        var display = new DisplayRect(1920, 0, 1920, 1080);
        var vs = new VirtualScreenBounds(0, 0, 3840, 1080);

        var (x, y) = InputInjector.MapNormalizedToVirtual(0.5, 0.5, display, vs);
        // Display center is at desktop x=2880 of 3840 -> 2880/3840 of 65535.
        Assert.Equal(49151, x);
        Assert.Equal(32768, y);
    }

    [Fact]
    public void MonitorLeftOfPrimary_NegativeOrigin_MapsCorrectly()
    {
        var display = new DisplayRect(-1920, 0, 1920, 1080);
        var vs = new VirtualScreenBounds(-1920, 0, 3840, 1080);

        var (x, _) = InputInjector.MapNormalizedToVirtual(0.5, 0.5, display, vs);
        // Display center is at desktop x=-960; relative to virtual origin -1920 -> 960/3840.
        Assert.Equal(16384, x);
    }

    [Fact]
    public void Corners_MapToBounds()
    {
        var display = new DisplayRect(0, 0, 1920, 1080);
        var vs = new VirtualScreenBounds(0, 0, 1920, 1080);

        var (x0, y0) = InputInjector.MapNormalizedToVirtual(0, 0, display, vs);
        var (x1, y1) = InputInjector.MapNormalizedToVirtual(1, 1, display, vs);
        Assert.Equal(0, x0);
        Assert.Equal(0, y0);
        Assert.Equal(65535, x1);
        Assert.Equal(65535, y1);
    }

    [Fact]
    public void OutOfRangeInput_Clamps()
    {
        var display = new DisplayRect(0, 0, 1920, 1080);
        var vs = new VirtualScreenBounds(0, 0, 1920, 1080);

        var (x, y) = InputInjector.MapNormalizedToVirtual(5, -3, display, vs);
        Assert.Equal(65535, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void DegenerateRects_ReturnZero()
    {
        var display = new DisplayRect(0, 0, 0, 0);
        var vs = new VirtualScreenBounds(0, 0, 0, 0);
        Assert.Equal((0, 0), InputInjector.MapNormalizedToVirtual(0.5, 0.5, display, vs));
    }
}
