using Rorrim.Agent.Pipeline;
using Rorrim.Codecs;
using Rorrim.Shared.Contracts;

namespace Rorrim.Agent.Pipeline;

/// <summary>
/// H.264 (OpenH264, screen-content-tuned) encoder adapter for <see cref="IVideoEncoder"/>. Captures
/// arrive as packed BGRA and are streamed out as Annex-B H.264. Cross-platform software codec —
/// the same bitstream decodes on any client platform OpenH264 supports (Windows/Linux/macOS).
/// </summary>
public sealed class OpenH264VideoEncoder : IVideoEncoder
{
    private H264FrameEncoder? _encoder;
    private int _width;
    private int _height;
    private int _framesEncoded;

    public int Width => _width;
    public int Height => _height;
    public bool IsEncoderRunning => _encoder is not null;
    public bool RequiresKeyFrame => false; // the encoder manages GOPs; new streams start with an IDR

    public void Initialize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Encoder dimensions must be positive.");
        if (_encoder is not null && _width == width && _height == height)
            return;
        _encoder?.Dispose();
        _encoder = new H264FrameEncoder(width, height);
        _width = width;
        _height = height;
        _framesEncoded = 0;
    }

    public EncodedFrame? Encode(RawFrame frame)
    {
        if (_encoder is null)
            return null;
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException(
                $"Frame ({frame.Width}x{frame.Height}) does not match encoder dimensions ({_width}x{_height}).",
                nameof(frame));

        var data = _encoder.Encode(frame.Bgra, frame.Width * 4);
        if (data is null)
            return null; // rate controller skipped the frame

        // Every fresh encoder stream starts with an IDR; later keyframes come from the GOP cycle.
        bool isKey = _framesEncoded == 0;
        _framesEncoded++;
        return new EncodedFrame(data, isKey, frame.TimestampUtf8Ticks, _width, _height, Array.Empty<byte>(), Codec.H264);
    }

    public void Dispose() => _encoder?.Dispose();
}
