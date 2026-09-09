using Rorrim.Agent.Display;
using Rorrim.Agent.Pipeline;

namespace Rorrim.Agent.Capture;

/// <summary>
/// Creates a video source for a display: DXGI Desktop Duplication is preferred (GPU path, lock
/// detection) but is validated with a probe frame — broken duplication delivers blank/black output
/// in some session/driver combos — and falls back to GDI BitBlt, which is slower but reliable.
/// </summary>
public static class VideoSourceFactory
{
    public static IVideoSource Create(
        DisplayAdapter display,
        Func<DisplayAdapter, IVideoSource>? dxgiFactory = null,
        Func<DisplayAdapter, IVideoSource>? gdiFactory = null,
        int probeAttempts = 3,
        int probeTimeoutMs = 250)
    {
        dxgiFactory ??= d => new DxgiVideoSource(d.DeviceName);
        gdiFactory ??= d => new GdiVideoSource(d.X, d.Y, d.Width, d.Height);

        IVideoSource? dxgi = null;
        try
        {
            dxgi = dxgiFactory(display);
            dxgi.Start();

            // Desktop Duplication always delivers an initial full-desktop frame; several attempts
            // tolerate slow device init. A probe that never arrives or is fully blank means the
            // duplication is not usable in this session — fall back to GDI.
            for (int i = 0; i < probeAttempts; i++)
            {
                var probe = dxgi.TryAcquire(probeTimeoutMs);
                if (probe is not null)
                {
                    if (!IsBlankFrame(probe.Value))
                    {
                        AgentLog.Write($"capture: using {dxgi.Kind} for {display.DeviceName}");
                        return dxgi;
                    }
                    AgentLog.Write($"capture: {dxgi.Kind} probe frame for {display.DeviceName} is blank");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write($"capture: {dxgi?.Kind ?? "dxgi"} unavailable for {display.DeviceName}: {ex.Message}");
        }

        dxgi?.Dispose();
        var gdi = gdiFactory(display);
        gdi.Start();
        AgentLog.Write($"capture: falling back to {gdi.Kind} for {display.DeviceName}");
        return gdi;
    }

    /// <summary>True when every pixel byte is zero (broken duplication signature).</summary>
    public static bool IsBlankFrame(in RawFrame frame) =>
        frame.Bgra.AsSpan().IndexOfAnyExcept((byte)0) < 0;
}
