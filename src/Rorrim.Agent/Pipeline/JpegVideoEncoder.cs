using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Pipeline;

/// <summary>
/// A pure-managed, cross-platform-safe JPEG encoder adapter for <see cref="IVideoEncoder"/>.
/// Used as an interim codec to validate the capture/web transport end-to-end before swapping in a
/// Media Foundation H.264 encoder. Each frame is encoded independently (every frame is a key frame).
/// </summary>
public sealed class JpegVideoEncoder : IVideoEncoder
{
    private int _width;
    private int _height;
    private bool _initialized;

    public int Width => _width;
    public int Height => _height;
    public bool IsEncoderRunning => _initialized;
    public bool RequiresKeyFrame => true;

    public void Initialize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Encoder dimensions must be positive.");
        _width = width;
        _height = height;
        _initialized = true;
    }

    public EncodedFrame? Encode(RawFrame frame)
    {
        if (!_initialized)
            return null;
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException(
                $"Frame ({frame.Width}x{frame.Height}) does not match encoder dimensions ({_width}x{_height}).",
                nameof(frame));

        using var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, _width, _height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* dst = (byte*)data.Scan0;
                int stride = _width * 4;
                fixed (byte* src = frame.Bgra)
                {
                    for (int y = 0; y < _height; y++)
                        Buffer.MemoryCopy(src + (y * stride), dst + (y * data.Stride), stride, stride);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return new EncodedFrame(ms.ToArray(), true, frame.TimestampUtf8Ticks, _width, _height, Array.Empty<byte>());
    }

    public void Dispose()
    {
        _initialized = false;
        _width = 0;
        _height = 0;
    }
}
