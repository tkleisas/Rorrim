using Rorrim.Shared.Contracts;

namespace Rorrim.Client.Streaming;

/// <summary>
/// Builds <see cref="PointerInput"/> messages from UI pointer/key events. Coordinates are mapped
/// from view-local values (0..viewWidth / 0..viewHeight) to normalized 0..1 desktop coordinates.
/// </summary>
public static class InputEncoder
{
    public static PointerInput Move(double xNorm, double yNorm) =>
        new() { Move = new MouseMove { X = Clamp01(xNorm), Y = Clamp01(yNorm) } };

    public static PointerInput ButtonDown(uint button, double xNorm, double yNorm) =>
        new() { Button = new MouseButton { Down = true, Button = button, X = Clamp01(xNorm), Y = Clamp01(yNorm) } };

    public static PointerInput ButtonUp(uint button, double xNorm, double yNorm) =>
        new() { Button = new MouseButton { Down = false, Button = button, X = Clamp01(xNorm), Y = Clamp01(yNorm) } };

    public static PointerInput Key(uint virtualKey, uint scanCode, bool down, bool extended) =>
        new() { Key = new KeyEvent { VirtualKey = virtualKey, ScanCode = scanCode, Down = down, Extended = extended } };

    public static PointerInput Scroll(double delta) =>
        new() { Scroll = new Scroll { Delta = delta } };

    public static double ToNorm(double value, double extent)
    {
        if (extent <= 0) return 0;
        return Clamp01(value / extent);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
