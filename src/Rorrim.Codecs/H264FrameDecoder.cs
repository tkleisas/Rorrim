using H264Sharp;

namespace Rorrim.Codecs;

/// <summary>
/// Stateful H.264 decoder over OpenH264 (via H264Sharp). Feed Annex-B byte streams; receive packed
/// BGRA frames. H.264 decoders are inherently stateful (reference frames), so an instance must be
/// kept for the whole stream and recreated when the stream restarts (new session / display switch).
/// The decoder self-heals: on bitstream corruption the state is reset, and the next IDR restores
/// the picture.
/// </summary>
public sealed class H264FrameDecoder : IDisposable
{
    private H264Decoder _decoder;
    private int _width;
    private int _height;

    private static bool? _nativeSupported;

    /// <summary>
    /// True when the OpenH264 native library loads on this machine. Platforms without a shipped
    /// native (e.g. macOS, which H264Sharp does not cover) return false, letting callers fall back
    /// to JPEG. The result is probed once and cached.
    /// </summary>
    public static bool IsSupported()
    {
        if (_nativeSupported.HasValue)
            return _nativeSupported.Value;
        try
        {
            using var probe = new H264FrameDecoder();
            _nativeSupported = true;
        }
        catch
        {
            _nativeSupported = false;
        }
        return _nativeSupported.Value;
    }

    public H264FrameDecoder()
    {
        _decoder = new H264Decoder();
        int rc = _decoder.Initialize();
        if (rc != 0)
        {
            _decoder.Dispose();
            throw new InvalidOperationException($"OpenH264 decoder initialization failed: {rc}");
        }
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Decodes one Annex-B frame. Returns true when a picture is available (H.264 decoders may
    /// buffer frames); <paramref name="bgra"/> then holds packed BGRA with the given stride.
    /// Returns false when no picture is ready yet.
    /// </summary>
    public bool Decode(byte[] annexB, ref byte[]? bgra)
    {
        var state = DecodingState.dsErrorFree;
        var outPlane = new YUVImagePointer();
        bool ok = _decoder.Decode(annexB, 0, annexB.Length, noDelay: true, out state, out outPlane);

        // Reorder buffering or parameter-set-only chunks yield no picture — that is normal.
        if (!ok || outPlane.Y == IntPtr.Zero)
            return false;

        _width = outPlane.Width;
        _height = outPlane.Height;
        int stride = _width * 4;
        var converted = Yuv.I420ToBgra(outPlane.Y, outPlane.U, outPlane.V,
            _width, _height, outPlane.StrideY, outPlane.StrideUV, stride);
        bgra = converted;
        return true;
    }

    /// <summary>Drops all reference frames; the next IDR starts a clean picture.</summary>
    public void Reset()
    {
        try
        {
            _decoder.Dispose();
        }
        catch
        {
            // The native decoder may already be unusable after corruption.
        }
        var fresh = new H264Decoder();
        if (fresh.Initialize() == 0)
            _decoder = fresh;
    }

    public void Dispose() => _decoder.Dispose();
}
