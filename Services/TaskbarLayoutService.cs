using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SpeedBar.Services;

public sealed class TaskbarLayoutService
{
    private int _scanRunning;

    public async Task<IReadOnlyList<HorizontalRange>?> ScanOccupiedRangesAsync()
    {
        if (Interlocked.Exchange(ref _scanRunning, 1) != 0) return null;
        try
        {
            return await Task.Run(ScanOccupiedRanges);
        }
        catch
        {
            return null;
        }
        finally
        {
            Volatile.Write(ref _scanRunning, 0);
        }
    }

    private static IReadOnlyList<HorizontalRange> ScanOccupiedRanges()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return [];

        GetWindowThreadProcessId(taskbar, out var explorerProcessId);
        var root = AutomationElement.FromHandle(taskbar);
        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Button);
        var buttons = root.FindAll(TreeScope.Descendants, condition);
        var ranges = new List<HorizontalRange>(buttons.Count);

        foreach (AutomationElement button in buttons)
        {
            try
            {
                var current = button.Current;
                if (current.ProcessId != explorerProcessId || current.IsOffscreen) continue;
                var bounds = current.BoundingRectangle;
                if (bounds.Width < 2 || bounds.Height < 2) continue;
                ranges.Add(new HorizontalRange(bounds.Left, bounds.Right));
            }
            catch (ElementNotAvailableException) { }
        }

        return ranges;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}

public readonly record struct HorizontalRange(double Left, double Right);
