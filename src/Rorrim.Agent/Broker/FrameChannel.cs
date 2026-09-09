using System.Threading.Channels;

namespace Rorrim.Agent.Broker;

/// <summary>
/// A bounded channel decoupling the capture/encode loop from the gRPC writer. When the channel is
/// full the writer blocks, providing natural backpressure instead of unbounded memory growth.
/// Supports multiple concurrent writers (e.g. frames and heartbeats) — writes are serialized by the
/// channel — but expects a single reader.
/// </summary>
public sealed class FrameChannel<T> : IDisposable
{
    private readonly Channel<T> _channel;

    public FrameChannel(int capacity = 8)
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false // frames and heartbeats share the channel
        });
    }

    public ValueTask WriteAsync(T item, CancellationToken ct) =>
        _channel.Writer.WriteAsync(item, ct);

    /// <summary>Completes with true when an item is available to read; false when the channel is closed.</summary>
    public Task<bool> WaitToReadAsync(CancellationToken ct) =>
        _channel.Reader.WaitToReadAsync(ct).AsTask();

    public bool TryRead(out T item) => _channel.Reader.TryRead(out item!);

    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => _channel.Writer.TryComplete();
}
