using NSubstitute;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Tests;

public class StreamControllerTests
{
    private static RawFrame MakeFrame(int tick = 0) => new(1920, 1080, tick, new byte[1920 * 1080 * 4]);

    private static (IVideoSource Source, IVideoEncoder Encoder, StreamController Controller) Setup(
        IVideoSource? source = null, IVideoEncoder? encoder = null,
        StreamControllerOptions? options = null)
    {
        var s = source ?? Substitute.For<IVideoSource>();
        var e = encoder ?? Substitute.For<IVideoEncoder>();
        s.Width.Returns(1920);
        s.Height.Returns(1080);
        e.Width.Returns(1920);
        e.Height.Returns(1080);
        return (s, e, new StreamController(s, e, options));
    }

    [Fact]
    public async Task Produce_Throws_WhenSourceNotStarted()
    {
        var (s, _, controller) = Setup();
        s.IsStarted.Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => { await foreach (var _ in controller.Produce().WithCancellation(TestToken(50))) { } });
    }

    [Fact]
    public async Task Produce_YieldsEncodedFrame_AndPassesRawFrame()
    {
        var (s, e, controller) = Setup();
        s.IsStarted.Returns(true);
        s.IsDesktopLocked.Returns(false);
        s.TryAcquire(Arg.Any<int>()).Returns(MakeFrame(100), (RawFrame?)null);
        e.RequiresKeyFrame.Returns(false);
        e.Encode(Arg.Any<RawFrame>()).Returns(x => new EncodedFrame(new byte[] { 1, 2, 3 }, true, 100, 1920, 1080, Array.Empty<byte>()));

        var results = new List<EncodedFrame>();
        await foreach (var f in controller.Produce().WithCancellation(TestToken(200)))
        {
            results.Add(f);
            e.Encode(Arg.Any<RawFrame>()).Returns((EncodedFrame?)null);
            if (results.Count >= 1) break;
        }

        Assert.Single(results);
        e.Received(1).Initialize(1920, 1080);
        e.Received(1).Encode(Arg.Is<RawFrame>(rf => rf.Width == 1920 && rf.TimestampUtf8Ticks == 100));
    }

    [Fact]
    public async Task Produce_CoalescesToNewestFrame()
    {
        var (s, e, controller) = Setup();
        s.IsStarted.Returns(true);
        s.IsDesktopLocked.Returns(false);
        s.CoalescesInternally.Returns(true); // DXGI-like source can report "nothing newer"
        // Two frames queued; should coalesce to the second and encode only it.
        s.TryAcquire(Arg.Any<int>()).Returns(
            MakeFrame(1),
            MakeFrame(2),
            (RawFrame?)null);
        e.RequiresKeyFrame.Returns(false);
        e.Encode(Arg.Any<RawFrame>()).Returns(_ => new EncodedFrame(new byte[] { 9 }, true, 0, 1920, 1080, Array.Empty<byte>()));

        var results = new List<EncodedFrame>();
        await foreach (var f in controller.Produce().WithCancellation(TestToken(200)))
        {
            results.Add(f);
            if (results.Count >= 1) break;
        }

        Assert.Single(results);
        e.Received(1).Encode(Arg.Is<RawFrame>(rf => rf.TimestampUtf8Ticks == 2));
        e.DidNotReceive().Encode(Arg.Is<RawFrame>(rf => rf.TimestampUtf8Ticks == 1));
    }

    [Fact]
    public async Task Produce_StopsWhenDesktopLocked()
    {
        var (s, e, controller) = Setup();
        s.IsStarted.Returns(true);
        s.IsDesktopLocked.Returns(true);

        var results = new List<EncodedFrame>();
        await foreach (var f in controller.Produce().WithCancellation(TestToken(50)))
        {
            results.Add(f);
        }

        Assert.Empty(results);
        e.DidNotReceive().Encode(Arg.Any<RawFrame>());
    }

    [Fact]
    public async Task Produce_InsertsKeyFrame_OnFirstFrame()
    {
        var (s, e, controller) = Setup();
        s.IsStarted.Returns(true);
        s.IsDesktopLocked.Returns(false);
        s.TryAcquire(Arg.Any<int>()).Returns(MakeFrame(100), (RawFrame?)null);
        e.RequiresKeyFrame.Returns(false);
        e.Encode(Arg.Any<RawFrame>())
            .Returns(_ => new EncodedFrame(new byte[] { 1 }, true, 100, 1920, 1080, Array.Empty<byte>()));

        var results = new List<EncodedFrame>();
        await foreach (var f in controller.Produce().WithCancellation(TestToken(200)))
        {
            results.Add(f);
            if (results.Count >= 1) break;
        }

        Assert.Single(results);
        Assert.True(results[0].IsKeyFrame);
    }

    [Fact]
    public async Task Produce_SkipsWhenEncoderReturnsNull()
    {
        var (s, e, controller) = Setup();
        s.IsStarted.Returns(true);
        s.IsDesktopLocked.Returns(false);
        s.TryAcquire(Arg.Any<int>()).Returns(MakeFrame(1), (RawFrame?)null);
        e.RequiresKeyFrame.Returns(false);
        e.Encode(Arg.Any<RawFrame>()).Returns((EncodedFrame?)null); // never ready

        // Iterate a bounded number of times; returns null repeatedly so nothing is produced.
        var results = new List<EncodedFrame>();
        var cts = new CancellationTokenSource(100);
        int iterations = 0;
        await foreach (var f in controller.Produce().WithCancellation(cts.Token))
        {
            results.Add(f);
            if (++iterations >= 5) break;
        }

        Assert.Empty(results);
    }

    private static CancellationToken TestToken(int ms)
    {
        var cts = new CancellationTokenSource(ms);
        return cts.Token;
    }
}
