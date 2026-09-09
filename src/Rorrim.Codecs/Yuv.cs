using System.Runtime.InteropServices;

namespace Rorrim.Codecs;

/// <summary>
/// Color conversion between packed BGRA (the capture pipeline's format) and I420/YUV420P planar
/// (OpenH264's input/output format). Full-range BT.601 on both directions so encode/decode
/// round-trips are consistent. Row-parallel: ~2-4 ms for 1080p.
/// </summary>
public static class Yuv
{
    public static byte[] BgraToI420(byte[] bgra, int width, int height, int stride)
    {
        var yuv = new byte[width * height + 2 * (width / 2) * (height / 2)];
        BgraToI420(bgra, width, height, stride, yuv);
        return yuv;
    }

    public static unsafe void BgraToI420(byte[] bgra, int width, int height, int stride, byte[] yuv)
    {
        int uo = width * height;
        int vo = uo + (width / 2) * (height / 2);

        fixed (byte* srcFixed = bgra, dstFixed = yuv)
        {
            IntPtr srcBase = (IntPtr)srcFixed;
            IntPtr dstBase = (IntPtr)dstFixed;
            Parallel.For(0, height, row =>
            {
                byte* srcRow = (byte*)srcBase + row * stride;
                byte* yRow = (byte*)dstBase + row * width;
                for (int x = 0; x < width; x++)
                {
                    byte b = srcRow[x * 4], g = srcRow[x * 4 + 1], r = srcRow[x * 4 + 2];
                    yRow[x] = (byte)((77 * r + 150 * g + 29 * b) >> 8);
                }
            });

            Parallel.For(0, height / 2, row =>
            {
                byte* srcRow = (byte*)srcBase + row * 2 * stride;
                byte* uRow = (byte*)dstBase + uo + row * (width / 2);
                byte* vRow = (byte*)dstBase + vo + row * (width / 2);
                for (int x = 0; x < width / 2; x++)
                {
                    int b = 0, g = 0, r = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            byte* p = srcRow + (dy * stride) + ((2 * x + dx) * 4);
                            b += p[0]; g += p[1]; r += p[2];
                        }
                    uRow[x] = (byte)((-43 * r - 85 * g + 128 * b >> 10) + 128);
                    vRow[x] = (byte)((128 * r - 107 * g - 21 * b >> 10) + 128);
                }
            });
        }
    }

    /// <summary>
    /// Converts an I420 frame (from the decoder's native buffers) into packed BGRA with the given
    /// stride. <paramref name="yStride"/> / <paramref name="uvStride"/> are the decoder's plane
    /// strides, which may exceed width/height.
    /// </summary>
    public static unsafe byte[] I420ToBgra(IntPtr yPlane, IntPtr uPlane, IntPtr vPlane,
        int width, int height, int yStride, int uvStride, int bgraStride)
    {
        var bgra = new byte[bgraStride * height];
        fixed (byte* dstFixed = bgra)
        {
            IntPtr dstBase = (IntPtr)dstFixed;
            Parallel.For(0, height, row =>
            {
                byte* yRow = (byte*)yPlane + row * yStride;
                byte* uRow = (byte*)uPlane + (row / 2) * uvStride;
                byte* vRow = (byte*)vPlane + (row / 2) * uvStride;
                byte* dstRow = (byte*)dstBase + row * bgraStride;
                for (int x = 0; x < width; x++)
                {
                    int yy = yRow[x];
                    int uu = uRow[x / 2] - 128;
                    int vv = vRow[x / 2] - 128;

                    // Full-range BT.601 inverse of BgraToI420.
                    int rr = (256 * yy + 360 * vv + 128) >> 8;
                    int gg = (256 * yy - 88 * uu - 184 * vv + 128) >> 8;
                    int bb = (256 * yy + 455 * uu + 128) >> 8;

                    dstRow[x * 4] = (byte)(bb < 0 ? 0 : bb > 255 ? 255 : bb);
                    dstRow[x * 4 + 1] = (byte)(gg < 0 ? 0 : gg > 255 ? 255 : gg);
                    dstRow[x * 4 + 2] = (byte)(rr < 0 ? 0 : rr > 255 ? 255 : rr);
                    dstRow[x * 4 + 3] = 255;
                }
            });
        }
        return bgra;
    }
}
