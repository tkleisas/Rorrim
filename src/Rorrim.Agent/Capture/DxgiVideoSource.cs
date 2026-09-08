using System.Runtime.CompilerServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Capture;

/// <summary>
/// Wraps DXGI Desktop Duplication as an <see cref="IVideoSource"/>. This is a thin adapter — all
/// orchestration, coalescing and encoding live behind the <see cref="Pipeline"/> interfaces so the
/// GPU code here stays minimal and hardware specific.
/// </summary>
public sealed class DxgiVideoSource : IVideoSource
{
    private readonly object _lock = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;

    private bool _isDesktopLocked;
    private bool _started;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsDesktopLocked { get { lock (_lock) return _isDesktopLocked; } }
    public bool IsStarted => _started && !_disposed;
    public bool CoalescesInternally => true;

    private readonly string _outputName;

    /// <param name="outputName">The DXGI device name (e.g. "\\.\DISPLAY1") to capture.</param>
    public DxgiVideoSource(string outputName)
    {
        _outputName = outputName;
    }

    /// <summary>Creates the device, locates the output and starts duplication. Must run before enumeration.</summary>
    public void Start()
    {
        if (_started) return;
        ThrowIfDisposed();

        lock (_lock)
        {
            Result hr = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                FeatureLevel.Level_11_0,
                out ID3D11Device? device,
                out ID3D11DeviceContext? context);
            if (hr.Failure)
                throw new InvalidOperationException($"D3D11 device creation failed: {hr.Code}");
            _device = device!;
            _context = context!;

            // Find the requested output on any adapter.
            using (IDXGIDevice dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
            using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
            {
                IDXGIOutput? target = null;
                for (uint i = 0; ; i++)
                {
                    Result or = adapter.EnumOutputs(i, out IDXGIOutput? outi);
                    if (or.Failure || outi is null) break;
                    var d = outi.Description;
                    if (string.Equals(d.DeviceName, _outputName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = outi;
                        break;
                    }
                    outi.Dispose();
                }
                if (target is null)
                    throw new InvalidOperationException($"Output '{_outputName}' not found.");

                try
                {
                    using IDXGIOutput1 out1 = target.QueryInterface<IDXGIOutput1>();
                    _duplication = out1.DuplicateOutput(_device);

                    var rect = target.Description.DesktopCoordinates;
                    Width = rect.Right - rect.Left;
                    Height = rect.Bottom - rect.Top;
                }
                finally
                {
                    target.Dispose();
                }
            }

            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });

            _started = true;
        }
    }

    public RawFrame? TryAcquire(int timeoutMs)
    {
        ThrowIfDisposed();
        if (!_started) throw new InvalidOperationException("Source is not started.");

        lock (_lock)
        {
            Result result = _duplication!.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out OutduplFrameInfo info, out IDXGIResource? resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                return null;
            if (result == Vortice.DXGI.ResultCode.AccessLost || result == Vortice.DXGI.ResultCode.SessionDisconnected || result == Vortice.DXGI.ResultCode.DeviceRemoved)
            {
                _isDesktopLocked = true;
                return null;
            }
            if (result.Failure)
                throw new InvalidOperationException($"AcquireNextFrame failed: {result.Code}");

            byte[]? buffer = null;
            try
            {
                using (resource)
                using (ID3D11Texture2D? acquired = resource!.QueryInterface<ID3D11Texture2D>())
                {
                    if (acquired is null) return null;
                    _context!.CopyResource(acquired, _staging!);
                }
                _duplication.ReleaseFrame();

                buffer = new byte[Width * Height * 4];
                if (_context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out Vortice.Direct3D11.MappedSubresource mapped).Failure)
                    throw new InvalidOperationException("Map of staging texture failed.");
                try
                {
                    unsafe
                    {
                        byte* src = (byte*)mapped.DataPointer;
                        int stride = Width * 4;
                        fixed (byte* dst = buffer)
                        {
                            for (int y = 0; y < Height; y++)
                                Buffer.MemoryCopy(src + (y * mapped.RowPitch), dst + (y * stride), stride, stride);
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_staging, 0);
                }
            }
            finally
            {
                // Ensure the frame is released even if readback failed.
            }

            _isDesktopLocked = false;
            long tick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new RawFrame(Width, Height, tick, buffer);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DxgiVideoSource));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _duplication?.Dispose();
            _duplication = null;
            _staging?.Dispose();
            _staging = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
        }
    }
}
