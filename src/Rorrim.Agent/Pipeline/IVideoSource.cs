namespace Rorrim.Agent.Pipeline;

/// <summary>
/// A single raw frame captured from a display, in tightly-packed B8G8R8A8 (BGRA) order.
/// </summary>
public readonly record struct RawFrame(
    int Width,
    int Height,
    long TimestampUtf8Ticks,
    byte[] Bgra);

/// <summary>
/// An encoded video frame to be streamed to the client.
/// </summary>
public readonly record struct EncodedFrame(
    byte[] Data,
    bool IsKeyFrame,
    long TimestampUs,
    int Width,
    int Height,
    byte[] CodecConfig,
    Shared.Contracts.Codec Codec = Shared.Contracts.Codec.Jpeg);

/// <summary>
/// Events that can interrupt/suspend the capture stream.
/// </summary>
public enum StreamStatusKind
{
    Running,
    DesktopLocked,
    DisplayLost,
    Disconnected
}

/// <summary>
/// Source of raw frames. Implemented by the DXGI Desktop Duplication adapter in production,
/// but by fakes in tests.
/// </summary>
public interface IVideoSource : IDisposable
{
    /// <summary>Short source identifier for diagnostics ("gdi", "dxgi", ...).</summary>
    string Kind { get; }

    int Width { get; }
    int Height { get; }
    bool IsDesktopLocked { get; }
    bool IsStarted { get; }

    /// <summary>Prepares the underlying capture resources (device, surfaces). No-op if already started.</summary>
    void Start();

    /// <summary>
    /// True when the source can tell whether a newer frame exists by returning null from
    /// <see cref="TryAcquire"/> with a zero timeout (DXGI Desktop Duplication semantics).
    /// When false (e.g. a polling GDI capture that produces a frame on every call), the controller
    /// must not coalesce by peeking, or it would spin forever.
    /// </summary>
    bool CoalescesInternally { get; }

    /// <summary>
    /// Attempts to acquire the most recently changed frame within <paramref name="timeoutMs"/>.
    /// Implementations coalesce: they should return the newest frame already retained.
    /// Returns the frame, or null if nothing new was produced within the timeout.
    /// </summary>
    RawFrame? TryAcquire(int timeoutMs);
}

/// <summary>
/// Encodes raw frames to H.264. Implemented by the Media Foundation adapter in production,
/// by fakes in tests.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool IsEncoderRunning { get; }
    bool RequiresKeyFrame { get; }

    /// <summary>
    /// Configures the encoder for the given stream dimensions.
    /// </summary>
    void Initialize(int width, int height);

    /// <summary>
    /// Codes the given raw frame. Returns the encoded output, or null if the encoder was not
    /// ready / produced no output for this frame.
    /// </summary>
    EncodedFrame? Encode(RawFrame frame);
}
