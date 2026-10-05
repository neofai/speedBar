using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SpeedBar.Models;

namespace SpeedBar.Services;

/// <summary>One UI-thread owner for overlay visibility, shell recovery and layout work.</summary>
public sealed class TaskbarController : IDisposable
{
    private readonly Window _window;
    private readonly Func<AppSettings> _settings;
    private readonly TaskbarLayoutService _layout = new();
    private readonly FullscreenService _fullscreen = new();
    private readonly OverlayVisibilityPolicy _visibility = new();
    private readonly DispatcherTimer _timer;
    private readonly ForegroundWindowWatcher _watcher;
    private readonly HwndSource? _source;
    private readonly uint _taskbarCreated;
    private IReadOnlyList<HorizontalRange>? _ranges;
    private TaskbarSnapshot _snapshot;
    private System.Windows.Size? _naturalSize;
    private long _nextScan;
    private long _rangesValidUntil;
    private int _generation;
    private bool _scanRunning;
    private bool _queued;
    private bool _blocked = true;
    private bool _paused;
    private bool _sessionUnavailable;
    private bool _disposed;

    public bool IsDisplayActive { get; private set; }
    public bool IsPaused => _paused;
    public event Action<bool>? DisplayActiveChanged;

    public TaskbarController(Window window, Func<AppSettings> settings)
    {
        _window = window;
        _settings = settings;
        TaskbarService.PrepareWindow(window);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(WindowProcedure);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _watcher = new ForegroundWindowWatcher(QueueRefresh);
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick += OnTick;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _timer.Start();
        Refresh();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        InvalidateLayout();
    }

    public void InvalidateLayout()
    {
        if (_disposed) return;
        _generation++;
        _ranges = null;
        _naturalSize = null;
        _nextScan = 0;
        _visibility.Reset();
        Suppress();
        QueueRefresh();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void QueueRefresh()
    {
        if (_disposed || _queued || _window.Dispatcher.HasShutdownStarted) return;
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _queued = false;
            if (!_disposed) Refresh();
        });
    }

    private void Refresh()
    {
        if (_disposed) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        var available = TaskbarService.TryGetSnapshot(out var snapshot);
        var blocked = _paused || _sessionUnavailable || !available ||
            _fullscreen.IsSuppressed(hwnd, snapshot.Handle);
        if (snapshot != _snapshot)
        {
            _snapshot = snapshot;
            _generation++;
            _ranges = null;
            _nextScan = 0;
        }
        if (blocked && !_blocked)
        {
            _generation++;
            _ranges = null;
            _nextScan = 0;
        }
        _blocked = blocked;
        if (!_visibility.ShouldShow(blocked, Environment.TickCount64))
        {
            Suppress();
            return;
        }
        if (!_scanRunning && Environment.TickCount64 >= _nextScan) ScanLayoutAsync();
        if (_ranges is null || Environment.TickCount64 >= _rangesValidUntil)
        {
            Suppress();
            return;
        }
        var settings = _settings();
        if (_naturalSize is null)
        {
            var content = (FrameworkElement)_window.Content;
            content.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            _naturalSize = content.DesiredSize;
        }
        var placement = TaskbarService.GetPlacement(snapshot, _naturalSize.Value,
            settings.OffsetX, settings.OffsetY, settings.DockLeft, _ranges);
        if (placement is null || !TaskbarService.Place(_window, placement.Value))
        {
            Suppress();
            return;
        }
        // A layout result is never allowed to show a window directly. Every path
        // passes through fresh fullscreen and taskbar checks above.
        if (!_window.IsVisible) _window.Show();
        _window.Opacity = 1;
        // Only promote on a hidden -> visible transition. Do not fight Explorer
        // if it demotes us while the overlay is already visible.
        if (!IsDisplayActive) TaskbarService.SetTopmost(_window, true);
        SetDisplayActive(true);
    }

    private async void ScanLayoutAsync()
    {
        _scanRunning = true;
        _nextScan = Environment.TickCount64 + 5000;
        var generation = _generation;
        var taskbar = _snapshot.Handle;
        try
        {
            var ranges = await _layout.ScanOccupiedRangesAsync(taskbar);
            if (_disposed || generation != _generation || _blocked || taskbar != TaskbarService.FindTaskbar()) return;
            // An unsuccessful scan must not keep stale positions over new buttons.
            _ranges = ranges;
            _rangesValidUntil = Environment.TickCount64 + 15000;
            QueueRefresh();
        }
        finally { _scanRunning = false; }
    }

    private void Suppress()
    {
        if (_window.IsVisible) _window.Hide();
        TaskbarService.SetTopmost(_window, false);
        SetDisplayActive(false);
    }

    private void SetDisplayActive(bool active)
    {
        if (IsDisplayActive == active) return;
        IsDisplayActive = active;
        DisplayActiveChanged?.Invoke(active);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated || message is 0x007E or 0x02E0) // display/DPI
            _window.Dispatcher.BeginInvoke(InvalidateLayout);
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE: clicks work without stealing focus
        }
        return IntPtr.Zero;
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => DispatchInvalidate();
    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => DispatchInvalidate();
    private void DispatchInvalidate()
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(InvalidateLayout);
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                _sessionUnavailable = true;
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                _sessionUnavailable = false;
            InvalidateLayout();
        });
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.Mode == PowerModes.Suspend) _sessionUnavailable = true;
            if (e.Mode == PowerModes.Resume) _sessionUnavailable = false;
            InvalidateLayout();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _watcher.Dispose();
        _layout.Dispose();
        _source?.RemoveHook(WindowProcedure);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
