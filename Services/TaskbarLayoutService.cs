using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SpeedBar.Services;

/// <summary>
/// Serializes Explorer UI Automation calls on one background MTA thread.
/// A busy or unavailable provider never creates another worker or queues more scans.
/// </summary>
public sealed class TaskbarLayoutService : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private ScanRequest? _request;
    private bool _disposed;

    public TaskbarLayoutService()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SpeedBar.TaskbarLayout"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public Task<IReadOnlyList<HorizontalRange>?> ScanOccupiedRangesAsync(IntPtr taskbar)
    {
        lock (_gate)
        {
            if (_disposed || taskbar == IntPtr.Zero || _request is not null)
                return Task.FromResult<IReadOnlyList<HorizontalRange>?>(null);

            _request = new ScanRequest(taskbar);
            _wake.Set();
            return _request.Completion.Task;
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                ScanRequest? request;
                lock (_gate)
                {
                    if (_disposed) return;
                    request = _request;
                }
                if (request is null) continue;

                IReadOnlyList<HorizontalRange>? ranges;
                try
                {
                    ranges = ScanOccupiedRanges(request.Taskbar);
                }
                catch (Exception exception)
                {
                    // Explorer can disappear or its accessibility provider can fail.
                    System.Diagnostics.Debug.WriteLine(exception);
                    ranges = null;
                }

                lock (_gate)
                {
                    _request = null;
                    if (_disposed) ranges = null;
                }
                request.Completion.TrySetResult(ranges);
            }
        }
        finally
        {
            // Only the worker disposes this handle, after it stops waiting. Dispose
            // never interrupts a COM call or blocks the UI waiting for Explorer.
            _wake.Dispose();
        }
    }

    private static IReadOnlyList<HorizontalRange>? ScanOccupiedRanges(IntPtr taskbar)
    {
        if (!IsWindow(taskbar) || GetWindowThreadProcessId(taskbar, out var explorerProcessId) == 0)
            return null;

        var root = AutomationElement.FromHandle(taskbar);
        var cache = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.None,
            TreeScope = TreeScope.Element
        };
        cache.Add(AutomationElement.ProcessIdProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        AutomationElementCollection buttons;
        using (cache.Activate())
            buttons = root.FindAll(TreeScope.Descendants, condition);

        var ranges = new List<HorizontalRange>(buttons.Count);
        foreach (AutomationElement button in buttons)
        {
            var cached = button.Cached;
            if (cached.ProcessId != explorerProcessId || cached.IsOffscreen) continue;
            var bounds = cached.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2 ||
                !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Right)) continue;
            ranges.Add(new HorizontalRange(bounds.Left, bounds.Right));
        }
        return ranges;
    }

    public void Dispose()
    {
        ScanRequest? request;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            request = _request;
            _request = null;
            _wake.Set();
        }
        request?.Completion.TrySetResult(null);
    }

    private sealed class ScanRequest(IntPtr taskbar)
    {
        public IntPtr Taskbar { get; } = taskbar;
        public TaskCompletionSource<IReadOnlyList<HorizontalRange>?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}

public readonly record struct HorizontalRange(double Left, double Right);
