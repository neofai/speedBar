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
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
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
        if (!IsWindow(hwnd) || !TryGetTaskbarRect(taskbar, out _) ||
            !IsWindowVisible(taskbar)) return false;

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

        return PlaceEmbedded(window, offsetX, dockLeft, occupiedRanges);
    }

    public static bool HasValidWindow(Window window) =>
        IsWindow(new WindowInteropHelper(window).Handle);

    public static bool IsEmbedded(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var taskbar = FindTaskbar();
        if (!IsWindow(hwnd) || taskbar == IntPtr.Zero || !IsWindow(taskbar)) return false;
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        return (style & WsChild) != 0 && GetParent(hwnd) == taskbar;
    }

    public static bool PlaceEmbedded(
        Window window,
        double offsetX,
        bool dockLeft,
        IReadOnlyList<HorizontalRange>? occupiedRanges = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var taskbar = FindTaskbar();
        if (!IsWindow(hwnd) || !TryGetTaskbarRect(taskbar, out var taskbarRect) ||
            GetParent(hwnd) != taskbar) return false;

        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        // Native taskbar clipping must not become the next requested WPF size.
        var contentSize = (window.Content as FrameworkElement)?.DesiredSize ?? default;
        var width = Math.Max(1, (int)Math.Ceiling(Math.Max(window.ActualWidth, contentSize.Width) * toDevice.M11));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Max(window.ActualHeight, contentSize.Height) * toDevice.M22));
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
        if (!ScreenToClient(taskbar, ref clientPoint)) return false;

        return SetWindowPos(
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
        if (!IsWindow(hwnd)) return;

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var wasChild = (style & WsChild) != 0;
        var ownerChanged = wasChild || GetWindowLongPtr(hwnd, -8) != taskbar;
        if (wasChild) SetParent(hwnd, IntPtr.Zero);
        var popupStyle = (style | WsPopup) & ~WsChild;
        if (style != popupStyle) SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(popupStyle));
        if (ownerChanged)
            SetWindowLongPtr(hwnd, -8, taskbar); // Clear a stale owner while Explorer is unavailable.

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var overlayStyle = exStyle | WsExToolWindow | WsExNoActivate;
        if (exStyle != overlayStyle) SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(overlayStyle));
        // Raise only when attaching to a new taskbar. Repeated HWND_TOPMOST calls
        // would cover menus and fullscreen windows which have since moved above us.
        if (!ownerChanged && !wasChild && style == popupStyle && exStyle == overlayStyle) return;
        SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            0x0001 | 0x0002 | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged | SwpShowWindow);
    }

    public static void PlaceNearTaskbar(
        Window window,
        double offsetX,
        double offsetY,
        bool dockLeft,
        IReadOnlyList<HorizontalRange>? occupiedRanges = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!IsWindow(hwnd)) return;
        var taskbar = FindTaskbar();
        var data = new AppBarData { CbSize = Marshal.SizeOf<AppBarData>() };
        if (!TryGetTaskbarRect(taskbar, out var taskbarRect))
        {
            if (SHAppBarMessage(5, ref data) == 0 || data.Rect.Width <= 0 || data.Rect.Height <= 0) return;
            taskbarRect = data.Rect;
        }

        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var contentSize = (window.Content as FrameworkElement)?.DesiredSize ?? default;
        var width = Math.Max(1, (int)Math.Ceiling(Math.Max(window.ActualWidth, contentSize.Width) * toDevice.M11));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Max(window.ActualHeight, contentSize.Height) * toDevice.M22));
        var margin = (int)Math.Round(Math.Max(0, offsetX) * toDevice.M11);
        var verticalOffset = (int)Math.Round(offsetY * toDevice.M22);
        int x, y;

        if (taskbarRect.Width >= taskbarRect.Height)
        {
            var useLeft = dockLeft && !IsTaskbarLeftAligned();
            var trayLeft = GetTrayLeft(taskbar, taskbarRect);
            if (occupiedRanges is { Count: > 0 })
            {
                var ranges = occupiedRanges.Append(new HorizontalRange(trayLeft, taskbarRect.Right)).ToArray();
                x = FindFreePosition(taskbarRect, ranges, width, margin, useLeft);
            }
            else
                x = useLeft ? taskbarRect.Left + margin : trayLeft - width - margin;
            x = Math.Clamp(x, taskbarRect.Left, Math.Max(taskbarRect.Left, taskbarRect.Right - width));
            y = taskbarRect.Top + Math.Max(0, (taskbarRect.Height - height) / 2) - verticalOffset;
        }
        else
        {
            x = taskbarRect.Left + Math.Max(0, (taskbarRect.Width - width) / 2);
            y = taskbarRect.Bottom - height - verticalOffset;
        }
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
    }

    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    private static bool TryGetTaskbarRect(IntPtr taskbar, out RectNative rect)
    {
        rect = default;
        return taskbar != IntPtr.Zero && IsWindow(taskbar) &&
            GetWindowRect(taskbar, out rect) && rect.Width > 0 && rect.Height > 0 &&
            GetClientRect(taskbar, out var client) && client.Width > 0 && client.Height > 0;
    }

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
    private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

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
