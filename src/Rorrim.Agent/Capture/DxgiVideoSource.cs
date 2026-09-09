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

    public string Kind => "dxgi";
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
            // Locate the output on ANY adapter first, then create the D3D11 device ON THAT ADAPTER.
            // Creating a default-adapter device for an output that is driven by another adapter
            // results in black duplication frames (observed on multi-adapter systems).
            IDXGIAdapter? targetAdapter = null;
            IDXGIOutput? targetOutput = null;
            IDXGIFactory1? factory = null;
            try
            {
                factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint a = 0; ; a++)
                {
                    Result adapterResult = factory.EnumAdapters(a, out IDXGIAdapter? adapter);
                    if (adapterResult.Failure || adapter is null) break;

                    bool found = false;
                    for (uint o = 0; ; o++)
                    {
                        Result outputResult = adapter.EnumOutputs(o, out IDXGIOutput? output);
                        if (outputResult.Failure || output is null) break;
                        if (!found && string.Equals(output.Description.DeviceName, _outputName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Keep adapter+output alive for device creation below.
                            targetAdapter = adapter;
                            targetOutput = output;
                            found = true;
                            break;
                        }
                        output.Dispose();
                    }

                    if (found) break;
                    adapter.Dispose();
                }

                if (targetAdapter is null || targetOutput is null)
                    throw new InvalidOperationException($"Output '{_outputName}' not found.");

                Result hr = D3D11.D3D11CreateDevice(
                    targetAdapter.NativePointer,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    FeatureLevel.Level_11_0,
                    out ID3D11Device? device,
                    out ID3D11DeviceContext? context);
                if (hr.Failure)
                    throw new InvalidOperationException($"D3D11 device creation failed: {hr.Code}");
                _device = device!;
                _context = context!;
            }
            finally
            {
                targetAdapter?.Dispose();
                factory?.Dispose();
            }

            try
            {
                using IDXGIOutput1 out1 = targetOutput.QueryInterface<IDXGIOutput1>();
                _duplication = out1.DuplicateOutput(_device);

                var rect = targetOutput.Description.DesktopCoordinates;
                Width = rect.Right - rect.Left;
                Height = rect.Bottom - rect.Top;
            }
            finally
            {
                targetOutput.Dispose();
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
                // Desktop switched away / session disconnected / GPU reset. Mark locked (the stream
                // pauses) and attempt to rebuild the duplication in the background so capture
                // resumes automatically when the desktop becomes available again.
                _isDesktopLocked = true;
                TryRecover();
                return null;
            }
            if (result.Failure)
                throw new InvalidOperationException($"AcquireNextFrame failed: {result.Code}");

            try
            {
                using (resource)
                using (ID3D11Texture2D? acquired = resource!.QueryInterface<ID3D11Texture2D>())
                {
                    if (acquired is null) return null;
                    // Copy the desktop texture INTO the staging texture (dst, src order).
                    _context!.CopyResource(_staging!, acquired);
                }
            }
            finally
            {
                // Release the duplication frame even if the readback below fails.
                _duplication.ReleaseFrame();
            }

            byte[] buffer = new byte[Width * Height * 4];
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

            _isDesktopLocked = false;
            long tick = DateTimeOffset.UtcNow.UtcTicks / 10; // microseconds
            return new RawFrame(Width, Height, tick, buffer);
        }
    }

    /// <summary>
    /// Attempts to rebuild the device + duplication after a loss event (best-effort; swallows
    /// errors so recovery can be retried on subsequent acquire calls while locked).
    /// </summary>
    private void TryRecover()
    {
        try
        {
            _duplication?.Dispose();
            _duplication = null;
            _staging?.Dispose();
            _staging = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _started = false;
            Start();
        }
        catch
        {
            // Recovery failed; the next TryAcquire while locked will retry.
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
