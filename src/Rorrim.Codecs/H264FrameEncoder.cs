using System.Runtime.InteropServices;
using H264Sharp;

namespace Rorrim.Codecs;

/// <summary>
/// Stateful H.264 encoder over OpenH264 (via H264Sharp). Feed packed BGRA frames; receive Annex-B
/// H.264 byte streams. Screen-content-tuned settings; every created encoder starts with an IDR, and
/// <see cref="ForceKeyFrame"/> can request one mid-stream (used when a new viewer joins).
/// Instances are single-threaded (one per capture loop).
/// </summary>
public sealed class H264FrameEncoder : IDisposable
{
    private readonly H264Encoder _encoder;
    private byte[]? _i420;
    private GCHandle _pin;
    private int _width;
    private int _height;

    public H264FrameEncoder(int width, int height, int bitrateBitsPerSecond = 4_000_000, int fps = 30)
    {
        _encoder = new H264Encoder();
        int rc = _encoder.Initialize(width, height, bitrateBitsPerSecond, fps, ConfigType.ScreenCaptureBasic);
        if (rc != 0)
        {
            _encoder.Dispose();
            throw new InvalidOperationException($"OpenH264 encoder initialization failed: {rc}");
        }
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Encodes one BGRA frame. Returns the concatenated Annex-B stream, or null when the rate
    /// controller skipped this frame (nothing changed / budget exceeded).
    /// </summary>
    public byte[]? Encode(byte[] bgra, int stride)
    {
        if (bgra.Length < stride * _height)
            throw new ArgumentException("Frame is smaller than the encoder dimensions.", nameof(bgra));

        int i420Length = _width * _height + 2 * (_width / 2) * (_height / 2);
        if (_i420 is null || _i420.Length != i420Length)
        {
            FreePin();
            _i420 = new byte[i420Length];
            _pin = GCHandle.Alloc(_i420, GCHandleType.Pinned);
        }

        Yuv.BgraToI420(bgra, _width, _height, stride, _i420);

        IntPtr y = _pin.AddrOfPinnedObject();
        var plane = new YUVImagePointer(
            y,
            y + _width * _height,
            y + _width * _height + (_width / 2) * (_height / 2),
            _width, _height, _width, _width / 2);

        if (!_encoder.Encode(plane, out EncodedData[]? layers) || layers is null)
            return null;

        byte[]? combined = null;
        int total = 0;
        foreach (var layer in layers)
            total += layer.Length;
        if (total == 0)
            return null;

        combined = new byte[total];
        int offset = 0;
        foreach (var layer in layers)
        {
            layer.GetBytes().AsSpan().CopyTo(combined.AsSpan(offset));
            offset += layer.Length;
        }
        return combined;
    }

    public void ForceKeyFrame() => _encoder.ForceIntraFrame();

    private void FreePin()
    {
        if (_pin.IsAllocated)
            _pin.Free();
    }

    public void Dispose()
    {
        FreePin();
        _encoder.Dispose();
    }
}
