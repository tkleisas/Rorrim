using System.Runtime.InteropServices;
using Rorrim.Shared.Contracts;

namespace Rorrim.Agent.Input;

/// <summary>The captured display's rectangle on the desktop, in pixels.</summary>
public readonly record struct DisplayRect(int X, int Y, int Width, int Height);

/// <summary>The bounds of the whole virtual desktop (all monitors), in pixels.</summary>
public readonly record struct VirtualScreenBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Applies <see cref="PointerInput"/> messages from the broker to the local desktop via SendInput.
/// Client coordinates are normalized (0..1) against the *captured display*; this injector maps them
/// into the display's desktop rectangle and then onto the virtual desktop, which SendInput's
/// MOUSEEVENTF_ABSOLUTE addressing spans — so input lands correctly with multi-monitor hosts.
/// </summary>
public sealed class InputInjector
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly VirtualScreenBounds _virtual;
    private DisplayRect _display;

    public InputInjector(DisplayRect display, VirtualScreenBounds? virtualScreen = null)
    {
        _display = display;
        _virtual = virtualScreen ?? QueryVirtualScreen();
    }

    /// <summary>
    /// Retargets the injector when the captured display changes. Input and switch commands are
    /// processed on the same receive loop, so subsequent input maps to the new display.
    /// </summary>
    public void SetDisplay(DisplayRect display) => _display = display;

    public void Inject(PointerInput input)
    {
        switch (input.ActionCase)
        {
            case PointerInput.ActionOneofCase.Move:
                SendMouseMove(input.Move.X, input.Move.Y);
                break;
            case PointerInput.ActionOneofCase.Button:
                SendButton(input.Button);
                break;
            case PointerInput.ActionOneofCase.Key:
                SendKey(input.Key);
                break;
            case PointerInput.ActionOneofCase.Scroll:
                SendMouseInput(MOUSEEVENTF_WHEEL, 0, 0,
                    (uint)Math.Clamp(input.Scroll.Delta * 120.0, int.MinValue, int.MaxValue), 0);
                break;
        }
    }

    /// <summary>
    /// Pure coordinate mapping: normalized (0..1) position within <paramref name="display"/> ->
    /// absolute virtual-desktop units (0..65535) as required by MOUSEEVENTF_ABSOLUTE.
    /// </summary>
    public static (int x, int y) MapNormalizedToVirtual(
        double xn, double yn, DisplayRect display, VirtualScreenBounds vs)
    {
        if (display.Width <= 0 || display.Height <= 0 || vs.Width <= 0 || vs.Height <= 0)
            return (0, 0);

        double absX = display.X + Clamp01(xn) * display.Width;
        double absY = display.Y + Clamp01(yn) * display.Height;

        int x = (int)Math.Round((absX - vs.X) * 65535.0 / vs.Width);
        int y = (int)Math.Round((absY - vs.Y) * 65535.0 / vs.Height);
        return (Math.Clamp(x, 0, 65535), Math.Clamp(y, 0, 65535));
    }

    private void SendMouseMove(double xn, double yn)
    {
        var (ax, ay) = MapNormalizedToVirtual(xn, yn, _display, _virtual);
        SendMouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, ax, ay, 0, 0);
    }

    private void SendButton(MouseButton button)
    {
        uint flags = button.Button switch
        {
            0 => MOUSEEVENTF_LEFTDOWN,
            1 => MOUSEEVENTF_RIGHTDOWN,
            2 => MOUSEEVENTF_MIDDLEDOWN,
            _ => 0
        };
        if (flags == 0) return;
        if (!button.Down)
            flags = flags switch
            {
                MOUSEEVENTF_LEFTDOWN => MOUSEEVENTF_LEFTUP,
                MOUSEEVENTF_RIGHTDOWN => MOUSEEVENTF_RIGHTUP,
                MOUSEEVENTF_MIDDLEDOWN => MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };
        var (ax, ay) = MapNormalizedToVirtual(button.X, button.Y, _display, _virtual);
        SendMouseInput(flags, ax, ay, 0, 0);
    }

    private void SendKey(KeyEvent key)
    {
        ushort vk = (ushort)key.VirtualKey;
        uint flags = (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0);
        if (!key.Down) flags |= KEYEVENTF_KEYUP;
        // Scan code fallback: some applications ignore VK-only input.
        ushort scan = key.ScanCode != 0 ? (ushort)key.ScanCode : (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        SendKeyInput(vk, scan, flags);
    }

    private void SendMouseInput(uint flags, double dx, double dy, uint wheel, uint extra)
    {
        var p = new MOUSEINPUT
        {
            dx = (int)dx,
            dy = (int)dy,
            mouseData = wheel,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = UIntPtr.Zero
        };
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = p } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void SendKeyInput(ushort vk, ushort scan, uint flags)
    {
        var p = new KEYBDINPUT
        {
            wVk = vk,
            wScan = scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = UIntPtr.Zero
        };
        var input = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = p } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static VirtualScreenBounds QueryVirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    // --- P/Invoke (SendInput) ---

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
