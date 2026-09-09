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

    private byte[]? _previous;
    private bool _started;
    private bool _disposed;

    public string Kind => "gdi";
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

        // BitBlt the target region into the offscreen bitmap (SRCCOPY). BitBlt does NOT include
        // the mouse cursor; draw it explicitly so viewers see the pointer.
        BitBlt(_memDc, 0, 0, _width, _height, _screenDc, _x, _y, SrcCopy);
        DrawCursorIntoCapture();

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

        // Change detection: return null when nothing changed so the stream idles on a static
        // desktop instead of re-encoding identical frames.
        if (_previous is not null && buffer.AsSpan().SequenceEqual(_previous))
            return null;
        _previous = buffer;

        long tick = DateTimeOffset.UtcNow.UtcTicks / 10; // microseconds
        return new RawFrame(_width, _height, tick, buffer);
    }

    /// <summary>
    /// Composites the current mouse cursor sprite into the captured bitmap. Without this the
    /// cursor is invisible to viewers (BitBlt excludes it), and on a static desktop the cursor is
    /// the only thing that changes between frames — so change detection would suppress all updates.
    /// </summary>
    private void DrawCursorIntoCapture()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == IntPtr.Zero)
            return;
        if (!GetIconInfo(ci.hCursor, out ICONINFO info))
            return;
        try
        {
            // ptScreenPos is the cursor tip in virtual-desktop coordinates; the sprite's top-left
            // is the tip minus the hotspot, relative to this capture region's origin.
            int x = ci.ptScreenPos.X - info.xHotspot - _x;
            int y = ci.ptScreenPos.Y - info.yHotspot - _y;
            DrawIconEx(_memDc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
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
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pIconInfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int xs, int ys, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

    private const int CURSOR_SHOWING = 0x0001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
}
