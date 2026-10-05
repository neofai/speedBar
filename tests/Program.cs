using System.Text.Json;
using SpeedBar.Models;
using SpeedBar.Services;
using Bounds = SpeedBar.Services.FullscreenService.PixelBounds;

namespace SpeedBar.RegressionTests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        // All configuration checks are in memory. Never call AppSettings.Load/Save here.
        PlacementTests();
        VisibilityTests();
        FullscreenTests();
        SettingsTests();
        MetricsTests();
        Console.WriteLine($"Regression tests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void PlacementTests()
    {
        Check("placement/empty-left", () => Equal<int?>(10, Place([])));
        Check("placement/empty-right", () => Equal<int?>(890, Place([], dockLeft: false)));
        Check("placement/negative-monitor-left", () => Equal<int?>(-1910,
            TaskbarPlacement.FindFreePosition(-1920, 0, [new(-1000, -800)], 100, 10, true)));
        Check("placement/negative-monitor-right", () => Equal<int?>(-110,
            TaskbarPlacement.FindFreePosition(-1920, 0, [new(-1000, -800)], 100, 10, false)));
        Check("placement/unsorted-overlapping-ranges-left", () => Equal<int?>(510,
            Place([new(300, 500), new(100, 350), new(700, 850)])));
        Check("placement/unsorted-overlapping-ranges-right", () => Equal<int?>(890,
            Place([new(300, 500), new(100, 350), new(700, 850)], dockLeft: false)));
        Check("placement/touching-ranges-have-no-gap", () => Equal<int?>(310,
            Place([new(100, 200), new(200, 300)])));
        Check("placement/clip-outside-monitor", () => Equal<int?>(30,
            Place([new(-100, 20), new(980, 1100)])));
        Check("placement/no-space", () => Equal<int?>(null, Place([new(-100, 1100)])));
        Check("placement/exact-fit-includes-both-margins", () => Equal<int?>(10,
            TaskbarPlacement.FindFreePosition(0, 120, [], 100, 10, true)));
        Check("placement/one-pixel-too-small", () => Equal<int?>(null,
            TaskbarPlacement.FindFreePosition(0, 119, [], 100, 10, true)));
        Check("placement/fractional-edges-left", () => Equal<int?>(111,
            Place([new(0, 100.5), new(250.25, 1000)])));
        Check("placement/fractional-edges-right", () => Equal<int?>(140,
            Place([new(0, 100.5), new(250.25, 1000)], dockLeft: false)));
        Check("placement/fractional-exact-gap-cannot-fit-integer-coordinate", () =>
        {
            HorizontalRange[] ranges = [new(0, 0.5), new(100.5, 200)];
            Equal<int?>(null, TaskbarPlacement.FindFreePosition(0, 200, ranges, 100, 0, true));
            Equal<int?>(null, TaskbarPlacement.FindFreePosition(0, 200, ranges, 100, 0, false));
        });
        Check("placement/ignore-invalid-ranges", () => Equal<int?>(10,
            Place([new(double.NaN, 100), new(100, double.PositiveInfinity), new(900, 100)])));
        Check("placement/negative-margin-is-zero", () => Equal<int?>(0,
            TaskbarPlacement.FindFreePosition(0, 1000, [], 100, -50, true)));
        Check("placement/oversized-margin-does-not-overflow", () => Equal<int?>(null,
            TaskbarPlacement.FindFreePosition(100, 1000, [], 100, int.MaxValue, true)));
        Check("placement/invalid-region", () =>
        {
            Equal<int?>(null, TaskbarPlacement.FindFreePosition(100, 100, [], 10, 0, true));
            Equal<int?>(null, TaskbarPlacement.FindFreePosition(100, 0, [], 10, 0, true));
            Equal<int?>(null, TaskbarPlacement.FindFreePosition(0, 100, [], 0, 0, true));
        });
        Check("placement/integer-extremes", () => Equal<int?>(int.MaxValue - 110,
            TaskbarPlacement.FindFreePosition(int.MinValue, int.MaxValue, [], 100, 10, false)));
    }

    private static int? Place(IEnumerable<HorizontalRange> ranges, bool dockLeft = true) =>
        TaskbarPlacement.FindFreePosition(0, 1000, ranges, 100, 10, dockLeft);

    private static void VisibilityTests()
    {
        Check("visibility/startup-waits-for-stable-clear-state", () =>
        {
            var policy = new OverlayVisibilityPolicy();
            Equal(false, policy.ShouldShow(false, 0));
            Equal(false, policy.ShouldShow(false, 799));
            Equal(true, policy.ShouldShow(false, 800));
            Equal(true, policy.ShouldShow(false, 801));
        });
        Check("visibility/fullscreen-hides-immediately", () =>
        {
            var policy = VisiblePolicy();
            Equal(false, policy.ShouldShow(true, 801));
            Equal(false, policy.ShouldShow(true, 100000));
        });
        Check("visibility/restore-delay-begins-after-fullscreen", () =>
        {
            var policy = VisiblePolicy();
            Equal(false, policy.ShouldShow(true, 900));
            Equal(false, policy.ShouldShow(false, 2000));
            Equal(false, policy.ShouldShow(false, 2799));
            Equal(true, policy.ShouldShow(false, 2800));
        });
        Check("visibility/transient-clear-state-does-not-flash", () =>
        {
            var policy = new OverlayVisibilityPolicy();
            Equal(false, policy.ShouldShow(false, 0));
            Equal(false, policy.ShouldShow(true, 700));
            Equal(false, policy.ShouldShow(false, 750));
            Equal(false, policy.ShouldShow(false, 1500));
            Equal(true, policy.ShouldShow(false, 1550));
        });
        Check("visibility/clock-reset-restarts-grace-period", () =>
        {
            var policy = new OverlayVisibilityPolicy();
            Equal(false, policy.ShouldShow(false, 1000));
            Equal(false, policy.ShouldShow(false, 900));
            Equal(false, policy.ShouldShow(false, 1699));
            Equal(true, policy.ShouldShow(false, 1700));
        });
        Check("visibility/pause-resume-does-not-reuse-visible-state", () =>
        {
            var policy = VisiblePolicy();
            policy.Reset();
            Equal(false, policy.ShouldShow(false, 60000));
            Equal(false, policy.ShouldShow(false, 60799));
            Equal(true, policy.ShouldShow(false, 60800));
        });
        Check("visibility/repeated-reset-is-safe", () =>
        {
            var policy = VisiblePolicy();
            policy.Reset();
            policy.Reset();
            Equal(false, policy.ShouldShow(true, 1200));
            Equal(false, policy.ShouldShow(false, 9000));
        });
    }

    private static OverlayVisibilityPolicy VisiblePolicy()
    {
        var policy = new OverlayVisibilityPolicy();
        Equal(false, policy.ShouldShow(false, 0));
        Equal(true, policy.ShouldShow(false, OverlayVisibilityPolicy.RestoreDelayMilliseconds));
        return policy;
    }

    private static void FullscreenTests()
    {
        var primary = new Bounds(0, 0, 1920, 1080);
        Check("fullscreen/exact-monitor", () => Equal(true, FullscreenService.CoversMonitor(primary, primary)));
        Check("fullscreen/two-physical-pixel-tolerance", () => Equal(true,
            FullscreenService.CoversMonitor(new(2, 2, 1918, 1078), primary)));
        Check("fullscreen/three-pixel-gap-is-not-fullscreen", () => Equal(false,
            FullscreenService.CoversMonitor(new(3, 0, 1920, 1080), primary)));
        Check("fullscreen/oversized-borderless-window", () => Equal(true,
            FullscreenService.CoversMonitor(new(-8, -8, 1928, 1088), primary)));
        Check("fullscreen/work-area-does-not-cover-taskbar", () => Equal(false,
            FullscreenService.CoversMonitor(new(0, 0, 1920, 1040), primary)));
        Check("fullscreen/negative-origin-monitor", () => Equal(true,
            FullscreenService.CoversMonitor(new(-2560, -1440, 0, 0), new(-2560, -1440, 0, 0))));
        Check("fullscreen/game-on-another-monitor", () => Equal(false,
            FullscreenService.CoversMonitor(new(-1920, 0, 0, 1080), primary)));
        Check("fullscreen/spanning-monitors", () => Equal(true,
            FullscreenService.CoversMonitor(new(-1920, 0, 1920, 1080), primary)));
        Check("fullscreen/high-dpi-physical-coordinates", () => Equal(true,
            FullscreenService.CoversMonitor(new(2, 2, 3838, 2158), new(0, 0, 3840, 2160))));
        Check("fullscreen/invalid-window-bounds", () =>
        {
            Equal(false, FullscreenService.CoversMonitor(new(0, 0, 0, 1080), primary));
            Equal(false, FullscreenService.CoversMonitor(new(1920, 1080, 0, 0), primary));
        });
        Check("fullscreen/invalid-monitor-bounds", () => Equal(false,
            FullscreenService.CoversMonitor(primary, new(0, 0, 1920, 0))));
        Check("fullscreen/negative-tolerance-is-zero", () => Equal(false,
            FullscreenService.CoversMonitor(new(1, 0, 1920, 1080), primary, -100)));
        Check("fullscreen/large-tolerance-cannot-mask-windowed-app", () => Equal(false,
            FullscreenService.CoversMonitor(new(100, 100, 1820, 980), primary, int.MaxValue)));
        Check("fullscreen/integer-extreme-coordinates", () => Equal(true,
            FullscreenService.CoversMonitor(new(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue),
                new(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue))));
    }

    private static void SettingsTests()
    {
        Check("settings/defaults-are-stable", () =>
        {
            var settings = new AppSettings();
            var original = JsonSerializer.Serialize(settings);
            settings.Normalize();
            Equal(original, JsonSerializer.Serialize(settings));
        });
        Check("settings/invalid-colors-fall-back-independently", () =>
        {
            var settings = new AppSettings
            {
                UploadColor = null!, DownloadColor = "", CpuColor = "#GGG",
                MemoryColor = "unknown", BackgroundColor = "ContextColor missing.icc 1,0,0,0"
            };
            settings.Normalize();
            var defaults = new AppSettings();
            Equal(defaults.UploadColor, settings.UploadColor);
            Equal(defaults.DownloadColor, settings.DownloadColor);
            Equal(defaults.CpuColor, settings.CpuColor);
            Equal(defaults.MemoryColor, settings.MemoryColor);
            Equal(defaults.BackgroundColor, settings.BackgroundColor);
        });
        Check("settings/legacy-colors-canonicalize", () =>
        {
            var settings = new AppSettings
            {
                UploadColor = " #abc ", DownloadColor = "#8abc", CpuColor = "#aabbcc",
                MemoryColor = "#80aabbcc", BackgroundColor = "Red"
            };
            settings.Normalize();
            Equal("#FFAABBCC", settings.UploadColor);
            Equal("#88AABBCC", settings.DownloadColor);
            Equal("#FFAABBCC", settings.CpuColor);
            Equal("#80AABBCC", settings.MemoryColor);
            Equal("#FFFF0000", settings.BackgroundColor);
        });
        Check("settings/nonfinite-values-use-defaults", () =>
        {
            var settings = new AppSettings { FontSize = double.NaN, OffsetX = double.PositiveInfinity, OffsetY = double.NegativeInfinity };
            settings.Normalize();
            Equal(12.0, settings.FontSize);
            Equal(8.0, settings.OffsetX);
            Equal(4.0, settings.OffsetY);
        });
        Check("settings/values-clamp-at-lower-bounds", () =>
        {
            var settings = new AppSettings { FontSize = -10, RefreshMilliseconds = int.MinValue, OffsetX = -10000, OffsetY = -10000 };
            settings.Normalize();
            Equal(10.0, settings.FontSize);
            Equal(500, settings.RefreshMilliseconds);
            Equal(-512.0, settings.OffsetX);
            Equal(-512.0, settings.OffsetY);
        });
        Check("settings/values-clamp-at-upper-bounds", () =>
        {
            var settings = new AppSettings { FontSize = 100, RefreshMilliseconds = int.MaxValue, OffsetX = 10000, OffsetY = 10000 };
            settings.Normalize();
            Equal(20.0, settings.FontSize);
            Equal(5000, settings.RefreshMilliseconds);
            Equal(512.0, settings.OffsetX);
            Equal(512.0, settings.OffsetY);
        });
        Check("settings/custom-refresh-is-preserved", () =>
        {
            var settings = new AppSettings { RefreshMilliseconds = 1500 };
            settings.Normalize();
            Equal(1500, settings.RefreshMilliseconds);
        });
        Check("settings/clone-preserves-all-fields-and-is-independent", () =>
        {
            var original = new AppSettings
            {
                UploadColor = "#FF102030", DownloadColor = "#FF203040", CpuColor = "#FF304050",
                MemoryColor = "#FF405060", BackgroundColor = "#80506070", TransparentBackground = true,
                FontSize = 18, RefreshMilliseconds = 1500, OffsetX = -20, OffsetY = 16,
                DockLeft = true, StartWithWindows = true
            };
            var clone = original.Clone();
            Equal(false, ReferenceEquals(original, clone));
            Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(clone));
            clone.FontSize = 10;
            clone.StartWithWindows = false;
            Equal(18.0, original.FontSize);
            Equal(true, original.StartWithWindows);
        });
        Check("settings/clone-normalization-does-not-mutate-source", () =>
        {
            var original = new AppSettings { FontSize = double.NaN, UploadColor = "bad-color" };
            var clone = original.Clone();
            Equal(true, double.IsNaN(original.FontSize));
            Equal("bad-color", original.UploadColor);
            Equal(12.0, clone.FontSize);
            Equal(new AppSettings().UploadColor, clone.UploadColor);
        });
        Check("settings/legacy-json-missing-fields-retains-defaults", () =>
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("{\"FontSize\":16,\"DockLeft\":true,\"UnknownOldField\":42}")!;
            settings.Normalize();
            Equal(16.0, settings.FontSize);
            Equal(true, settings.DockLeft);
            Equal(1000, settings.RefreshMilliseconds);
            Equal(new AppSettings().UploadColor, settings.UploadColor);
        });
    }

    private static void MetricsTests()
    {
        Check("network/bytes-per-second", () => Equal(200.0, SystemMetricsService.CalculateRate(1000, 1400, 2)));
        Check("network/subsecond-interval", () => Equal(800.0, SystemMetricsService.CalculateRate(1000, 1400, 0.5)));
        Check("network/idle-adapter", () => Equal(0.0, SystemMetricsService.CalculateRate(1000, 1000, 1)));
        Check("network/reset-counter-cannot-underflow", () => Equal(0.0, SystemMetricsService.CalculateRate(long.MaxValue, 20, 1)));
        Check("network/negative-counters-are-discarded", () =>
        {
            Equal(0.0, SystemMetricsService.CalculateRate(-100, 100, 1));
            Equal(0.0, SystemMetricsService.CalculateRate(100, -100, 1));
        });
        Check("network/nonpositive-elapsed-time", () =>
        {
            Equal(0.0, SystemMetricsService.CalculateRate(0, 100, 0));
            Equal(0.0, SystemMetricsService.CalculateRate(0, 100, -1));
        });
        Check("network/nonfinite-elapsed-time", () =>
        {
            Equal(0.0, SystemMetricsService.CalculateRate(0, 100, double.NaN));
            Equal(0.0, SystemMetricsService.CalculateRate(0, 100, double.PositiveInfinity));
        });
        Check("network/overflowing-rate-is-discarded", () => Equal(0.0, SystemMetricsService.CalculateRate(0, 100, double.Epsilon)));
        Check("network/large-counters-keep-small-delta", () => Equal(50.0,
            SystemMetricsService.CalculateRate(long.MaxValue - 100, long.MaxValue, 2)));
        Check("cpu/kernel-includes-idle-time", () => Near(100.0 * 100 / 150,
            SystemMetricsService.CalculateCpuPercent(100, 200, 300, 150, 300, 350)));
        Check("cpu/all-idle", () => Equal(0.0, SystemMetricsService.CalculateCpuPercent(0, 0, 0, 100, 100, 0)));
        Check("cpu/all-busy", () => Equal(100.0, SystemMetricsService.CalculateCpuPercent(0, 0, 0, 0, 100, 100)));
        Check("cpu/zero-elapsed-counters", () => Equal(0.0, SystemMetricsService.CalculateCpuPercent(100, 200, 300, 100, 200, 300)));
        Check("cpu/any-counter-rollback-resets-baseline", () =>
        {
            Equal(0.0, SystemMetricsService.CalculateCpuPercent(100, 200, 300, 99, 250, 350));
            Equal(0.0, SystemMetricsService.CalculateCpuPercent(100, 200, 300, 150, 199, 350));
            Equal(0.0, SystemMetricsService.CalculateCpuPercent(100, 200, 300, 150, 250, 299));
        });
        Check("cpu/inconsistent-idle-time-does-not-wrap", () => Equal(0.0,
            SystemMetricsService.CalculateCpuPercent(0, 0, 0, 200, 100, 50)));
        Check("cpu/large-total-does-not-overflow", () => Near(75.0,
            SystemMetricsService.CalculateCpuPercent(0, 0, 0, ulong.MaxValue / 2, ulong.MaxValue, ulong.MaxValue)));
        Check("metrics/initial-live-sample-establishes-baselines", () =>
        {
            var snapshot = new SystemMetricsService().Sample();
            AssertFreshBaseline(snapshot);
            ValidPercent(snapshot.MemoryPercent);
        });
        Check("metrics/pause-resume-first-sample-is-fresh", () =>
        {
            var metrics = new SystemMetricsService();
            metrics.Sample();
            metrics.Sample();
            metrics.Reset();
            AssertFreshBaseline(metrics.Sample());
            metrics.Reset();
            metrics.Reset();
            AssertFreshBaseline(metrics.Sample());
        });
    }

    private static void AssertFreshBaseline(MetricsSnapshot snapshot)
    {
        Equal(0.0, snapshot.UploadBytesPerSecond);
        Equal(0.0, snapshot.DownloadBytesPerSecond);
        Equal(0.0, snapshot.CpuPercent);
    }

    private static void ValidPercent(double actual)
    {
        if (!double.IsFinite(actual) || actual < 0 || actual > 100)
            throw new InvalidOperationException($"Invalid percentage: {actual}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void Near(double expected, double actual)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-8)
            throw new InvalidOperationException($"Expected approximately {expected}, actual {actual}.");
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }
}
