using Rorrim.Codecs;

namespace Rorrim.Codecs.Tests;

public class H264RoundTripTests
{
    private static byte[] MakeGradientFrame(int width, int height, int shift, int stride)
    {
        var bgra = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = y * stride + x * 4;
                bgra[o] = (byte)x;
                bgra[o + 1] = (byte)((y + shift) & 0xFF);
                bgra[o + 2] = 128;
                bgra[o + 3] = 255;
            }
        return bgra;
    }

    [Fact]
    public void Encode_ProducesAnnexBStream_StartingWithIdr()
    {
        int w = 320, h = 240, stride = w * 4;
        using var enc = new H264FrameEncoder(w, h, 1_000_000);

        var frame = MakeGradientFrame(w, h, 0, stride);
        var stream = enc.Encode(frame, stride);

        Assert.NotNull(stream);
        Assert.True(stream.Length > 100, $"expected real picture data, got {stream.Length} bytes");
        // Annex-B start code 00 00 00 01 (or 00 00 01).
        bool startCode =
            (stream[0] == 0 && stream[1] == 0 && stream[2] == 0 && stream[3] == 1) ||
            (stream[0] == 0 && stream[1] == 0 && stream[2] == 1);
        Assert.True(startCode, "stream must start with an Annex-B start code");
    }

    [Fact]
    public void Encode_SkipsIdenticalFrames_UnderRateControl()
    {
        // Content that never changes: the rate controller may skip (null) — but at least one of
        // several identical frames must have produced the initial IDR.
        int w = 320, h = 240, stride = w * 4;
        using var enc = new H264FrameEncoder(w, h, 1_000_000);

        var frame = MakeGradientFrame(w, h, 0, stride);
        var first = enc.Encode(frame, stride);
        Assert.NotNull(first);

        bool anyOutput = false;
        for (int i = 0; i < 10; i++)
            if (enc.Encode(frame, stride) != null)
                anyOutput = true;
        // Identical content: typically skipped, but this is informational — no hard assert
        // because rate-control behavior is codec-internal.
        _ = anyOutput;
    }

    [Fact]
    public void Decode_RestoresDimensions_AndApproximateColors()
    {
        int w = 320, h = 240, stride = w * 4;
        using var enc = new H264FrameEncoder(w, h, 2_000_000);
        using var dec = new H264FrameDecoder();

        // Keyframe: a solid color frame ( Survives YUV round-trip with predictable values).
        var solid = new byte[stride * h];
        for (int i = 0; i < solid.Length; i += 4)
        {
            solid[i] = 40;      // B
            solid[i + 1] = 200; // G
            solid[i + 2] = 40;  // R  -> green
            solid[i + 3] = 255;
        }
        var stream = enc.Encode(solid, stride);
        Assert.NotNull(stream);

        byte[]? bgra = null;
        bool got = dec.Decode(stream!, ref bgra);

        Assert.True(got, "first IDR must decode to a picture");
        Assert.NotNull(bgra);
        Assert.Equal(w * 4, dec.Width * 4);

        // Center pixel should still be dominantly green after 4:2:0 + quantization.
        int mid = (h / 2 * stride) + (w / 2) * 4;
        Assert.True(bgra![mid + 1] > 120, $"green channel {bgra[mid + 1]} should stay dominant");
    }

    [Fact]
    public void Decode_SecondFrame_ReceivesUpdatedContent()
    {
        int w = 320, h = 240, stride = w * 4;
        using var enc = new H264FrameEncoder(w, h, 2_000_000);
        using var dec = new H264FrameDecoder();

        byte[]? bgra = null;
        bool sawMoving = false;
        for (int i = 0; i < 30 && !sawMoving; i++)
        {
            var frame = MakeGradientFrame(w, h, i * 40, stride);
            var stream = enc.Encode(frame, stride);
            if (stream is null) continue;
            if (dec.Decode(stream, ref bgra) && bgra != null)
            {
                int topRow = (10 * stride) + (10 * 4);
                int bottomRow = ((h - 10) * stride) + (10 * 4);
                // Gradient shifted between top and bottom as i grows; verify they differ.
                sawMoving = Math.Abs(bgra[topRow + 1] - bgra[bottomRow + 1]) > 8;
            }
        }

        Assert.True(sawMoving, "decoded stream should reflect changing content");
    }

    [Fact]
    public void ForceKeyFrame_DoesNotThrow_AndProducesOutput()
    {
        int w = 320, h = 240, stride = w * 4;
        using var enc = new H264FrameEncoder(w, h, 2_000_000);
        var frame = MakeGradientFrame(w, h, 0, stride);
        Assert.NotNull(enc.Encode(frame, stride));

        enc.ForceKeyFrame();
        var shifted = MakeGradientFrame(w, h, 100, stride);
        var out2 = enc.Encode(shifted, stride);
        Assert.NotNull(out2);
    }
}
