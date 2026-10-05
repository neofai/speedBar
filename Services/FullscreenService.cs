using System.Runtime.InteropServices;
using System.Text;

namespace SpeedBar.Services;

/// <summary>
/// Combines the shell's presentation state with physical-pixel window geometry.
/// Only the foreground/owner chain and one previously seen fullscreen HWND are inspected.
/// </summary>
public sealed class FullscreenService
{
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsCaption = 0x00c00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private readonly uint _processId = (uint)Environment.ProcessId;

    private IntPtr _trackedFullscreen;
    private uint _trackedProcessId;
    private IntPtr _trackedMonitor;

    public bool IsSuppressed(IntPtr overlay, IntPtr taskbar)
    {
        // These states apply to the session, including an exclusive game on another screen.
        // A failed shell query does not prevent the independent geometry checks below.
        var shellSuppressed = SHQueryUserNotificationState(out var state) >= 0 &&
            state is UserNotificationState.NotPresent or UserNotificationState.Busy or
                UserNotificationState.RunningD3DFullScreen or UserNotificationState.PresentationMode;

        // WPF may call us from a system-DPI-aware thread. DWM frame bounds are always
        // physical pixels, so temporarily use the same coordinate space for all native reads.
        var previousDpiContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return IsGeometrySuppressed(overlay, taskbar) || shellSuppressed;
        }
        finally
        {
            if (previousDpiContext != IntPtr.Zero)
                SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    public void Reset()
    {
        _trackedFullscreen = IntPtr.Zero;
        _trackedProcessId = 0;
        _trackedMonitor = IntPtr.Zero;
    }

    private bool IsGeometrySuppressed(IntPtr overlay, IntPtr taskbar)
    {
        var anchor = IsWindow(taskbar) ? taskbar : overlay;
        var monitor = MonitorFromWindow(anchor, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            Reset();
            return false;
        }

        if (_trackedMonitor != monitor) Reset();
        var bounds = info.Monitor.ToBounds();
        var foreground = GetAncestor(GetForegroundWindow(), GaRoot);
        var fullscreen = FindFullscreenOwner(foreground, overlay, bounds);
        if (fullscreen != IntPtr.Zero)
        {
            _trackedFullscreen = fullscreen;
            GetWindowThreadProcessId(fullscreen, out _trackedProcessId);
            _trackedMonitor = monitor;
            return true;
        }

        // Preserve suppression when focus moves to another monitor or a temporary game
        // dialog. HWND/process/geometry checks prevent keeping a closed or reused handle.
        GetWindowThreadProcessId(_trackedFullscreen, out var processId);
        if (_trackedFullscreen == IntPtr.Zero || processId == 0 || processId != _trackedProcessId ||
            !IsCandidate(_trackedFullscreen, overlay) || !IsFullscreen(_trackedFullscreen, bounds))
        {
            Reset();
            return false;
        }

        if (IsOrdinaryForegroundCoveringGame(foreground, overlay, monitor, bounds))
        {
            Reset();
            return false;
        }

        return true;
    }

    private IntPtr FindFullscreenOwner(IntPtr foreground, IntPtr overlay, PixelBounds monitor)
    {
        // A popup's GA_ROOT is the popup, not its owner. Follow a bounded owner chain so
        // a game's dialog does not briefly bring the taskbar overlay back over the game.
        for (var depth = 0; foreground != IntPtr.Zero && depth < 16; depth++)
        {
            if (IsCandidate(foreground, overlay) && IsFullscreen(foreground, monitor))
                return foreground;
            var owner = GetWindow(foreground, GwOwner);
            if (owner == foreground) break;
            foreground = owner;
        }

        return IntPtr.Zero;
    }

    private bool IsCandidate(IntPtr hwnd, IntPtr overlay)
    {
        if (hwnd == IntPtr.Zero || hwnd == overlay || !IsWindow(hwnd) ||
            !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == _processId) return false;

        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) < 0 || cloaked == 0;
    }

    private static bool IsFullscreen(IntPtr hwnd, PixelBounds monitor)
    {
        var style = GetWindowLong(hwnd, GwlStyle);
        var hasWindowFrame = (style & (WsCaption | WsThickFrame)) != 0;
        // In auto-hide mode a maximized framed window can reach the monitor edges.
        // Maximizing such a window is still not the same as entering fullscreen.
        if (IsZoomed(hwnd) && hasWindowFrame) return false;
        if (TryGetClientBounds(hwnd, out var client) && CoversMonitor(client, monitor)) return true;

        // Borderless DirectComposition windows may expose an unhelpful client rect.
        // Never use frame bounds for a framed/maximized app: invisible resize borders
        // and work-area bounds otherwise produce false fullscreen matches.
        return !hasWindowFrame &&
            DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RectNative frame,
                Marshal.SizeOf<RectNative>()) >= 0 && CoversMonitor(frame.ToBounds(), monitor);
    }

    private bool IsOrdinaryForegroundCoveringGame(
        IntPtr foreground, IntPtr overlay, IntPtr monitor, PixelBounds monitorBounds)
    {
        if (!IsCandidate(foreground, overlay) || MonitorFromWindow(foreground, 0) != monitor)
            return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == _trackedProcessId) return false;
        var extendedStyle = GetWindowLong(foreground, GwlExStyle);
        if ((extendedStyle & (WsExToolWindow | WsExNoActivate)) != 0) return false;
        if (!TryGetClientBounds(foreground, out var client)) return false;

        // Alt-Tab to an ordinary app clearly covering this screen should restore the bar.
        // Small transient popups are insufficient evidence that the game is no longer visible.
        var width = Math.Max(0L, (long)Math.Min(client.Right, monitorBounds.Right) -
            Math.Max(client.Left, monitorBounds.Left));
        var height = Math.Max(0L, (long)Math.Min(client.Bottom, monitorBounds.Bottom) -
            Math.Max(client.Top, monitorBounds.Top));
        var monitorWidth = (long)monitorBounds.Right - monitorBounds.Left;
        var monitorHeight = (long)monitorBounds.Bottom - monitorBounds.Top;
        var centerX = ((long)monitorBounds.Left + monitorBounds.Right) / 2;
        var centerY = ((long)monitorBounds.Top + monitorBounds.Bottom) / 2;
        return client.Left <= centerX && client.Right >= centerX &&
            client.Top <= centerY && client.Bottom >= centerY &&
            width * (double)height >= monitorWidth * (double)monitorHeight * 0.1;
    }

    private static bool TryGetClientBounds(IntPtr hwnd, out PixelBounds bounds)
    {
        bounds = default;
        if (!GetClientRect(hwnd, out var client)) return false;
        var topLeft = new PointNative { X = client.Left, Y = client.Top };
        var bottomRight = new PointNative { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight)) return false;
        bounds = new PixelBounds(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    public readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom);

    /// <summary>Tests physical-pixel coverage, including negative-origin and mixed-DPI monitors.</summary>
    public static bool CoversMonitor(PixelBounds bounds, PixelBounds monitor, int tolerance = 2)
    {
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top ||
            monitor.Right <= monitor.Left || monitor.Bottom <= monitor.Top) return false;
        tolerance = Math.Clamp(tolerance, 0, 2);
        return (long)bounds.Left <= (long)monitor.Left + tolerance &&
            (long)bounds.Top <= (long)monitor.Top + tolerance &&
            (long)bounds.Right >= (long)monitor.Right - tolerance &&
            (long)bounds.Bottom >= (long)monitor.Bottom - tolerance;
    }

    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly PixelBounds ToBounds() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState state);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref PointNative point);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RectNative value, int size);
}
