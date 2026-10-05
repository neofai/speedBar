using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SpeedBar.Services;

/// <summary>Native geometry and placement only. Visibility is owned by TaskbarController.</summary>
public static class TaskbarService
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x8;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;
    private const uint PositionFlags = 0x0010 | 0x0200; // NOACTIVATE | NOOWNERZORDER
    private static long _alignmentCheckedAt = -5000;
    private static bool _leftAligned;

    public static void PrepareWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExToolWindow | WsExNoActivate));
        // Keep the popup independent: destroying Explorer must not destroy our HWND.
        SetWindowLongPtr(hwnd, -8, IntPtr.Zero);
    }

    public static bool HasValidWindow(Window window) => IsWindow(new WindowInteropHelper(window).Handle);
    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    public static bool TryGetSnapshot(out TaskbarSnapshot snapshot)
    {
        snapshot = default;
        var taskbar = FindTaskbar();
        if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) ||
            !GetWindowRect(taskbar, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0)
            return false;
        if (DwmGetWindowAttribute(taskbar, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        var monitor = MonitorFromWindow(taskbar, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        // Auto-hide leaves a thin strip or moves the taskbar outside the display.
        if (bounds.Left < info.Monitor.Left || bounds.Top < info.Monitor.Top ||
            bounds.Right > info.Monitor.Right || bounds.Bottom > info.Monitor.Bottom ||
            Math.Min(bounds.Width, bounds.Height) < 16) return false;
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var trayLeft = tray != IntPtr.Zero && GetWindowRect(tray, out var trayBounds)
            ? Math.Clamp(trayBounds.Left, bounds.Left, bounds.Right) : bounds.Right;
        var dpi = GetDpiForWindow(taskbar);
        snapshot = new(taskbar, bounds, dpi == 0 ? 96u : dpi, trayLeft, IsTaskbarLeftAligned());
        return true;
    }

    public static bool IsTaskbarLeftAligned()
    {
        var now = Environment.TickCount64;
        if (now - _alignmentCheckedAt < 2000) return _leftAligned;
        _alignmentCheckedAt = now;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            _leftAligned = key?.GetValue("TaskbarAl") is int value && value == 0;
        }
        catch { _leftAligned = false; }
        return _leftAligned;
    }

    public static PixelRect? GetPlacement(TaskbarSnapshot taskbar, System.Windows.Size contentSize,
        double offsetX, double offsetY, bool dockLeft, IReadOnlyList<HorizontalRange> occupied)
    {
        var scale = taskbar.Dpi / 96.0;
        var bounds = taskbar.Bounds;
        // The Viewbox keeps large font settings inside the physical taskbar height.
        var fit = Math.Min(1, bounds.Height / Math.Max(1, contentSize.Height * scale));
        var width = Math.Max(1, (int)Math.Ceiling(contentSize.Width * scale * fit));
        var height = Math.Max(1, (int)Math.Floor(contentSize.Height * scale * fit));
        if (bounds.Width < bounds.Height || width > bounds.Width || height > bounds.Height) return null;
        var margin = (int)Math.Round(Math.Max(0, offsetX) * scale);
        var ranges = occupied.Append(new HorizontalRange(taskbar.TrayLeft, bounds.Right));
        var x = TaskbarPlacement.FindFreePosition(bounds.Left, bounds.Right, ranges, width,
            margin, dockLeft && !taskbar.LeftAligned);
        if (x is null) return null;
        var y = Math.Clamp(bounds.Top + (bounds.Height - height) / 2 - (int)Math.Round(offsetY * scale),
            bounds.Top, bounds.Bottom - height);
        return new PixelRect(x.Value, y, x.Value + width, y + height);
    }

    public static bool Place(Window window, PixelRect bounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!IsWindow(hwnd)) return false;
        if (GetWindowRect(hwnd, out var current) && current.Equals(bounds)) return true;
        return SetWindowPos(hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            PositionFlags | 0x0004); // NOZORDER; deliberately no SHOWWINDOW
    }

    public static void SetTopmost(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!IsWindow(hwnd)) return;
        var topmost = (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0;
        if (topmost == enabled) return;
        SetWindowPos(hwnd, new IntPtr(enabled ? -1 : -2), 0, 0, 0, 0,
            PositionFlags | 0x0001 | 0x0002); // NOSIZE | NOMOVE; never reveal here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public PixelRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? name);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out PixelRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern IntPtr GetWindowLongPtr32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern IntPtr SetWindowLongPtr32(IntPtr hwnd, int index, IntPtr value);
    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLongPtr32(hwnd, index);
    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLongPtr32(hwnd, index, value);
}

public readonly record struct TaskbarSnapshot(IntPtr Handle, PixelRect Bounds, uint Dpi, int TrayLeft, bool LeftAligned);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
