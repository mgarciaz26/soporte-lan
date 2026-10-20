using System.Runtime.InteropServices;

namespace SoporteLan.Agent.Input;

/// <summary>Inyección de ratón y teclado con SendInput.</summary>
internal static class InputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMoveFlag = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheelFlag = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    private const uint KeyUp = 0x0002;
    private const uint KeyExtended = 0x0001;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static void MouseMove(int x, int y)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(2, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(2, GetSystemMetrics(SmCyVirtualScreen));

        var normalizedX = (x - left) * 65535.0 / (width - 1);
        var normalizedY = (y - top) * 65535.0 / (height - 1);

        var input = new Input { Type = InputMouse };
        input.Data.Mouse.Dx = (int)Math.Round(Math.Clamp(normalizedX, 0, 65535));
        input.Data.Mouse.Dy = (int)Math.Round(Math.Clamp(normalizedY, 0, 65535));
        input.Data.Mouse.Flags = MouseMoveFlag | MouseAbsolute | MouseVirtualDesk;

        Send(input);
    }

    public static void MouseButton(int button, bool down)
    {
        var flags = button switch
        {
            0 => down ? MouseLeftDown : MouseLeftUp,
            1 => down ? MouseRightDown : MouseRightUp,
            2 => down ? MouseMiddleDown : MouseMiddleUp,
            _ => 0u,
        };

        if (flags == 0) return;

        var input = new Input { Type = InputMouse };
        input.Data.Mouse.Flags = flags;
        Send(input);
    }

    public static void MouseWheel(int delta)
    {
        var input = new Input { Type = InputMouse };
        input.Data.Mouse.MouseData = (uint)delta;
        input.Data.Mouse.Flags = MouseWheelFlag;
        Send(input);
    }

    public static void Key(ushort virtualKey, bool down)
    {
        var input = new Input { Type = InputKeyboard };
        input.Data.Keyboard.VirtualKey = virtualKey;
        input.Data.Keyboard.Flags = (down ? 0u : KeyUp) | (IsExtended(virtualKey) ? KeyExtended : 0u);
        Send(input);
    }

    public static bool TryGetCursor(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static void Send(Input input)
    {
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static bool IsExtended(ushort virtualKey) => virtualKey switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // re pág, av pág, fin, inicio, flechas
        0x2D or 0x2E => true,                                                // insert, supr
        0x5B or 0x5C or 0x5D => true,                                        // windows, apps
        0x6F => true,                                                        // división del teclado numérico
        0xA3 or 0xA5 => true,                                                // ctrl y alt derechos
        _ => false,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}
