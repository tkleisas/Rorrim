using System.Threading.Channels;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Broker;

/// <summary>
/// A bounded, single-producer-single-consumer channel of encoded frames, decoupling the capture
/// encode loop from the gRPC writer. When the channel is full the producer blocks, providing
/// natural backpressure instead of unbounded memory growth.
/// </summary>
public sealed class FrameChannel : IDisposable
{
    private readonly Channel<EncodedFrame> _channel;

    public FrameChannel(int capacity = 8)
    {
        _channel = Channel.CreateBounded<EncodedFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public ValueTask WriteAsync(EncodedFrame frame, CancellationToken ct) =>
        _channel.Writer.WriteAsync(frame, ct);

    public IAsyncEnumerable<EncodedFrame> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => _channel.Writer.TryComplete();
}
