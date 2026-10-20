using System.Runtime.InteropServices;

namespace SoporteLan.Agent.Capture;

/// <summary>Rectángulo de un monitor en coordenadas del escritorio virtual.</summary>
internal readonly record struct DisplayMonitor(int X, int Y, int Width, int Height, bool Primary);

/// <summary>
/// Enumera los monitores activos con la API clásica de user32. No hace falta
/// WMI ni Direct3D: es una llamada barata y sin dependencias.
/// </summary>
internal static class MonitorLayout
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint MonitorInfoPrimary = 1;

    /// <summary>Monitores ordenados de izquierda a derecha (y de arriba abajo).</summary>
    public static DisplayMonitor[] Enumerate()
    {
        var monitors = new List<DisplayMonitor>(4);

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr handle, IntPtr hdc, ref Rect rect, IntPtr data) =>
                {
                    var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(handle, ref info))
                    {
                        var area = info.Monitor;
                        monitors.Add(new DisplayMonitor(
                            area.Left,
                            area.Top,
                            area.Right - area.Left,
                            area.Bottom - area.Top,
                            (info.Flags & MonitorInfoPrimary) != 0));
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch
        {
            // Si falla la enumeración se usa el escritorio virtual como un solo monitor.
        }

        if (monitors.Count == 0)
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);

            if (width > 0 && height > 0)
                monitors.Add(new DisplayMonitor(x, y, width, height, true));
        }

        return monitors.OrderBy(monitor => monitor.X).ThenBy(monitor => monitor.Y).ToArray();
    }

    /// <summary>Unión de todos los monitores (el escritorio virtual).</summary>
    public static DisplayMonitor Bounds(IReadOnlyList<DisplayMonitor> monitors)
    {
        if (monitors.Count == 0) return default;

        var left = monitors.Min(monitor => monitor.X);
        var top = monitors.Min(monitor => monitor.Y);
        var right = monitors.Max(monitor => monitor.X + monitor.Width);
        var bottom = monitors.Max(monitor => monitor.Y + monitor.Height);

        return new DisplayMonitor(left, top, right - left, bottom - top, false);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
