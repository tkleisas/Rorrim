using Rorrim.Shared.Contracts;
using SkiaSharp;

namespace Rorrim.Client.Streaming;

/// <summary>
/// Decodes an encoded video <see cref="Frame"/> (currently JPEG) into packed BGRA pixels suitable for
/// Avalonia's Bgra8888 WriteableBitmap. Normalizes decode output so the UI never deals with channel
/// ordering.
/// </summary>
public static class FrameDecoder
{
    public static DecodedFrame Decode(Frame frame)
    {
        var bytes = frame.Data.ToByteArray();
        using var image = SKImage.FromEncodedData(bytes);
        if (image is null)
            throw new InvalidOperationException("Failed to decode frame payload.");
        using var decoded = SKBitmap.FromImage(image);
        if (decoded is null)
            throw new InvalidOperationException("Failed to decode frame into bitmap.");

        int w = decoded.Width, h = decoded.Height;
        if (decoded.ColorType == SKColorType.Bgra8888)
            return new DecodedFrame(w, h, decoded.Bytes);

        // Normalize non-BGRA decodes to Bgra8888. (SKPixmap/PeekPixels are unreliable here.)
        using var converted = decoded.Copy(SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Failed to convert frame to BGRA.");
        return new DecodedFrame(w, h, converted.Bytes);
    }
}

public readonly record struct DecodedFrame(int Width, int Height, byte[] Bgra);
