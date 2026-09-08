using NSubstitute;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Tests;

public class StreamControllerCoalescingTests
{
    private static RawFrame MakeFrame(int tick = 0) => new(1920, 1080, tick, new byte[1920 * 1080 * 4]);

    [Fact]
    public async Task Produce_DoesNotCoalesce_WhenSourceDoesNotCoalesceInternally()
    {
        // Simulates a polling source (e.g. GDI) that returns a fresh frame on every call. The
        // controller must encode the frame directly instead of spinning a peek loop that never
        // ends. Here the source returns the SAME_frame repeatedly — if the controller coalesces it
        // would hang; if it doesn't, it yields once and we break.
        var s = Substitute.For<IVideoSource>();
        var e = Substitute.For<IVideoEncoder>();
        s.Width.Returns(1920);
        s.Height.Returns(1080);
        e.Width.Returns(1920);
        e.Height.Returns(1080);
        s.IsStarted.Returns(true);
        s.IsDesktopLocked.Returns(false);
        s.CoalescesInternally.Returns(false); // GDI-style
        s.TryAcquire(Arg.Any<int>()).Returns(MakeFrame(7), MakeFrame(7), MakeFrame(7));
        e.RequiresKeyFrame.Returns(false);
        e.Encode(Arg.Any<RawFrame>())
            .Returns(_ => new EncodedFrame(new byte[] { 1 }, true, 0, 1920, 1080, Array.Empty<byte>()));

        var controller = new StreamController(s, e);

        using var cts = new CancellationTokenSource(2000);
        var results = new List<EncodedFrame>();
        await foreach (var f in controller.Produce().WithCancellation(cts.Token))
        {
            results.Add(f);
            if (results.Count >= 1) break; // if the controller coalesced, this line never reached
        }

        Assert.Single(results);
        // The controller must pass the frame straight through without peeking again.
        e.Received(1).Encode(Arg.Is<RawFrame>(rf => rf.TimestampUtf8Ticks == 7));
    }
}
