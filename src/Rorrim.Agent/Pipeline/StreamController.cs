namespace Rorrim.Agent.Pipeline;

/// <summary>
/// Orchestrates the capture loop: drains frames from an <see cref="IVideoSource"/>, hands them to an
/// <see cref="IVideoEncoder"/>, and yields <see cref="EncodedFrame"/>s at a target rate. This is the
/// testable core — the DXGI capturing and Media Foundation encoding are thin adapters behind the
/// interfaces defined in this namespace.
/// </summary>
public sealed class StreamController : IDisposable
{
    private readonly IVideoSource _source;
    private readonly IVideoEncoder _encoder;
    private readonly StreamControllerOptions _options;

    private int _framesSinceKeyFrame;

    public StreamStatusKind Status { get; private set; } = StreamStatusKind.Running;

    public StreamController(IVideoSource source, IVideoEncoder encoder, StreamControllerOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _options = options ?? StreamControllerOptions.Default;
    }

    public async IAsyncEnumerable<EncodedFrame> Produce([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            if (!_source.IsStarted)
                throw new InvalidOperationException("The video source has not been started.");

            // Read the source's geometry and configure the encoder once.
            _encoder.Initialize(_source.Width, _source.Height);

            var pacingStopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                // Desktop locked (secure screen): pause the stream but keep polling so it resumes
                // automatically when the user unlocks, instead of tearing the session down.
                // TryAcquire during the pause gives the source a chance to recover (DXGI rebuilds
                // its duplication after unlock / device loss).
                if (_source.IsDesktopLocked)
                {
                    Status = StreamStatusKind.DesktopLocked;
                    _ = _source.TryAcquire(_options.LockPollIntervalMs);
                    if (await DelayOrCanceled(_options.LockPollIntervalMs, ct)) yield break;
                    continue;
                }

                RawFrame? frame = _source.TryAcquire(_options.AcquireTimeoutMs);
                if (frame is null)
                {
                    if (await DelayOrCanceled(_options.IdleDelayMs, ct)) yield break;
                    continue;
                }

                // Coalesce: if a fresher frame is immediately available, keep only the latest.
                // Only safe when the source can report "nothing new" (DXGI); polling sources (GDI)
                // would spin here, so we encode the frame as-is.
                RawFrame outFrame = frame.Value;
                if (_source.CoalescesInternally)
                {
                    RawFrame? latest = frame;
                    while ((frame = _source.TryAcquire(0)) is not null)
                    {
                        latest = frame;
                    }
                    outFrame = latest!.Value;
                }

                bool needKeyFrame = _encoder.RequiresKeyFrame
                    || _framesSinceKeyFrame >= _options.KeyFrameIntervalFrames
                    || _framesSinceKeyFrame == 0;

                if (needKeyFrame) _framesSinceKeyFrame = 0;

                EncodedFrame? encoded = _encoder.Encode(outFrame);
                if (encoded is null)
                {
                    if (await DelayOrCanceled(_options.IdleDelayMs, ct)) yield break;
                    continue;
                }

                _framesSinceKeyFrame++;
                Status = StreamStatusKind.Running;
                yield return encoded.Value;

                // Target frame pacing: delay the remainder of the frame interval when the
                // capture+encode cycle finished early (0 disables pacing).
                if (_options.TargetFrameIntervalMs > 0)
                {
                    int elapsed = (int)pacingStopwatch.ElapsedMilliseconds;
                    pacingStopwatch.Restart();
                    int wait = _options.TargetFrameIntervalMs - elapsed;
                    if (wait > 0 && await DelayOrCanceled(wait, ct)) yield break;
                }
            }
        }
        finally
        {
            // No-op: disposal is owned by <see cref="Dispose"/> so a client can enumerate once.
        }
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _source?.Dispose();
    }

    /// <summary>
    /// Delays <paramref name="ms"/> miliseconds. Returns true if cancellation was requested so the
    /// caller can break cleanly, returns false when the delay completed normally.
    /// </summary>
    private static async Task<bool> DelayOrCanceled(int ms, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ms, ct).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}

public sealed record StreamControllerOptions
{
    public static StreamControllerOptions Default { get; } = new();

    /// <summary>How long to wait for a new frame from the source.</summary>
    public int AcquireTimeoutMs { get; init; } = 16;

    /// <summary>How long to sleep when no frame was produced/skipped.</summary>
    public int IdleDelayMs { get; init; } = 4;

    /// <summary>How long to sleep between lock-state polls while the desktop is locked.</summary>
    public int LockPollIntervalMs { get; init; } = 1000;

    /// <summary>Minimum interval between frames in ms (target FPS cap). 0 disables pacing.</summary>
    public int TargetFrameIntervalMs { get; init; } = 0;

    /// <summary>Force a keyframe every N frames.</summary>
    public int KeyFrameIntervalFrames { get; init; } = 120;
}
