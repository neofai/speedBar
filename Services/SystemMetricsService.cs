using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SpeedBar.Services;

public sealed class SystemMetricsService
{
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private long _lastReceived;
    private long _lastSent;
    private DateTime _lastNetworkSample = DateTime.UtcNow;
    private bool _initialized;

    public MetricsSnapshot Sample()
    {
        var (sent, received) = ReadNetworkTotals();
        var now = DateTime.UtcNow;
        var seconds = Math.Max((now - _lastNetworkSample).TotalSeconds, 0.001);
        var upload = _initialized ? Math.Max(0, sent - _lastSent) / seconds : 0;
        var download = _initialized ? Math.Max(0, received - _lastReceived) / seconds : 0;
        _lastSent = sent;
        _lastReceived = received;
        _lastNetworkSample = now;

        var cpu = ReadCpu();
        var memory = ReadMemory();
        _initialized = true;
        return new MetricsSnapshot(upload, download, cpu, memory);
    }

    private static (long Sent, long Received) ReadNetworkTotals()
    {
        long sent = 0, received = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                var stats = adapter.GetIPv4Statistics();
                sent += stats.BytesSent;
                received += stats.BytesReceived;
            }
            catch { }
        }
        return (sent, received);
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return 0;

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        var idleDelta = idle - _lastIdle;
        var totalDelta = kernel - _lastKernel + user - _lastUser;
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (!_initialized || totalDelta == 0) return 0;
        return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static double ReadMemory()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status) ? status.MemoryLoad : 0;
    }

    private static ulong ToUInt64(FileTime time) =>
        ((ulong)time.HighDateTime << 32) | time.LowDateTime;

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
