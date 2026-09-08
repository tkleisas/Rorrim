using NSubstitute;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Tests;

public class JpegVideoEncoderTests
{
    [Fact]
    public void Constructor_SetsDimensions()
    {
        using var encoder = new JpegVideoEncoder();
        encoder.Initialize(640, 480);
        Assert.Equal(640, encoder.Width);
        Assert.Equal(480, encoder.Height);
        Assert.True(encoder.IsEncoderRunning);
    }

    [Fact]
    public void Encode_ProducesValidJpegBytes()
    {
        using var encoder = new JpegVideoEncoder();
        encoder.Initialize(2, 2);
        // 2x2 BGRA (top-left blue, rest red-ish) — all 0xFF alpha.
        var px = new byte[]
        {
            255,0,0,255,  0,255,0,255,
            0,0,255,255,  255,255,255,255
        };
        var frame = new RawFrame(2, 2, 1, px);
        var result = encoder.Encode(frame);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Value.Data);
        // JPEG magic bytes FFD8.
        Assert.Equal(0xFF, result.Value.Data[0]);
        Assert.Equal(0xD8, result.Value.Data[1]);
        Assert.True(result.Value.IsKeyFrame);
        Assert.Equal(2, result.Value.Width);
        Assert.Equal(2, result.Value.Height);
    }

    [Fact]
    public void Encode_ReturnsNull_BeforeInitialized()
    {
        using var encoder = new JpegVideoEncoder();
        var frame = new RawFrame(2, 2, 1, new byte[16]);
        Assert.Null(encoder.Encode(frame));
        Assert.False(encoder.IsEncoderRunning);
    }

    [Fact]
    public void Encode_Throws_OnDimensionMismatch()
    {
        using var encoder = new JpegVideoEncoder();
        encoder.Initialize(2, 2);
        var badFrame = new RawFrame(4, 4, 1, new byte[4 * 4 * 4]);
        Assert.Throws<ArgumentException>(() => encoder.Encode(badFrame));
    }
}
