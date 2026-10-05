using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SpeedBar.Services;

public sealed class SystemMetricsService
{
    private static readonly TimeSpan AdapterRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, NetworkBaseline> _networkBaselines = new(StringComparer.Ordinal);
    private NetworkInterface[] _adapters = [];
    private long _lastAdapterRefresh;
    private bool _adaptersInitialized;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _cpuInitialized;

    public MetricsSnapshot Sample()
    {
        lock (_syncRoot)
        {
            var (upload, download) = ReadNetworkRates();
            return new MetricsSnapshot(upload, download, ReadCpu(), ReadMemory());
        }
    }

    // The first resumed sample establishes fresh baselines instead of averaging
    // traffic and CPU over a suspended or fullscreen period.
    public void Reset()
    {
        lock (_syncRoot)
        {
            _networkBaselines.Clear();
            _adapters = [];
            _adaptersInitialized = false;
            _cpuInitialized = false;
        }
    }

    private (double Upload, double Download) ReadNetworkRates()
    {
        var now = Stopwatch.GetTimestamp();
        RefreshAdapters(now);
        double upload = 0, download = 0;
        foreach (var adapter in _adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    _networkBaselines.Remove(adapter.Id);
                    continue;
                }

                var stats = adapter.GetIPv4Statistics();
                var sampleTime = Stopwatch.GetTimestamp();
                var current = new NetworkBaseline(stats.BytesSent, stats.BytesReceived, sampleTime);
                if (_networkBaselines.TryGetValue(adapter.Id, out var previous))
                {
                    var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, sampleTime).TotalSeconds;
                    upload += CalculateRate(previous.Sent, current.Sent, seconds);
                    download += CalculateRate(previous.Received, current.Received, seconds);
                }
                _networkBaselines[adapter.Id] = current;
            }
            catch (NetworkInformationException)
            {
                // A disconnected adapter must not retain a stale baseline when it returns.
                _networkBaselines.Remove(adapter.Id);
            }
            catch (InvalidOperationException)
            {
                _networkBaselines.Remove(adapter.Id);
            }
        }
        return (upload, download);
    }

    private void RefreshAdapters(long now)
    {
        if (_adaptersInitialized && Stopwatch.GetElapsedTime(_lastAdapterRefresh, now) < AdapterRefreshInterval)
            return;

        _lastAdapterRefresh = now;
        _adaptersInitialized = true;
        try
        {
            _adapters = NetworkInterface.GetAllNetworkInterfaces();
            var activeIds = _adapters.Select(adapter => adapter.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _networkBaselines.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                _networkBaselines.Remove(id);
        }
        catch (NetworkInformationException)
        {
            _adapters = [];
            _networkBaselines.Clear();
        }
    }

    internal static double CalculateRate(long previous, long current, double seconds)
    {
        // Adapter replacement/counter reset is a new baseline, never a negative rate or a spike.
        if (previous < 0 || current < previous || !double.IsFinite(seconds) || seconds <= 0)
            return 0;
        var rate = (current - previous) / seconds;
        return double.IsFinite(rate) ? rate : 0;
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            _cpuInitialized = false;
            return 0;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        var cpu = _cpuInitialized ? CalculateCpuPercent(_lastIdle, _lastKernel, _lastUser, idle, kernel, user) : 0;
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        _cpuInitialized = true;
        return cpu;
    }

    internal static double CalculateCpuPercent(ulong previousIdle, ulong previousKernel, ulong previousUser,
        ulong idle, ulong kernel, ulong user)
    {
        if (idle < previousIdle || kernel < previousKernel || user < previousUser)
            return 0;

        var idleDelta = idle - previousIdle;
        var kernelDelta = kernel - previousKernel;
        var userDelta = user - previousUser;
        // Kernel time includes idle time. Guard subtraction against unsigned wraparound.
        if (idleDelta > kernelDelta)
            return 0;
        var totalDelta = (double)kernelDelta + userDelta;
        return totalDelta <= 0 ? 0 : Math.Clamp(100.0 * (kernelDelta - idleDelta + (double)userDelta) / totalDelta, 0, 100);
    }

    private static double ReadMemory()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status) ? Math.Clamp(status.MemoryLoad, 0, 100) : 0;
    }

    private static ulong ToUInt64(FileTime time) =>
        ((ulong)time.HighDateTime << 32) | time.LowDateTime;

    private readonly record struct NetworkBaseline(long Sent, long Received, long Timestamp);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}

public readonly record struct MetricsSnapshot(
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    double CpuPercent,
    double MemoryPercent);
