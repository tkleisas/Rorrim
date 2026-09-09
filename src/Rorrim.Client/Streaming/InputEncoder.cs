using Avalonia.Input;
using Rorrim.Shared.Contracts;
using Key = Avalonia.Input.Key;
using MouseButton = Rorrim.Shared.Contracts.MouseButton;

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

    /// <summary>Builds a key press/release input for the host.</summary>
    public static PointerInput KeyPress(uint virtualKey, uint scanCode, bool down, bool extended) =>
        new() { Key = new KeyEvent { VirtualKey = virtualKey, ScanCode = scanCode, Down = down, Extended = extended } };

    public static PointerInput Scroll(double delta) =>
        new() { Scroll = new Scroll { Delta = delta } };

    public static double ToNorm(double value, double extent)
    {
        if (extent <= 0) return 0;
        return Clamp01(value / extent);
    }

    /// <summary>
    /// Maps an Avalonia <see cref="Key"/> to a Win32 virtual-key code (VK) for injection on the
    /// host. Returns vk=0 for keys with no mapping. Arrows/navigation are marked extended, matching
    /// the main-keyboard variant of those keys.
    /// </summary>
    public static (uint vk, uint scan, bool ext) MapKey(Key key) => key switch
    {
        Key.LeftShift => (0xA0, 0, false),
        Key.RightShift => (0xA1, 0, true),
        Key.LeftCtrl => (0xA2, 0, false),
        Key.RightCtrl => (0xA3, 0, true),
        Key.LeftAlt => (0xA4, 0, false),
        Key.RightAlt => (0xA5, 0, true),
        Key.LWin => (0x5B, 0, true),
        Key.RWin => (0x5C, 0, true),
        Key.Enter => (0x0D, 0, false),
        Key.Tab => (0x09, 0, false),
        Key.Back => (0x08, 0, false),
        Key.Space => (0x20, 0, false),
        Key.Escape => (0x1B, 0, false),
        Key.CapsLock => (0x14, 0, false),
        Key.Left => (0x25, 0, true),
        Key.Up => (0x26, 0, true),
        Key.Right => (0x27, 0, true),
        Key.Down => (0x28, 0, true),
        Key.Home => (0x24, 0, true),
        Key.End => (0x23, 0, true),
        Key.PageUp => (0x21, 0, true),
        Key.PageDown => (0x22, 0, true),
        Key.Insert => (0x2D, 0, true),
        Key.Delete => (0x2E, 0, true),
        Key.OemMinus => (0xBD, 0, false),
        Key.OemPlus => (0xBB, 0, false),
        Key.OemComma => (0xBC, 0, false),
        Key.OemPeriod => (0xBE, 0, false),
        Key.OemSemicolon => (0xBA, 0, false),
        Key.OemQuestion => (0xBF, 0, false),
        Key.OemTilde => (0xC0, 0, false),
        Key.OemOpenBrackets => (0xDB, 0, false),
        Key.OemCloseBrackets => (0xDD, 0, false),
        Key.OemPipe => (0xDC, 0, false),
        Key.OemQuotes => (0xDE, 0, false),
        Key.OemBackslash => (0xE2, 0, false),
        _ when key >= Key.A && key <= Key.Z => ((uint)(0x41 + (int)key - (int)Key.A), 0, false),
        _ when key >= Key.D0 && key <= Key.D9 => ((uint)(0x30 + (int)key - (int)Key.D0), 0, false),
        _ when key >= Key.NumPad0 && key <= Key.NumPad9 => ((uint)(0x60 + (int)key - (int)Key.NumPad0), 0, false),
        _ when key >= Key.F1 && key <= Key.F12 => ((uint)(0x70 + (int)key - (int)Key.F1), 0, false),
        _ => (0, 0, false)
    };

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
