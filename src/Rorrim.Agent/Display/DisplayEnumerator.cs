using Vortice.DXGI;
using SharpGen.Runtime;

namespace Rorrim.Agent.Display;

/// <summary>A physical display attached to the host, as reported by DXGI.</summary>
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

    /// <summary>The monitor at the virtual-desktop origin is treated as primary.</summary>
    public bool IsPrimary => X == 0 && Y == 0;
}

/// <summary>
/// Enumerates the physical displays/adapters attached to the host via DXGI.
/// Every displayed output is a candidate capture source.
/// </summary>
public static class DisplayEnumerator
{
    /// <summary>
    /// Resolves a display id from the wire (device name like "\\.\DISPLAY1", or a 0-based index)
    /// to an adapter. Falls back to the first display when the id is empty or unknown.
    /// </summary>
    public static DisplayAdapter? Resolve(IReadOnlyList<DisplayAdapter> displays, string? id)
    {
        if (displays.Count == 0)
            return null;
        if (string.IsNullOrWhiteSpace(id))
            return displays[0];
        if (int.TryParse(id, out int idx) && idx >= 0 && idx < displays.Count)
            return displays[idx];
        foreach (var d in displays)
        {
            if (string.Equals(d.DeviceName, id, StringComparison.OrdinalIgnoreCase))
                return d;
        }
        return displays[0];
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
