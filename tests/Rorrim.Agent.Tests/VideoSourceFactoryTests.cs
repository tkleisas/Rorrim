using NSubstitute;
using Rorrim.Agent.Capture;
using Rorrim.Agent.Display;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Tests;

public class VideoSourceFactoryTests
{
    private static readonly DisplayAdapter Display = new("gpu", 0, "\\\\.\\DISPLAY1", 0, 0, 800, 600);

    private static RawFrame Frame(params byte[] firstBytes)
    {
        var bgra = new byte[800 * 600 * 4];
        firstBytes.CopyTo(bgra, 0);
        return new RawFrame(800, 600, 1, bgra);
    }

    private static IVideoSource MakeSource(string kind, params RawFrame?[] acquireResults)
    {
        var s = Substitute.For<IVideoSource>();
        s.Kind.Returns(kind);
        s.Width.Returns(800);
        s.Height.Returns(600);
        int i = 0;
        s.TryAcquire(Arg.Any<int>()).Returns(_ => i < acquireResults.Length ? acquireResults[i++] : null);
        return s;
    }

    [Fact]
    public void UsesDxgi_WhenProbeDeliversContent()
    {
        var dxgi = MakeSource("dxgi", Frame(1, 2, 3, 4));
        var source = VideoSourceFactory.Create(Display, _ => dxgi, _ => throw new Xunit.Sdk.XunitException("gdi must not be used"));

        Assert.Same(dxgi, source);
        Assert.Equal("dxgi", source.Kind);
    }

    [Fact]
    public void FallsBackToGdi_WhenDxgiProbeIsBlank()
    {
        var dxgi = MakeSource("dxgi", Frame(0, 0, 0, 0)); // all-zero frame
        var gdi = MakeSource("gdi");
        var source = VideoSourceFactory.Create(Display, _ => dxgi, _ => gdi);

        Assert.Same(gdi, source);
        dxgi.Received(1).Dispose();
    }

    [Fact]
    public void FallsBackToGdi_WhenDxgiNeverProducesAProbeFrame()
    {
        var dxgi = MakeSource("dxgi", (RawFrame?)null, (RawFrame?)null, (RawFrame?)null);
        var gdi = MakeSource("gdi");
        var source = VideoSourceFactory.Create(Display, _ => dxgi, _ => gdi, probeAttempts: 3);

        Assert.Same(gdi, source);
        dxgi.Received(3).TryAcquire(Arg.Any<int>());
        dxgi.Received(1).Dispose();
    }

    [Fact]
    public void FallsBackToGdi_WhenDxgiStartThrows()
    {
        var gdi = MakeSource("gdi");
        var source = VideoSourceFactory.Create(
            Display,
            _ => throw new InvalidOperationException("D3D11 device creation failed"),
            _ => gdi);

        Assert.Same(gdi, source);
        Assert.Equal("gdi", source.Kind);
    }
}

public class BlankFrameDetectionTests
{
    [Fact]
    public void AllZeroBuffer_IsBlank()
    {
        var frame = new RawFrame(4, 4, 0, new byte[4 * 4 * 4]);
        Assert.True(VideoSourceFactory.IsBlankFrame(frame));
    }

    [Fact]
    public void AnyNonZeroByte_IsNotBlank()
    {
        var bgra = new byte[4 * 4 * 4];
        bgra[7] = 1; // alpha of pixel 1
        var frame = new RawFrame(4, 4, 0, bgra);
        Assert.False(VideoSourceFactory.IsBlankFrame(frame));
    }
}
