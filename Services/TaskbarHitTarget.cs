using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SpeedBar.Services;

public sealed class TaskbarHitTarget : IDisposable
{
    private const string ClassName = "SpeedBar.TaskbarHitTarget";
    private static readonly WindowProc WindowProcedure = WndProc;
    private static readonly Dictionary<IntPtr, TaskbarHitTarget> Instances = [];
    private static ushort _classAtom;

    private IntPtr _handle;

    public Action<int>? LeftButtonDown { get; init; }
    public Action? RightButtonUp { get; init; }

    public IntPtr Handle => _handle;

    public void UpdateFromWindow(Window window)
    {
        var content = new WindowInteropHelper(window).Handle;
        var parent = GetParent(content);
        if (content == IntPtr.Zero || parent == IntPtr.Zero ||
            !GetWindowRect(content, out var rect))
            return;

        EnsureCreated(parent);
        if (_handle == IntPtr.Zero) return;

        var topLeft = new PointNative { X = rect.Left, Y = rect.Top };
        ScreenToClient(parent, ref topLeft);
        SetWindowPos(
            _handle,
            IntPtr.Zero, // HWND_TOP within taskbar children, never above taskbar menus
            topLeft.X,
            topLeft.Y,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            0x0010 | 0x0040); // SWP_NOACTIVATE | SWP_SHOWWINDOW
    }

    private void EnsureCreated(IntPtr parent)
    {
        if (_handle != IntPtr.Zero && IsWindow(_handle) && GetParent(_handle) == parent)
            return;

        if (_handle != IntPtr.Zero && IsWindow(_handle))
            DestroyWindow(_handle);

        RegisterWindowClass();
        _handle = CreateWindowEx(
            0x08000000U | 0x00080000U, // NOACTIVATE | LAYERED
            ClassName,
            null,
            0x80000000U, // Create an ownerless, hidden popup before cross-process parenting
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (_handle == IntPtr.Zero) return;

        SetWindowLong(_handle, -16, 0x40000000); // GWL_STYLE, WS_CHILD
        SetParent(_handle, parent);
        if (GetParent(_handle) != parent)
        {
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
            return;
        }

        Instances[_handle] = this;
        SetLayeredWindowAttributes(_handle, 0, 1, 0x00000002); // LWA_ALPHA
    }

    private static void RegisterWindowClass()
    {
        if (_classAtom != 0) return;
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = 0x0008, // CS_DBLCLKS
            WindowProc = WindowProcedure,
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), // IDC_ARROW
            ClassName = ClassName
        };
        _classAtom = RegisterClassEx(ref windowClass);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var target))
        {
            switch (message)
            {
                case 0x0201: // WM_LBUTTONDOWN
                    target.LeftButtonDown?.Invoke(1);
                    return IntPtr.Zero;
                case 0x0203: // WM_LBUTTONDBLCLK
                    target.LeftButtonDown?.Invoke(2);
                    return IntPtr.Zero;
                case 0x0205: // WM_RBUTTONUP
                    target.RightButtonUp?.Invoke();
                    return IntPtr.Zero;
                case 0x0020: // WM_SETCURSOR
                    SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));
                    return new IntPtr(1);
                case 0x0014: // WM_ERASEBKGND
                    return new IntPtr(1);
                case 0x000F: // WM_PAINT
                    var dc = BeginPaint(hwnd, out var paint);
                    GetClientRect(hwnd, out var client);
                    FillRect(dc, ref client, GetStockObject(4)); // BLACK_BRUSH, then alpha=1
                    EndPaint(hwnd, ref paint);
                    return IntPtr.Zero;
                case 0x0082: // WM_NCDESTROY
                    Instances.Remove(hwnd);
                    target._handle = IntPtr.Zero;
                    break;
            }
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        Hide();
    }

    public void Hide()
    {
        if (_handle != IntPtr.Zero && IsWindow(_handle))
            DestroyWindow(_handle);
        Instances.Remove(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WindowProc WindowProc;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr DeviceContext;
        public bool Erase;
        public RectNative Paint;
        public bool Restore;
        public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr deviceContext, ref RectNative rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectIndex);
}
