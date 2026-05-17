using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TempMonitor;

public sealed class HardwareSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public float CpuUsage { get; init; }
    public float CpuUsageMax { get; init; }
    public float GpuUsagePercent { get; init; }
    public float? GpuTemperature { get; init; }
    public float? GpuTemperatureMax { get; init; }
    public float? GpuPowerWatts { get; init; }
    public float RamUsedGb { get; init; }
    public float RamAvailableGb { get; init; }
    public float RamUsedMaxGb { get; init; }
    public float RamUsagePercent { get; init; }
    public float TotalRamGb { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramUsedMaxGb { get; init; }
    public string? NetworkInterfaceName { get; init; }
    public float NetTotalBytesPerSecond { get; init; }
    public float NetUploadBytesPerSecond { get; init; }
    public float NetUploadMaxBytesPerSecond { get; init; }
    public float NetDownloadBytesPerSecond { get; init; }
    public float NetDownloadMaxBytesPerSecond { get; init; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal sealed class MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public MemoryStatusEx()
    {
        dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
    }
}

public sealed class HardwareMonitorService : IDisposable
{
    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    private static readonly Lazy<HardwareMonitorService> LazyInstance = new(() => new HardwareMonitorService());

    private const int InterfaceRefreshIntervalSeconds = 10;
    private const int ZeroTrafficRecheckSeconds = 5;
    private const int MaxLogBytes = 256 * 1024;
    private const int RetainedLogBytes = 128 * 1024;

    private readonly object _syncRoot = new();
    private readonly object _logLock = new();
    private readonly Dictionary<string, PerformanceCounter> _trafficCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _maxValues = new()
    {
        { "CPU_USAGE", 0 },
        { "GPU_TEMP", 0 },
        { "RAM", 0 },
        { "VRAM", 0 },
        { "UP", 0 },
        { "DOWN", 0 }
    };
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private readonly string _logPath;

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _recvCounter;
    private PerformanceCounter? _sentCounter;
    private VendorGpuMonitor? _vendorGpuMonitor;
    private string? _interfaceName;
    private int _zeroTrafficSeconds;
    private int _networkRefreshCounter = InterfaceRefreshIntervalSeconds;
    private bool _disposed;

    private CancellationTokenSource? _cts;
    private Thread? _pollThread;

    public static HardwareMonitorService Instance => LazyInstance.Value;

    public event Action<HardwareSnapshot>? DataUpdated;

    public HardwareSnapshot LatestSnapshot { get; private set; } = new();

    private HardwareMonitorService()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? AppContext.BaseDirectory;
        string exeDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        _logPath = Path.Combine(exeDir, "TempMonitor.log");

        Initialize();

        _cts = new CancellationTokenSource();
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "HardwareMonitorPoll" };
        _pollThread.Start();
    }

    public void ResetMaxValues()
    {
        lock (_syncRoot)
        {
            foreach (string key in _maxValues.Keys)
            {
                _maxValues[key] = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DataUpdated = null;

        _cts?.Cancel();

        if (_pollThread != null && !_pollThread.Join(3000))
        {
            try { _pollThread.Interrupt(); } catch { }
        }

        _cts?.Dispose();
        _cts = null;
        _pollThread = null;

        lock (_syncRoot)
        {
            _cpuCounter?.Dispose();
            _recvCounter?.Dispose();
            _sentCounter?.Dispose();
            _vendorGpuMonitor?.Dispose();
            _vendorGpuMonitor = null;

            foreach (PerformanceCounter counter in _trafficCounters.Values)
            {
                counter.Dispose();
            }

            _trafficCounters.Clear();
        }
    }

    private void PollLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (!_cts!.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    PollMetrics();
                }
                else
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        }
    }

    private void Initialize()
    {
        lock (_syncRoot)
        {
            try
            {
                _vendorGpuMonitor = new VendorGpuMonitor();
                _vendorGpuMonitor.TryInitialize();

                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();

                RefreshNetworkInterfaces();
                SelectBestNetworkInterface();
                ClearReportedError("init");
            }
            catch (Exception ex)
            {
                ReportError("init", ex);
            }
        }
    }

    private void PollMetrics()
    {
        if (_disposed) return;

        HardwareSnapshot snapshot;
        lock (_syncRoot)
        {
            snapshot = BuildSnapshot();
            LatestSnapshot = snapshot;
        }

        DataUpdated?.Invoke(snapshot);
    }

    private HardwareSnapshot BuildSnapshot()
    {
        float cpuUsage = ReadCpuUsage();
        (float ramUsedGb, float ramUsagePercent, float totalRamGb) = ReadRamUsage();
        (float upload, float download) = ReadNetworkMetrics();

        float gpuUsagePercent = 0;
        float? gpuTemp = null;
        float? gpuPowerWatts = null;
        float? vramGb = null;

        if (_vendorGpuMonitor?.Initialized == true)
        {
            var gpu = _vendorGpuMonitor.Read();
            gpuTemp = gpu.Temperature;
            gpuUsagePercent = gpu.Usage ?? 0;
            vramGb = gpu.VramUsedGb;
            gpuPowerWatts = gpu.PowerWatts;
        }

        UpdateMax("CPU_USAGE", cpuUsage);
        UpdateMaxIfHasValue("GPU_TEMP", gpuTemp);
        UpdateMax("RAM", ramUsedGb);
        UpdateMaxIfHasValue("VRAM", vramGb);
        UpdateMax("UP", upload);
        UpdateMax("DOWN", download);

        return new HardwareSnapshot
        {
            Timestamp = DateTime.Now,
            CpuUsage = cpuUsage,
            CpuUsageMax = _maxValues["CPU_USAGE"],
            GpuUsagePercent = gpuUsagePercent,
            GpuTemperature = gpuTemp,
            GpuTemperatureMax = _maxValues["GPU_TEMP"] > 0 ? _maxValues["GPU_TEMP"] : null,
            GpuPowerWatts = gpuPowerWatts,
            RamUsedGb = ramUsedGb,
            RamAvailableGb = Math.Max(0, totalRamGb - ramUsedGb),
            RamUsedMaxGb = _maxValues["RAM"],
            RamUsagePercent = ramUsagePercent,
            TotalRamGb = totalRamGb,
            VramUsedGb = vramGb,
            VramUsedMaxGb = _maxValues["VRAM"] > 0 ? _maxValues["VRAM"] : null,
            NetworkInterfaceName = _interfaceName,
            NetTotalBytesPerSecond = upload + download,
            NetUploadBytesPerSecond = upload,
            NetUploadMaxBytesPerSecond = _maxValues["UP"],
            NetDownloadBytesPerSecond = download,
            NetDownloadMaxBytesPerSecond = _maxValues["DOWN"]
        };
    }

    private float ReadCpuUsage()
    {
        if (_cpuCounter == null) return 0;

        try
        {
            float cpuUsage = _cpuCounter.NextValue();
            ClearReportedError("metric-cpu-usage");
            return cpuUsage;
        }
        catch (Exception ex)
        {
            ReportError("metric-cpu-usage", ex);
            return 0;
        }
    }

    private (float UsedGb, float UsagePercent, float TotalGb) ReadRamUsage()
    {
        try
        {
            var memoryStatus = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(memoryStatus)) return (0, 0, 0);

            double total = memoryStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double used = (memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            ClearReportedError("metric-ram");
            return ((float)used, memoryStatus.dwMemoryLoad, (float)total);
        }
        catch (Exception ex)
        {
            ReportError("metric-ram", ex);
            return (0, 0, 0);
        }
    }

    private (float Upload, float Download) ReadNetworkMetrics()
    {
        _networkRefreshCounter++;
        if (_networkRefreshCounter >= InterfaceRefreshIntervalSeconds)
        {
            _networkRefreshCounter = 0;
            RefreshNetworkInterfaces();
            SelectBestNetworkInterface();
        }

        if (_recvCounter == null || _sentCounter == null) return (0, 0);

        try
        {
            float upload = _sentCounter.NextValue();
            float download = _recvCounter.NextValue();

            if (upload <= 0 && download <= 0)
            {
                _zeroTrafficSeconds++;
            }
            else
            {
                _zeroTrafficSeconds = 0;
            }

            if (_zeroTrafficSeconds >= ZeroTrafficRecheckSeconds)
            {
                _zeroTrafficSeconds = 0;
                SelectBestNetworkInterface();
            }

            ClearReportedError("metric-network");
            return (upload, download);
        }
        catch (Exception ex)
        {
            ReportError("metric-network", ex);
            RefreshNetworkInterfaces();
            SelectBestNetworkInterface();
            return (0, 0);
        }
    }

    private void RefreshNetworkInterfaces()
    {
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            string[] instanceNames = category.GetInstanceNames();

            foreach (string instance in instanceNames)
            {
                if (ShouldIgnoreInterface(instance) || _trafficCounters.ContainsKey(instance))
                    continue;

                var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance);
                counter.NextValue();
                _trafficCounters[instance] = counter;
            }

            var staleInstances = _trafficCounters.Keys
                .Where(instance => !instanceNames.Contains(instance, StringComparer.Ordinal))
                .ToList();

            foreach (string instance in staleInstances)
            {
                _trafficCounters[instance].Dispose();
                _trafficCounters.Remove(instance);

                if (string.Equals(_interfaceName, instance, StringComparison.Ordinal))
                {
                    SetActiveNetworkInterface(null);
                }
            }

            ClearReportedError("network-refresh");
        }
        catch (Exception ex)
        {
            ReportError("network-refresh", ex);
        }
    }

    private void SelectBestNetworkInterface()
    {
        if (_trafficCounters.Count == 0) return;

        try
        {
            string? bestInstance = null;
            float highestTraffic = -1;

            foreach ((string instance, PerformanceCounter counter) in _trafficCounters)
            {
                float value;
                try
                {
                    value = counter.NextValue();
                    ClearReportedError($"network-counter:{instance}");
                }
                catch (Exception ex)
                {
                    ReportError($"network-counter:{instance}", ex);
                    continue;
                }

                if (value > highestTraffic)
                {
                    highestTraffic = value;
                    bestInstance = instance;
                }
            }

            if (bestInstance != null && !string.Equals(bestInstance, _interfaceName, StringComparison.Ordinal))
            {
                SetActiveNetworkInterface(bestInstance);
            }

            ClearReportedError("network-select");
        }
        catch (Exception ex)
        {
            ReportError("network-select", ex);
        }
    }

    private void SetActiveNetworkInterface(string? interfaceName)
    {
        _recvCounter?.Dispose();
        _sentCounter?.Dispose();
        _recvCounter = null;
        _sentCounter = null;
        _interfaceName = interfaceName;

        if (string.IsNullOrWhiteSpace(interfaceName)) return;

        _recvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", interfaceName);
        _sentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", interfaceName);
        _recvCounter.NextValue();
        _sentCounter.NextValue();
    }

    private static bool ShouldIgnoreInterface(string instanceName) =>
        instanceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
        instanceName.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) ||
        instanceName.Contains("Teredo", StringComparison.OrdinalIgnoreCase);

    private void UpdateMax(string key, float current)
    {
        if (current > _maxValues[key])
            _maxValues[key] = current;
    }

    private void UpdateMaxIfHasValue(string key, float? current)
    {
        if (current.HasValue)
            UpdateMax(key, current.Value);
    }

    private void ReportError(string key, Exception ex)
    {
        if (!_reportedErrors.Add(key)) return;

        try
        {
            lock (_logLock)
            {
                TrimLogIfNeeded();
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {key}: {ex}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private void TrimLogIfNeeded()
    {
        if (!File.Exists(_logPath)) return;

        var fileInfo = new FileInfo(_logPath);
        if (fileInfo.Length <= MaxLogBytes) return;

        using var source = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long bytesToKeep = Math.Min(RetainedLogBytes, source.Length);
        source.Seek(-bytesToKeep, SeekOrigin.End);

        byte[] buffer = new byte[bytesToKeep];
        int bytesRead = source.Read(buffer, 0, buffer.Length);
        if (bytesRead <= 0) return;

        int startIndex = Array.IndexOf(buffer, (byte)'\n');
        if (startIndex < 0 || startIndex >= bytesRead - 1)
            startIndex = 0;
        else
            startIndex++;

        File.WriteAllBytes(_logPath, buffer[startIndex..bytesRead]);
    }

    private void ClearReportedError(string key) => _reportedErrors.Remove(key);
}
