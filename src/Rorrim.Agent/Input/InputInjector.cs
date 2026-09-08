using System.Runtime.InteropServices;
using Rorrim.Shared.Contracts;

namespace Rorrim.Agent.Input;

/// <summary>
/// Applies <see cref="PointerInput"/> messages from the broker to the local desktop via SendInput.
/// Works at High Integrity so it can reach elevated and non-elevated targets.
/// </summary>
public static class InputInjector
{
    public static void Inject(PointerInput input)
    {
        if (input.ActionCase switch
        {
            PointerInput.ActionOneofCase.Move => false, // handled separately (relative/absolute move)
            PointerInput.ActionOneofCase.Button => TryButton(input.Button),
            PointerInput.ActionOneofCase.Key => TryKey(input.Key),
            PointerInput.ActionOneofCase.Scroll => TryScroll(input.Scroll),
            _ => false
        })
            return;

        // Mouse move: SendInput with MOUSEEVENTF_MOVE (absolute, normalized 0..1).
        if (input.Move is { } move)
            SendMouseMove(move.X, move.Y);
    }

    private static bool TryButton(MouseButton button)
    {
        uint flags = button.Button switch
        {
            0 => MOUSEEVENTF_LEFTDOWN,
            1 => MOUSEEVENTF_RIGHTDOWN,
            2 => MOUSEEVENTF_MIDDLEDOWN,
            _ => 0
        };
        if (flags == 0) return false;
        if (!button.Down)
            flags = flags switch
            {
                MOUSEEVENTF_LEFTDOWN => MOUSEEVENTF_LEFTUP,
                MOUSEEVENTF_RIGHTDOWN => MOUSEEVENTF_RIGHTUP,
                MOUSEEVENTF_MIDDLEDOWN => MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };
        SendMouseInput(flags, button.X * 65535.0, button.Y * 65535.0, 0, 0);
        return true;
    }

    private static bool TryKey(KeyEvent key)
    {
        ushort vk = (ushort)key.VirtualKey;
        uint flags = (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0);
        if (!key.Down) flags |= KEYEVENTF_KEYUP;
        return SendKeyInput(vk, (ushort)key.ScanCode, flags);
    }

    private static bool TryScroll(Scroll scroll)
    {
        SendMouseInput(MOUSEEVENTF_WHEEL, 0, 0, (uint)(scroll.Delta * 120.0), 0);
        return true;
    }

    private static void SendMouseMove(double x, double y)
    {
        SendMouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, x * 65535.0, y * 65535.0, 0, 0);
    }

    public static void SendMouseInput(uint flags, double dx, double dy, uint wheel, uint extra)
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

    public static bool SendKeyInput(ushort vk, ushort scan, uint flags)
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
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        return sent == 1;
    }

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
}
