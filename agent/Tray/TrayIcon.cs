using System.Runtime.InteropServices;
using System.Text;

namespace SoporteLan.Agent.Tray;

internal sealed record TraySnapshot(
    string Machine,
    string Address,
    int Port,
    string Pin,
    int SecondsLeft,
    bool SessionActive,
    string? Remote);

/// <summary>
/// Icono en el área de notificación (junto al reloj) con menú contextual y
/// una ventana emergente que muestra el PIN. Todo con Win32: sin WinForms y
/// sin añadir megas al ejecutable.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const int WmPaint = 0x000F;
    private const int WmTimer = 0x0113;
    private const int WmActivate = 0x0006;
    private const int WmKeyDown = 0x0100;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmTray = 0x0400 + 1;

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;

    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private const int IdShow = 1;
    private const int IdCopy = 2;
    private const int IdExit = 3;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;

    private const int SwShow = 5;
    private const int SpiGetWorkArea = 0x0030;

    private const int VkEscape = 0x1B;

    private const uint CsDropShadow = 0x00020000;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    private const string TrayClass = "SoporteLanTrayWindow";
    private const string PopupClass = "SoporteLanPinWindow";

    private readonly Func<TraySnapshot> _snapshot;
    private readonly Action _exit;

    private Thread? _thread;
    private IntPtr _trayHandle;
    private IntPtr _popupHandle;
    private IntPtr _iconLarge;
    private IntPtr _iconSmall;

    private WndProc? _trayProc;
    private WndProc? _popupProc;
    private NotifyIconData _iconData;
    private string _lastTip = string.Empty;
    private long _popupShownAt;
    private bool _disposed;

    public TrayIcon(Func<TraySnapshot> snapshot, Action exit)
    {
        _snapshot = snapshot;
        _exit = exit;
    }

    public void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "soporte-tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Muestra la ventana del PIN (también se usa con --preview).</summary>
    public void ShowPin()
    {
        if (_trayHandle != IntPtr.Zero)
            PostMessageW(_trayHandle, WmTray, IntPtr.Zero, new IntPtr(WmLButtonUp));
    }

    private void Run()
    {
        try
        {
            RunCore();
        }
        catch (Exception exception)
        {
            Log.Error("Fallo en el icono de bandeja", exception);
        }
    }

    private void RunCore()
    {
        var instance = GetModuleHandleW(null);

        _trayProc = TrayWindowProc;
        var trayClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(),
            WindowProc = _trayProc,
            Instance = instance,
            ClassName = TrayClass,
        };

        if (RegisterClassExW(ref trayClass) == 0) return;

        _trayHandle = CreateWindowExW(
            0, TrayClass, "Soporte LAN", 0, 0, 0, 0, 0, MessageOnlyWindow, IntPtr.Zero, instance, IntPtr.Zero);

        if (_trayHandle == IntPtr.Zero) return;

        LoadIcon(instance, out _iconLarge, out _iconSmall);

        _iconData = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = _trayHandle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = WmTray,
            Icon = _iconLarge != IntPtr.Zero ? _iconLarge : _iconSmall,
            Tip = "Soporte LAN",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

        var added = Shell_NotifyIconW(NimAdd, ref _iconData);
        Log.Info($"Icono de bandeja agregado: {added}.");
        UpdateTip();

        SetTimer(_trayHandle, 1, 1000, IntPtr.Zero);

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private IntPtr TrayWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmTray:
            {
                var events = lParam.ToInt64() & 0xFFFF;
                if (events == WmLButtonUp) ShowPopup();
                else if (events == WmRButtonUp) ShowMenu();
                return IntPtr.Zero;
            }

            case WmTimer:
                UpdateTip();
                return IntPtr.Zero;

            case WmClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void UpdateTip()
    {
        if (_trayHandle == IntPtr.Zero) return;

        var snapshot = _snapshot();
        var tip = $"Soporte LAN - PIN {snapshot.Pin}";
        if (snapshot.SessionActive) tip = $"Soporte LAN - sesion con {snapshot.Remote}";
        if (tip == _lastTip) return;

        _lastTip = tip;
        _iconData.Tip = tip;
        Shell_NotifyIconW(NimModify, ref _iconData);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        AppendMenuW(menu, MfString, IdShow, "Mostrar PIN");
        AppendMenuW(menu, MfString, IdCopy, "Copiar PIN");
        AppendMenuW(menu, MfSeparator, 0, null);
        AppendMenuW(menu, MfString, IdExit, "Salir");

        SetMenuDefaultItem(menu, IdShow, 0);
        GetCursorPos(out var point);
        SetForegroundWindow(_trayHandle);

        var command = TrackPopupMenu(
            menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _trayHandle, IntPtr.Zero);

        DestroyMenu(menu);

        switch (command)
        {
            case IdShow:
                ShowPopup();
                break;
            case IdCopy:
                CopyToClipboard(_snapshot().Pin);
                break;
            case IdExit:
                _exit();
                PostQuitMessage(0);
                break;
        }
    }

    private void ShowPopup()
    {
        HidePopup();

        if (_popupProc is null)
        {
            _popupProc = PopupWindowProc;

            var windowClass = new WindowClass
            {
                Size = Marshal.SizeOf<WindowClass>(),
                Style = CsDropShadow,
                WindowProc = _popupProc,
                Instance = GetModuleHandleW(null),
                ClassName = PopupClass,
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                Log.Error($"No se pudo registrar la ventana del PIN ({Marshal.GetLastWin32Error()}).");
                return;
            }
        }

        var scale = GetDpiForSystem() / 96.0;
        var width = (int)Math.Round(380 * scale);
        var height = (int)Math.Round(210 * scale);

        if (!SystemParametersInfoW(SpiGetWorkArea, 0, out var workArea, 0)) return;

        var x = workArea.Right - width - (int)Math.Round(12 * scale);
        var y = workArea.Bottom - height - (int)Math.Round(12 * scale);

        _popupHandle = CreateWindowExW(
            WsExToolWindow | WsExTopmost,
            PopupClass,
            "Soporte LAN",
            WsPopup | WsVisible,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

        if (_popupHandle == IntPtr.Zero)
        {
            Log.Error($"No se pudo crear la ventana del PIN ({Marshal.GetLastWin32Error()}).");
            return;
        }

        Log.Info("Ventana del PIN mostrada.");
        ApplyRoundedCorners(_popupHandle);
        _popupShownAt = Environment.TickCount64;
        SetTimer(_popupHandle, 1, 500, IntPtr.Zero);
        ShowWindow(_popupHandle, SwShow);
        SetForegroundWindow(_popupHandle);
        SetFocus(_popupHandle);
    }

    private void HidePopup()
    {
        if (_popupHandle == IntPtr.Zero) return;
        KillTimer(_popupHandle, 1);
        DestroyWindow(_popupHandle);
        _popupHandle = IntPtr.Zero;
    }

    private IntPtr PopupWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmPaint:
                PaintPopup(hwnd);
                return IntPtr.Zero;

            case WmTimer:
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WmLButtonDown:
                if (Environment.TickCount64 - _popupShownAt > 250) HidePopup();
                return IntPtr.Zero;

            case WmKeyDown:
                if ((int)wParam == VkEscape) HidePopup();
                return IntPtr.Zero;

            case WmActivate:
                if (((int)wParam & 0xFFFF) == 0 && Environment.TickCount64 - _popupShownAt > 400)
                    HidePopup();
                return IntPtr.Zero;

            case WmClose:
                HidePopup();
                return IntPtr.Zero;

            case 0x0014: // WM_ERASEBKGND
                return new IntPtr(1);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void PaintPopup(IntPtr hwnd)
    {
        var snapshot = _snapshot();
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var size = (int)Math.Round(380 * scale);
        var height = (int)Math.Round(210 * scale);

        var hdc = GetDC(hwnd);
        if (hdc == IntPtr.Zero) return;

        try
        {
            var area = new Rect { Left = 0, Top = 0, Right = size, Bottom = height };

            var background = CreateSolidBrush(0x001A120E);
            FillRect(hdc, ref area, background);
            DeleteObject(background);

            var border = CreateSolidBrush(0x00413022);
            FrameRect(hdc, ref area, border);
            DeleteObject(border);

            SetBkMode(hdc, 1); // TRANSPARENT

            Draw(hdc, "SOPORTE LAN", 18, 14, 11, 700, 0x00AD998A, 340, scale);
            Draw(hdc, snapshot.Machine, 18, 32, 16, 600, 0x00F3EDE6, 340, scale);
            Draw(hdc, $"IP {snapshot.Address}  -  puerto {snapshot.Port}", 18, 56, 12, 400, 0x00AD998A, 344, scale);

            Draw(hdc, "PIN", 18, 88, 11, 700, 0x00AD998A, 60, scale);
            Draw(hdc, snapshot.Pin, 18, 102, 40, 700, 0x0097DC3D, 250, scale);

            var renew = snapshot.SecondsLeft >= 0 ? $"Renueva en {snapshot.SecondsLeft} s" : "PIN fijo";
            Draw(hdc, renew, 238, 116, 11, 400, 0x00AD998A, 130, scale);

            var status = snapshot.SessionActive ? $"Sesion activa - {snapshot.Remote}" : "Sin sesion";
            Draw(hdc, status, 18, 158, 12, 400, snapshot.SessionActive ? 0x0097DC3Du : 0x00AD998Au, 344, scale);

            Draw(hdc, "Clic para cerrar  -  clic derecho en el icono para mas opciones",
                18, 182, 10, 400, 0x008C7A6B, 344, scale);
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
            ValidateRect(hwnd, IntPtr.Zero);
        }
    }

    private static void Draw(
        IntPtr hdc, string text, int x, int y, int fontSize, int weight, uint color, int width, double scale)
    {
        var font = CreateFontW(
            -(int)Math.Round(fontSize * scale), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var previous = SelectObject(hdc, font);
        SetTextColor(hdc, color);

        var area = new Rect
        {
            Left = (int)Math.Round(x * scale),
            Top = (int)Math.Round(y * scale),
            Right = (int)Math.Round((x + width) * scale),
            Bottom = (int)Math.Round((y + fontSize + 8) * scale),
        };

        DrawTextW(hdc, text, -1, ref area, 0x8820); // LEFT | SINGLELINE | NOPREFIX | END_ELLIPSIS

        SelectObject(hdc, previous);
        DeleteObject(font);
    }

    private static void CopyToClipboard(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;

        try
        {
            EmptyClipboard();
            var handle = GlobalAlloc(0x0042, (UIntPtr)((text.Length + 1) * 2));
            if (handle == IntPtr.Zero) return;

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero) return;

            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(handle);
            SetClipboardData(13, handle); // CF_UNICODETEXT
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        var preference = DwmCornerRound;
        DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    private static void LoadIcon(IntPtr instance, out IntPtr large, out IntPtr small)
    {
        large = IntPtr.Zero;
        small = IntPtr.Zero;

        var executable = Environment.ProcessPath;
        if (executable is not null)
            ExtractIconEx(executable, 0, out large, out small, 1);

        if (small == IntPtr.Zero)
            small = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION

        if (large == IntPtr.Zero) large = small;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_trayHandle != IntPtr.Zero)
        {
            Shell_NotifyIconW(NimDelete, ref _iconData);
            PostMessageW(_trayHandle, WmClose, IntPtr.Zero, IntPtr.Zero);
            _trayHandle = IntPtr.Zero;
        }

        if (_iconSmall != IntPtr.Zero) DestroyIcon(_iconSmall);
        if (_iconLarge != IntPtr.Zero && _iconLarge != _iconSmall) DestroyIcon(_iconLarge);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public int Size;
        public uint Style;
        public WndProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Hwnd;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Cursor;
        public uint Private;
    }

    private static readonly IntPtr MessageOnlyWindow = new(-3);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out Message message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern ulong SetTimer(IntPtr hwnd, ulong id, uint interval, IntPtr callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, ulong id);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int length, ref Rect rect, uint format);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(
        uint action, uint param, out Rect rect, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
