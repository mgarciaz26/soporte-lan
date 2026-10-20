using System.Runtime.InteropServices;

namespace SoporteLan.Agent.Capture;

/// <summary>
/// Captura GDI del escritorio virtual completo (todos los monitores) en un
/// DIB de 32 bpp. Es la vía más ligera: sin Direct3D, sin WinRT y sin
/// dependencias externas.
/// </summary>
internal sealed unsafe class ScreenCapturer : IDisposable
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;

    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;

    private IntPtr _dib;
    private IntPtr _previousObject;
    private byte* _bits;
    private int _width;
    private int _height;

    public ScreenCapturer()
    {
        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo obtener el DC de pantalla.");

        _memoryDc = CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, _screenDc);
            throw new InvalidOperationException("No se pudo crear el DC de memoria.");
        }
    }

    public int Width => _width;
    public int Height => _height;
    public int OriginX { get; private set; }
    public int OriginY { get; private set; }

    /// <summary>Detecta cambios de resolución o de configuración de monitores.</summary>
    public bool Refresh()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var w = GetSystemMetrics(SmCxVirtualScreen);
        var h = GetSystemMetrics(SmCyVirtualScreen);

        if (w <= 0 || h <= 0) return false;
        if (w == _width && h == _height && x == OriginX && y == OriginY && _dib != IntPtr.Zero)
            return true;

        ReleaseDib();

        OriginX = x;
        OriginY = y;

        var info = new BitmapInfo();
        info.Header.Size = (uint)sizeof(BitmapInfoHeader);
        info.Header.Width = w;
        info.Header.Height = -h; // negativo = top-down
        info.Header.Planes = 1;
        info.Header.BitCount = 32;
        info.Header.Compression = 0;

        _dib = CreateDIBSection(_memoryDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) return false;

        _bits = (byte*)bits;
        _previousObject = SelectObject(_memoryDc, _dib);
        _width = w;
        _height = h;
        return true;
    }

    /// <summary>Devuelve BGRA del escritorio virtual, o vacío si no se pudo capturar.</summary>
    public ReadOnlySpan<byte> Capture()
    {
        if (_dib == IntPtr.Zero || _bits == null) return default;

        if (!BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, OriginX, OriginY, SrcCopy | CaptureBlt))
            return default;

        GdiFlush();
        return new ReadOnlySpan<byte>(_bits, _width * _height * 4);
    }

    private void ReleaseDib()
    {
        if (_dib == IntPtr.Zero) return;

        if (_previousObject != IntPtr.Zero)
            SelectObject(_memoryDc, _previousObject);

        DeleteObject(_dib);
        _dib = IntPtr.Zero;
        _previousObject = IntPtr.Zero;
        _bits = null;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        ReleaseDib();
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hDc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
