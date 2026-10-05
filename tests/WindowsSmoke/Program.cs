using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpeedBar.Models;
using SpeedBar.Services;
using Forms = System.Windows.Forms;

namespace SpeedBar.WindowsSmoke;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "fixture" => RunFixture(args[1]),
                "worker" => RunWorker(args[1]),
                _ => RunIsolated()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (args.Length > 1) File.WriteAllText(Path.Combine(args[1], "fatal-" + args[0] + ".txt"), exception.ToString());
            return 1;
        }
    }

    private static int RunIsolated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SpeedBar.WindowsSmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var desktopName = "SpeedBarSmoke_" + Guid.NewGuid().ToString("N");
        var desktop = Native.CreateDesktop(desktopName, null, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");
        Process? fixture = null;
        Process? worker = null;
        try
        {
            fixture = StartOnDesktop(desktopName, "fixture", directory);
            var ready = Path.Combine(directory, "fixture.json");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(ready) && !fixture.HasExited && wait.ElapsedMilliseconds < 10000) Thread.Sleep(50);
            if (!File.Exists(ready)) throw new InvalidOperationException("Fixture failed to initialize: " + directory);
            worker = StartOnDesktop(desktopName, "worker", directory);
            if (!worker.WaitForExit(55000)) throw new TimeoutException("Smoke worker exceeded 55 seconds");
            var report = Path.Combine(directory, "report.json");
            Console.WriteLine(File.Exists(report) ? File.ReadAllText(report) : "No report; inspect " + directory);
            foreach (var error in Directory.GetFiles(directory, "fatal-*.txt")) Console.Error.WriteLine(File.ReadAllText(error));
            Console.WriteLine("Evidence directory: " + directory);
            return File.Exists(report) && JsonDocument.Parse(File.ReadAllText(report)).RootElement.GetProperty("Passed").GetBoolean() ? 0 : 1;
        }
        finally
        {
            // Only processes created above belong to this test. No shell or user process is touched.
            foreach (var process in new[] { worker, fixture })
            {
                if (process is null) continue;
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
                finally { process.Dispose(); }
            }
            Native.CloseDesktop(desktop);
        }
    }

    private static Process StartOnDesktop(string desktop, string mode, string directory)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SpeedBar.WindowsSmoke.exe");
        var startup = new Native.StartupInfo
        {
            Size = Marshal.SizeOf<Native.StartupInfo>(),
            Desktop = "WinSta0\\" + desktop
        };
        var command = new StringBuilder($"\"{executable}\" {mode} \"{directory}\"");
        if (!Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
                IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess on isolated desktop failed");
        try
        {
            var process = Process.GetProcessById((int)info.ProcessId);
            _ = process.Handle;
            return process;
        }
        finally { Native.CloseHandle(info.Process); Native.CloseHandle(info.Thread); }
    }

    private static int RunFixture(string directory)
    {
        Forms.Application.EnableVisualStyles();
        using var fixture = new Fixture(directory);
        Forms.Application.Run(fixture);
        return 0;
    }

    private static int RunWorker(string directory)
    {
        var result = 1;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var report = new SmokeReport();
            try
            {
                await RunChecks(directory, report);
                report.Passed = true;
                result = 0;
            }
            catch (Exception exception) { report.Error = exception.ToString(); }
            finally
            {
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }

    private static async Task RunChecks(string directory, SmokeReport report)
    {
        var fixture = JsonSerializer.Deserialize<FixtureInfo>(File.ReadAllText(Path.Combine(directory, "fixture.json")))!;
        var fixtureWindow = new IntPtr(fixture.Window);
        var taskbar = new IntPtr(fixture.Taskbar);
        Check(TaskbarService.FindTaskbar() == taskbar, "isolated mock taskbar discovered", report);
        Check(fixture.ProcessId != Environment.ProcessId, "fullscreen fixture belongs to a different process", report);
        report.ShellNotificationHResult = Native.SHQueryUserNotificationState(out var shellState);
        report.ShellNotificationState = shellState;
        var window = new MainWindow();
        // Loading settings is read-only. Replace them only in this test process; never save.
        SetField(window, "_settings", new AppSettings { RefreshMilliseconds = 500 });
        Invoke(window, "ApplySettings");
        TaskbarController? controller = null;
        try
        {
            window.Show();
            await WaitUntil(() => Read<TaskbarController?>(window, "_taskbar") is not null, 3000, "MainWindow Loaded");
            controller = Read<TaskbarController>(window, "_taskbar");
            controller.DisplayActiveChanged += active => report.Transitions.Add(new(Environment.TickCount64, active));
            var hwnd = new WindowInteropHelper(window).Handle;
            var fullscreen = new FullscreenService();
            Command(fixtureWindow, 0);
            await Task.Delay(100);
            report.ForegroundAvailable = Native.GetForegroundWindow() == fixtureWindow;
            SeedObservedFullscreen(fullscreen, fixtureWindow, taskbar, fixture.ProcessId);
            Check(!fullscreen.IsSuppressed(hwnd, taskbar), "ordinary native window is not fullscreen", report);
            await WaitUntil(() => controller.IsDisplayActive, 8000, "initial controller display");
            Check(window.IsVisible && Native.IsWindowVisible(hwnd), "startup shows actual WPF HWND", report);
            Check(Read<IReadOnlyList<HorizontalRange>?>(controller, "_ranges") is { Count: > 0 },
                "real UI Automation reads the mock taskbar button", report);

            Command(fixtureWindow, 1);
            await Task.Delay(700);
            Check(Native.IsZoomed(fixtureWindow), "fixture is genuinely maximized", report);
            SeedObservedFullscreen(fullscreen, fixtureWindow, taskbar, fixture.ProcessId);
            Check(!fullscreen.IsSuppressed(hwnd, taskbar), "ordinary maximized HWND is not fullscreen", report);
            Check(controller.IsDisplayActive, "maximized app retains taskbar display", report);
            Check(Read<DispatcherTimer>(window, "_timer").IsEnabled, "actual MainWindow metrics timer runs while visible", report);

            Command(fixtureWindow, 2);
            // A desktop that is never switched to the input desktop cannot own foreground
            // focus. Seed only the identity normally recorded on first activation; subsequent
            // decisions still use the production Win32 geometry, visibility and process checks.
            SeedObservedFullscreen(fullscreen, fixtureWindow, taskbar, fixture.ProcessId);
            SeedObservedFullscreen(Read<FullscreenService>(controller, "_fullscreen"), fixtureWindow, taskbar, fixture.ProcessId);
            await WaitUntil(() => !controller.IsDisplayActive && !window.IsVisible, 2500, "fullscreen hide");
            Check(fullscreen.IsSuppressed(hwnd, taskbar), "borderless monitor-sized HWND detected", report);
            Check(!Native.IsWindowVisible(hwnd), "fullscreen hides actual overlay HWND", report);
            Check((Native.GetWindowLong(hwnd, -20) & 8) == 0, "fullscreen demotes TOPMOST", report);
            await AssertNoBackgroundWork(window, controller, report, "fullscreen");

            var restoredAt = Environment.TickCount64;
            Command(fixtureWindow, 0);
            await Task.Delay(500);
            Check(!controller.IsDisplayActive && !window.IsVisible, "restoration remains hidden before 800 ms", report);
            await WaitUntil(() => controller.IsDisplayActive, 7000, "fullscreen restore");
            Check(Environment.TickCount64 - restoredAt >= OverlayVisibilityPolicy.RestoreDelayMilliseconds,
                "restoration honors stable interval", report);

            controller.SetPaused(true);
            Check(!controller.IsDisplayActive && !window.IsVisible, "manual pause hides immediately", report);
            Command(fixtureWindow, 2);
            await Task.Delay(300);
            Command(fixtureWindow, 0);
            await AssertNoBackgroundWork(window, controller, report, "manual pause after fullscreen exit");
            Check(controller.IsPaused && !controller.IsDisplayActive, "fullscreen exit cannot undo manual pause", report);
            controller.SetPaused(false);
            await WaitUntil(() => controller.IsDisplayActive, 7000, "manual resume");
            Check(window.IsVisible, "manual resume restores display", report);

            Command(fixtureWindow, 3);
            await WaitUntil(() => !controller.IsDisplayActive, 2500, "taskbar destruction hides overlay");
            Check(TaskbarService.FindTaskbar() == IntPtr.Zero && !window.IsVisible,
                "missing taskbar hides actual MainWindow", report);
            Check(Native.IsWindow(hwnd), "taskbar destruction preserves independent overlay HWND", report);
            taskbar = Command(fixtureWindow, 4);
            Check(taskbar != IntPtr.Zero && TaskbarService.FindTaskbar() == taskbar,
                "replacement mock taskbar discovered", report);
            await WaitUntil(() => controller.IsDisplayActive, 7000, "taskbar recreation restores overlay");
            Check(new WindowInteropHelper(window).Handle == hwnd && Read<TaskbarSnapshot>(controller, "_snapshot").Handle == taskbar,
                "taskbar recreation reuses overlay with fresh taskbar snapshot", report);
            Check(Read<IReadOnlyList<HorizontalRange>?>(controller, "_ranges") is { Count: > 0 },
                "replacement taskbar gets a fresh real UIA scan", report);

            var layout = Read<object>(controller, "_layout");
            var watcher = Read<object>(controller, "_watcher");
            var timer = Read<DispatcherTimer>(controller, "_timer");
            await WaitUntil(() => !Read<bool>(window, "_sampling"), 3000, "last metric sample settling");
            Invoke(window, "CloseApplication");
            Check(Read<bool>(window, "_resourcesReleased") && !Read<DispatcherTimer>(window, "_timer").IsEnabled,
                "actual MainWindow close releases metrics resources", report);
            Check(!timer.IsEnabled, "Dispose stops taskbar timer", report);
            Check(Read<IntPtr>(watcher, "_foregroundHook") == IntPtr.Zero &&
                Read<IntPtr>(watcher, "_locationHook") == IntPtr.Zero, "Dispose removes native event hooks", report);
            Check(Read<Thread>(layout, "_worker").Join(1500) && Read<bool>(layout, "_disposed"),
                "Dispose releases layout worker", report);
            Check(!Native.IsWindow(hwnd), "closing destroys only the test overlay HWND", report);
        }
        finally
        {
            controller?.Dispose();
            if (!Read<bool>(window, "_resourcesReleased")) Invoke(window, "CloseApplication");
            Native.PostMessage(fixtureWindow, Fixture.CommandMessage, new IntPtr(9), IntPtr.Zero);
        }
    }

    private static async Task AssertNoBackgroundWork(MainWindow window, TaskbarController controller, SmokeReport report, string phase)
    {
        await WaitUntil(() => !Read<bool>(controller, "_scanRunning"), 3000, "in-flight scan settling");
        await WaitUntil(() => !Read<bool>(window, "_sampling"), 3000, "in-flight metrics sample settling");
        var nextScan = Read<long>(controller, "_nextScan");
        var layout = Read<object>(controller, "_layout");
        var metricsTimer = Read<DispatcherTimer>(window, "_timer");
        var metrics = Read<object>(window, "_metrics");
        var lastKernel = Read<ulong>(metrics, "_lastKernel");
        var generation = Read<int>(window, "_sampleGeneration");
        var transitions = report.Transitions.Count;
        var until = Environment.TickCount64 + 5500; // Longer than the live 5-second layout refresh period.
        while (Environment.TickCount64 < until)
        {
            if (controller.IsDisplayActive || Read<bool>(controller, "_scanRunning") ||
                Read<long>(controller, "_nextScan") != nextScan || Read<object?>(layout, "_request") is not null ||
                metricsTimer.IsEnabled || Read<bool>(window, "_sampling") ||
                Read<int>(window, "_sampleGeneration") != generation || Read<ulong>(metrics, "_lastKernel") != lastKernel)
                throw new InvalidOperationException("Background/UIA work resumed during " + phase);
            await Task.Delay(75);
        }
        Check(report.Transitions.Count == transitions && !controller.IsDisplayActive,
            phase + ": no display activation, UIA requests or actual metrics samples for 5.5 s", report);
    }

    private static IntPtr Command(IntPtr fixture, int command)
    {
        if (Native.SendMessageTimeout(fixture, Fixture.CommandMessage, new IntPtr(command), IntPtr.Zero,
                2, 2000, out var result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Fixture command failed");
        return result;
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeout, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException(description);
            await Task.Delay(40);
        }
    }

    private static T Read<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void SeedObservedFullscreen(FullscreenService service, IntPtr hwnd, IntPtr taskbar, int processId)
    {
        SetField(service, "_trackedFullscreen", hwnd);
        SetField(service, "_trackedProcessId", (uint)processId);
        SetField(service, "_trackedMonitor", Native.MonitorFromWindow(taskbar, 2));
    }

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Invoke(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);

    private static void Check(bool condition, string description, SmokeReport report)
    {
        if (!condition) throw new InvalidOperationException(description);
        report.Checks.Add(description);
    }

    private sealed class Fixture(string directory) : Forms.Form
    {
        public const uint CommandMessage = 0x8001;
        private Native.WindowProcedure? _procedure;
        private IntPtr _taskbar;
        private System.Drawing.Rectangle _screen;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Text = "SpeedBar isolated smoke fixture";
            _screen = Forms.Screen.PrimaryScreen!.Bounds;
            _procedure = Native.DefWindowProc;
            var definition = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = Native.GetModuleHandle(null), ClassName = "Shell_TrayWnd"
            };
            if (Native.RegisterClassEx(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            CreateTaskbar();
            ApplyMode(0);
            File.WriteAllText(Path.Combine(directory, "fixture.json"), JsonSerializer.Serialize(
                new FixtureInfo(Handle.ToInt64(), _taskbar.ToInt64(), Environment.ProcessId)));
        }

        private void CreateTaskbar()
        {
            var instance = Native.GetModuleHandle(null);
            _taskbar = Native.CreateWindowEx(0x08000080, "Shell_TrayWnd", "Mock taskbar",
                0x90000000, _screen.Left, _screen.Bottom - 48, _screen.Width, 48,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_taskbar == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.CreateWindowEx(0, "BUTTON", "Mock task button", 0x50000000,
                50, 2, 100, 42, _taskbar, new IntPtr(1), instance, IntPtr.Zero);
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if ((uint)message.Msg == CommandMessage)
            {
                var command = message.WParam.ToInt32();
                if (command == 9) Close();
                else if (command == 3) { Native.DestroyWindow(_taskbar); _taskbar = IntPtr.Zero; }
                else if (command == 4) CreateTaskbar();
                else ApplyMode(command);
                message.Result = command == 4 ? _taskbar : new IntPtr(1);
                return;
            }
            base.WndProc(ref message);
        }

        private void ApplyMode(int mode)
        {
            WindowState = Forms.FormWindowState.Normal;
            FormBorderStyle = mode == 2 ? Forms.FormBorderStyle.None : Forms.FormBorderStyle.Sizable;
            Bounds = mode == 2 ? _screen : new System.Drawing.Rectangle(_screen.Left + 180, _screen.Top + 120, 640, 400);
            if (mode == 1) WindowState = Forms.FormWindowState.Maximized;
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_taskbar != IntPtr.Zero) Native.DestroyWindow(_taskbar);
            base.OnClosed(e);
        }
    }

    private sealed record FixtureInfo(long Window, long Taskbar, int ProcessId);
    private sealed record Transition(long AtMilliseconds, bool Active);
    private sealed class SmokeReport
    {
        public bool Passed { get; set; }
        public string Isolation { get; set; } = "CreateDesktop; never switched input desktop; fixture and worker are separate processes";
        public int ShellNotificationState { get; set; }
        public int ShellNotificationHResult { get; set; }
        public bool ForegroundAvailable { get; set; }
        public List<string> Checks { get; } = [];
        public List<Transition> Transitions { get; } = [];
        public string? Error { get; set; }
        public string Limitation { get; set; } = "The isolated desktop is never made the input desktop. The previously-observed fullscreen HWND/PID/monitor fields are seeded for ordinary, maximized and fullscreen windows because foreground activation is unavailable; all following native geometry/visibility, MainWindow metrics, UIA and controller decisions use production code. Foreground event delivery, exclusive Direct3D and real Explorer are not exercised.";
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int Size;
            public string? Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedBytes;
            public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
        {
            public uint Size, Style;
            public IntPtr Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string? MenuName, ClassName;
            public IntPtr SmallIcon;
        }
        public delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateDesktop(string desktop, string? device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? module);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string text, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    }
}
