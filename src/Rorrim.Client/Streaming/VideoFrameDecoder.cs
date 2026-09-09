using Rorrim.Codecs;
using Rorrim.Shared.Contracts;

namespace Rorrim.Client.Streaming;

/// <summary>
/// Routes incoming frames to the right decoder: H.264 (OpenH264, stateful) or JPEG (SkiaSharp).
/// One instance per gRPC session — the H.264 decoder's reference-frame state must match the stream.
/// </summary>
public sealed class VideoFrameDecoder : IDisposable
{
    private H264FrameDecoder? _h264;

    public DecodedFrame Decode(Frame frame)
    {
        if (frame.Codec == Codec.H264)
        {
            _h264 ??= new H264FrameDecoder();
            byte[]? bgra = null;
            if (_h264.Decode(frame.Data.ToByteArray(), ref bgra) && bgra is not null)
                return new DecodedFrame(_h264.Width, _h264.Height, bgra);

            // No picture ready yet (decoder priming on parameter-set NALs / reorder buffer):
            // yield a zero-size frame the UI skips.
            return new DecodedFrame(0, 0, Array.Empty<byte>());
        }

        return FrameDecoder.Decode(frame);
    }

    public void Dispose() => _h264?.Dispose();
}
