using Rorrim.Client.Streaming;
using Rorrim.Shared.Contracts;
using SkiaSharp;

namespace Rorrim.Client.Tests;

public class FrameDecoderTests
{
    private static Frame JpegFrame(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        var data = img.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        return new Frame { Data = Google.Protobuf.ByteString.CopyFrom(data), Width = (uint)w, Height = (uint)h };
    }

    [Fact]
    public void Decode_ReturnsExpectedDimensions()
    {
        var frame = JpegFrame(64, 48, SKColors.Red);
        var decoded = FrameDecoder.Decode(frame);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(48, decoded.Height);
        Assert.Equal(64 * 48 * 4, decoded.Bgra.Length);
    }

    [Fact]
    public void Decode_ProducesCorrectColor()
    {
        var frame = JpegFrame(32, 32, SKColors.Blue);
        var decoded = FrameDecoder.Decode(frame);
        Assert.Equal(32, decoded.Width);
        // Decoded output is BGRA; center pixel should be blue (B dominant, R low).
        int cx = (decoded.Height / 2) * decoded.Width * 4 + (decoded.Width / 2) * 4;
        Assert.True(decoded.Bgra[cx] > 200, "blue channel should be dominant");   // B
        Assert.True(decoded.Bgra[cx + 2] < 100, "red channel should be low");       // R
    }

    [Fact]
    public void Decode_Throws_OnGarbageBytes()
    {
        var frame = new Frame { Data = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 }) };
        Assert.Throws<InvalidOperationException>(() => FrameDecoder.Decode(frame));
    }
}

public class InputEncoderTests
{
    [Fact]
    public void Move_NormalizesCoordinates()
    {
        var msg = InputEncoder.Move(0.5, 0.25);
        Assert.Equal(0.5, msg.Move.X, 3);
        Assert.Equal(0.25, msg.Move.Y, 3);
    }

    [Fact]
    public void ToNorm_MapsViewToNormalized()
    {
        Assert.Equal(0.5, InputEncoder.ToNorm(1920, 3840), 3);
        Assert.Equal(1.0, InputEncoder.ToNorm(9999, 1920), 3);
        Assert.Equal(0.0, InputEncoder.ToNorm(-5, 1920), 3);
    }

    [Fact]
    public void ButtonDown_SetsFlagAndCoords()
    {
        var msg = InputEncoder.ButtonDown(1, 0.3, 0.4);
        Assert.True(msg.Button.Down);
        Assert.Equal(1u, msg.Button.Button);
        Assert.Equal(0.3, msg.Button.X, 3);
    }

    [Fact]
    public void Key_MapsFlags()
    {
        var msg = InputEncoder.Key(65, 30, down: true, extended: false);
        Assert.True(msg.Key.Down);
        Assert.False(msg.Key.Extended);
        Assert.Equal(65u, msg.Key.VirtualKey);
    }

    [Fact]
    public void Scroll_BuildsDelta()
    {
        var msg = InputEncoder.Scroll(-2.0);
        Assert.Equal(-2.0, msg.Scroll.Delta, 3);
    }
}
