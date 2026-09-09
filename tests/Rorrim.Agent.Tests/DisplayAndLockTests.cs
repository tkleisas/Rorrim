using NSubstitute;
using Rorrim.Agent.Display;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Tests;

public class DisplayResolveTests
{
    private static readonly DisplayAdapter D1 = new("gpu1", 0, "\\\\.\\DISPLAY1", 0, 0, 1920, 1080);
    private static readonly DisplayAdapter D2 = new("gpu1", 1, "\\\\.\\DISPLAY2", 1920, 0, 2560, 1440);
    private static readonly DisplayAdapter[] TwoDisplays = [D1, D2];

    [Fact]
    public void EmptyOrNullId_ResolvesFirstDisplay() =>
        Assert.Equal(D1.DeviceName, DisplayEnumerator.Resolve(TwoDisplays, null)!.Value.DeviceName);

    [Fact]
    public void Index_ResolvesPositionally() =>
        Assert.Equal(D2.DeviceName, DisplayEnumerator.Resolve(TwoDisplays, "1")!.Value.DeviceName);

    [Fact]
    public void DeviceName_ResolvesCaseInsensitively() =>
        Assert.Equal(D2.DeviceName, DisplayEnumerator.Resolve(TwoDisplays, "\\\\.\\display2")!.Value.DeviceName);

    [Fact]
    public void UnknownId_FallsBackToFirstDisplay() =>
        Assert.Equal(D1.DeviceName, DisplayEnumerator.Resolve(TwoDisplays, "\\\\.\\DISPLAY9")!.Value.DeviceName);

    [Fact]
    public void OutOfRangeIndex_FallsBackToFirstDisplay() =>
        Assert.Equal(D1.DeviceName, DisplayEnumerator.Resolve(TwoDisplays, "7")!.Value.DeviceName);

    [Fact]
    public void EmptyList_ReturnsNull() =>
        Assert.Null(DisplayEnumerator.Resolve([], "0"));

    [Fact]
    public void OriginDisplay_IsPrimary() =>
        Assert.True(D1.IsPrimary);
}

public class StreamControllerLockResumeTests
{
    [Fact]
    public async Task Produce_PausesWhileLocked_AndResumesAfterUnlock()
    {
        var s = Substitute.For<IVideoSource>();
        var e = Substitute.For<IVideoEncoder>();
        s.Width.Returns(1920);
        s.Height.Returns(1080);
        e.Width.Returns(1920);
        e.Height.Returns(1080);
        s.IsStarted.Returns(true);

        int lockChecks = 0;
        s.IsDesktopLocked.Returns(ci =>
        {
            lockChecks++;
            return lockChecks <= 2; // locked for the first two polls
        });
        s.CoalescesInternally.Returns(false);
        // Two recovery probes run while locked (both yield nothing); the acquire after unlock
        // delivers the frame.
        s.TryAcquire(Arg.Any<int>())
            .Returns(
                (RawFrame?)null,
                (RawFrame?)null,
                new RawFrame(1920, 1080, 1, new byte[1920 * 1080 * 4]),
                (RawFrame?)null);
        e.RequiresKeyFrame.Returns(false);
        e.Encode(Arg.Any<RawFrame>())
            .Returns(_ => new EncodedFrame(new byte[] { 1 }, true, 1, 1920, 1080, Array.Empty<byte>()));

        var controller = new StreamController(s, e, new StreamControllerOptions { LockPollIntervalMs = 10 });

        var results = new List<EncodedFrame>();
        using var cts = new CancellationTokenSource(1000);
        await foreach (var f in controller.Produce().WithCancellation(cts.Token))
        {
            results.Add(f);
            break;
        }

        Assert.Single(results);
        Assert.Equal(StreamStatusKind.Running, controller.Status);
    }
}
