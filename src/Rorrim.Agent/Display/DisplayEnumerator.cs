using Vortice.DXGI;
using SharpGen.Runtime;

namespace Rorrim.Agent.Display;

/// <summary>
/// Enumerates the physical displays/adapters attached to the host via DXGI.
/// Every displayed output is a candidate capture source.
/// </summary>
public static class DisplayEnumerator
{
    public readonly record struct DisplayAdapter(
        string AdapterName,
        int OutputIndex,
        string DeviceName,
        int X,
        int Y,
        int Width,
        int Height)
    {
        public string Description => $"{DeviceName} | {Width}x{Height} @({X},{Y})";
    }

    public static IReadOnlyList<DisplayAdapter> Enumerate()
    {
        var results = new List<DisplayAdapter>();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIdx = 0; ; adapterIdx++)
        {
            Result adapterResult = factory.EnumAdapters(adapterIdx, out IDXGIAdapter? adapter);
            if (adapterResult.Failure) break;
            if (adapter is null) break;

            using (adapter)
            {
                var adapterDesc = adapter.Description;
                for (uint outputIdx = 0; ; outputIdx++)
                {
                    Result outputResult = adapter.EnumOutputs(outputIdx, out IDXGIOutput? output);
                    if (outputResult.Failure) break;
                    if (output is null) break;

                    using (output)
                    {
                        var desc = output.Description;
                        var rect = desc.DesktopCoordinates;
                        int w = rect.Right - rect.Left;
                        int h = rect.Bottom - rect.Top;
                        results.Add(new DisplayAdapter(
                            adapterDesc.Description,
                            (int)outputIdx,
                            desc.DeviceName,
                            rect.Left,
                            rect.Top,
                            w,
                            h));
                    }
                }
            }
        }

        return results;
    }
}
