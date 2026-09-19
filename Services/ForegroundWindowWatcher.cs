using System.Runtime.InteropServices;

namespace SpeedBar.Services;

/// <summary>
/// Observes foreground activation and resizing without injecting into other processes.
/// Create and dispose this watcher on the UI thread that owns its message loop.
/// </summary>
public sealed class ForegroundWindowWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;

    private readonly Action _changed;
    // The native hooks retain a function pointer, not a managed reference.
    private readonly WinEventProc _callback;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private bool _disposed;

    public ForegroundWindowWatcher(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        _changed = changed;
        _callback = OnWinEvent;

        // A failed hook returns zero; the regular taskbar timer remains the fallback.
        // Do not skip our process: opening settings also changes the foreground window.
        _foregroundHook = SetWinEventHook(
            EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback,
            0, 0, WineventOutOfContext);
        _locationHook = SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _callback,
            0, 0, WineventOutOfContext);
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_disposed) return;
        if (eventType != EventSystemForeground &&
            (eventType != EventObjectLocationChange ||
             objectId != ObjidWindow || childId != ChildidSelf ||
             hwnd == IntPtr.Zero || hwnd != GetForegroundWindow())) return;

        // OUTOFCONTEXT callbacks are delivered to the thread that installed the hooks.
        // The owner queues/coalesces its work to avoid reentering WPF from this callback.
        try
        {
            _changed();
        }
        catch (Exception exception)
        {
            // Managed exceptions must not escape through the native callback boundary.
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        GC.KeepAlive(_callback);
    }

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId,
        uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
