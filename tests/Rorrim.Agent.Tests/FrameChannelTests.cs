using Rorrim.Agent.Broker;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Tests;

public class FrameChannelTests
{
    private static EncodedFrame MakeFrame(byte v) => new(new[] { v }, true, v, 1920, 1080, Array.Empty<byte>());

    [Fact]
    public async Task WriteAndRead_DeliversFramesInOrder()
    {
        using var ch = new FrameChannel<EncodedFrame>(capacity: 4);
        await ch.WriteAsync(MakeFrame(1), CancellationToken.None);
        await ch.WriteAsync(MakeFrame(2), CancellationToken.None);
        ch.Complete();

        var got = new List<EncodedFrame>();
        await foreach (var f in ch.ReadAllAsync(CancellationToken.None))
            got.Add(f);

        Assert.Equal(2, got.Count);
        Assert.Equal(1, got[0].Data[0]);
        Assert.Equal(2, got[1].Data[0]);
    }

    [Fact]
    public async Task ReadCompletes_AfterWriterCompletes()
    {
        using var ch = new FrameChannel<EncodedFrame>(capacity: 2);
        ch.Complete();
        var count = 0;
        var cts = new CancellationTokenSource(1000);
        await foreach (var _ in ch.ReadAllAsync(cts.Token))
            count++;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Backpressure_BlocksWriter_WhenFull()
    {
        using var ch = new FrameChannel<EncodedFrame>(capacity: 1);
        // Fill the one-slot channel.
        await ch.WriteAsync(MakeFrame(1), CancellationToken.None);

        // A second write to a full channel should not complete until a reader drains.
        var task = ch.WriteAsync(MakeFrame(2), CancellationToken.None).AsTask();
        var completed = await Task.WhenAny(task, Task.Delay(200));
        Assert.NotSame(task, completed); // still pending (blocked)

        // Drain and it should complete.
        await using (var r = ch.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            await r.MoveNextAsync();
        }
        await task;
    }
}
