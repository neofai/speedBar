using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SpeedBar.Services;

public static class TaskbarService
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;

#if DEBUG
    static TaskbarService()
    {
        var taskbar = new RectNative { Left = 0, Top = 0, Right = 1000, Bottom = 48 };
        HorizontalRange[] occupied = [new(200, 300), new(700, 800)];
        System.Diagnostics.Debug.Assert(FindFreePosition(taskbar, occupied, 100, 10, true) == 10);
        System.Diagnostics.Debug.Assert(FindFreePosition(taskbar, occupied, 100, 10, false) == 890);
    }
#endif

    public static void PrepareWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    public static bool TryEmbedIntoTaskbar(
        Window window,
        double offsetX,
        bool dockLeft,
        IReadOnlyList<HorizontalRange>? occupiedRanges = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var taskbar = FindTaskbar();
        if (hwnd == IntPtr.Zero || taskbar == IntPtr.Zero) return false;

        if (!IsEmbedded(window))
        {
            var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            style = (style | WsChild) & ~WsPopup;
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            exStyle = (exStyle | WsExToolWindow | WsExNoActivate) & ~WsExTopmost;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));

            SetParent(hwnd, taskbar);
            if (GetParent(hwnd) != taskbar) return false;
        }

        PlaceEmbedded(window, offsetX, dockLeft, occupiedRanges);
        return true;
    }

    public static bool IsEmbedded(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        return (style & WsChild) != 0 && GetParent(hwnd) == FindTaskbar();
    }

    public static void PlaceEmbedded(
        Window window,
        double offsetX,
        bool dockLeft,
        IReadOnlyList<HorizontalRange>? occupiedRanges = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var taskbar = FindTaskbar();
        if (hwnd == IntPtr.Zero || taskbar == IntPtr.Zero || GetParent(hwnd) != taskbar) return;
        if (!GetWindowRect(taskbar, out var taskbarRect)) return;

        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * toDevice.M11));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * toDevice.M22));
        var margin = (int)Math.Round(Math.Max(0, offsetX) * toDevice.M11);
        var useLeft = dockLeft && !IsTaskbarLeftAligned();
        var screenX = occupiedRanges is { Count: > 0 }
            ? FindFreePosition(taskbarRect, occupiedRanges, width, margin, useLeft)
            : useLeft
                ? taskbarRect.Left + margin
                : GetTrayLeft(taskbar, taskbarRect) - width - margin;
        screenX = Math.Clamp(
            screenX,
            taskbarRect.Left,
            Math.Max(taskbarRect.Left, taskbarRect.Right - width));
        var screenY = taskbarRect.Top + Math.Max(0, (taskbarRect.Height - height) / 2);
        var clientPoint = new PointNative { X = screenX, Y = screenY };
        ScreenToClient(taskbar, ref clientPoint);

        SetWindowPos(
            hwnd,
            HwndTop,
            clientPoint.X,
            clientPoint.Y,
            width,
            Math.Min(height, taskbarRect.Height),
            SwpNoActivate | SwpShowWindow);
    }

    private static int FindFreePosition(
        RectNative taskbarRect,
        IReadOnlyList<HorizontalRange> occupiedRanges,
        int windowWidth,
        int margin,
        bool dockLeft)
    {
        var ranges = occupiedRanges
            .Select(range => new HorizontalRange(
                Math.Clamp(range.Left, taskbarRect.Left, taskbarRect.Right),
                Math.Clamp(range.Right, taskbarRect.Left, taskbarRect.Right)))
            .Where(range => range.Right > range.Left)
            .OrderBy(range => range.Left)
            .ToArray();

        double cursor = taskbarRect.Left;
        double? rightmostPosition = null;
        foreach (var range in ranges)
        {
            if (range.Left - cursor >= windowWidth + margin * 2)
            {
                if (dockLeft)
                    return (int)Math.Round(cursor + margin);
                rightmostPosition = range.Left - margin - windowWidth;
            }
            cursor = Math.Max(cursor, range.Right);
        }

        if (taskbarRect.Right - cursor >= windowWidth + margin * 2)
        {
            if (dockLeft)
                return (int)Math.Round(cursor + margin);
            rightmostPosition = taskbarRect.Right - margin - windowWidth;
        }

        return rightmostPosition is double value
            ? (int)Math.Round(value)
            : dockLeft
                ? taskbarRect.Left + margin
                : GetTrayLeft(FindTaskbar(), taskbarRect) - windowWidth - margin;
    }

    public static bool IsTaskbarLeftAligned()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void UseOwnedOverlay(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var taskbar = FindTaskbar();
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style = (style | WsPopup) & ~WsChild;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
        SetParent(hwnd, IntPtr.Zero);
        if (taskbar != IntPtr.Zero)
            SetWindowLongPtr(hwnd, -8, taskbar);

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExToolWindow | WsExNoActivate | WsExTopmost;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
        SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            0x0001 | 0x0002 | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public static void PlaceNearTaskbar(
        Window window,
        double offsetX,
        double offsetY,
        bool dockLeft)
    {
        var data = new AppBarData { CbSize = Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(5, ref data) == 0) return;

        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(data.Rect.Left, data.Rect.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(data.Rect.Right, data.Rect.Bottom));
        var taskbarWidth = bottomRight.X - topLeft.X;
        var taskbarHeight = bottomRight.Y - topLeft.Y;

        if (taskbarWidth >= taskbarHeight)
        {
            if (dockLeft && !IsTaskbarLeftAligned())
            {
                window.Left = topLeft.X + offsetX;
            }
            else
            {
                var trayLeft = bottomRight.X;
                var taskbar = FindTaskbar();
                var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect))
                    trayLeft = transform.Transform(new System.Windows.Point(trayRect.Left, trayRect.Top)).X;
                window.Left = trayLeft - window.ActualWidth - offsetX;
            }
            window.Top = topLeft.Y + Math.Max(0, (taskbarHeight - window.ActualHeight) / 2) - offsetY;
        }
        else
        {
            window.Left = topLeft.X + Math.Max(0, (taskbarWidth - window.ActualWidth) / 2);
            window.Top = bottomRight.Y - window.ActualHeight - offsetY;
        }
    }

    private static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    private static int GetTrayLeft(IntPtr taskbar, RectNative taskbarRect)
    {
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect)
            ? trayRect.Left
            : taskbarRect.Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int CbSize;
        public IntPtr HWnd;
        public uint CallbackMessage;
        public uint Edge;
        public RectNative Rect;
        public IntPtr LParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hwnd, int index, IntPtr value);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLongPtr32(hwnd, index);

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLongPtr32(hwnd, index, value);
}
