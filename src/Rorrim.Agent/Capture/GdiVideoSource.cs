using System.Runtime.InteropServices;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Capture;

/// <summary>
/// A GDI <see cref="BitBlt"/> based video source. DXGI Desktop Duplication is preferred but returns
/// blank frames in some sessions/GPU driver combos (observed here: content-rich desktop, duplication
/// black, GDI correct); this adapter is the reliable cross-version fallback. Output is 24bpp BGRA-
/// compatible so the same <see cref="IVideoEncoder"/> works unchanged.
/// </summary>
public sealed class GdiVideoSource : IVideoSource
{
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;

    private bool _started;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public bool IsDesktopLocked => false; // GDI captures whatever is on the virtual desktop; no lock state.
    public bool IsStarted => _started && !_disposed;
    public bool CoalescesInternally => false; // GDI produces a fresh frame on every call.

    /// <param name="x">Virtual-desktop left offset of the target display.</param>
    /// <param name="y">Virtual-desktop top offset of the target display.</param>
    /// <param name="width">Display width in pixels.</param>
    /// <param name="height">Display height in pixels.</param>
    public GdiVideoSource(int x, int y, int width, int height)
    {
        _x = x; _y = y; _width = width; _height = height;
    }

    /// <summary>Creates the offscreen buffers. Must be called before the first <see cref="TryAcquire"/>.</summary>
    public void Start()
    {
        if (_started) return;
        ThrowIfDisposed();

        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        _bitmap = CreateCompatibleBitmap(_screenDc, _width, _height);
        _oldBitmap = SelectObject(_memDc, _bitmap);
        _started = true;
    }

    public RawFrame? TryAcquire(int timeoutMs)
    {
        ThrowIfDisposed();
        if (!_started) throw new InvalidOperationException("Source is not started.");

        // BitBlt the target region into the offscreen bitmap (SRCCOPY).
        BitBlt(_memDc, 0, 0, _width, _height, _screenDc, _x, _y, SrcCopy);

        var bmi = new BITMAPINFO();
        bmi.biSize = Marshal.SizeOf<BITMAPINFO>();
        bmi.biWidth = _width;
        bmi.biHeight = -_height; // top-down
        bmi.biPlanes = 1;
        bmi.biBitCount = 32;
        bmi.biCompression = 0; // BI_RGB

        byte[] buffer = new byte[_width * _height * 4];
        int got = GetDIBits(_screenDc, _bitmap, 0, (uint)_height, buffer, ref bmi, 0);
        if (got == 0)
            throw new InvalidOperationException("GetDIBits failed to read the captured frame.");

        long tick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new RawFrame(_width, _height, tick, buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_oldBitmap != IntPtr.Zero) SelectObject(_memDc, _oldBitmap);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GdiVideoSource));
    }

    private const int SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int xs, int ys, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
}
